using Juvin.Core.Hotkeys;
using Juvin.Platform.Hotkeys;

namespace Juvin.App.Tests.Fakes;

/// <summary>A hotkey backend the test presses by hand; it fails to start with <see cref="StartFailure" /> when that is set.</summary>
public sealed class FakeHotkeyBackend(string id, bool isAvailable = true) : IHotkeyBackend
{
    public string? StartFailure { get; set; }
    public KeyChord? Chord { get; private set; }
    public bool IsRunning { get; private set; }
    public int StartCount { get; private set; }

    public string Id { get; } = id;
    public bool IsAvailable { get; } = isAvailable;

    public event Action? Pressed;

    public Task StartAsync(KeyChord chord, CancellationToken ct)
    {
        StartCount++;
        if (StartFailure is not null)
        {
            throw new HotkeyBackendException(StartFailure);
        }

        Chord = chord;
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public void Press() => Pressed?.Invoke();
}
