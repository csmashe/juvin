using Juvin.Core.Hotkeys;
using Juvin.Platform.Hotkeys;

namespace Juvin.App.Tests.Fakes;

/// <summary>A backend that switches chords while it listens; it refuses the switch with <see cref="ChangeFailure" /> when that is set.</summary>
public sealed class FakeChordChangeBackend(string id) : IHotkeyBackend, IHotkeyChordChange
{
    public string? ChangeFailure { get; init; }
    public KeyChord? Chord { get; private set; }
    public bool IsRunning { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int ChangeCount { get; private set; }

    public string Id { get; } = id;
    public bool IsAvailable => true;

    public event Action? Pressed;

    public event Action<string>? Stopped
    {
        add { }
        remove { }
    }

    public void ChangeChord(KeyChord chord)
    {
        ChangeCount++;
        if (ChangeFailure is not null)
        {
            throw new HotkeyBackendException(ChangeFailure);
        }

        Chord = chord;
    }

    public Task StartAsync(KeyChord chord, CancellationToken ct)
    {
        StartCount++;
        Chord = chord;
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public void Press() => Pressed?.Invoke();
}
