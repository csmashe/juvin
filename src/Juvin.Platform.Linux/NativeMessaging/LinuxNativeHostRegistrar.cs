using System.ComponentModel;
using System.Diagnostics;
using Juvin.Core;
using Juvin.Core.Localization;
using Juvin.Core.Providers;
using Juvin.Core.Services;
using Juvin.Platform.Unix;
using Juvin.Platform.Unix.Ipc;

namespace Juvin.Platform.Linux.NativeMessaging;

/// <summary>
///     Registers the host per browser packaging: native and Snap Firefox share
///     <c>~/.mozilla</c> (Snap reaches it through the WebExtensions portal), each Flatpak browser
///     gets a manifest and a copy of the host inside its own sandbox home, and Chromium browsers
///     use their user data directory. Run from an AppImage, whose mount moves on every launch, the
///     other browsers get a copy of the host in Juvin's data directory.
/// </summary>
public sealed class LinuxNativeHostRegistrar : INativeHostRegistrar
{
    private const string ZenFlatpakId = "app.zen_browser.zen";

    private const UnixFileMode OwnerOnlyMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Func<string?> _locateHost;
    private readonly Localizer _localizer;
    private readonly TimeSpan _commandTimeout;
    private readonly bool _runsFromAppImage;
    private readonly string _stableHostPath;
    private readonly LinuxBrowserPaths _paths;
    private readonly IProcessRunner _processRunner;
    private readonly IReadOnlyList<LinuxBrowser> _browsers;

    public LinuxNativeHostRegistrar(Func<string?> locateHost, Localizer localizer)
        : this(
            locateHost,
            localizer,
            LinuxBrowserPaths.FromEnvironment(),
            new ProcessRunner(),
            LinuxProcessPath.RunsFromAppImage(Environment.GetEnvironmentVariable, Environment.ProcessPath),
            JuvinEnvironment.DataPath,
            TimeSpan.FromSeconds(30)
        )
    {
    }

    /// <param name="locateHost">Resolved on every call, so a host that appears after startup is picked up without a restart.</param>
    /// <param name="localizer">Supplies the user-readable registration errors.</param>
    /// <param name="paths">The directories detection and registration read and write.</param>
    /// <param name="processRunner">Runs <c>flatpak</c> for the sandbox setup some browsers need.</param>
    /// <param name="runsFromAppImage">Whether the host sits in an AppImage mount, so manifests need a copy of it.</param>
    /// <param name="dataPath">Where the host copy for an AppImage goes.</param>
    /// <param name="commandTimeout">How long each <c>flatpak</c> command may run.</param>
    internal LinuxNativeHostRegistrar(
        Func<string?> locateHost,
        Localizer localizer,
        LinuxBrowserPaths paths,
        IProcessRunner processRunner,
        bool runsFromAppImage,
        string dataPath,
        TimeSpan commandTimeout
    )
    {
        _locateHost = locateHost;
        _localizer = localizer;
        _commandTimeout = commandTimeout;
        _runsFromAppImage = runsFromAppImage;
        _stableHostPath = Path.Join(dataPath, "juvin-host");
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
                HasSnap: true
            ),
            Flatpak("firefox-flatpak", "Firefox", "org.mozilla.firefox"),
            Flatpak("zen-flatpak", "Zen", ZenFlatpakId),
            Chromium("chrome", "Google Chrome", Path.Join(config, "google-chrome"), ["google-chrome", "google-chrome-stable"]),
            Chromium("chromium", "Chromium", Path.Join(config, "chromium"), ["chromium", "chromium-browser"], hasSnap: true),
            Chromium("brave", "Brave", Path.Join(config, "BraveSoftware", "Brave-Browser"), ["brave-browser", "brave"], hasSnap: true),
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

