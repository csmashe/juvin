using System.Diagnostics;
using Juvin.Core.Localization;
using Juvin.Core.Services;
using Juvin.Platform.Unix;

namespace Juvin.Platform.Mac.NativeMessaging;

/// <summary>
///     Writes the host manifest into each browser's per-user <c>NativeMessagingHosts</c> folder
///     under <c>~/Library/Application Support</c>. macOS browsers have no sandbox that needs extra setup.
/// </summary>
public sealed class MacNativeHostRegistrar : INativeHostRegistrar
{
    private readonly Func<string?> _locateHost;
    private readonly Func<string, bool> _canExecute;
    private readonly Localizer _localizer;
    private readonly string _home;
    private readonly string _systemApplications;
    private readonly IReadOnlyList<MacBrowser> _browsers;

    public MacNativeHostRegistrar(Func<string?> locateHost, Localizer localizer)
        : this(
            locateHost,
            localizer,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "/Applications",
            UnixFileAccess.CanExecute
        )
    {
    }

    internal MacNativeHostRegistrar(
        Func<string?> locateHost,
        Localizer localizer,
        string home,
        string systemApplications,
        Func<string, bool> canExecute
    )
    {
        _locateHost = locateHost;
        _canExecute = canExecute;
        _localizer = localizer;
        _home = home;
        _systemApplications = systemApplications;

        var support = Path.Join(home, "Library", "Application Support");
        _browsers =
        [
            Browser("firefox", "Firefox", BrowserFamily.Firefox, "Firefox.app", Path.Join(support, "Mozilla")),
            Browser("chrome", "Google Chrome", BrowserFamily.Chromium, "Google Chrome.app", Path.Join(support, "Google", "Chrome")),
            Browser("chromium", "Chromium", BrowserFamily.Chromium, "Chromium.app", Path.Join(support, "Chromium")),
            Browser("brave", "Brave", BrowserFamily.Chromium, "Brave Browser.app", Path.Join(support, "BraveSoftware", "Brave-Browser")),
            Browser("edge", "Microsoft Edge", BrowserFamily.Chromium, "Microsoft Edge.app", Path.Join(support, "Microsoft Edge")),
        ];
    }

    public IReadOnlyList<SandboxEndpoint> SandboxEndpoints => [];

    public IReadOnlyList<BrowserTarget> DetectBrowsers()
    {
        return
        [
            .. _browsers
                .Where(b =>
                    Directory.Exists(Path.Join(_systemApplications, b.Bundle))
                    || Directory.Exists(Path.Join(_home, "Applications", b.Bundle))
                )
                .Select(b => b.Target),
        ];
    }

    public HostRegistration CheckRegistration(BrowserTarget browser)
    {
        var definition = Find(browser);
        var hostPath = _locateHost();
        var state = NativeHostManifest.Check(definition.ManifestPath, definition.Target.Family, hostPath);
        return state == HostRegistration.Registered && !_canExecute(hostPath!) ? HostRegistration.Stale : state;
    }

    public Task RegisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        var hostPath = NativeHostManifest.RequireHost(_locateHost());
        var definition = Find(browser);
        // Gatekeeper runs an app that was never moved from a random read-only path that is gone on the next launch.
        if (hostPath.Contains("/AppTranslocation/", StringComparison.Ordinal))
        {
            throw new NativeHostRegistrationException(_localizer["BrowserConnector.RegisterTranslocated"]);
        }

        if (!_canExecute(hostPath))
        {
            throw new NativeHostRegistrationException(_localizer["BrowserConnector.HostNotExecutable"]);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(definition.ManifestPath)!);
            AtomicFile.WriteAllText(definition.ManifestPath, NativeHostManifest.Render(definition.Target.Family, hostPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NativeHostRegistrationException($"Could not write the connector files for {definition.Target.DisplayName}: {ex.Message}", ex);
        }

        Trace.WriteLine($"[MacNativeHostRegistrar] Registered {definition.Target.Id}.");
        return Task.CompletedTask;
    }

    /// <remarks>A manifest that names another installation's host is left in place.</remarks>
    public Task UnregisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        var definition = Find(browser);
        var registered = NativeHostManifest.ReadHostPath(definition.ManifestPath);
        if (registered is null)
        {
            return Task.CompletedTask;
        }

        if (registered != _locateHost())
        {
            Trace.WriteLine($"[MacNativeHostRegistrar] Left {definition.Target.Id}: it is registered to another installation.");
            return Task.CompletedTask;
        }

        try
        {
            File.Delete(definition.ManifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NativeHostRegistrationException($"Could not remove the connector files for {definition.Target.DisplayName}: {ex.Message}", ex);
        }

        Trace.WriteLine($"[MacNativeHostRegistrar] Unregistered {definition.Target.Id}.");
        return Task.CompletedTask;
    }

    private MacBrowser Find(BrowserTarget browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        return _browsers.FirstOrDefault(b => b.Target.Id == browser.Id)
            ?? throw new ArgumentException($"Unknown browser '{browser.Id}'.", nameof(browser));
    }

    private static MacBrowser Browser(string id, string displayName, BrowserFamily family, string bundle, string supportDirectory)
    {
        return new MacBrowser(
            new BrowserTarget(id, displayName, family, BrowserPackaging.Native),
            bundle,
            Path.Join(supportDirectory, "NativeMessagingHosts", NativeHostManifest.FileName)
        );
    }

    private sealed record MacBrowser(BrowserTarget Target, string Bundle, string ManifestPath);
}
