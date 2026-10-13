using System.Runtime.Versioning;
using Juvin.Platform.Linux.Launcher;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class AppImageLauncherIntegrationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string _appImage;

    public AppImageLauncherIntegrationTests()
    {
        _appImage = _temp.File("Juvin.AppImage");
        Directory.CreateDirectory(Path.GetDirectoryName(ProcessPath)!);
        WriteIcon(Path.Join("256x256", "apps", "juvin.png"), "png 256");
        WriteIcon(Path.Join("symbolic", "apps", "juvin-symbolic.svg"), "<svg/>");
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string AppDir => _temp.File("mount");

    private string ProcessPath => Path.Join(AppDir, "usr", "bin", "juvin");

    private string SourceIcons => Path.Join(AppDir, "usr", "share", "icons", "hicolor");

    private string DataHome => _temp.File("data");

    private string EntryPath => Path.Join(DataHome, "applications", "juvin-appimage.desktop");

    private string TargetIcon(string relative) => Path.Join(DataHome, "icons", "hicolor", relative);

    private void WriteIcon(string relative, string contents)
    {
        var path = Path.Join(SourceIcons, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private Task RunAsync(string? processPath = null, string? dataHome = null)
    {
        var environment = new Dictionary<string, string?> { ["APPDIR"] = AppDir, ["APPIMAGE"] = _appImage };
        return new AppImageLauncherIntegration(environment.GetValueOrDefault, processPath ?? ProcessPath, dataHome ?? DataHome)
            .EnsureInstalledAsync(CancellationToken.None);
    }

    private string ExecLine() => File.ReadAllLines(EntryPath).Single(line => line.StartsWith("Exec=", StringComparison.Ordinal));

    [LinuxFact]
    public async Task NotAnAppImage_WritesNothing()
    {
        await RunAsync(processPath: _temp.File("elsewhere/juvin"));

        Assert.False(Directory.Exists(DataHome));
    }

    [LinuxFact]
    public async Task AppImage_WritesTheEntryAndCopiesTheIcons()
    {
        await RunAsync();

        var entry = await File.ReadAllTextAsync(EntryPath);
        Assert.Contains($"Exec=\"{_appImage}\"\n", entry);
        Assert.Contains("Icon=juvin\n", entry);
        Assert.Contains("StartupWMClass=juvin\n", entry);
        Assert.Equal("png 256", await File.ReadAllTextAsync(TargetIcon(Path.Join("256x256", "apps", "juvin.png"))));
        Assert.Equal("<svg/>", await File.ReadAllTextAsync(TargetIcon(Path.Join("symbolic", "apps", "juvin-symbolic.svg"))));
    }

    [LinuxFact]
    public async Task CopyingIcons_TouchesTheThemeRootSoTheDesktopRescans()
    {
        var root = TargetIcon("");
        Directory.CreateDirectory(root);
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(root, past);

        await RunAsync();

        Assert.NotEqual(past, Directory.GetLastWriteTimeUtc(root));
    }

    [LinuxFact]
    public async Task SecondRun_ChangesNothing()
    {
        await RunAsync();
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(EntryPath, past);
        var icon = TargetIcon(Path.Join("256x256", "apps", "juvin.png"));
        var iconTime = File.GetLastWriteTimeUtc(icon);

        await RunAsync();

        Assert.Equal(past, File.GetLastWriteTimeUtc(EntryPath));
        Assert.Equal(iconTime, File.GetLastWriteTimeUtc(icon));
    }

    [LinuxFact]
    public async Task ChangedIcon_IsCopiedAgain()
    {
        await RunAsync();
        WriteIcon(Path.Join("256x256", "apps", "juvin.png"), "new png");

        await RunAsync();

        Assert.Equal("new png", await File.ReadAllTextAsync(TargetIcon(Path.Join("256x256", "apps", "juvin.png"))));
    }

    [LinuxFact]
    public async Task MovedAppImage_RewritesExec()
    {
        await RunAsync();
        _appImage = _temp.File("Moved.AppImage");

        await RunAsync();

        Assert.Equal($"Exec=\"{_appImage}\"", ExecLine());
    }

    [LinuxFact]
    public async Task SpecialCharacters_AreEscaped()
    {
        _appImage = "/tmp/a\"b\\c$d`e%f.AppImage";

        await RunAsync();

        Assert.Equal("Exec=\"/tmp/a\\\\\"b\\\\\\\\c\\\\$d\\\\`e%%f.AppImage\"", ExecLine());
    }

    [LinuxFact]
    public async Task MissingIconTree_StillWritesTheEntry()
    {
        Directory.Delete(Path.Join(AppDir, "usr", "share"), recursive: true);

        await RunAsync();

        Assert.True(File.Exists(EntryPath));
    }

    [LinuxFact]
    public async Task UnwritableDataHome_DoesNotThrow()
    {
        var blocked = _temp.File("blocked");
        await File.WriteAllTextAsync(blocked, "a file, not a directory");

        await RunAsync(dataHome: blocked);

        Assert.False(File.Exists(Path.Join(blocked, "applications", "juvin-appimage.desktop")));
    }
}
