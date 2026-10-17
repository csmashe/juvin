using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Juvin.Core;
using Juvin.Core.Services;
using Juvin.Platform.Unix;
using Microsoft.Win32.SafeHandles;

namespace Juvin.Platform.Linux.Secrets;

internal sealed class SecretProtectionException(string message, Exception? innerException = null)
    : CryptographicException(message, innerException);

/// <summary>
///     Authenticated at-rest protection with a random per-install key held in a 0600 key file.
///     Format: magic <c>JVSP</c>, version byte, nonce, tag, ciphertext (AES-256-GCM).
/// </summary>
internal static partial class FileKeySecretProtection
{
    private const byte CurrentVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 5;

    private static readonly byte[] s_magic = [.. "JVSP"u8];
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    /// <param name="plainText">The secret.</param>
    /// <param name="associatedData">
    ///     Authenticated but not encrypted; binds the ciphertext to its secret name so one file
    ///     cannot be swapped in for another.
    /// </param>
    /// <param name="keyFilePath">Key file, created on first use.</param>
    public static byte[] Encrypt(string plainText, byte[] associatedData, string keyFilePath)
    {
        var key = EnsureKeyFile(keyFilePath);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var combined = new byte[HeaderSize + NonceSize + TagSize + bytes.Length];
            s_magic.CopyTo(combined, 0);
            combined[s_magic.Length] = CurrentVersion;

            var nonce = combined.AsSpan(HeaderSize, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(
                nonce,
                bytes,
                combined.AsSpan(HeaderSize + NonceSize + TagSize),
                combined.AsSpan(HeaderSize + NonceSize, TagSize),
                associatedData
            );
            CryptographicOperations.ZeroMemory(bytes);
            return combined;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Throws <see cref="SecretProtectionException" /> when the data cannot be authenticated.</summary>
    public static string Decrypt(byte[] combined, byte[] associatedData, string keyFilePath)
    {
        if (
            combined.Length < HeaderSize + NonceSize + TagSize
            || !combined.AsSpan(0, s_magic.Length).SequenceEqual(s_magic)
            || combined[s_magic.Length] != CurrentVersion
        )
        {
            throw new SecretProtectionException("Stored secret has an unknown format.");
        }

        var key =
            TryReadExistingKeyFile(keyFilePath)
            ?? throw new SecretProtectionException(
                $"Secret protection key '{keyFilePath}' is missing."
            );
        try
        {
            var cipher = combined.AsSpan(HeaderSize + NonceSize + TagSize);
            var plaintext = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                combined.AsSpan(HeaderSize, NonceSize),
                cipher,
                combined.AsSpan(HeaderSize + NonceSize, TagSize),
                plaintext,
                associatedData
            );
            try
            {
                return s_strictUtf8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex) when (ex is CryptographicException and not SecretProtectionException or DecoderFallbackException)
        {
            throw new SecretProtectionException("Stored secret could not be decrypted.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] EnsureKeyFile(string keyFilePath)
    {
        var directory = Path.GetDirectoryName(keyFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            JuvinEnvironment.CreatePrivateDirectory(directory);
        }

        var existingKey = TryReadExistingKeyFile(keyFilePath);
        if (existingKey is not null)
        {
            return existingKey;
        }

        var generated = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            // Publishing the complete staged 0600 file preserves atomic visibility; opening
            // the final path with O_CREAT | O_EXCL would expose partial key material.
            AtomicFile.WriteAllBytesCreateNew(keyFilePath, generated);
        }
        catch (IOException ex)
        {
            // A concurrent creator may have won the race; use its key if it is valid.
            try
            {
                existingKey = TryReadExistingKeyFile(keyFilePath);
            }
            catch (SecretProtectionException inner)
            {
                // Keep the write failure visible: the re-read's rejection alone would hide
                // e.g. an ENOSPC behind a not-a-regular-file message.
                throw new SecretProtectionException(inner.Message, new AggregateException(ex, inner));
            }

            if (existingKey is not null)
            {
                return existingKey;
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generated);
        }

        return TryReadExistingKeyFile(keyFilePath)
            ?? throw new SecretProtectionException(
                $"Secret protection key '{keyFilePath}' could not be validated."
            );
    }

    private static byte[]? TryReadExistingKeyFile(string path)
    {
        var fd = NativeFile.OpenExisting(path, out var openError);
        if (fd < 0)
        {
            if (openError == UnixNative.ErrorNoEntry)
            {
                return null;
            }

            if (openError == UnixNative.ErrorSymbolicLink)
            {
                throw new SecretProtectionException(
                    $"Secret protection key '{path}' must be a regular file, not a symbolic link."
                );
            }

            throw new SecretProtectionException(
                $"Secret protection key '{path}' could not be validated.",
                new Win32Exception(openError)
            );
        }

        using var handle = new SafeFileHandle(fd, ownsHandle: true);

        if (!NativeFile.TryGetStatus(fd, out var stat, out var statError))
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' could not be validated.",
                new Win32Exception(statError)
            );
        }

        if (!NativeFile.HasTypeAndMode(stat))
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' file type, permissions, and ownership could not be determined."
            );
        }

        if (!NativeFile.IsRegular(stat.Mode))
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' must be a regular file."
            );
        }

        if (!NativeFile.HasOwnerOnlyMode(stat.Mode))
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' must have Unix mode 0600."
            );
        }

        // Mode 0600 does not prove the file is ours: under sudo with a shared HOME another user
        // could plant a key they know. Reject foreign owners.
        if (!NativeFile.IsOwnedByEffectiveUser(stat))
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' must be owned by the current user."
            );
        }

        var key = new byte[KeySize];
        try
        {
            var bytesRead = 0;
            while (bytesRead < key.Length)
            {
                var read = RandomAccess.Read(handle, key.AsSpan(bytesRead), bytesRead);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
            }

            Span<byte> extraByte = stackalloc byte[1];
            // ReSharper disable once InvertIf -- the rejection stays a guard clause so the success path ends the block
            if (bytesRead != KeySize || RandomAccess.Read(handle, extraByte, KeySize) != 0)
            {
                extraByte.Clear();
                throw new SecretProtectionException(
                    $"Secret protection key '{path}' must contain exactly {KeySize} bytes."
                );
            }

            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new SecretProtectionException(
                $"Secret protection key '{path}' could not be validated.",
                ex
            );
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private static partial class NativeFile
    {
        private const int AtEmptyPath = 0x1000;
        private const uint StatxType = 0x0001;
        private const uint StatxMode = 0x0002;
        private const uint StatxUid = 0x0008;
        private const uint StatxTypeAndMode = StatxType | StatxMode | StatxUid;
        private const ushort FileTypeMask = 0xF000;
        private const ushort FileTypeRegular = 0x8000;
        private const ushort PermissionAndSpecialBitsMask = 0x0FFF;
        private const ushort OwnerReadWriteMode = 0x0180;

        public static int OpenExisting(string path, out int error)
        {
            while (true)
            {
                // O_NONBLOCK prevents a hostile FIFO from hanging before descriptor validation.
                var fd = open(
                    path,
                    UnixNative.OpenReadOnly
                        | UnixNative.OpenCloseOnExec
                        | UnixNative.OpenNoFollow
                        | UnixNative.OpenNonBlock
                );
                if (fd >= 0)
                {
                    error = 0;
                    return fd;
                }

                error = Marshal.GetLastPInvokeError();
                if (error != UnixNative.ErrorInterrupted)
                {
                    return -1;
                }
            }
        }

        public static bool TryGetStatus(int fd, out StatxBuffer stat, out int error)
        {
            while (true)
            {
                if (statx(fd, string.Empty, AtEmptyPath, StatxTypeAndMode, out stat) == 0)
                {
                    error = 0;
                    return true;
                }

                error = Marshal.GetLastPInvokeError();
                // ReSharper disable once InvertIf -- reads as retry on EINTR, fail otherwise
                if (error != UnixNative.ErrorInterrupted)
                {
                    stat = default;
                    return false;
                }
            }
        }

        public static bool HasTypeAndMode(StatxBuffer stat) => (stat.Mask & StatxTypeAndMode) == StatxTypeAndMode;

        public static bool IsRegular(ushort mode) => (mode & FileTypeMask) == FileTypeRegular;

        public static bool IsOwnedByEffectiveUser(StatxBuffer stat) => stat.UserId == geteuid();

        public static bool HasOwnerOnlyMode(ushort mode) =>
            (mode & PermissionAndSpecialBitsMask) == OwnerReadWriteMode;

        [StructLayout(LayoutKind.Sequential, Size = 256)]
        public struct StatxBuffer
        {
            public uint Mask;
            public uint BlockSize;
            public ulong Attributes;
            public uint LinkCount;
            public uint UserId;
            public uint GroupId;
            public ushort Mode;
        }

        // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int open(string path, int flags);

        // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc")]
        private static partial uint geteuid();

        // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int statx(
            int directoryFileDescriptor,
            string path,
            int flags,
            uint mask,
            out StatxBuffer buffer
        );
    }
}
