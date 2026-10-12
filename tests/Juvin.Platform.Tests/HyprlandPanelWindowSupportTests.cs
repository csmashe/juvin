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
        [{"address":"0x1","class":"juvin","title":"Juvin","floating":false,"pinned":false},
         {"address":"0x2","class":"juvin","title":"Juvin rewrite","floating":true,"pinned":PINNED}]
        """;

    private readonly RecordingSupport _inner = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly DesktopShortcutTestSession _session = new();

    public void Dispose() => _session.Dispose();

    [LinuxFact]
    public async Task PinsTheUnpinnedPanelOnce_ByClassAndTitle()
    {
        _runner.Handler = (_, args) => args[0] == "-j" ? Ok(Clients.Replace("PINNED", "false")) : Ok("ok");

        await Support().PinAsync(Title);

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

            return Ok(args[0] == "-j" ? Clients.Replace("PINNED", "false") : "ok");
        };
        var support = Support();

        var first = Task.Run(() => support.PinAsync(Title));
        await Task.Delay(50);
        await support.PinAsync(Title);
        gate.SetResult();
        await first;

        Assert.Single(_runner.Calls, call => call.Arguments[0] == "dispatch");
    }

    [LinuxFact]
    public async Task LeavesAPinnedPanelAlone()
    {
        _runner.Handler = (_, _) => Ok(Clients.Replace("PINNED", "true"));

        await Support().PinAsync(Title);

        Assert.Single(_runner.Calls);
    }

    [LinuxFact]
    public async Task RetriesWhileHyprlandHasNotListedThePanel_ThenGivesUp()
    {
        _runner.Handler = (_, _) => Ok("[]");

        await Support().PinAsync(Title);

        Assert.Equal(3, _runner.Calls.Count);
        Assert.All(_runner.Calls, call => Assert.Equal("-j", call.Arguments[0]));
    }

    [LinuxFact]
    public void OnlyTheX11HintsRunOutsideHyprland()
    {
        _session.With("XDG_CURRENT_DESKTOP", "GNOME");
        var support = new HyprlandPanelWindowSupport(_inner, _runner, _session.Session, TimeSpan.Zero);

        support.ShowOnAllWorkspaces(42, Title);

        Assert.Equal([(42, Title)], _inner.Calls);
        Assert.Empty(_runner.Calls);
    }

    [LinuxFact]
    public void FindPanel_IgnoresOtherWindowsAndBadJson()
    {
        Assert.Null(HyprlandPanelWindowSupport.FindPanel("{}", Title));
        Assert.Null(HyprlandPanelWindowSupport.FindPanel(Clients.Replace("PINNED", "false"), "Set up Juvin"));
        Assert.Equal(("0x2", true), HyprlandPanelWindowSupport.FindPanel(Clients.Replace("PINNED", "true"), Title));
    }

    private HyprlandPanelWindowSupport Support()
    {
        _session.With("HYPRLAND_INSTANCE_SIGNATURE", "abc");
        _session.WithTool("hyprctl");
        return new HyprlandPanelWindowSupport(_inner, _runner, _session.Session, TimeSpan.Zero);
    }

    private static ProcessResult Ok(string stdout) => new(0, stdout, "");

    private sealed class RecordingSupport : IPanelWindowSupport
    {
        public List<(nint Handle, string Title)> Calls { get; } = [];

        public void ShowOnAllWorkspaces(nint windowHandle, string title) => Calls.Add((windowHandle, title));
    }
}
