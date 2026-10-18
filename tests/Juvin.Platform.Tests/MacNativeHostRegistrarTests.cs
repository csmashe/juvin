using System.Runtime.Versioning;
using Juvin.Core.Localization;
using Juvin.Platform.Mac.NativeMessaging;

namespace Juvin.Platform.Tests;

// The registrar is plain file I/O, so these run on every OS even though the assembly targets macOS.
[SupportedOSPlatform("macos")]
public sealed class MacNativeHostRegistrarTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public MacNativeHostRegistrarTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(SystemApplications);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string Home => _temp.File("home");

    private string SystemApplications => _temp.File("Applications");

    private string HostPath => _temp.File("juvin-host");

    private bool _hostExecutable = true;

    private MacNativeHostRegistrar CreateRegistrar(string? hostPath = null) =>
        new(() => hostPath ?? HostPath, new Localizer(), Home, SystemApplications, _ => _hostExecutable);

    private string ChromeManifestPath => Path.Join(Home, "Library", "Application Support", "Google", "Chrome", "NativeMessagingHosts", NativeHostManifest.FileName);

    private static BrowserTarget Chrome => new("chrome", "Google Chrome", BrowserFamily.Chromium, BrowserPackaging.Native);

    [Fact]
    public void DetectBrowsers_FindsSystemAndUserApplications()
    {
        Directory.CreateDirectory(Path.Join(SystemApplications, "Firefox.app"));
        Directory.CreateDirectory(Path.Join(Home, "Applications", "Brave Browser.app"));
        Directory.CreateDirectory(Path.Join(SystemApplications, "Microsoft Edge.app"));

        var found = CreateRegistrar().DetectBrowsers();

        Assert.Equal(["firefox", "brave", "edge"], found.Select(b => b.Id));
        Assert.All(found, b => Assert.Equal(BrowserPackaging.Native, b.Packaging));
        Assert.Empty(CreateRegistrar().SandboxEndpoints);
    }

    [Fact]
    public async Task Register_WritesToApplicationSupportAndRoundTrips()
    {
        var registrar = CreateRegistrar();
        var chrome = new BrowserTarget("chrome", "Google Chrome", BrowserFamily.Chromium, BrowserPackaging.Native);

        await registrar.RegisterAsync(chrome, CancellationToken.None);

        var manifestPath = Path.Join(Home, "Library", "Application Support", "Google", "Chrome", "NativeMessagingHosts", NativeHostManifest.FileName);
        Assert.Equal(NativeHostManifest.Render(BrowserFamily.Chromium, HostPath), await File.ReadAllTextAsync(manifestPath));
        Assert.Equal(HostRegistration.Registered, registrar.CheckRegistration(chrome));
        Assert.Equal(HostRegistration.Stale, CreateRegistrar(_temp.File("other")).CheckRegistration(chrome));

        await registrar.UnregisterAsync(chrome, CancellationToken.None);

        Assert.Equal(HostRegistration.NotRegistered, registrar.CheckRegistration(chrome));
    }

    [Fact]
    public async Task Register_FirefoxUsesTheMozillaFolder()
    {
        var firefox = new BrowserTarget("firefox", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Native);

        await CreateRegistrar().RegisterAsync(firefox, CancellationToken.None);

        Assert.True(File.Exists(Path.Join(Home, "Library", "Application Support", "Mozilla", "NativeMessagingHosts", NativeHostManifest.FileName)));
    }

    [Fact]
    public async Task Register_ThrowsWhenTheHostWasNotFound()
    {
        var registrar = new MacNativeHostRegistrar(() => null, new Localizer(), Home, SystemApplications, _ => true);
        var edge = new BrowserTarget("edge", "Microsoft Edge", BrowserFamily.Chromium, BrowserPackaging.Native);

        await Assert.ThrowsAsync<NativeHostRegistrationException>(() => registrar.RegisterAsync(edge, CancellationToken.None));
    }

    [Fact]
    public async Task Unregister_LeavesAManifestThatNamesAnotherInstallation()
    {
        await CreateRegistrar(_temp.File("other")).RegisterAsync(Chrome, CancellationToken.None);

        await CreateRegistrar().UnregisterAsync(Chrome, CancellationToken.None);

        Assert.Equal(_temp.File("other"), NativeHostManifest.ReadHostPath(ChromeManifestPath));
    }

    [Fact]
    public async Task Register_RefusesATranslocatedApp()
    {
        const string translocated = "/private/var/folders/xy/T/AppTranslocation/1A2B/d/Juvin.app/Contents/MacOS/juvin-host";
        var registrar = CreateRegistrar(translocated);

        var error = await Assert.ThrowsAsync<NativeHostRegistrationException>(() => registrar.RegisterAsync(Chrome, CancellationToken.None));

        Assert.Contains("Applications", error.Message);
        Assert.False(File.Exists(ChromeManifestPath));
    }

    [Fact]
    public async Task Register_RefusesAHostThatCannotBeRun()
    {
        _hostExecutable = false;

        var error = await Assert.ThrowsAsync<NativeHostRegistrationException>(() => CreateRegistrar().RegisterAsync(Chrome, CancellationToken.None));

        Assert.Equal(new Localizer()["BrowserConnector.HostNotExecutable"], error.Message);
        Assert.False(File.Exists(ChromeManifestPath));
    }

    [Fact]
    public async Task CheckRegistration_IsStaleOnceTheHostCannotBeRun()
    {
        await CreateRegistrar().RegisterAsync(Chrome, CancellationToken.None);
        _hostExecutable = false;

        Assert.Equal(HostRegistration.Stale, CreateRegistrar().CheckRegistration(Chrome));
    }
}
