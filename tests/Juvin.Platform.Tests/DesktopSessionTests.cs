using System.Runtime.Versioning;
using Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class DesktopSessionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [LinuxFact]
    public void ARunningSway_WinsOverTheDesktopANestedSessionInherited()
    {
        var socket = _temp.File("sway-ipc.sock");
        File.WriteAllText(socket, "");

        Assert.Equal("sway", Detect(("SWAYSOCK", socket), ("XDG_CURRENT_DESKTOP", "KDE")));
    }

    [LinuxFact]
    public void ASwaySocketLeftFromAnEarlierSession_LosesToTheCurrentDesktop()
    {
        Assert.Equal("kde", Detect(("SWAYSOCK", _temp.File("gone.sock")), ("XDG_CURRENT_DESKTOP", "KDE")));
    }

    [LinuxFact]
    public void AHyprlandSignatureLeftFromAnEarlierSession_LosesToTheCurrentDesktop()
    {
        Assert.Equal("gnome", Detect(("HYPRLAND_INSTANCE_SIGNATURE", "gone"), ("XDG_RUNTIME_DIR", _temp.File("run")), ("XDG_CURRENT_DESKTOP", "ubuntu:GNOME")));
    }

    [LinuxFact]
    public void ARunningHyprland_IsFoundByItsRuntimeFolder()
    {
        var runtime = _temp.File("run");
        Directory.CreateDirectory(Path.Join(runtime, "hypr", "abc"));

        Assert.Equal("hyprland", Detect(("HYPRLAND_INSTANCE_SIGNATURE", "abc"), ("XDG_RUNTIME_DIR", runtime), ("XDG_CURRENT_DESKTOP", "KDE")));
    }

    [LinuxFact]
    public void ACompositorVariableAlone_StillNamesTheDesktop()
    {
        Assert.Equal("sway", Detect(("SWAYSOCK", _temp.File("gone.sock"))));
    }

    private static string? Detect(params (string Name, string Value)[] variables) =>
        DesktopSession.DetectDesktop(name => variables.FirstOrDefault(v => v.Name == name).Value);
}
