using System.Runtime.Versioning;
using Juvin.Platform.Linux;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxProcessPathTests
{
    private const string AppImage = "/home/a/Juvin.AppImage";
    private const string AppDir = "/tmp/.mount_JuvinX";

    private static Func<string, string?> Environment(string? appImage, string? appDir) =>
        name => name switch
        {
            "APPIMAGE" => appImage,
            "APPDIR" => appDir,
            _ => null,
        };

    [Theory]
    [InlineData(null, null, "/opt/juvin/juvin", "/opt/juvin/juvin")]
    [InlineData("", "", "/opt/juvin/juvin", "/opt/juvin/juvin")]
    [InlineData(AppImage, AppDir, AppDir + "/usr/bin/juvin", AppImage)]
    [InlineData(AppImage, AppDir + "/", AppDir + "/usr/bin/juvin", AppImage)]
    [InlineData(AppImage, AppDir, "/opt/juvin/juvin", "/opt/juvin/juvin")]
    [InlineData(AppImage, AppDir, AppDir + "-other/usr/bin/juvin", AppDir + "-other/usr/bin/juvin")]
    [InlineData(AppImage, null, AppDir + "/usr/bin/juvin", AppDir + "/usr/bin/juvin")]
    [InlineData(null, AppDir, AppDir + "/usr/bin/juvin", AppDir + "/usr/bin/juvin")]
    [InlineData(AppImage, AppDir, null, null)]
    public void Resolve_UsesTheAppImageFileOnlyWhenTheProcessRunsFromItsMount(string? appImage, string? appDir, string? processPath, string? expected)
    {
        Assert.Equal(expected, LinuxProcessPath.Resolve(Environment(appImage, appDir), processPath));
    }

    [Fact]
    public void RunsFromAppImage_IgnoresVariablesInheritedFromAnotherAppImage()
    {
        Assert.False(LinuxProcessPath.RunsFromAppImage(Environment("/home/a/Editor.AppImage", "/tmp/.mount_Editor"), "/opt/juvin/juvin"));
        Assert.True(LinuxProcessPath.RunsFromAppImage(Environment(AppImage, AppDir), AppDir + "/usr/bin/juvin"));
    }
}
