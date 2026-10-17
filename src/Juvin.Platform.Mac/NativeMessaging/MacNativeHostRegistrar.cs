using System.Diagnostics;
using Juvin.Core.Services;

namespace Juvin.Platform.Mac.NativeMessaging;

/// <summary>
///     Writes the host manifest into each browser's per-user <c>NativeMessagingHosts</c> folder
///     under <c>~/Library/Application Support</c>. macOS browsers have no sandbox that needs extra setup.
/// </summary>
public sealed class MacNativeHostRegistrar : INativeHostRegistrar
{
    private readonly Func<string?> _locateHost;
    private readonly string _home;
    private readonly string _systemApplications;
    private readonly IReadOnlyList<MacBrowser> _browsers;

    public MacNativeHostRegistrar(Func<string?> locateHost)
        : this(locateHost, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "/Applications")
    {
    }

    internal MacNativeHostRegistrar(Func<string?> locateHost, string home, string systemApplications)
    {
        _locateHost = locateHost;
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
        return NativeHostManifest.Check(definition.ManifestPath, definition.Target.Family, _locateHost());
    }

    public Task RegisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        var hostPath = NativeHostManifest.RequireHost(_locateHost());
        var definition = Find(browser);
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

    public Task UnregisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        var definition = Find(browser);
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
