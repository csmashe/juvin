using System.Runtime.Versioning;
using System.Xml.Linq;
using Juvin.Platform.Mac.Launcher;

namespace Juvin.Platform.Tests;

// The plist is plain file I/O, so these run on every OS even though the assembly targets macOS.
[SupportedOSPlatform("macos")]
public sealed class LaunchAgentLoginItemTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string Home => _temp.File("home");

    private string PlistPath => Path.Join(Home, "Library", "LaunchAgents", "com.juvin.app.plist");

    private LaunchAgentLoginItem Create(string? processPath = "/Applications/Juvin.app/Contents/MacOS/juvin") => new(processPath, Home);

    private static string Value(XElement dict, string key) =>
        dict.Elements("key").Single(k => k.Value == key).ElementsAfterSelf().First().ToString(SaveOptions.DisableFormatting);

    [Fact]
    public async Task Enable_WritesALaunchAgentThatStartsInTheBackground()
    {
        var item = Create("/Applications/A & B <Juvin>.app/Contents/MacOS/juvin");

        await item.EnableAsync(CancellationToken.None);

        var dict = XDocument.Load(PlistPath).Root!.Element("dict")!;
        Assert.Equal("<string>com.juvin.app</string>", Value(dict, "Label"));
        Assert.Equal(
            ["/Applications/A & B <Juvin>.app/Contents/MacOS/juvin", "--background"],
            dict.Elements("key").Single(k => k.Value == "ProgramArguments").ElementsAfterSelf().First().Elements("string").Select(e => e.Value)
        );
        Assert.Equal("<true />", Value(dict, "RunAtLoad"));
        Assert.Equal("<true />", Value(dict, "JuvinManaged"));
    }

    [Fact]
    public async Task Disable_RemovesTheLaunchAgent_AndIsIdempotent()
    {
        var item = Create();
        await item.EnableAsync(CancellationToken.None);

        await item.DisableAsync(CancellationToken.None);
        await item.DisableAsync(CancellationToken.None);

        Assert.False(File.Exists(PlistPath));
    }

    [Fact]
    public async Task AForeignLaunchAgent_IsNotEnabledAndNotRemoved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        await File.WriteAllTextAsync(PlistPath, "<plist><dict><key>Label</key><string>com.juvin.app</string></dict></plist>");
        var item = Create();

        await item.DisableAsync(CancellationToken.None);

        Assert.True(File.Exists(PlistPath));
    }

    [Fact]
    public async Task UnknownExecutable_IsUnsupportedAndEnableThrows()
    {
        var item = Create(processPath: null);

        Assert.False(item.IsSupported);
        await Assert.ThrowsAsync<LoginItemException>(() => item.EnableAsync(CancellationToken.None));
    }

    [Fact]
    public void TheDotnetHost_IsUnsupported()
    {
        Assert.False(Create("/usr/local/share/dotnet/dotnet").IsSupported);
    }
}
