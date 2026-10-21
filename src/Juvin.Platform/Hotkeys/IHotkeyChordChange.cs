using Juvin.Core.Hotkeys;

namespace Juvin.Platform.Hotkeys;

/// <summary>Extra surface of a hotkey backend that can switch chords while it listens, so a shortcut change does not restart it.</summary>
public interface IHotkeyChordChange
{
    /// <summary>Listens for <paramref name="chord" /> instead of the current one. Called only while the backend is started.</summary>
    /// <exception cref="HotkeyBackendException">The backend cannot listen for the chord; the caller restarts selection instead.</exception>
    void ChangeChord(KeyChord chord);
}
