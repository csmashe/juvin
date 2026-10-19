using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Juvin.Platform.Mac.Panel;

/// <summary>
///     Lets the panel's NSWindow show on whichever Space is active, including another app's full-screen
///     Space, instead of switching the user back to the Space it was first shown on. Avalonia already
///     keeps it at the floating level through Topmost. Unverified on hardware.
/// </summary>
/// <remarks>
///     AppKit throws an Objective-C exception, which .NET cannot catch, for an unknown selector or a
///     conflicting behavior, so the handle is checked to be an NSWindow and every flag that conflicts
///     with the ones set here is cleared first.
/// </remarks>
public sealed partial class MacPanelWindowSupport : IPanelWindowSupport
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

    public void PrepareWindow(nint windowHandle, string? handleDescriptor)
    {
        Apply(windowHandle, handleDescriptor);
    }

    // Avalonia may reset the behavior while the window is hidden, so it is set again after every show.
    public void ShowOnAllWorkspaces(nint windowHandle, string? handleDescriptor, string title)
    {
        Apply(windowHandle, handleDescriptor);
    }

    internal static nuint PanelBehavior(nuint current, bool stageManagerRoles)
    {
        var cleared = current & ~CollectionBehavior.Conflicting;
        const nuint wanted = CollectionBehavior.CanJoinAllSpaces | CollectionBehavior.FullScreenAuxiliary;
        return cleared | wanted | (stageManagerRoles ? CollectionBehavior.CanJoinAllApplications : 0);
    }

    private static void Apply(nint handle, string? descriptor)
    {
        if (handle == 0 || descriptor is not ("NSWindow" or "NSView"))
        {
            return;
        }

        try
        {
            var window = descriptor == "NSView" ? SendPointer(handle, Selector("window")) : handle;
            var windowClass = GetClass("NSWindow");
            if (window == 0 || windowClass == 0 || !SendBool(window, Selector("isKindOfClass:"), windowClass))
            {
                Trace.WriteLine($"[MacPanelWindowSupport] The {descriptor} handle is not an NSWindow.");
                return;
            }

            var current = SendNUInt(window, Selector("collectionBehavior"));
            var next = PanelBehavior(current, OperatingSystem.IsMacOSVersionAtLeast(13));
            if (next != current)
            {
                SendSetNUInt(window, Selector("setCollectionBehavior:"), next);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"[MacPanelWindowSupport] Could not show the panel on every Space ({ex.GetType().Name}).");
        }
    }

    private static nint Selector(string name) => RegisterSelector(name);

    [LibraryImport(ObjCLibrary, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint RegisterSelector(string name);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendPointer(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SendBool(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nuint SendNUInt(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendSetNUInt(nint receiver, nint selector, nuint value);

    /// <summary>NSWindowCollectionBehavior bits.</summary>
    internal static class CollectionBehavior
    {
        public const nuint CanJoinAllSpaces = 1 << 0;
        public const nuint MoveToActiveSpace = 1 << 1;
        public const nuint Managed = 1 << 2;
        public const nuint FullScreenPrimary = 1 << 7;
        public const nuint FullScreenAuxiliary = 1 << 8;
        // ReSharper disable once MemberCanBePrivate.Global -- the table lists AppKit's bits uniformly
        public const nuint FullScreenNone = 1 << 9;
        public const nuint FullScreenAllowsTiling = 1 << 11;
        // ReSharper disable once MemberCanBePrivate.Global -- the table lists AppKit's bits uniformly
        public const nuint FullScreenDisallowsTiling = 1 << 12;
        public const nuint Primary = 1 << 16;
        // ReSharper disable once MemberCanBePrivate.Global -- the table lists AppKit's bits uniformly
        public const nuint Auxiliary = 1 << 17;
        public const nuint CanJoinAllApplications = 1 << 18;

        // AppKit allows one Space placement, one full-screen role, and one Stage Manager role. Tiling is for
        // full-screen primary windows only, and a managed window would be listed in Mission Control.
        public const nuint Conflicting =
            MoveToActiveSpace | Managed
            | FullScreenPrimary | FullScreenNone | FullScreenAllowsTiling | FullScreenDisallowsTiling
            | Primary | Auxiliary;
    }
}
