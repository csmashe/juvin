using System.Runtime.Versioning;
using Juvin.Core.Localization;
using Juvin.Platform.Linux.NativeMessaging;
using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxNativeHostRegistrarTests : IDisposable
{
    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly TempDirectory _temp = new();
    private readonly FakeProcessRunner _runner = new();
    private bool _runsFromAppImage;

    public LinuxNativeHostRegistrarTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(Path.GetDirectoryName(HostPath)!);
        File.WriteAllText(HostPath, "self-contained host");
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string Home => _temp.File("home");

    private string Bin => _temp.File("bin");

    private string HostPath => _temp.File(Path.Join("app", "juvin-host"));

    private string FlatpakApps => _temp.File("flatpak-apps");

    private string Snap => _temp.File("snap");

    private string SnapBin => Path.Join(Snap, "bin");

    private string DataPath => _temp.File("data");

    private string StableHost => Path.Join(DataPath, "juvin-host");

    private LinuxNativeHostRegistrar CreateRegistrar(string? hostPath = null, string? runtimeDirectory = null, TimeSpan? commandTimeout = null)
    {
        var paths = new LinuxBrowserPaths(Home, Path.Join(Home, ".config"), runtimeDirectory, [Bin, SnapBin], [FlatpakApps], Snap);
        return new LinuxNativeHostRegistrar(
            () => hostPath ?? HostPath,
            new Localizer(),
            paths,
            _runner,
            _runsFromAppImage,
            DataPath,
            commandTimeout ?? TimeSpan.FromSeconds(30)
        );
    }

    private void CreateHomeDirectory(string relative) => Directory.CreateDirectory(Path.Join(Home, relative));

    private void InstallFlatpak(string flatpakId) => Directory.CreateDirectory(Path.Join(FlatpakApps, flatpakId));

    private void CreateExecutable(string name, string? directory = null, string contents = "")
    {
        directory ??= Bin;
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, name);
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, Executable);
    }

    [LinuxFact]
    public void DetectBrowsers_FindsNothingInAnEmptyHome()
    {
        Assert.Empty(CreateRegistrar().DetectBrowsers());
    }

    [LinuxFact]
    public void DetectBrowsers_FindsEveryPackagingInDisplayOrder()
    {
        CreateExecutable("firefox");
        InstallFlatpak("org.mozilla.firefox");
        InstallFlatpak("app.zen_browser.zen");
        CreateExecutable("google-chrome-stable");
        CreateExecutable("chromium");
        CreateExecutable("brave-browser");
        CreateExecutable("microsoft-edge");

        var found = CreateRegistrar().DetectBrowsers();

        Assert.Equal(["firefox", "firefox-flatpak", "zen-flatpak", "chrome", "chromium", "brave", "edge"], found.Select(b => b.Id));
        Assert.Equal(BrowserPackaging.Native, found[0].Packaging);
        Assert.Equal(BrowserPackaging.Flatpak, found[1].Packaging);
        Assert.All(found.Take(3), b => Assert.Equal(BrowserFamily.Firefox, b.Family));
        Assert.All(found.Skip(3), b => Assert.Equal(BrowserFamily.Chromium, b.Family));
    }

    [LinuxFact]
    public void DetectBrowsers_IgnoresANonExecutableFileOnThePath()
    {
        File.WriteAllText(Path.Join(Bin, "firefox"), "");

        Assert.Empty(CreateRegistrar().DetectBrowsers());
    }

    [LinuxFact]
    public void DetectBrowsers_IgnoresLeftoverProfileDirectories()
    {
        CreateHomeDirectory(".mozilla");
        CreateHomeDirectory(".config/chromium");
        CreateHomeDirectory(".var/app/org.mozilla.firefox");
        CreateHomeDirectory("snap/chromium");

        Assert.Empty(CreateRegistrar().DetectBrowsers());
    }

    [LinuxFact]
    public void DetectBrowsers_LeftoverSnapProfileDoesNotMakeANativeBrowserASnap()
    {
        CreateExecutable("chromium");
        CreateHomeDirectory("snap/chromium");

        var chromium = Assert.Single(CreateRegistrar().DetectBrowsers());

        Assert.Equal(BrowserPackaging.Native, chromium.Packaging);
    }

    [LinuxFact]
    public void DetectBrowsers_ReportsSnapFirefoxFromItsLauncher()
    {
        CreateExecutable("firefox", SnapBin);

        var firefox = Assert.Single(CreateRegistrar().DetectBrowsers());

        Assert.Equal(new BrowserTarget("firefox", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Snap), firefox);
    }

    [LinuxFact]
    public void DetectBrowsers_ANativeBrowserBesideItsSnapIsNative()
    {
        CreateExecutable("brave-browser");
        CreateExecutable("brave", SnapBin);
        Directory.CreateDirectory(Path.Join(Snap, "brave", "current"));

        var brave = Assert.Single(CreateRegistrar().DetectBrowsers());

        Assert.Equal(BrowserPackaging.Native, brave.Packaging);
    }

    [LinuxFact]
    public void DetectBrowsers_UbuntuWrapperScriptIsSnap()
    {
        CreateExecutable("firefox", contents: "#!/bin/sh\n# Transitional package: Firefox is a snap.\nexec /snap/bin/firefox \"$@\"\n");

        Assert.Equal(BrowserPackaging.Snap, Assert.Single(CreateRegistrar().DetectBrowsers()).Packaging);
    }

    [LinuxFact]
    public void DetectBrowsers_ALauncherUnderAnAliasedSnapRootIsSnap()
    {
        // Fedora: /snap is a symlink to /var/lib/snapd/snap, and PATH names the mount, not the link.
        var mount = Path.Join(Home, "var-lib-snapd-snap");
        Directory.CreateDirectory(Path.Join(mount, "bin"));
        var link = Path.Join(Home, "snap-link");
        Directory.CreateSymbolicLink(link, mount);
        CreateExecutable("snap");
        File.CreateSymbolicLink(Path.Join(mount, "bin", "chromium"), Path.Join(Bin, "snap"));
        var registrar = new LinuxNativeHostRegistrar(
            () => HostPath,
            new Localizer(),
            new LinuxBrowserPaths(Home, Path.Join(Home, ".config"), null, [Path.Join(mount, "bin")], [FlatpakApps], link),
            _runner,
            runsFromAppImage: false,
            DataPath,
            TimeSpan.FromSeconds(30)
        );

        Assert.Equal(BrowserPackaging.Snap, Assert.Single(registrar.DetectBrowsers()).Packaging);
    }

    [LinuxFact]
    public void DetectBrowsers_SymlinkIntoTheSnapRootIsSnap()
    {
        CreateExecutable("chromium", SnapBin);
        File.CreateSymbolicLink(Path.Join(Bin, "chromium"), Path.Join(SnapBin, "chromium"));
        var registrar = new LinuxNativeHostRegistrar(
            () => HostPath,
            new Localizer(),
            new LinuxBrowserPaths(Home, Path.Join(Home, ".config"), null, [Bin], [FlatpakApps], Snap),
            _runner,
            runsFromAppImage: false,
            DataPath,
            TimeSpan.FromSeconds(30)
        );

        Assert.Equal(BrowserPackaging.Snap, Assert.Single(registrar.DetectBrowsers()).Packaging);
    }

    [LinuxFact]
    public void DetectBrowsers_AScriptThatDoesNotMentionSnapIsNative()
    {
        CreateExecutable("firefox", contents: "#!/bin/sh\nexec /usr/lib/firefox/firefox \"$@\"\n");

        Assert.Equal(BrowserPackaging.Native, Assert.Single(CreateRegistrar().DetectBrowsers()).Packaging);
    }

    [LinuxFact]
    public async Task SnapChromium_IsDetectedButCannotRegister()
    {
        CreateExecutable("chromium", SnapBin);
        var registrar = CreateRegistrar();

        var chromium = Assert.Single(registrar.DetectBrowsers());
        Assert.Equal(BrowserPackaging.Snap, chromium.Packaging);
        var error = await Assert.ThrowsAsync<NativeHostRegistrationException>(() => registrar.RegisterAsync(chromium, CancellationToken.None));

        Assert.Contains("Snap", error.Message);
        Assert.Equal(HostRegistration.NotRegistered, registrar.CheckRegistration(chromium));
    }

    [LinuxFact]
    public async Task Register_WritesTheManifestAndCheckRoundTrips()
    {
        CreateExecutable("google-chrome");
        var registrar = CreateRegistrar();
        var chrome = Assert.Single(registrar.DetectBrowsers());
        Assert.Equal(HostRegistration.NotRegistered, registrar.CheckRegistration(chrome));

        await registrar.RegisterAsync(chrome, CancellationToken.None);

        var manifestPath = Path.Join(Home, ".config/google-chrome/NativeMessagingHosts", NativeHostManifest.FileName);
        Assert.Equal(NativeHostManifest.Render(BrowserFamily.Chromium, HostPath), await File.ReadAllTextAsync(manifestPath));
        Assert.NotEqual(0xEF, (await File.ReadAllBytesAsync(manifestPath))[0]);
        Assert.Equal(HostRegistration.Registered, registrar.CheckRegistration(chrome));
        Assert.Equal(HostRegistration.Stale, CreateRegistrar(_temp.File("elsewhere")).CheckRegistration(chrome));
        Assert.Empty(_runner.Calls);
    }

    [LinuxFact]
    public async Task Unregister_RemovesTheManifest()
    {
        var registrar = CreateRegistrar();
        var firefox = new BrowserTarget("firefox", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Native);
        await registrar.RegisterAsync(firefox, CancellationToken.None);

        await registrar.UnregisterAsync(firefox, CancellationToken.None);

        Assert.False(File.Exists(Path.Join(Home, ".mozilla/native-messaging-hosts", NativeHostManifest.FileName)));
        Assert.Equal(HostRegistration.NotRegistered, registrar.CheckRegistration(firefox));
    }

    [LinuxFact]
    public async Task Register_SnapFirefoxPreApprovesThePortalWhenFlatpakIsAvailable()
    {
        CreateExecutable("flatpak");
        var snap = new BrowserTarget("firefox", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Snap);

        await CreateRegistrar().RegisterAsync(snap, CancellationToken.None);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal(Path.Join(Bin, "flatpak"), call.FileName);
        Assert.Equal(["permission-set", "webextensions", NativeHost.Name, "snap.firefox", "yes"], call.Arguments);
    }

    [LinuxFact]
    public async Task Register_SnapFirefoxStillSucceedsWithoutFlatpak()
    {
        var registrar = CreateRegistrar();
        var snap = new BrowserTarget("firefox", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Snap);

        await registrar.RegisterAsync(snap, CancellationToken.None);

        Assert.Empty(_runner.Calls);
        Assert.Equal(HostRegistration.Registered, registrar.CheckRegistration(snap));
    }

    [LinuxFact]
    public async Task Register_FlatpakCopiesTheHostIntoTheSandbox()
    {
        InstallFlatpak("org.mozilla.firefox");
        var registrar = CreateRegistrar(runtimeDirectory: "/run/user/1000");
        var flatpak = Assert.Single(registrar.DetectBrowsers());
        Assert.Empty(registrar.SandboxEndpoints);

        await registrar.RegisterAsync(flatpak, CancellationToken.None);

        var copy = Path.Join(Home, ".var/app/org.mozilla.firefox/data/juvin/juvin-host");
        var manifestPath = Path.Join(Home, ".var/app/org.mozilla.firefox/.mozilla/native-messaging-hosts", NativeHostManifest.FileName);
        Assert.Equal("self-contained host", await File.ReadAllTextAsync(copy));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(copy));
        Assert.Equal(copy, NativeHostManifest.ReadHostPath(manifestPath));
        Assert.Equal(HostRegistration.Registered, registrar.CheckRegistration(flatpak));
        Assert.Equal([new SandboxEndpoint("firefox-flatpak", "/run/user/1000/app/org.mozilla.firefox/juvin/connector.sock")], registrar.SandboxEndpoints);
        Assert.Empty(_runner.Calls);
    }

    [LinuxFact]
    public async Task CheckRegistration_FlatpakIsStaleWhenTheCopyIsMissingOrDiffers()
    {
        var registrar = CreateRegistrar();
        var flatpak = new BrowserTarget("firefox-flatpak", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Flatpak);
        await registrar.RegisterAsync(flatpak, CancellationToken.None);
        var copy = Path.Join(Home, ".var/app/org.mozilla.firefox/data/juvin/juvin-host");

        await File.WriteAllTextAsync(HostPath, "a newer self-contained host");
        Assert.Equal(HostRegistration.Stale, registrar.CheckRegistration(flatpak));

        await registrar.RegisterAsync(flatpak, CancellationToken.None);
        Assert.Equal(HostRegistration.Registered, registrar.CheckRegistration(flatpak));
        await File.WriteAllTextAsync(HostPath, "A NEWER SELF-CONTAINED HOST");
        File.SetLastWriteTimeUtc(HostPath, File.GetLastWriteTimeUtc(copy).AddSeconds(5));
        Assert.Equal(HostRegistration.Stale, registrar.CheckRegistration(flatpak));

        File.Delete(copy);
        Assert.Equal(HostRegistration.Stale, registrar.CheckRegistration(flatpak));
    }

    [LinuxFact]
    public async Task Unregister_FlatpakRemovesTheManifestAndTheCopy()
    {
        var registrar = CreateRegistrar(runtimeDirectory: "/run/user/1000");
        var flatpak = new BrowserTarget("firefox-flatpak", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Flatpak);
        await registrar.RegisterAsync(flatpak, CancellationToken.None);

        await registrar.UnregisterAsync(flatpak, CancellationToken.None);

        Assert.False(File.Exists(Path.Join(Home, ".var/app/org.mozilla.firefox/data/juvin/juvin-host")));
        Assert.Equal(HostRegistration.NotRegistered, registrar.CheckRegistration(flatpak));
        Assert.Empty(registrar.SandboxEndpoints);
    }

    [LinuxFact]
    public async Task Register_FlatpakRejectsAFrameworkDependentHost()
    {
        await File.WriteAllTextAsync(Path.Join(Path.GetDirectoryName(HostPath)!, "juvin-host.dll"), "");
        var registrar = CreateRegistrar();
        var flatpak = new BrowserTarget("firefox-flatpak", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Flatpak);

        var error = await Assert.ThrowsAsync<NativeHostRegistrationException>(() => registrar.RegisterAsync(flatpak, CancellationToken.None));

        Assert.Contains("self-contained", error.Message);
        Assert.Equal(HostRegistration.NotRegistered, registrar.CheckRegistration(flatpak));
    }

    [LinuxFact]
    public async Task Register_ZenPersistsItsMozillaDirectoryFirst()
    {
        CreateExecutable("flatpak");
        var zen = new BrowserTarget("zen-flatpak", "Zen", BrowserFamily.Firefox, BrowserPackaging.Flatpak);

        await CreateRegistrar().RegisterAsync(zen, CancellationToken.None);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal(["override", "--user", "--persist=.mozilla", "app.zen_browser.zen"], call.Arguments);
        Assert.True(File.Exists(Path.Join(Home, ".var/app/app.zen_browser.zen/.mozilla/native-messaging-hosts", NativeHostManifest.FileName)));
    }

    [LinuxFact]
    public async Task Register_ZenFailsWithoutFlatpak()
    {
        var zen = new BrowserTarget("zen-flatpak", "Zen", BrowserFamily.Firefox, BrowserPackaging.Flatpak);

        await Assert.ThrowsAsync<NativeHostRegistrationException>(() => CreateRegistrar().RegisterAsync(zen, CancellationToken.None));
    }

    [LinuxFact]
    public async Task Register_ThrowsWhenTheHostWasNotFound()
    {
        var paths = new LinuxBrowserPaths(Home, Path.Join(Home, ".config"), null, [Bin], [FlatpakApps]);
        var registrar = new LinuxNativeHostRegistrar(() => null, new Localizer(), paths, _runner, _runsFromAppImage, DataPath, TimeSpan.FromSeconds(30));
        var chrome = new BrowserTarget("chrome", "Google Chrome", BrowserFamily.Chromium, BrowserPackaging.Native);

        await Assert.ThrowsAsync<NativeHostRegistrationException>(() => registrar.RegisterAsync(chrome, CancellationToken.None));
    }

    [LinuxFact]
    public async Task Register_FromAnAppImageCopiesTheHostOutOfTheMount()
    {
        _runsFromAppImage = true;
        var registrar = CreateRegistrar();
        var chrome = new BrowserTarget("chrome", "Google Chrome", BrowserFamily.Chromium, BrowserPackaging.Native);

        await registrar.RegisterAsync(chrome, CancellationToken.None);

        var manifestPath = Path.Join(Home, ".config/google-chrome/NativeMessagingHosts", NativeHostManifest.FileName);
        Assert.Equal(StableHost, NativeHostManifest.ReadHostPath(manifestPath));
        Assert.Equal("self-contained host", await File.ReadAllTextAsync(StableHost));
        Assert.Equal(Executable, File.GetUnixFileMode(StableHost));
        Assert.Equal(HostRegistration.Registered, registrar.CheckRegistration(chrome));

        await File.WriteAllTextAsync(HostPath, "a newer self-contained host");
        Assert.Equal(HostRegistration.Stale, registrar.CheckRegistration(chrome));

        _runsFromAppImage = false;
        Assert.Equal(HostRegistration.Stale, CreateRegistrar().CheckRegistration(chrome));
    }

    [LinuxFact]
    public async Task Unregister_FromAnAppImageKeepsTheCopyUntilNoManifestUsesIt()
    {
        _runsFromAppImage = true;
        var registrar = CreateRegistrar();
        var chrome = new BrowserTarget("chrome", "Google Chrome", BrowserFamily.Chromium, BrowserPackaging.Native);
        var firefox = new BrowserTarget("firefox", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Native);
        await registrar.RegisterAsync(chrome, CancellationToken.None);
        await registrar.RegisterAsync(firefox, CancellationToken.None);

        await registrar.UnregisterAsync(chrome, CancellationToken.None);
        Assert.True(File.Exists(StableHost));

        await registrar.UnregisterAsync(firefox, CancellationToken.None);
        Assert.False(File.Exists(StableHost));
    }

    [LinuxFact]
    public async Task Register_FlatpakFromAnAppImageStillUsesTheSandboxCopy()
    {
        _runsFromAppImage = true;
        var flatpak = new BrowserTarget("firefox-flatpak", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Flatpak);

        await CreateRegistrar().RegisterAsync(flatpak, CancellationToken.None);

        Assert.True(File.Exists(Path.Join(Home, ".var/app/org.mozilla.firefox/data/juvin/juvin-host")));
        Assert.False(File.Exists(StableHost));
    }

    [LinuxFact]
    public async Task Register_AfterSwitchingFromTheAppImageRemovesItsUnusedCopy()
    {
        var chrome = new BrowserTarget("chrome", "Google Chrome", BrowserFamily.Chromium, BrowserPackaging.Native);
        var firefox = new BrowserTarget("firefox", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Native);
        _runsFromAppImage = true;
        await CreateRegistrar().RegisterAsync(chrome, CancellationToken.None);
        await CreateRegistrar().RegisterAsync(firefox, CancellationToken.None);
        _runsFromAppImage = false;

        await CreateRegistrar().RegisterAsync(chrome, CancellationToken.None);
        Assert.True(File.Exists(StableHost));

        await CreateRegistrar().RegisterAsync(firefox, CancellationToken.None);
        Assert.False(File.Exists(StableHost));
    }

    [LinuxFact]
    public async Task Unregister_LeavesAManifestThatNamesAnotherInstallation()
    {
        var chrome = new BrowserTarget("chrome", "Google Chrome", BrowserFamily.Chromium, BrowserPackaging.Native);
        var other = _temp.File("other/juvin-host");
        await CreateRegistrar(other).RegisterAsync(chrome, CancellationToken.None);

        await CreateRegistrar().UnregisterAsync(chrome, CancellationToken.None);

        var manifestPath = Path.Join(Home, ".config/google-chrome/NativeMessagingHosts", NativeHostManifest.FileName);
        Assert.Equal(other, NativeHostManifest.ReadHostPath(manifestPath));
    }

    [LinuxFact]
    public async Task Unregister_FlatpakLeavesAnotherInstallationsManifestAndTheCopy()
    {
        var flatpak = new BrowserTarget("firefox-flatpak", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Flatpak);
        await CreateRegistrar().RegisterAsync(flatpak, CancellationToken.None);
        var manifestPath = Path.Join(Home, ".var/app/org.mozilla.firefox/.mozilla/native-messaging-hosts", NativeHostManifest.FileName);
        await File.WriteAllTextAsync(manifestPath, NativeHostManifest.Render(BrowserFamily.Firefox, "/elsewhere/juvin-host"));

        await CreateRegistrar().UnregisterAsync(flatpak, CancellationToken.None);

        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(Path.Join(Home, ".var/app/org.mozilla.firefox/data/juvin/juvin-host")));
    }

    [LinuxFact]
    public async Task Register_AFlatpakCommandThatHangsTimesOut()
    {
        CreateExecutable("flatpak");
        _runner.NeverCompletes = true;
        var zen = new BrowserTarget("zen-flatpak", "Zen", BrowserFamily.Firefox, BrowserPackaging.Flatpak);

        var error = await Assert.ThrowsAsync<NativeHostRegistrationException>(() =>
            CreateRegistrar(commandTimeout: TimeSpan.FromMilliseconds(50)).RegisterAsync(zen, CancellationToken.None)
        );

        Assert.Contains("did not finish", error.Message);
    }

    [LinuxFact]
    public async Task Register_CancellingAHungFlatpakCommandStaysACancellation()
    {
        CreateExecutable("flatpak");
        _runner.NeverCompletes = true;
        var zen = new BrowserTarget("zen-flatpak", "Zen", BrowserFamily.Firefox, BrowserPackaging.Flatpak);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateRegistrar().RegisterAsync(zen, cancellation.Token));
    }
}