        return browser.HasSnap && _paths.IsSnap(executable)
            ? browser.Target with { Packaging = BrowserPackaging.Snap }
            : browser.Target;
    }

    public HostRegistration CheckRegistration(BrowserTarget browser)
    {
        var definition = Find(browser);
        var hostPath = _locateHost();
        if (HostCopyPath(definition) is not { } copy)
        {
            var direct = NativeHostManifest.Check(ManifestPath(definition), definition.Family, hostPath);
            return direct == HostRegistration.Registered && !UnixFileAccess.CanExecute(hostPath!) ? HostRegistration.Stale : direct;
        }

        var state = NativeHostManifest.Check(ManifestPath(definition), definition.Family, hostPath is null ? null : copy);
        return state == HostRegistration.Registered && (!IsCurrentCopy(copy, hostPath!) || !UnixFileAccess.CanExecute(copy))
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
            else if (_runsFromAppImage)
            {
                CopyHost(hostPath, _stableHostPath);
                manifestHost = _stableHostPath;
            }

            // A copy is made executable; the host itself can lose its mode in an archive or a manual copy.
            if (!UnixFileAccess.CanExecute(manifestHost))
            {
                throw new NativeHostRegistrationException(_localizer["BrowserConnector.HostNotExecutable"]);
            }

            Directory.CreateDirectory(definition.ManifestDirectory);
            AtomicFile.WriteAllText(ManifestPath(definition), NativeHostManifest.Render(definition.Family, manifestHost));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NativeHostRegistrationException($"Could not write the connector files for {definition.DisplayName}: {ex.Message}", ex);
        }

        if (definition.FlatpakId is null && !_runsFromAppImage)
        {
            // An earlier AppImage run may have left its copy; once no manifest names it, it is unused.
            try
            {
                RemoveStableCopyIfUnused();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[LinuxNativeHostRegistrar] Could not remove the unused host copy ({ex.GetType().Name}).");
            }
        }

        if (browser.Packaging == BrowserPackaging.Snap)
        {
            await PreApprovePortalAsync(ct).ConfigureAwait(false);
        }

        Trace.WriteLine($"[LinuxNativeHostRegistrar] Registered {definition.Id}.");
    }

    /// <remarks>A manifest that names another installation's host is left in place, and so is anything it uses.</remarks>
    public Task UnregisterAsync(BrowserTarget browser, CancellationToken ct)
    {
        var definition = Find(browser);
        var manifestPath = ManifestPath(definition);
        var registered = NativeHostManifest.ReadHostPath(manifestPath);
        if (registered is not null && !IsOwnHost(definition, registered))
        {
            Trace.WriteLine($"[LinuxNativeHostRegistrar] Left {definition.Id}: it is registered to another installation.");
            return Task.CompletedTask;
        }

        try
        {
            if (registered is not null)
            {
                File.Delete(manifestPath);
            }

            if (definition.FlatpakId is not null)
            {
                File.Delete(SandboxHostPath(definition));
            }
            else
            {
                RemoveStableCopyIfUnused();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NativeHostRegistrationException($"Could not remove the connector files for {definition.DisplayName}: {ex.Message}", ex);
        }

        Trace.WriteLine($"[LinuxNativeHostRegistrar] Unregistered {definition.Id}.");
        return Task.CompletedTask;
    }

    /// <summary>Whether a manifest naming <paramref name="registered" /> belongs to this installation: its host, or a copy it keeps.</summary>
    private bool IsOwnHost(LinuxBrowser browser, string registered) =>
        browser.FlatpakId is not null
            ? registered == SandboxHostPath(browser)
            : registered == _stableHostPath || registered == _locateHost();

    private void RemoveStableCopyIfUnused()
    {
        if (
            !File.Exists(_stableHostPath)
            || _browsers.Any(b => b.FlatpakId is null && NativeHostManifest.ReadHostPath(ManifestPath(b)) == _stableHostPath)
        )
        {
            return;
        }

        File.Delete(_stableHostPath);
        Trace.WriteLine("[LinuxNativeHostRegistrar] Removed the unused host copy.");
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
        CopyHost(hostPath, target);
        return target;
    }

    /// <summary>Copies the host with the source's timestamp, so <see cref="IsCurrentCopy" /> can tell when it goes stale.</summary>
    private static void CopyHost(string hostPath, string target)
    {
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
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The original failure is the one to report.
            }

            throw;
        }
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_commandTimeout);
        try
        {
            exitCode = (await _processRunner.RunAsync(flatpak, arguments, null, null, timeout.Token).ConfigureAwait(false)).ExitCode;
        }
        catch (Win32Exception ex)
        {
            Trace.WriteLine($"[LinuxNativeHostRegistrar] flatpak {arguments[0]} could not start: {ex.NativeErrorCode}.");
            exitCode = -1;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Trace.WriteLine($"[LinuxNativeHostRegistrar] flatpak {arguments[0]} timed out.");
            if (required)
            {
                throw new NativeHostRegistrationException(
                    _localizer.Format("BrowserConnector.RegisterFlatpakTimedOut", Math.Round(_commandTimeout.TotalSeconds))
                );
            }

            return;
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

    /// <summary>The copy of the host the browser's manifest points at, or null when it points at the host itself.</summary>
    private string? HostCopyPath(LinuxBrowser browser) =>
        browser.FlatpakId is not null ? SandboxHostPath(browser) : _runsFromAppImage ? _stableHostPath : null;

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
            HasSnap: false
        );
    }

    private static LinuxBrowser Chromium(
        string id,
        string displayName,
        string userDataDirectory,
        IReadOnlyList<string> executables,
        bool hasSnap = false
    )
    {
        return new LinuxBrowser(
            id,
            displayName,
            BrowserFamily.Chromium,
            Path.Join(userDataDirectory, "NativeMessagingHosts"),
            FlatpakId: null,
            executables,
            hasSnap
        );
    }

    /// <param name="HasSnap">Whether the browser ships as a Snap, so a found executable may be its launcher.</param>
    private sealed record LinuxBrowser(
        string Id,
        string DisplayName,
        BrowserFamily Family,
        string ManifestDirectory,
        string? FlatpakId,
        IReadOnlyList<string> Executables,
        bool HasSnap
    )
    {
        public BrowserTarget Target =>
            new(Id, DisplayName, Family, FlatpakId is null ? BrowserPackaging.Native : BrowserPackaging.Flatpak);
    }
}
