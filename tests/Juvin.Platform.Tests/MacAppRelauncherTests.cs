using System.Diagnostics;
using System.Runtime.Versioning;
using Juvin.Platform.Mac.Launcher;

namespace Juvin.Platform.Tests;

// The process start sits behind a fake, so these run on every OS.
[SupportedOSPlatform("macos")]
public sealed class MacAppRelauncherTests
{
    private const string Executable = "/Applications/Juvin.app/Contents/MacOS/Juvin";

    [Theory]
    [InlineData(Executable, "/Applications/Juvin.app")]
    [InlineData("/Users/me/Apps/My Juvin.app/Contents/MacOS/Juvin", "/Users/me/Apps/My Juvin.app")]
    [InlineData("/Users/me/src/Juvin/bin/Debug/net10.0/Juvin", null)]
    [InlineData("/usr/local/share/dotnet/dotnet", null)]
    [InlineData("/Applications/Juvin/Contents/MacOS/Juvin", null)]
    [InlineData(null, null)]
    public void BundlePath_FindsTheAppBundleAroundTheExecutable(string? processPath, string? expected)
    {
        Assert.Equal(expected, MacAppRelauncher.BundlePath(processPath));
    }

    [Fact]
    public void Relaunch_StartsAShellThatWaitsForThisProcess_ThenRequestsExit()
    {
        ProcessStartInfo? started = null;
        var exits = 0;
        var relauncher = new MacAppRelauncher(Executable, 4242, info => { started = info; return true; }, () => exits++);

        Assert.True(relauncher.CanRelaunch);
        Assert.True(relauncher.Relaunch());

        Assert.NotNull(started);
        Assert.Equal("/bin/sh", started.FileName);
        Assert.False(started.UseShellExecute);
        Assert.Equal(["-c", MacAppRelauncher.Script, "sh", "4242", "/Applications/Juvin.app"], started.ArgumentList);
        Assert.Equal(1, exits);
    }

    [Fact]
    public void Relaunch_DoesNotExitWhenTheShellCannotStart()
    {
        var exits = 0;
        var relauncher = new MacAppRelauncher(Executable, 4242, _ => false, () => exits++);

        Assert.False(relauncher.Relaunch());
        Assert.Equal(0, exits);
    }

    [Fact]
    public void Relaunch_IsUnavailableOutsideABundle()
    {
        var starts = 0;
        var relauncher = new MacAppRelauncher("/usr/local/share/dotnet/dotnet", 4242, _ => ++starts > 0, () => { });

        Assert.False(relauncher.CanRelaunch);
        Assert.False(relauncher.Relaunch());
        Assert.Equal(0, starts);
    }
}
