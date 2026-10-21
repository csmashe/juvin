using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Juvin.Platform.Mac.Panel;

/// <summary>
///     Lets the panel's NSWindow show on whichever Space is active, including another app's full-screen
///     Space, and gives focus back to the app the user was in when the panel hides. Avalonia already keeps
///     it at the floating level through Topmost.
/// </summary>
/// <remarks>
///     A regular (Dock) app that activates leaves another app's full-screen Space for its own, so Juvin is
///     an accessory app while the panel shows. The panel's collection behavior is set only after that
///     switch and cleared before the switch back: on macOS 26 a window that had it while the app was
///     regular never joins a full-screen Space (checked with <c>scripts/mac/panel-harness.swift</c>).
///     AppKit throws an Objective-C exception, which .NET cannot catch, for an unknown selector or a
///     conflicting behavior, so the handle is checked to be an NSWindow and every conflicting flag is
///     cleared first.
/// </remarks>
public sealed partial class MacPanelWindowSupport : IPanelWindowSupport
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const nint RegularPolicy = 0;
    private const nint AccessoryPolicy = 1;
    // Ignored from macOS 14, where an active app may still hand activation to another.
    private const nuint ActivateIgnoringOtherApps = 1 << 1;

    // Touched on the UI thread only.
    private nint _window;
    private int? _returnTo;
    private bool _madeAccessory;

    public void PrepareWindow(nint windowHandle, string? handleDescriptor)
    {
        _window = WindowOf(windowHandle, handleDescriptor);
    }

    // Avalonia may reset the behavior while the window is shown, so it is set again on the turn after.
    public void ShowOnAllWorkspaces(nint windowHandle, string? handleDescriptor, string title)
    {
        _window = WindowOf(windowHandle, handleDescriptor);
        if (_madeAccessory)
        {
            SetPanelBehavior(_window, on: true);
        }
    }

    public void BeforeShow() => TryNative("prepare the panel to show", () =>
    {
        var workspace = SendPointer(GetClass("NSWorkspace"), Selector("sharedWorkspace"));
        var frontmost = workspace == 0 ? 0 : SendPointer(workspace, Selector("frontmostApplication"));
        var frontmostPid = frontmost == 0 ? 0 : SendInt(frontmost, Selector("processIdentifier"));
        _returnTo = AppToReturnTo(_returnTo, frontmostPid, Environment.ProcessId);

        var app = SharedApplication();
        if (!_madeAccessory && app != 0 && SendPointer(app, Selector("activationPolicy")) == RegularPolicy)
        {
            _madeAccessory = SendBool(app, Selector("setActivationPolicy:"), AccessoryPolicy);
        }

        if (_madeAccessory)
        {
            SetPanelBehavior(_window, on: true);
        }
    });

    /// <remarks>
    ///     The other app is activated before the panel hides: hiding the key window of an active app makes
    ///     AppKit bring another of its windows, the main window, forward instead.
    /// </remarks>
    public void Hide(Action hideWindow)
    {
        var returnTo = _returnTo;
        _returnTo = null;
        if (returnTo is { } pid)
        {
            TryNative("give focus back to the previous app", () =>
            {
                if (!SendBool(SharedApplication(), Selector("isActive")))
                {
                    return;
                }

                var target = SendPointerInt(GetClass("NSRunningApplication"), Selector("runningApplicationWithProcessIdentifier:"), pid);
                if (target == 0 || !SendBoolNUInt(target, Selector("activateWithOptions:"), ActivateIgnoringOtherApps))
                {
                    Trace.WriteLine("[MacPanelWindowSupport] The previous app did not take focus back.");
                }
            });
        }

        hideWindow();

        if (!_madeAccessory)
        {
            return;
        }

        SetPanelBehavior(_window, on: false);
        _madeAccessory = false;
        TryNative("restore the Dock icon", () => SendBool(SharedApplication(), Selector("setActivationPolicy:"), RegularPolicy));
    }

    /// <summary>
    ///     The app to give focus back to: the frontmost one, unless that is Juvin itself, as when a capture
    ///     arrives while the panel already has focus, or there is none.
    /// </summary>
    internal static int? AppToReturnTo(int? remembered, int frontmostPid, int ownPid) =>
        frontmostPid > 0 && frontmostPid != ownPid ? frontmostPid : remembered;

    internal static nuint PanelBehavior(nuint current, bool stageManagerRoles)
    {
        var cleared = current & ~CollectionBehavior.Conflicting;
        const nuint wanted = CollectionBehavior.CanJoinAllSpaces | CollectionBehavior.FullScreenAuxiliary;
        return cleared | wanted | (stageManagerRoles ? CollectionBehavior.CanJoinAllApplications : 0);
    }

    internal static nuint WithoutPanelBehavior(nuint current) =>
        current & ~(CollectionBehavior.CanJoinAllSpaces | CollectionBehavior.FullScreenAuxiliary | CollectionBehavior.CanJoinAllApplications);

    /// <returns>The NSWindow, or 0 when the handle is not one.</returns>
    private static nint WindowOf(nint handle, string? descriptor)
    {
        if (handle == 0 || descriptor is not ("NSWindow" or "NSView"))
        {
            return 0;
        }

        try
        {
            var window = descriptor == "NSView" ? SendPointer(handle, Selector("window")) : handle;
            var windowClass = GetClass("NSWindow");
            if (window != 0 && windowClass != 0 && SendBool(window, Selector("isKindOfClass:"), windowClass))
            {
                return window;
            }

            Trace.WriteLine($"[MacPanelWindowSupport] The {descriptor} handle is not an NSWindow.");
            return 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"[MacPanelWindowSupport] Could not read the panel window ({ex.GetType().Name}).");
            return 0;
        }
    }

    private static void SetPanelBehavior(nint window, bool on)
    {
        if (window == 0)
        {
            return;
        }

        TryNative(on ? "show the panel on every Space" : "clear the panel's Space behavior", () =>
        {
            var current = SendNUInt(window, Selector("collectionBehavior"));
            var next = on ? PanelBehavior(current, OperatingSystem.IsMacOSVersionAtLeast(13)) : WithoutPanelBehavior(current);
            if (next != current)
            {
                SendSetNUInt(window, Selector("setCollectionBehavior:"), next);
            }
        });
    }

    private static void TryNative(string what, Action call)
    {
        try
        {
            call();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"[MacPanelWindowSupport] Could not {what} ({ex.GetType().Name}).");
        }
    }

    private static nint SharedApplication() => SendPointer(GetClass("NSApplication"), Selector("sharedApplication"));

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
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SendBool(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    // ReSharper disable once InconsistentNaming -- spelled NUInt like SendNUInt and SendSetNUInt beside it
    private static partial bool SendBoolNUInt(nint receiver, nint selector, nuint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial int SendInt(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendPointerInt(nint receiver, nint selector, int argument);

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
