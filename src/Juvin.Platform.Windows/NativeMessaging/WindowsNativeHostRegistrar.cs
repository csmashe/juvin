using System.Diagnostics;
using System.Security;
using Juvin.Core;
using Juvin.Core.Services;
using Microsoft.Win32;

namespace Juvin.Platform.Windows.NativeMessaging;

/// <summary>
///     Writes one Gecko and one Chromium manifest into the app's data folder and points a per-user
///     registry key per browser at the matching one. Chromium browsers share their manifest.
/// </summary>
public sealed class WindowsNativeHostRegistrar : INativeHostRegistrar
{
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    private static readonly IReadOnlyList<WindowsBrowser> s_browsers =
    [
        Browser("firefox", "Firefox", BrowserFamily.Firefox, "firefox.exe", @"Software\Mozilla\NativeMessagingHosts"),
        Browser("chrome", "Google Chrome", BrowserFamily.Chromium, "chrome.exe", @"Software\Google\Chrome\NativeMessagingHosts"),
        Browser("edge", "Microsoft Edge", BrowserFamily.Chromium, "msedge.exe", @"Software\Microsoft\Edge\NativeMessagingHosts"),
        Browser("brave", "Brave", BrowserFamily.Chromium, "brave.exe", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts"),
    ];

    private readonly Func<string?> _locateHost;
    private readonly string _manifestDirectory;
    private readonly RegistryKey _userRoot;
    private readonly RegistryKey? _machineRoot;

    public WindowsNativeHostRegistrar(Func<string?> locateHost)
        : this(locateHost, Path.Join(JuvinEnvironment.DataPath, "NativeHost"), Registry.CurrentUser, Registry.LocalMachine)
    {
    }

    /// <param name="locateHost">Resolved on every call, so a host that appears after startup is picked up without a restart.</param>
    /// <param name="manifestDirectory">Where the two manifest files are written.</param>
    /// <param name="userRoot">Stands in for <c>HKEY_CURRENT_USER</c>; registration keys and per-user App Paths live under it.</param>
    /// <param name="machineRoot">Stands in for <c>HKEY_LOCAL_MACHINE</c>; only read for App Paths.</param>
    internal WindowsNativeHostRegistrar(Func<string?> locateHost, string manifestDirectory, RegistryKey userRoot, RegistryKey? machineRoot)
    {
        _locateHost = locateHost;
        _manifestDirectory = manifestDirectory;
        _userRoot = userRoot;
        _machineRoot = machineRoot;
    }

    public IReadOnlyList<SandboxEndpoint> SandboxEndpoints => [];

    public IReadOnlyList<BrowserTarget> DetectBrowsers()
    {
        return [.. s_browsers.Where(b => HasAppPath(_userRoot, b.Executable) || HasAppPath(_machineRoot, b.Executable)).Select(b => b.Target)];
    }

    public HostRegistration CheckRegistration(BrowserTarget browser)
    {
        var definition = Find(browser);
        var registered = ReadRegisteredManifest(definition);
        if (registered is null)
        {
            return HostRegistration.NotRegistered;
        }

        var manifestPath = ManifestPath(definition.Target.Family);
        if (!string.Equals(registered, manifestPath, StringComparison.OrdinalIgnoreCase))
        {
            return HostRegistration.Stale;
        }

        // The key without its file is a broken registration, not a missing one.
        return NativeHostManifest.Check(manifestPath, definition.Target.Family, _locateHost()) is HostRegistration.Registered
            ? HostRegistration.Registered
            : HostRegistration.Stale;
    }

    public Task RegisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        var hostPath = NativeHostManifest.RequireHost(_locateHost());
        var definition = Find(browser);
        var manifestPath = ManifestPath(definition.Target.Family);
        try
        {
            Directory.CreateDirectory(_manifestDirectory);
            AtomicFile.WriteAllText(manifestPath, NativeHostManifest.Render(definition.Target.Family, hostPath));
            using var key = _userRoot.CreateSubKey(HostKey(definition));
            key.SetValue(null, manifestPath, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new NativeHostRegistrationException($"Could not register the connector for {definition.Target.DisplayName}: {ex.Message}", ex);
        }

        Trace.WriteLine($"[WindowsNativeHostRegistrar] Registered {definition.Target.Id}.");
        return Task.CompletedTask;
    }

    /// <remarks>A key whose manifest names another installation's host is left in place.</remarks>
    public Task UnregisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        var definition = Find(browser);
        var manifestPath = ManifestPath(definition.Target.Family);
        if (ReadRegisteredManifest(definition) is not { } registered)
        {
            return Task.CompletedTask;
        }

        // Another installation of the same user shares the manifest location, so only the host it
        // names proves ownership; a broken manifest there works for nobody and may go.
        var hostPath = _locateHost();
        var registeredHost = NativeHostManifest.ReadHostPath(registered);
        var ours = registeredHost is null
            ? string.Equals(registered, manifestPath, StringComparison.OrdinalIgnoreCase)
            : hostPath is not null && string.Equals(registeredHost, hostPath, StringComparison.OrdinalIgnoreCase);
        if (!ours)
        {
            Trace.WriteLine($"[WindowsNativeHostRegistrar] Left {definition.Target.Id}: it is registered to another installation.");
            return Task.CompletedTask;
        }

        try
        {
            _userRoot.DeleteSubKeyTree(HostKey(definition), throwOnMissingSubKey: false);

            var stillUsed = s_browsers.Any(b =>
                b.Target.Family == definition.Target.Family
                && string.Equals(ReadRegisteredManifest(b), manifestPath, StringComparison.OrdinalIgnoreCase)
            );
            if (!stillUsed)
            {
                File.Delete(manifestPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new NativeHostRegistrationException($"Could not remove the connector for {definition.Target.DisplayName}: {ex.Message}", ex);
        }

        Trace.WriteLine($"[WindowsNativeHostRegistrar] Unregistered {definition.Target.Id}.");
        return Task.CompletedTask;
    }

    private string ManifestPath(BrowserFamily family) =>
        Path.Join(_manifestDirectory, family == BrowserFamily.Firefox ? NativeHost.Name + ".firefox.json" : NativeHost.Name + ".chromium.json");

    private static string HostKey(WindowsBrowser browser) => browser.RegistryParent + @"\" + NativeHost.Name;

    private string? ReadRegisteredManifest(WindowsBrowser browser)
    {
        using var key = _userRoot.OpenSubKey(HostKey(browser));
        return key?.GetValue(null) as string;
    }

    private static bool HasAppPath(RegistryKey? root, string executable)
    {
        if (root is null)
        {
            return false;
        }

        try
        {
            using var key = root.OpenSubKey(AppPathsKey + @"\" + executable);
            return key is not null;
        }
        catch (SecurityException)
        {
            return false;
        }
    }

    private static WindowsBrowser Find(BrowserTarget browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        return s_browsers.FirstOrDefault(b => b.Target.Id == browser.Id)
            ?? throw new ArgumentException($"Unknown browser '{browser.Id}'.", nameof(browser));
    }

    private static WindowsBrowser Browser(string id, string displayName, BrowserFamily family, string executable, string registryParent)
    {
        return new WindowsBrowser(new BrowserTarget(id, displayName, family, BrowserPackaging.Native), executable, registryParent);
    }

    private sealed record WindowsBrowser(BrowserTarget Target, string Executable, string RegistryParent);
}
