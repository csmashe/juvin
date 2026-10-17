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
        {"id":1,"type":"root","focus":[2],"nodes":[{"id":2,"type":"output","name":"Virtual-1","focus":[3],"nodes":[
          {"id":3,"type":"workspace","name":"1","rect":{"x":0,"y":23,"width":1280,"height":777},
           "nodes":[{"id":7,"type":"con","name":"Juvin","rect":{"x":0,"y":48,"width":1280,"height":752},"window_properties":{"class":"juvin"}}],
           "floating_nodes":[{"id":8,"type":"floating_con","name":"Juvin rewrite","rect":{"x":410,"y":91,"width":460,"height":640},"window_properties":{"class":"juvin"}}]}]}]}
        """;

    private const string TwoOutputs =
        """
        {"id":1,"type":"root","focus":[4,2],"nodes":[
          {"id":2,"type":"output","name":"Virtual-1","focus":[3],"nodes":[
            {"id":3,"type":"workspace","name":"1","rect":{"x":0,"y":23,"width":1280,"height":777},
             "floating_nodes":[{"id":8,"type":"floating_con","name":"Juvin rewrite","rect":{"x":410,"y":91,"width":460,"height":640},"window_properties":{"class":"juvin"}}]}]},
          {"id":4,"type":"output","name":"Virtual-2","focus":[6,5],"nodes":[
            {"id":5,"type":"workspace","name":"2","rect":{"x":1280,"y":23,"width":1920,"height":1057}},
            {"id":6,"type":"workspace","name":"3","rect":{"x":1280,"y":23,"width":1920,"height":1057}}]}]}
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
        Assert.Equal(["[con_id=8] move container to output \"Virtual-1\", sticky enable, move absolute position 804 39"], _runner.Calls[1].Arguments);
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

        support.ShowOnAllWorkspaces(42, "XID", Title);

        Assert.Equal([(42, "XID", Title)], _inner.Calls);
        Assert.Empty(_runner.Calls);
    }

    [LinuxFact]
    public void FindPanel_IgnoresOtherWindowsAndBadJson()
    {
        Assert.Null(SwayPanelWindowSupport.FindPanel("[]", Title));
        Assert.Null(SwayPanelWindowSupport.FindPanel(Tree, "Set up Juvin"));
        Assert.Equal((8L, "Virtual-1", 804, 39), SwayPanelWindowSupport.FindPanel(Tree, Title));
    }

    [LinuxFact]
    public void FindPanel_PlacesThePanelOnTheFocusedOutput()
    {
        Assert.Equal((8L, "Virtual-2", 2724, 39), SwayPanelWindowSupport.FindPanel(TwoOutputs, Title));
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
        public List<(nint Handle, string? Descriptor, string Title)> Calls { get; } = [];

        public void ShowOnAllWorkspaces(nint windowHandle, string? handleDescriptor, string title) => Calls.Add((windowHandle, handleDescriptor, title));
    }
}
