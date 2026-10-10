using System.ComponentModel;
using System.Diagnostics;
using Juvin.Core.Providers;
using Juvin.Core.Services;
using Juvin.Platform.Unix.Ipc;

namespace Juvin.Platform.Linux.NativeMessaging;

/// <summary>
///     Registers the host per browser packaging: native and Snap Firefox share
///     <c>~/.mozilla</c> (Snap reaches it through the WebExtensions portal), each Flatpak browser
///     gets a manifest and a copy of the host inside its own sandbox home, and Chromium browsers
///     use their user data directory.
/// </summary>
public sealed class LinuxNativeHostRegistrar : INativeHostRegistrar
{
    private const string ZenFlatpakId = "app.zen_browser.zen";

    private const UnixFileMode OwnerOnlyMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Func<string?> _locateHost;
    private readonly LinuxBrowserPaths _paths;
    private readonly IProcessRunner _processRunner;
    private readonly IReadOnlyList<LinuxBrowser> _browsers;

    public LinuxNativeHostRegistrar(Func<string?> locateHost)
        : this(locateHost, LinuxBrowserPaths.FromEnvironment(), new ProcessRunner())
    {
    }

    /// <param name="locateHost">Resolved on every call, so a host that appears after startup is picked up without a restart.</param>
    /// <param name="paths">The directories detection and registration read and write.</param>
    /// <param name="processRunner">Runs <c>flatpak</c> for the sandbox setup some browsers need.</param>
    internal LinuxNativeHostRegistrar(Func<string?> locateHost, LinuxBrowserPaths paths, IProcessRunner processRunner)
    {
        _locateHost = locateHost;
        _paths = paths;
        _processRunner = processRunner;

        var home = paths.Home;
        var config = paths.ConfigHome;
        _browsers =
        [
            new LinuxBrowser(
                "firefox",
                "Firefox",
                BrowserFamily.Firefox,
                Path.Join(home, ".mozilla", "native-messaging-hosts"),
                FlatpakId: null,
                Executables: ["firefox"],
                SnapName: "firefox"
            ),
            Flatpak("firefox-flatpak", "Firefox", "org.mozilla.firefox"),
            Flatpak("zen-flatpak", "Zen", ZenFlatpakId),
            Chromium("chrome", "Google Chrome", Path.Join(config, "google-chrome"), ["google-chrome", "google-chrome-stable"]),
            Chromium("chromium", "Chromium", Path.Join(config, "chromium"), ["chromium", "chromium-browser"], "chromium"),
            Chromium("brave", "Brave", Path.Join(config, "BraveSoftware", "Brave-Browser"), ["brave-browser", "brave"], "brave"),
            Chromium("edge", "Microsoft Edge", Path.Join(config, "microsoft-edge"), ["microsoft-edge", "microsoft-edge-stable"]),
        ];
    }

    public IReadOnlyList<SandboxEndpoint> SandboxEndpoints =>
    [
        .. _browsers
            .Where(b => b.FlatpakId is not null && File.Exists(ManifestPath(b)))
            .Select(b => (b.Id, Address: SocketPathResolver.FlatpakConnectorPath(b.FlatpakId, _paths.RuntimeDirectory)))
            .Where(e => e.Address is not null)
            .Select(e => new SandboxEndpoint(e.Id, e.Address!)),
    ];

    public IReadOnlyList<BrowserTarget> DetectBrowsers()
    {
        return [.. _browsers.Select(Detect).OfType<BrowserTarget>()];
    }

    /// <summary>An executable on the path, or an installed Flatpak. A leftover profile directory is not an install.</summary>
    private BrowserTarget? Detect(LinuxBrowser browser)
    {
        if (browser.FlatpakId is not null)
        {
            return _paths.IsFlatpakInstalled(browser.FlatpakId) ? browser.Target : null;
        }

        var executable = browser.Executables.Select(_paths.FindExecutable).FirstOrDefault(found => found is not null);
        if (executable is null)
        {
            return null;
        }

        return browser.SnapName is not null && _paths.IsSnap(executable, browser.SnapName)
            ? browser.Target with { Packaging = BrowserPackaging.Snap }
            : browser.Target;
    }

