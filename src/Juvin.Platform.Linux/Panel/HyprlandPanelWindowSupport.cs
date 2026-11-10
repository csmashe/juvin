using System.Diagnostics;
using System.Text.Json;
using Juvin.Core.Providers;
using Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

namespace Juvin.Platform.Linux.Panel;

/// <summary>
///     On Hyprland the X11 sticky state does nothing; a floating window follows workspaces only when
///     pinned. After the X11 hints, finds the panel in <c>hyprctl clients</c> by class and title, pins
///     it once, and gives it the X input focus when Hyprland has focused it. Elsewhere it is the X11
///     support alone.
/// </summary>
/// <remarks>
///     Hyprland's XWayland window manager keeps the panel as its focused X window across an unmap, so on
///     the next show it skips the X focus request as already done; the X server moved the focus away on
///     the unmap, and the keys Hyprland routes to the panel never reach it.
/// </remarks>
public sealed class HyprlandPanelWindowSupport : IPanelWindowSupport
{
    private const string WindowClass = "juvin";
    private const int Attempts = 3;

    private readonly TimeSpan _retryDelay;
    private readonly Action<nint> _focusXWindow;
    private readonly IPanelWindowSupport _inner;
    private readonly IProcessRunner _runner;
    private readonly DesktopSession _session;
    private int _pinning;

    public HyprlandPanelWindowSupport(IPanelWindowSupport inner, IProcessRunner runner)
        : this(inner, runner, DesktopSession.FromEnvironment(), TimeSpan.FromMilliseconds(200), X11PanelWindowSupport.FocusWindow)
    {
    }

    internal HyprlandPanelWindowSupport(
        IPanelWindowSupport inner,
        IProcessRunner runner,
        DesktopSession session,
        TimeSpan retryDelay,
        Action<nint> focusXWindow
    )
    {
        _inner = inner;
        _runner = runner;
        _session = session;
        _retryDelay = retryDelay;
        _focusXWindow = focusXWindow;
    }

    public void PrepareWindow(nint windowHandle, string? handleDescriptor) => _inner.PrepareWindow(windowHandle, handleDescriptor);

    public void BeforeShow() => _inner.BeforeShow();

    public void Hide(Action hideWindow) => _inner.Hide(hideWindow);

    public void ShowOnAllWorkspaces(nint windowHandle, string? handleDescriptor, string title)
    {
        _inner.ShowOnAllWorkspaces(windowHandle, handleDescriptor, title);
        if (_session.DesktopId == "hyprland" && _session.HasExecutable("hyprctl"))
        {
            _ = PinAsync(title, handleDescriptor == "XID" ? windowHandle : 0);
        }
    }

    /// <summary>
    ///     Pins the panel and focuses <paramref name="xWindow" /> (none when 0); waits briefly for Hyprland
    ///     to list a window it has only just mapped. One at a time: the dispatcher toggles, so two
    ///     overlapping checks could pin and then unpin.
    /// </summary>
    internal async Task PinAsync(string title, nint xWindow)
    {
        if (Interlocked.Exchange(ref _pinning, 1) == 1)
        {
            return;
        }

        try
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var clients = await DesktopSession.RunAsync(_runner, "hyprctl", ["-j", "clients"], CancellationToken.None).ConfigureAwait(false);
                var panel = FindPanel(clients.StandardOutput, title);
                // ReSharper disable once InvertIf -- a found panel stays the positive case; the retry delay ends the loop body
                if (panel is { } found)
                {
                    if (found.Focused && xWindow != 0)
                    {
                        _focusXWindow(xWindow);
                    }

                    if (!found.Pinned)
                    {
                        await DesktopSession.RunAsync(_runner, "hyprctl", ["dispatch", "pin", $"address:{found.Address}"], CancellationToken.None).ConfigureAwait(false);
                        Trace.WriteLine("[HyprlandPanelWindowSupport] Pinned the panel.");
                    }

                    return;
                }

                await Task.Delay(_retryDelay).ConfigureAwait(false);
            }

            Trace.WriteLine("[HyprlandPanelWindowSupport] Hyprland did not list the panel.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            Trace.WriteLine($"[HyprlandPanelWindowSupport] Could not pin the panel ({ex.GetType().Name}).");
        }
        finally
        {
            Volatile.Write(ref _pinning, 0);
        }
    }

    /// <remarks>Focused: Hyprland numbers its focus history newest first, so 0 is the focused window.</remarks>
    internal static (string Address, bool Pinned, bool Focused)? FindPanel(string clientsJson, string title)
    {
        using var document = JsonDocument.Parse(clientsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var client in document.RootElement.EnumerateArray())
        {
            // ReSharper disable once InvertIf -- the match conditions read better as one positive test than as a negated continue
            if (
                client.TryGetProperty("class", out var cls)
                && cls.GetString() == WindowClass
                && client.TryGetProperty("title", out var t)
                && t.GetString() == title
                && client.TryGetProperty("address", out var address)
                && address.GetString() is { } found
            )
            {
                var pinned = client.TryGetProperty("pinned", out var p) && p.ValueKind == JsonValueKind.True;
                var focused = client.TryGetProperty("focusHistoryID", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out var id) && id == 0;
                return (found, pinned, focused);
            }
        }

        return null;
    }
}
