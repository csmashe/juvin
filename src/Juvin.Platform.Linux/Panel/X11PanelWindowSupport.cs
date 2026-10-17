using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Juvin.Platform.Linux.Panel;

/// <summary>
///     Asks the window manager to put the panel's X11 window, which Avalonia uses under XWayland
///     too, on every desktop (<c>_NET_WM_DESKTOP</c> 0xFFFFFFFF) and to keep it fixed when a large
///     desktop's viewport scrolls (<c>_NET_WM_STATE_STICKY</c>). Does nothing when the handle is not
///     an X window or no X display opens, and traces any failure; the panel still works on a single
///     workspace.
/// </summary>
public sealed partial class X11PanelWindowSupport : IPanelWindowSupport
{
    private const string Lib = "libX11.so.6";
    private const int ClientMessage = 33;
    private const int Format32 = 32;
    private const long NetWmStateAdd = 1;
    private const long SourceApplication = 1;
    private const long AllDesktops = 0xFFFFFFFF;

    // EWMH requires both masks on the root window for _NET_WM_STATE and _NET_WM_DESKTOP requests.
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

    public void ShowOnAllWorkspaces(nint windowHandle, string? handleDescriptor, string title)
    {
        if (windowHandle == 0 || handleDescriptor != "XID")
        {
            return;
        }

        try
        {
            SendHints(windowHandle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            Trace.WriteLine($"[X11PanelWindowSupport] Could not show the panel on every workspace ({ex.GetType().Name}).");
        }
    }

    private static void SendHints(nint window)
    {
        var display = XOpenDisplay(null);
        if (display == 0)
        {
            return;
        }

        try
        {
            var root = XRootWindow(display, XDefaultScreen(display));
            var sticky = (long)XInternAtom(display, "_NET_WM_STATE_STICKY", false);
            // data.l: action, first property, second property (none), source indication.
            SendClientMessage(display, root, window, "_NET_WM_STATE", [NetWmStateAdd, sticky, 0, SourceApplication]);
            // data.l: desktop, source indication.
            SendClientMessage(display, root, window, "_NET_WM_DESKTOP", [AllDesktops, SourceApplication]);
            _ = XFlush(display);
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    private static void SendClientMessage(nint display, nuint root, nint window, string messageType, long[] data)
    {
        var ev = Marshal.AllocHGlobal(XEventSize);
        try
        {
            for (var i = 0; i < XEventSize; i += 8)
            {
                Marshal.WriteInt64(ev, i, 0);
            }

            Marshal.WriteInt32(ev, OffType, ClientMessage);
            Marshal.WriteIntPtr(ev, OffDisplay, display);
            Marshal.WriteInt64(ev, OffWindow, window);
            Marshal.WriteInt64(ev, OffMessageType, (long)XInternAtom(display, messageType, false));
            Marshal.WriteInt32(ev, OffFormat, Format32);
            for (var i = 0; i < data.Length; i++)
            {
                Marshal.WriteInt64(ev, OffData + i * 8, data[i]);
            }

            _ = XSendEvent(display, root, false, (nint)(SubstructureNotifyMask | SubstructureRedirectMask), ev);
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
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