    public HostRegistration CheckRegistration(BrowserTarget browser)
    {
        var definition = Find(browser);
        var hostPath = _locateHost();
        if (definition.FlatpakId is null)
        {
            return NativeHostManifest.Check(ManifestPath(definition), hostPath);
        }

        var sandboxHost = SandboxHostPath(definition);
        var state = NativeHostManifest.Check(ManifestPath(definition), hostPath is null ? null : sandboxHost);
        return state == HostRegistration.Registered && !IsCurrentCopy(sandboxHost, hostPath!)
            ? HostRegistration.Stale
            : state;
    }

    public async Task RegisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        var hostPath = NativeHostManifest.RequireHost(_locateHost());
        var definition = Find(browser);
        if (browser.Packaging == BrowserPackaging.Snap && definition.Family == BrowserFamily.Chromium)
        {
            // Only Firefox's Snap launches hosts through the WebExtensions portal; a confined Chromium cannot start one.
            throw new NativeHostRegistrationException(
                $"The Snap version of {definition.DisplayName} cannot start the Juvin browser connector. Install {definition.DisplayName} from its vendor package instead."
            );
        }

        try
        {
            var manifestHost = hostPath;
            if (definition.FlatpakId is not null)
            {
                manifestHost = await PrepareSandboxAsync(definition, hostPath, ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(definition.ManifestDirectory);
            AtomicFile.WriteAllText(ManifestPath(definition), NativeHostManifest.Render(definition.Family, manifestHost));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NativeHostRegistrationException($"Could not write the connector files for {definition.DisplayName}: {ex.Message}", ex);
        }

        if (browser.Packaging == BrowserPackaging.Snap)
        {
            await PreApprovePortalAsync(ct).ConfigureAwait(false);
        }

        Trace.WriteLine($"[LinuxNativeHostRegistrar] Registered {definition.Id}.");
    }

    public Task UnregisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        var definition = Find(browser);
        try
        {
            File.Delete(ManifestPath(definition));
            if (definition.FlatpakId is not null)
            {
                File.Delete(SandboxHostPath(definition));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NativeHostRegistrationException($"Could not remove the connector files for {definition.DisplayName}: {ex.Message}", ex);
        }

        Trace.WriteLine($"[LinuxNativeHostRegistrar] Unregistered {definition.Id}.");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     A Flatpak browser starts the host inside its sandbox, which cannot run files from the
    ///     host file system, so the host is copied into the browser's own data directory.
    /// </summary>
    private async Task<string> PrepareSandboxAsync(LinuxBrowser definition, string hostPath, CancellationToken ct)
    {
        if (IsFrameworkDependent(hostPath))
        {
            throw new NativeHostRegistrationException(
                $"{definition.DisplayName} runs in a Flatpak sandbox, which needs a self-contained build of the Juvin browser connector. This build of Juvin cannot connect to it."
            );
        }

        if (definition.FlatpakId == ZenFlatpakId)
        {
            // Zen's Flatpak only persists ~/.zen; without this the manifest directory is not visible inside the sandbox.
            await RunFlatpakAsync(["override", "--user", "--persist=.mozilla", ZenFlatpakId], required: true, ct)
                .ConfigureAwait(false);
        }

        var target = SandboxHostPath(definition);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory, OwnerOnlyMode);
        // Copy beside the target and rename over it: a running host keeps its old inode instead of failing with "text file busy".
        var temp = Path.Join(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(hostPath, temp, overwrite: true);
            File.SetUnixFileMode(temp, OwnerOnlyMode);
            File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(hostPath));
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        return target;
    }

    /// <summary>
    ///     Stores the approval the WebExtensions portal would otherwise ask for on first use, a
    ///     dialog that only appears when the browser has focus. Registering is the user's consent.
    /// </summary>
    private Task PreApprovePortalAsync(CancellationToken ct)
    {
        return RunFlatpakAsync(["permission-set", "webextensions", NativeHost.Name, "snap.firefox", "yes"], required: false, ct);
    }

    private async Task RunFlatpakAsync(IReadOnlyList<string> arguments, bool required, CancellationToken ct)
    {
        var flatpak = _paths.FindExecutable("flatpak");
        if (flatpak is null)
        {
            Trace.WriteLine($"[LinuxNativeHostRegistrar] flatpak not found; skipped flatpak {arguments[0]}.");
            if (required)
            {
                throw new NativeHostRegistrationException("The flatpak command was not found, so the browser's sandbox could not be configured.");
            }

            return;
        }

        int exitCode;
        try
        {
            exitCode = (await _processRunner.RunAsync(flatpak, arguments, null, null, ct).ConfigureAwait(false)).ExitCode;
        }
        catch (Win32Exception ex)
        {
            Trace.WriteLine($"[LinuxNativeHostRegistrar] flatpak {arguments[0]} could not start: {ex.NativeErrorCode}.");
            exitCode = -1;
        }

        if (exitCode == 0)
        {
            return;
        }

        Trace.WriteLine($"[LinuxNativeHostRegistrar] flatpak {arguments[0]} exited with {exitCode}.");
        if (required)
        {
            throw new NativeHostRegistrationException($"Configuring the browser's Flatpak sandbox failed (flatpak exit code {exitCode}).");
        }
    }

    private static bool IsFrameworkDependent(string hostPath)
    {
        var directory = Path.GetDirectoryName(hostPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(hostPath);
        return File.Exists(Path.Join(directory, stem + ".dll"))
            || File.Exists(Path.Join(directory, stem + ".runtimeconfig.json"));
    }

    /// <summary>
    ///     The copy carries the source's timestamp, so a rebuilt host of the same size still shows as
    ///     stale. The copy is never partial: it is written beside the target and renamed over it.
    /// </summary>
    private static bool IsCurrentCopy(string copy, string source)
    {
        try
        {
            return new FileInfo(copy) is { Exists: true } copyInfo
                && new FileInfo(source) is { Exists: true } sourceInfo
                && copyInfo.Length == sourceInfo.Length
                && copyInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ManifestPath(LinuxBrowser browser) => Path.Join(browser.ManifestDirectory, NativeHostManifest.FileName);

    private string SandboxHostPath(LinuxBrowser browser) =>
        Path.Join(FlatpakAppDirectory(browser.FlatpakId!), "data", "juvin", "juvin-host");

    private string FlatpakAppDirectory(string flatpakId) => Path.Join(_paths.Home, ".var", "app", flatpakId);

    private LinuxBrowser Find(BrowserTarget browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        return _browsers.FirstOrDefault(b => b.Id == browser.Id)
            ?? throw new ArgumentException($"Unknown browser '{browser.Id}'.", nameof(browser));
    }

    private LinuxBrowser Flatpak(string id, string displayName, string flatpakId)
    {
        var appDirectory = FlatpakAppDirectory(flatpakId);
        return new LinuxBrowser(
            id,
            displayName,
            BrowserFamily.Firefox,
            Path.Join(appDirectory, ".mozilla", "native-messaging-hosts"),
            flatpakId,
            Executables: [],
            SnapName: null
        );
    }

    private static LinuxBrowser Chromium(
        string id,
        string displayName,
        string userDataDirectory,
        IReadOnlyList<string> executables,
        string? snapName = null
    )
    {
        return new LinuxBrowser(
            id,
            displayName,
            BrowserFamily.Chromium,
            Path.Join(userDataDirectory, "NativeMessagingHosts"),
            FlatpakId: null,
            executables,
            snapName
        );
    }

    /// <param name="SnapName">The browser's Snap, when it has one; a Snap is detected by its launcher or data directory.</param>
    private sealed record LinuxBrowser(
        string Id,
        string DisplayName,
        BrowserFamily Family,
        string ManifestDirectory,
        string? FlatpakId,
        IReadOnlyList<string> Executables,
        string? SnapName
    )
    {
        public BrowserTarget Target =>
            new(Id, DisplayName, Family, FlatpakId is null ? BrowserPackaging.Native : BrowserPackaging.Flatpak);
    }
}
