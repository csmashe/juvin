using System.Runtime.Versioning;
using Juvin.Platform.Windows.NativeMessaging;
using Microsoft.Win32;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsNativeHostRegistrarTests : IDisposable
{
    private readonly string _rootName = @"Software\JuvinTests\" + Guid.NewGuid().ToString("N");
    private readonly TempDirectory _temp = new();
    private RegistryKey? _userRoot;
    private RegistryKey? _machineRoot;

    public void Dispose()
    {
        _userRoot?.Dispose();
        _machineRoot?.Dispose();
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
        }

        _temp.Dispose();
    }

    private string HostPath => _temp.File("juvin-host.exe");

    private string ManifestDirectory => _temp.File("NativeHost");

    private WindowsNativeHostRegistrar CreateRegistrar(string? hostPath = null)
    {
        _userRoot ??= Registry.CurrentUser.CreateSubKey(_rootName + @"\user");
        _machineRoot ??= Registry.CurrentUser.CreateSubKey(_rootName + @"\machine");
        return new WindowsNativeHostRegistrar(() => hostPath ?? HostPath, ManifestDirectory, _userRoot, _machineRoot);
    }

    private static BrowserTarget Chromium(string id, string name) => new(id, name, BrowserFamily.Chromium, BrowserPackaging.Native);

    [WindowsFact]
    public void DetectBrowsers_ReadsUserAndMachineAppPaths()
    {
        var registrar = CreateRegistrar();
        _userRoot!.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe").Dispose();
        _machineRoot!.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe").Dispose();

        Assert.Equal(["firefox", "edge"], registrar.DetectBrowsers().Select(b => b.Id));
        Assert.Empty(registrar.SandboxEndpoints);
    }

    [WindowsFact]
    public async Task Register_WritesTheManifestAndTheKey()
    {
        var registrar = CreateRegistrar();
        var firefox = new BrowserTarget("firefox", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Native);
        Assert.Equal(HostRegistration.NotRegistered, registrar.CheckRegistration(firefox));

        await registrar.RegisterAsync(firefox, CancellationToken.None);

        var manifestPath = Path.Join(ManifestDirectory, "com.juvin.host.firefox.json");
        using var key = _userRoot!.OpenSubKey(@"Software\Mozilla\NativeMessagingHosts\com.juvin.host");
        Assert.Equal(manifestPath, key?.GetValue(null));
        Assert.Equal(HostPath, NativeHostManifest.ReadHostPath(manifestPath));
        Assert.Equal(HostRegistration.Registered, registrar.CheckRegistration(firefox));
        Assert.Equal(HostRegistration.Stale, CreateRegistrar(_temp.File("other.exe")).CheckRegistration(firefox));
    }

    [WindowsFact]
    public async Task Unregister_KeepsTheSharedChromiumManifestWhileAnotherBrowserUsesIt()
    {
        var registrar = CreateRegistrar();
        var chrome = Chromium("chrome", "Google Chrome");
        var edge = Chromium("edge", "Microsoft Edge");
        await registrar.RegisterAsync(chrome, CancellationToken.None);
        await registrar.RegisterAsync(edge, CancellationToken.None);
        var manifestPath = Path.Join(ManifestDirectory, "com.juvin.host.chromium.json");

        await registrar.UnregisterAsync(chrome, CancellationToken.None);

        Assert.Equal(HostRegistration.NotRegistered, registrar.CheckRegistration(chrome));
        Assert.Equal(HostRegistration.Registered, registrar.CheckRegistration(edge));
        Assert.True(File.Exists(manifestPath));

        await registrar.UnregisterAsync(edge, CancellationToken.None);

        Assert.False(File.Exists(manifestPath));
        Assert.Null(_userRoot!.OpenSubKey(@"Software\Microsoft\Edge\NativeMessagingHosts\com.juvin.host"));
    }

    [WindowsFact]
    public async Task Unregister_LeavesARegistrationThatNamesAnotherInstallation()
    {
        var firefox = new BrowserTarget("firefox", "Firefox", BrowserFamily.Firefox, BrowserPackaging.Native);
        await CreateRegistrar().RegisterAsync(firefox, CancellationToken.None);

        await CreateRegistrar(_temp.File("other.exe")).UnregisterAsync(firefox, CancellationToken.None);

        Assert.Equal(HostRegistration.Registered, CreateRegistrar().CheckRegistration(firefox));
        Assert.True(File.Exists(Path.Join(ManifestDirectory, "com.juvin.host.firefox.json")));
    }

    [WindowsFact]
    public async Task Register_ThrowsWhenTheHostWasNotFound()
    {
        var registrar = new WindowsNativeHostRegistrar(() => null, ManifestDirectory, Registry.CurrentUser, null);

        await Assert.ThrowsAsync<NativeHostRegistrationException>(() => registrar.RegisterAsync(Chromium("brave", "Brave"), CancellationToken.None));
    }
}
