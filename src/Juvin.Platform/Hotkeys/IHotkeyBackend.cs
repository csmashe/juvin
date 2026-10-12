using Juvin.Core.Hotkeys;

namespace Juvin.Platform.Hotkeys;

/// <summary>
///     A source of global shortcut presses. The app registers several per OS in order of preference
///     and starts the first available one, or the one the user picked. The app localizes the
///     backend's name from <see cref="Id" />.
/// </summary>
public interface IHotkeyBackend
{
    /// <summary>Stable id such as <c>evdev</c>, <c>sharphook</c>, or <c>desktop-shortcut</c>; also the settings value.</summary>
    string Id { get; }

    /// <summary>A cheap synchronous check (environment, file access probes, binary lookups) that never spawns a process.</summary>
    bool IsAvailable { get; }

    /// <summary>Raised once per press of the chord, on any thread; holding the keys does not repeat it.</summary>
    event Action? Pressed;

    /// <summary>Starts listening for <paramref name="chord" />. Called again only after <see cref="StopAsync" />.</summary>
    /// <exception cref="HotkeyBackendException">The backend cannot listen here; the message is shown to the user.</exception>
    Task StartAsync(KeyChord chord, CancellationToken ct);

    Task StopAsync();
}
