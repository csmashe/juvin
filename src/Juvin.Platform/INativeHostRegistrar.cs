namespace Juvin.Platform;

/// <summary>
///     Registers the native messaging host with the browsers on this machine. Detection only reads
///     the file system and registry; registration writes a host manifest per browser and, for a
///     sandboxed browser, puts the host where the sandbox can run it.
/// </summary>
public interface INativeHostRegistrar
{
    /// <summary>Browsers found on this machine, in display order. Cheap: no processes are spawned.</summary>
    IReadOnlyList<BrowserTarget> DetectBrowsers();

    /// <summary>Whether <paramref name="browser" /> has a manifest that points at the current host executable.</summary>
    HostRegistration CheckRegistration(BrowserTarget browser);

    /// <summary>
    ///     Writes the manifest for <paramref name="browser" /> and any one-time sandbox setup it needs.
    ///     Throws <see cref="NativeHostRegistrationException" /> with a user-readable message when the
    ///     host cannot be registered as things stand.
    /// </summary>
    Task RegisterAsync(BrowserTarget browser, CancellationToken ct);

    // ReSharper disable once UnusedMemberInSuper.Global -- exercised by the registrar tests; the UI exposes it with uninstall
    // ReSharper disable once UnusedParameter.Global -- contract; the Linux registrar's sandbox copy honours it
    Task UnregisterAsync(BrowserTarget browser, CancellationToken ct);

    /// <summary>
    ///     Extra connector endpoints the app must listen on so registered sandboxed browsers can
    ///     reach it (one per Flatpak browser on Linux). Empty where every browser can use the main
    ///     endpoint.
    /// </summary>
    IReadOnlyList<SandboxEndpoint> SandboxEndpoints { get; }
}

/// <param name="BrowserId">The <see cref="BrowserTarget.Id" /> whose sandbox reaches the app only through this address.</param>
public sealed record SandboxEndpoint(string BrowserId, string Address);

public enum BrowserFamily
{
    Chromium,
    Firefox,
}

public enum BrowserPackaging
{
    Native,
    Flatpak,
    Snap,
}

public enum HostRegistration
{
    NotRegistered,

    /// <summary>A manifest exists and points at the current host executable.</summary>
    Registered,

    /// <summary>A manifest exists but points somewhere else, typically an older install.</summary>
    Stale,
}

/// <param name="Id">Stable identifier such as <c>chrome</c> or <c>firefox-flatpak</c>; used in logs and as the row key.</param>
/// <param name="DisplayName">Name shown to the user, without the packaging.</param>
/// <param name="Family">Decides the native messaging manifest format.</param>
/// <param name="Packaging">How the browser is installed; sandboxed packagings need extra setup.</param>
public sealed record BrowserTarget(string Id, string DisplayName, BrowserFamily Family, BrowserPackaging Packaging);

public sealed class NativeHostRegistrationException(string message, Exception? inner = null) : Exception(message, inner);
