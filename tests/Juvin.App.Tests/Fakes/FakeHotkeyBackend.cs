using Juvin.Core.Hotkeys;
using Juvin.Platform.Hotkeys;

namespace Juvin.App.Tests.Fakes;

/// <summary>A hotkey backend the test presses by hand; it fails to start with <see cref="StartFailure" /> when that is set.</summary>
public sealed class FakeHotkeyBackend(string id, bool isAvailable = true) : IHotkeyBackend
{
    public string? StartFailure { get; set; }

    public bool StartFailureNeedsPermission { get; init; }
    public KeyChord? Chord { get; private set; }
    public bool IsRunning { get; private set; }
    public int StartCount { get; private set; }

    public string Id { get; } = id;
    public bool IsAvailable { get; } = isAvailable;

    public event Action? Pressed;

    public event Action<string>? Stopped;

    public Task StartAsync(KeyChord chord, CancellationToken ct)
    {
        StartCount++;
        if (StartFailure is not null)
        {
            throw new HotkeyBackendException(StartFailure, StartFailureNeedsPermission);
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

    /// <summary>Stops listening on its own, as a backend that lost its devices does.</summary>
    public void StopOnItsOwn(string reason)
    {
        IsRunning = false;
        Stopped?.Invoke(reason);
    }
}
