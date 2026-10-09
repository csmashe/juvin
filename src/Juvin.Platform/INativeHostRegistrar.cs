// ReSharper disable UnusedType.Global -- consumed by the setup wizard once the registrar lands in Phase 3
// ReSharper disable UnusedMember.Global -- consumed by the setup wizard once the registrar lands in Phase 3
// ReSharper disable ClassNeverInstantiated.Global -- created by the per-OS registrars in Phase 3
// ReSharper disable NotAccessedPositionalProperty.Global -- read by the registrars and the wizard in Phase 3

namespace Juvin.Platform;

/// <summary>Registers the native messaging host with browsers. Interface only until Phase 3 implements it.</summary>
public interface INativeHostRegistrar
{
    IReadOnlyList<BrowserTarget> DetectBrowsers();

    bool IsRegistered(BrowserTarget browser);

    Task RegisterAsync(BrowserTarget browser);

    Task UnregisterAsync(BrowserTarget browser);
}

public enum BrowserFamily
{
    Chromium,
    Firefox,
}

/// <param name="Id">Stable identifier such as <c>chrome</c> or <c>firefox-flatpak</c>.</param>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="Family">Decides the native messaging manifest format.</param>
public sealed record BrowserTarget(string Id, string DisplayName, BrowserFamily Family);
