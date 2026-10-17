using System.Runtime.Versioning;
using Juvin.Core.Providers;
using Juvin.Platform.Linux.Panel;
using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class SwayPanelWindowSupportTests : IDisposable
{
    private const string Title = "Juvin rewrite";

    private const string Tree =
        """
        {"id":1,"type":"root","nodes":[{"id":2,"type":"output","nodes":[
          {"id":3,"type":"workspace","name":"1","rect":{"x":0,"y":23,"width":1280,"height":777},
           "nodes":[{"id":7,"type":"con","name":"Juvin","rect":{"x":0,"y":48,"width":1280,"height":752},"window_properties":{"class":"juvin"}}],
           "floating_nodes":[{"id":8,"type":"floating_con","name":"Juvin rewrite","rect":{"x":410,"y":91,"width":460,"height":640},"window_properties":{"class":"juvin"}}]}]}]}
        """;

    private readonly RecordingSupport _inner = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly DesktopShortcutTestSession _session = new();

    public void Dispose() => _session.Dispose();

    [LinuxFact]
    public async Task MakesThePanelStickyAndMovesItTopRight_ByClassAndTitle()
    {
        _runner.Handler = (_, args) => args[0] == "-t" ? Ok(Tree) : Ok("[{\"success\":true}]");

        await Support().PlaceAsync(Title);

        Assert.Equal(["swaymsg", "swaymsg"], _runner.Calls.Select(call => call.FileName));
        Assert.Equal(["[con_id=8] sticky enable, move absolute position 804 39"], _runner.Calls[1].Arguments);
    }

    [LinuxFact]
    public async Task RetriesWhileSwayHasNotListedThePanel_ThenGivesUp()
    {
        _runner.Handler = (_, _) => Ok("""{"id":1,"type":"root","nodes":[]}""");

        await Support().PlaceAsync(Title);

        Assert.Equal(3, _runner.Calls.Count);
        Assert.All(_runner.Calls, call => Assert.Equal("-t", call.Arguments[0]));
    }

    [LinuxFact]
    public void OnlyTheInnerHintsRunOutsideSway()
    {
        _session.With("XDG_CURRENT_DESKTOP", "GNOME");
        var support = new SwayPanelWindowSupport(_inner, _runner, _session.Session, TimeSpan.Zero);

        support.ShowOnAllWorkspaces(42, Title);

        Assert.Equal([(42, Title)], _inner.Calls);
        Assert.Empty(_runner.Calls);
    }

    [LinuxFact]
    public void FindPanel_IgnoresOtherWindowsAndBadJson()
    {
        Assert.Null(SwayPanelWindowSupport.FindPanel("[]", Title));
        Assert.Null(SwayPanelWindowSupport.FindPanel(Tree, "Set up Juvin"));
        Assert.Equal((8L, 804, 39), SwayPanelWindowSupport.FindPanel(Tree, Title));
    }

    private SwayPanelWindowSupport Support()
    {
        _session.With("SWAYSOCK", "/run/user/1000/sway-ipc.sock");
        _session.WithTool("swaymsg");
        return new SwayPanelWindowSupport(_inner, _runner, _session.Session, TimeSpan.Zero);
    }

    private static ProcessResult Ok(string stdout) => new(0, stdout, "");

    private sealed class RecordingSupport : IPanelWindowSupport
    {
        public List<(nint Handle, string Title)> Calls { get; } = [];

        public void ShowOnAllWorkspaces(nint windowHandle, string title) => Calls.Add((windowHandle, title));
    }
}
