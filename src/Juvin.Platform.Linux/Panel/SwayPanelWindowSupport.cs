using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Juvin.Core.Providers;
using Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

namespace Juvin.Platform.Linux.Panel;

/// <summary>
///     Sway centres every floating X11 window it maps and ignores the X11 sticky state. After the
///     inner hints, finds the panel in <c>swaymsg -t get_tree</c> by class and title, makes it
///     sticky, and moves it to the top right of its workspace. Runs on every show, because Sway
///     centres the panel again each time it is mapped. Elsewhere it is the inner support alone.
/// </summary>
public sealed class SwayPanelWindowSupport : IPanelWindowSupport
{
    private const string WindowClass = "juvin";
    private const int Attempts = 3;

    // The panel's own margin from the screen edges, in logical pixels like Sway's layout.
    private const int ScreenMargin = 16;

    private readonly TimeSpan _retryDelay;
    private readonly IPanelWindowSupport _inner;
    private readonly IProcessRunner _runner;
    private readonly DesktopSession _session;

    public SwayPanelWindowSupport(IPanelWindowSupport inner, IProcessRunner runner)
        : this(inner, runner, DesktopSession.FromEnvironment(), TimeSpan.FromMilliseconds(200))
    {
    }

    internal SwayPanelWindowSupport(IPanelWindowSupport inner, IProcessRunner runner, DesktopSession session, TimeSpan retryDelay)
    {
        _inner = inner;
        _runner = runner;
        _session = session;
        _retryDelay = retryDelay;
    }

    public void ShowOnAllWorkspaces(nint windowHandle, string title)
    {
        _inner.ShowOnAllWorkspaces(windowHandle, title);
        if (_session.DesktopId == "sway" && _session.HasExecutable("swaymsg"))
        {
            _ = PlaceAsync(title);
        }
    }

    /// <summary>Waits briefly for Sway to list a window it has only just mapped.</summary>
    internal async Task PlaceAsync(string title)
    {
        try
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var tree = await DesktopSession.RunAsync(_runner, "swaymsg", ["-t", "get_tree"], CancellationToken.None).ConfigureAwait(false);
                // ReSharper disable once InvertIf -- a found panel stays the positive case; the retry delay ends the loop body
                if (FindPanel(tree.StandardOutput, title) is { } panel)
                {
                    var command = string.Create(CultureInfo.InvariantCulture, $"[con_id={panel.Id}] sticky enable, move absolute position {panel.X} {panel.Y}");
                    var result = await DesktopSession.RunAsync(_runner, "swaymsg", [command], CancellationToken.None).ConfigureAwait(false);
                    Trace.WriteLine(
                        result.ExitCode == 0
                            ? "[SwayPanelWindowSupport] Placed the panel."
                            : $"[SwayPanelWindowSupport] Sway refused to place the panel (exit {result.ExitCode})."
                    );
                    return;
                }

                await Task.Delay(_retryDelay).ConfigureAwait(false);
            }

            Trace.WriteLine("[SwayPanelWindowSupport] Sway did not list the panel.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException or System.ComponentModel.Win32Exception)
        {
            Trace.WriteLine($"[SwayPanelWindowSupport] Could not place the panel ({ex.GetType().Name}).");
        }
    }

    /// <summary>The panel's container id and the top-right position within its workspace, which excludes the bar.</summary>
    internal static (long Id, int X, int Y)? FindPanel(string treeJson, string title)
    {
        using var document = JsonDocument.Parse(treeJson);
        return Find(document.RootElement, null, title);
    }

    private static (long Id, int X, int Y)? Find(JsonElement node, JsonElement? workspace, string title)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (node.TryGetProperty("type", out var type) && type.GetString() == "workspace")
        {
            workspace = node;
        }

        if (
            workspace is { } ws
            && node.TryGetProperty("name", out var name)
            && name.GetString() == title
            && node.TryGetProperty("window_properties", out var properties)
            && properties.TryGetProperty("class", out var cls)
            && cls.GetString() == WindowClass
        )
        {
            var area = ws.GetProperty("rect");
            var width = node.GetProperty("rect").GetProperty("width").GetInt32();
            return (
                node.GetProperty("id").GetInt64(),
                area.GetProperty("x").GetInt32() + area.GetProperty("width").GetInt32() - width - ScreenMargin,
                area.GetProperty("y").GetInt32() + ScreenMargin
            );
        }

        foreach (var key in (ReadOnlySpan<string>)["nodes", "floating_nodes"])
        {
            if (!node.TryGetProperty(key, out var children) || children.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var child in children.EnumerateArray())
            {
                if (Find(child, workspace, title) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }
}
