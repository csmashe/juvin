using System.Text;

namespace Juvin.Core.Services;

/// <summary>
///     Whole-file writes that never leave a half-written target: the content is written and flushed
///     to a temporary file beside the target, then moved over it. The directory is not flushed, which
///     .NET cannot do, so on Linux a power cut right after the move can still lose the new content.
/// </summary>
public static class AtomicFile
{
    public const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents)
    {
        WriteAllBytes(path, s_utf8NoBom.GetBytes(contents));
    }

    /// <summary>Writes <paramref name="contents" />, replacing any existing file; on Unix the file gets exactly <paramref name="unixMode" />, whatever the umask.</summary>
    public static void WriteAllText(string path, string contents, UnixFileMode unixMode)
    {
        var tempPath = WriteTemp(path, s_utf8NoBom.GetBytes(contents), unixMode);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(tempPath, unixMode);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        Publish(tempPath, path, overwrite: true);
    }

    /// <summary>Writes <paramref name="bytes" />, replacing any existing file. New files are 0600 on Unix.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var tempPath = WriteTemp(path, bytes, OwnerReadWrite);
        Publish(tempPath, path, overwrite: true);
    }

    /// <summary>
    ///     Writes <paramref name="bytes" /> only if <paramref name="path" /> does not exist yet;
    ///     throws <see cref="IOException" /> otherwise. The file appears complete or not at all.
    /// </summary>
    public static void WriteAllBytesCreateNew(
        string path,
        ReadOnlySpan<byte> bytes,
        UnixFileMode unixMode = OwnerReadWrite
    )
    {
        var tempPath = WriteTemp(path, bytes, unixMode);
        Publish(tempPath, path, overwrite: false);
    }

    /// <summary>Matches the staging files of <paramref name="fileName" />, which an interrupted write can leave behind.</summary>
    public static string TempFilePattern(string fileName) => "." + fileName + ".*.tmp";

    /// <summary>
    ///     Whether <paramref name="candidate" /> is a staging file of exactly <paramref name="fileName" />: the wildcard in
    ///     <see cref="TempFilePattern" /> would also match the staging files of <c>fileName.bak</c>.
    /// </summary>
    public static bool IsTempFileOf(string fileName, string candidate)
    {
        var prefix = "." + fileName + ".";
        const string suffix = ".tmp";
        var name = Path.GetFileName(candidate);
        var tokenLength = name.Length - prefix.Length - suffix.Length;
        return tokenLength == 32
            && name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(suffix, StringComparison.Ordinal)
            && name.AsSpan(prefix.Length, tokenLength).ToArray().All(char.IsAsciiHexDigitLower);
    }

    private static string WriteTemp(string path, ReadOnlySpan<byte> bytes, UnixFileMode unixMode)
    {
        var fullPath = Path.GetFullPath(path);
        var tempPath = Path.Join(
            Path.GetDirectoryName(fullPath),
            "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp"
        );

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = unixMode;
        }

        try
        {
            using var stream = new FileStream(tempPath, options);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return tempPath;
    }

    private static void Publish(string tempPath, string path, bool overwrite)
    {
        try
        {
            // Without overwrite .NET links rather than renames on Unix, so an existing target is
            // never replaced.
            File.Move(tempPath, path, overwrite);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless; the original failure is what matters.
        }
    }
}
