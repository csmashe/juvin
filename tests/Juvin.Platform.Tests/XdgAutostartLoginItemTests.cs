using System.Runtime.Versioning;
using Juvin.Platform.Linux.Launcher;

namespace Juvin.Platform.Tests;

// The entry holds Linux paths and line endings, so the tests run only there, like the AppImage launcher's.
[SupportedOSPlatform("linux")]
public sealed class XdgAutostartLoginItemTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly Dictionary<string, string?> _environment = [];

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string ConfigHome => _temp.File("config");

    private string EntryPath => Path.Join(ConfigHome, "autostart", "juvin.desktop");

    private XdgAutostartLoginItem Create(string? processPath = "/opt/juvin/juvin") => new(_environment.GetValueOrDefault, processPath, ConfigHome);

    private string ExecLine() => File.ReadAllLines(EntryPath).Single(line => line.StartsWith("Exec=", StringComparison.Ordinal));

    [LinuxFact]
    public async Task Enable_WritesAnAutostartEntryThatStartsInTheBackground()
    {
        var item = Create();

        await item.EnableAsync(CancellationToken.None);

        var entry = await File.ReadAllTextAsync(EntryPath);
        Assert.Equal("Exec=\"/opt/juvin/juvin\" --background", ExecLine());
        Assert.Contains("X-Juvin-Managed=true\n", entry);
        Assert.Contains("Hidden=false\n", entry);
        Assert.Contains("X-GNOME-Autostart-enabled=true\n", entry);
        Assert.Contains("StartupWMClass=juvin\n", entry);
    }

    [LinuxFact]
    public async Task Enable_FromAnAppImage_NamesTheAppImageFile()
    {
        var appDir = _temp.File("mount");
        _environment["APPDIR"] = appDir;
        _environment["APPIMAGE"] = "/home/me/Apps/Juvin $1.AppImage";

        await Create(Path.Join(appDir, "usr", "bin", "juvin")).EnableAsync(CancellationToken.None);

        Assert.Equal("Exec=\"/home/me/Apps/Juvin \\\\$1.AppImage\" --background", ExecLine());
    }

    [LinuxFact]
    public async Task Enable_Twice_LeavesTheEntryUntouched()
    {
        var item = Create();
        await item.EnableAsync(CancellationToken.None);
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(EntryPath, past);

        await item.EnableAsync(CancellationToken.None);

        Assert.Equal(past, File.GetLastWriteTimeUtc(EntryPath));
    }

    [LinuxFact]
    public async Task Disable_RemovesTheEntry_AndIsIdempotent()
    {
        var item = Create();
        await item.EnableAsync(CancellationToken.None);

        await item.DisableAsync(CancellationToken.None);
        await item.DisableAsync(CancellationToken.None);

        Assert.False(File.Exists(EntryPath));
    }

    [LinuxFact]
    public async Task AForeignEntry_IsNotEnabledAndNotRemoved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        await File.WriteAllTextAsync(EntryPath, "[Desktop Entry]\nExec=juvin\n");
        var item = Create();

        await item.DisableAsync(CancellationToken.None);

        Assert.True(File.Exists(EntryPath));
    }

    [LinuxFact]
    public async Task UnknownExecutable_IsUnsupportedAndEnableThrows()
    {
        var item = Create(processPath: null);

        Assert.False(item.IsSupported);
        await Assert.ThrowsAsync<LoginItemException>(() => item.EnableAsync(CancellationToken.None));
    }

    [LinuxFact]
    public async Task UnwritableConfigHome_Throws()
    {
        await File.WriteAllTextAsync(ConfigHome, "a file, not a directory");

        await Assert.ThrowsAsync<LoginItemException>(() => Create().EnableAsync(CancellationToken.None));
    }
}
