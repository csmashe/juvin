using System.Runtime.InteropServices;

namespace Juvin.Platform.Mac.Setup;

/// <summary>The Accessibility trust calls, behind a seam so the setup task is testable off macOS.</summary>
internal interface IAccessibilityTrust
{
    bool IsTrusted();

    /// <summary>Asks macOS to show its Accessibility prompt for this process.</summary>
    void Prompt();
}

/// <summary>ApplicationServices trust checks. Unverified on hardware.</summary>
internal sealed partial class AccessibilityTrust : IAccessibilityTrust
{
    private const string ApplicationServicesFramework =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public bool IsTrusted()
    {
        return AXIsProcessTrusted();
    }

    public void Prompt()
    {
        var applicationServices = NativeLibrary.Load(ApplicationServicesFramework);
        var coreFoundation = NativeLibrary.Load(CoreFoundationFramework);

        // The option key and boolean are framework globals, so the export holds a pointer to the object.
        IntPtr[] keys = [Marshal.ReadIntPtr(NativeLibrary.GetExport(applicationServices, "kAXTrustedCheckOptionPrompt"))];
        IntPtr[] values = [Marshal.ReadIntPtr(NativeLibrary.GetExport(coreFoundation, "kCFBooleanTrue"))];
        var options = CFDictionaryCreate(
            IntPtr.Zero,
            keys,
            values,
            1,
            NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks")
        );
        if (options == IntPtr.Zero)
        {
            throw new InvalidOperationException("The Accessibility prompt options could not be created.");
        }

        try
        {
            _ = AXIsProcessTrustedWithOptions(options);
        }
        finally
        {
            CFRelease(options);
        }
    }

    [LibraryImport(ApplicationServicesFramework)]
    [return: MarshalAs(UnmanagedType.U1)]
    // ReSharper disable once InconsistentNaming -- the native ApplicationServices function name
    private static partial bool AXIsProcessTrusted();

    [LibraryImport(ApplicationServicesFramework)]
    [return: MarshalAs(UnmanagedType.U1)]
    // ReSharper disable once InconsistentNaming -- the native ApplicationServices function name
    private static partial bool AXIsProcessTrustedWithOptions(IntPtr options);

    [LibraryImport(CoreFoundationFramework)]
    // ReSharper disable once InconsistentNaming -- the native CoreFoundation function name
    private static partial IntPtr CFDictionaryCreate(
        IntPtr allocator,
        [In] IntPtr[] keys,
        [In] IntPtr[] values,
        nint count,
        IntPtr keyCallBacks,
        IntPtr valueCallBacks
    );

    [LibraryImport(CoreFoundationFramework)]
    private static partial void CFRelease(IntPtr reference);
}
