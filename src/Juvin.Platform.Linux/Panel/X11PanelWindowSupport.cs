using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Juvin.Platform.Linux.Panel;

/// <summary>
///     Sets <c>_NET_WM_STATE_STICKY</c> on the panel's X11 window, which Avalonia uses under
///     XWayland too, so the window manager shows it on every workspace. Does nothing when no X
///     display opens and traces any failure; the panel still works on a single workspace.
/// </summary>
public sealed partial class X11PanelWindowSupport : IPanelWindowSupport
{
    private const string Lib = "libX11.so.6";
    private const int ClientMessage = 33;
    private const int Format32 = 32;
    private const long NetWmStateAdd = 1;
    private const long SourceApplication = 1;

    // EWMH requires both masks on the root window for a _NET_WM_STATE request.
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;

    // XEvent is a union of at most 192 bytes on LP64; the offsets are XClientMessageEvent's.
    private const int XEventSize = 192;
    private const int OffType = 0;
    private const int OffDisplay = 24;
    private const int OffWindow = 32;
    private const int OffMessageType = 40;
    private const int OffFormat = 48;
    private const int OffData = 56;

    public void ShowOnAllWorkspaces(nint windowHandle, string title)
    {
        if (windowHandle == 0)
        {
            return;
        }

        try
        {
            SendSticky(windowHandle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            Trace.WriteLine($"[X11PanelWindowSupport] Could not make the panel sticky ({ex.GetType().Name}).");
        }
    }

    private static void SendSticky(nint window)
    {
        var display = XOpenDisplay(null);
        if (display == 0)
        {
            return;
        }

        nint ev = 0;
        try
        {
            var root = XRootWindow(display, XDefaultScreen(display));
            var wmState = XInternAtom(display, "_NET_WM_STATE", false);
            var sticky = XInternAtom(display, "_NET_WM_STATE_STICKY", false);

            ev = Marshal.AllocHGlobal(XEventSize);
            for (var i = 0; i < XEventSize; i += 8)
            {
                Marshal.WriteInt64(ev, i, 0);
            }

            Marshal.WriteInt32(ev, OffType, ClientMessage);
            Marshal.WriteIntPtr(ev, OffDisplay, display);
            Marshal.WriteInt64(ev, OffWindow, window);
            Marshal.WriteInt64(ev, OffMessageType, (long)wmState);
            Marshal.WriteInt32(ev, OffFormat, Format32);

            // data.l: action, first property, second property (none), source indication.
            Marshal.WriteInt64(ev, OffData, NetWmStateAdd);
            Marshal.WriteInt64(ev, OffData + 8, (long)sticky);
            Marshal.WriteInt64(ev, OffData + 24, SourceApplication);

            _ = XSendEvent(display, root, false, (nint)(SubstructureNotifyMask | SubstructureRedirectMask), ev);
            _ = XFlush(display);
        }
        finally
        {
            if (ev != 0)
            {
                Marshal.FreeHGlobal(ev);
            }

            _ = XCloseDisplay(display);
        }
    }

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XOpenDisplay(string? name);

    [LibraryImport(Lib)]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport(Lib)]
    private static partial int XDefaultScreen(nint display);

    [LibraryImport(Lib)]
    private static partial nuint XRootWindow(nint display, int screen);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [LibraryImport(Lib)]
    private static partial int XSendEvent(
        nint display,
        nuint window,
        [MarshalAs(UnmanagedType.Bool)] bool propagate,
        nint eventMask,
        nint eventSend
    );

    [LibraryImport(Lib)]
    private static partial int XFlush(nint display);
}
