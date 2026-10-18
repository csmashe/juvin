using System.Runtime.Versioning;
using Juvin.Platform.Linux;
using Juvin.Platform.Mac;
using Juvin.Platform.Windows;

namespace Juvin.Platform.Tests;

// The directory lists use Unix separators, so they are checked where those apply.
public sealed class ToolSearchPathsTests
{
    [UnixFact]
    [SupportedOSPlatform("macos")]
    public void Mac_SearchesHomebrewThenThePerUserDirectories()
    {
        Assert.Equal(
            ["/opt/homebrew/bin", "/usr/local/bin", "/Users/ada/.npm-global/bin", "/Users/ada/.bun/bin", "/Users/ada/.local/bin"],
            MacToolSearchPaths.For("/Users/ada")
        );
        Assert.Equal(["/opt/homebrew/bin", "/usr/local/bin"], MacToolSearchPaths.For(""));
    }

    [UnixFact]
    [SupportedOSPlatform("linux")]
    public void Linux_SearchesThePerUserDirectories()
    {
        Assert.Equal(["/home/ada/.local/bin", "/home/ada/.npm-global/bin", "/home/ada/.bun/bin"], LinuxToolSearchPaths.For("/home/ada"));
        Assert.Empty(LinuxToolSearchPaths.For(""));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_AddsNothing()
    {
        Assert.Empty(new WindowsToolSearchPaths().Directories);
    }
}
