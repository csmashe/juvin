// ReSharper disable UnusedType.Global -- consumed by the app once the hotkey backends land in Phase 4
// ReSharper disable UnusedMember.Global -- part of the platform contract implemented per OS
// ReSharper disable EventNeverSubscribedTo.Global -- part of the platform contract implemented per OS

namespace Juvin.Platform;

/// <summary>A global shortcut source. Implemented per OS in Phase 4.</summary>
public interface IHotkeyBackend
{
    string Name { get; }

    bool IsAvailable { get; }

    event Action? Pressed;

    Task StartAsync(string shortcut, CancellationToken ct);

    Task StopAsync();
}
