using System.Runtime.Versioning;
using Juvin.Core.Providers;
using Juvin.Platform.Linux.Panel;
using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class HyprlandPanelWindowSupportTests : IDisposable
{
    private const string Title = "Juvin rewrite";

    private const string Clients =
        """
        [{"address":"0x1","class":"juvin","title":"Juvin","floating":false,"pinned":false,"focusHistoryID":1},
         {"address":"0x2","class":"juvin","title":"Juvin rewrite","floating":true,"pinned":PINNED,"focusHistoryID":FOCUS}]
        """;

    private const nint XWindow = 0x2c00007;

    private readonly List<nint> _focused = [];
    private readonly RecordingSupport _inner = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly DesktopShortcutTestSession _session = new();

    public void Dispose() => _session.Dispose();

    [LinuxFact]
    public async Task PinsTheUnpinnedPanelOnce_ByClassAndTitle()
    {
        _runner.Handler = (_, args) => args[0] == "-j" ? Ok(Panel(pinned: false)) : Ok("ok");

        await Support().PinAsync(Title, XWindow);

        Assert.Equal(["hyprctl", "hyprctl"], _runner.Calls.Select(call => call.FileName));
        Assert.Equal(["dispatch", "pin", "address:0x2"], _runner.Calls[1].Arguments);
    }

    [LinuxFact]
    public async Task OverlappingShowsRunOnePinCheck()
    {
        var gate = new TaskCompletionSource();
        _runner.Handler = (_, args) =>
        {
            if (args[0] == "-j")
            {
                gate.Task.Wait(TimeSpan.FromSeconds(5));
            }

            return Ok(args[0] == "-j" ? Panel(pinned: false) : "ok");
        };
        var support = Support();

        var first = Task.Run(() => support.PinAsync(Title, XWindow));
        await Task.Delay(50);
        await support.PinAsync(Title, XWindow);
        gate.SetResult();
        await first;

        Assert.Single(_runner.Calls, call => call.Arguments[0] == "dispatch");
    }

    [LinuxFact]
    public async Task LeavesAPinnedPanelAlone()
    {
        _runner.Handler = (_, _) => Ok(Panel(pinned: true));

        await Support().PinAsync(Title, XWindow);

        Assert.Single(_runner.Calls);
    }

    [LinuxFact]
    public async Task RetriesWhileHyprlandHasNotListedThePanel_ThenGivesUp()
    {
        _runner.Handler = (_, _) => Ok("[]");

        await Support().PinAsync(Title, XWindow);

        Assert.Equal(3, _runner.Calls.Count);
        Assert.All(_runner.Calls, call => Assert.Equal("-j", call.Arguments[0]));
    }

    [LinuxFact]
    public void OnlyTheX11HintsRunOutsideHyprland()
    {
        _session.With("XDG_CURRENT_DESKTOP", "GNOME");
        var support = new HyprlandPanelWindowSupport(_inner, _runner, _session.Session, TimeSpan.Zero, _focused.Add);

        support.ShowOnAllWorkspaces(42, "XID", Title);

        Assert.Equal([(42, "XID", Title)], _inner.Calls);
        Assert.Empty(_runner.Calls);
        Assert.Empty(_focused);
    }

    [LinuxFact]
    public async Task FocusesThePanelsXWindow_WhenHyprlandHasFocusedIt()
    {
        _runner.Handler = (_, _) => Ok(Panel(pinned: true, focus: "0"));

        await Support().PinAsync(Title, XWindow);

        Assert.Equal([XWindow], _focused);
    }

    [LinuxFact]
    public async Task LeavesTheXFocusAlone_WhenHyprlandFocusedAnotherWindow()
    {
        _runner.Handler = (_, _) => Ok(Panel(pinned: true, focus: "1"));

        await Support().PinAsync(Title, XWindow);

        Assert.Empty(_focused);
    }

    [LinuxFact]
    public async Task LeavesTheXFocusAlone_WithoutAnXWindow()
    {
        _runner.Handler = (_, _) => Ok(Panel(pinned: true, focus: "0"));

        await Support().PinAsync(Title, 0);

        Assert.Empty(_focused);
    }

    [LinuxFact]
    public void FindPanel_IgnoresOtherWindowsAndBadJson()
    {
        Assert.Null(HyprlandPanelWindowSupport.FindPanel("{}", Title));
        Assert.Null(HyprlandPanelWindowSupport.FindPanel(Panel(pinned: false), "Set up Juvin"));
        Assert.Equal(("0x2", true, false), HyprlandPanelWindowSupport.FindPanel(Panel(pinned: true), Title));
        Assert.Equal(("0x2", false, true), HyprlandPanelWindowSupport.FindPanel(Panel(pinned: false, focus: "0"), Title));
    }

    private HyprlandPanelWindowSupport Support()
    {
        _session.With("HYPRLAND_INSTANCE_SIGNATURE", "abc");
        _session.WithTool("hyprctl");
        return new HyprlandPanelWindowSupport(_inner, _runner, _session.Session, TimeSpan.Zero, _focused.Add);
    }

    private static string Panel(bool pinned, string focus = "2") => Clients.Replace("PINNED", pinned ? "true" : "false").Replace("FOCUS", focus);

    private static ProcessResult Ok(string stdout) => new(0, stdout, "");

    private sealed class RecordingSupport : IPanelWindowSupport
    {
        public List<(nint Handle, string? Descriptor, string Title)> Calls { get; } = [];

        public void PrepareWindow(nint windowHandle, string? handleDescriptor)
        {
        }

        public void ShowOnAllWorkspaces(nint windowHandle, string? handleDescriptor, string title) => Calls.Add((windowHandle, handleDescriptor, title));

        public void BeforeShow()
        {
        }

        public void Hide(Action hideWindow) => hideWindow();
    }
}
