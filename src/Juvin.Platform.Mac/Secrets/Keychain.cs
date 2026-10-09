using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Juvin.Platform.Mac.Secrets;

/// <summary>Minimal Security.framework generic-password calls built on CoreFoundation dictionaries.</summary>
internal static partial class Keychain
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    // ReSharper disable once InconsistentNaming -- mirrors CoreFoundation's kCFStringEncodingUTF8
    private const uint CFStringEncodingUtf8 = 0x08000100;

    private static readonly Lazy<Symbols> s_symbols = new(Symbols.Load);

    public static byte[]? Find(string service, string account)
    {
        var symbols = s_symbols.Value;
        using var refs = new CFReferences();
        var query = refs.Dictionary(
            (symbols.Class, symbols.ClassGenericPassword),
            (symbols.AttrService, refs.String(service)),
            (symbols.AttrAccount, refs.String(account)),
            (symbols.ReturnData, symbols.BooleanTrue),
            (symbols.MatchLimit, symbols.MatchLimitOne)
        );

        var status = SecItemCopyMatching(query, out var result);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }

        ThrowIfFailed(status, "read");
        refs.Track(result);

        var length = (int)CFDataGetLength(result);
        var bytes = new byte[length];
        Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
        return bytes;
    }

    public static void Upsert(string service, string account, byte[] secret)
    {
        var symbols = s_symbols.Value;
        using var refs = new CFReferences();
        var serviceRef = refs.String(service);
        var accountRef = refs.String(account);
        var data = refs.Data(secret);

        var query = refs.Dictionary(
            (symbols.Class, symbols.ClassGenericPassword),
            (symbols.AttrService, serviceRef),
            (symbols.AttrAccount, accountRef)
        );
        var update = refs.Dictionary((symbols.ValueData, data));

        var status = SecItemUpdate(query, update);
        if (status == ErrSecItemNotFound)
        {
            var item = refs.Dictionary(
                (symbols.Class, symbols.ClassGenericPassword),
                (symbols.AttrService, serviceRef),
                (symbols.AttrAccount, accountRef),
                (symbols.ValueData, data)
            );
            status = SecItemAdd(item, IntPtr.Zero);
        }

        ThrowIfFailed(status, "write");
    }

    public static void Delete(string service, string account)
    {
        var symbols = s_symbols.Value;
        using var refs = new CFReferences();
        var query = refs.Dictionary(
            (symbols.Class, symbols.ClassGenericPassword),
            (symbols.AttrService, refs.String(service)),
            (symbols.AttrAccount, refs.String(account))
        );

        var status = SecItemDelete(query);
        if (status != ErrSecItemNotFound)
        {
            ThrowIfFailed(status, "delete");
        }
    }

    private static void ThrowIfFailed(int status, string operation)
    {
        if (status != ErrSecSuccess)
        {
            throw new CryptographicException($"Keychain {operation} failed with OSStatus {status}.");
        }
    }

    /// <summary>Owns every CoreFoundation object created for one call and releases them together.</summary>
    // ReSharper disable once InconsistentNaming -- CF is CoreFoundation's own prefix for the objects this owns
    private sealed class CFReferences : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public IntPtr String(string value)
        {
            return Track(CFStringCreateWithCString(IntPtr.Zero, value, CFStringEncodingUtf8));
        }

        public IntPtr Data(byte[] value)
        {
            return Track(CFDataCreate(IntPtr.Zero, value, value.Length));
        }

        public IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] pairs)
        {
            var symbols = s_symbols.Value;
            var keys = pairs.Select(p => p.Key).ToArray();
            var values = pairs.Select(p => p.Value).ToArray();
            return Track(
                CFDictionaryCreate(
                    IntPtr.Zero,
                    keys,
                    values,
                    pairs.Length,
                    symbols.DictionaryKeyCallBacks,
                    symbols.DictionaryValueCallBacks
                )
            );
        }

        public IntPtr Track(IntPtr reference)
        {
            if (reference == IntPtr.Zero)
            {
                throw new CryptographicException("A CoreFoundation object could not be created.");
            }

            _owned.Add(reference);
            return reference;
        }

        public void Dispose()
        {
            foreach (var reference in _owned)
            {
                CFRelease(reference);
            }

            _owned.Clear();
        }
    }

    /// <summary>
    ///     Security and CoreFoundation export their constants as global variables, not functions,
    ///     so they are read from the loaded framework images.
    /// </summary>
    private sealed record Symbols(
        IntPtr Class,
        IntPtr ClassGenericPassword,
        IntPtr AttrService,
        IntPtr AttrAccount,
        IntPtr ValueData,
        IntPtr ReturnData,
        IntPtr MatchLimit,
        IntPtr MatchLimitOne,
        IntPtr BooleanTrue,
        IntPtr DictionaryKeyCallBacks,
        IntPtr DictionaryValueCallBacks
    )
    {
        public static Symbols Load()
        {
            var security = NativeLibrary.Load(SecurityFramework);
            var coreFoundation = NativeLibrary.Load(CoreFoundationFramework);

            return new Symbols(
                ReadPointer(security, "kSecClass"),
                ReadPointer(security, "kSecClassGenericPassword"),
                ReadPointer(security, "kSecAttrService"),
                ReadPointer(security, "kSecAttrAccount"),
                ReadPointer(security, "kSecValueData"),
                ReadPointer(security, "kSecReturnData"),
                ReadPointer(security, "kSecMatchLimit"),
                ReadPointer(security, "kSecMatchLimitOne"),
                ReadPointer(coreFoundation, "kCFBooleanTrue"),
                NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks")
            );
        }

        private static IntPtr ReadPointer(IntPtr library, string name)
        {
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
        }
    }

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemDelete(IntPtr query);

    [LibraryImport(CoreFoundationFramework, StringMarshalling = StringMarshalling.Utf8)]
    // ReSharper disable once InconsistentNaming -- native CoreFoundation function name; LibraryImport EntryPoint defaults to the method name.
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [LibraryImport(CoreFoundationFramework)]
    // ReSharper disable once InconsistentNaming -- native CoreFoundation function name; LibraryImport EntryPoint defaults to the method name.
    private static partial IntPtr CFDataCreate(IntPtr allocator, [In] byte[] bytes, nint length);

    [LibraryImport(CoreFoundationFramework)]
    // ReSharper disable once InconsistentNaming -- native CoreFoundation function name; LibraryImport EntryPoint defaults to the method name.
    private static partial IntPtr CFDictionaryCreate(
        IntPtr allocator,
        [In] IntPtr[] keys,
        [In] IntPtr[] values,
        nint count,
        IntPtr keyCallBacks,
        IntPtr valueCallBacks
    );

    [LibraryImport(CoreFoundationFramework)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(CoreFoundationFramework)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(CoreFoundationFramework)]
    private static partial void CFRelease(IntPtr reference);
}
