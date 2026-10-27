using Juvin.Core.Hotkeys;
using Juvin.Platform.Hotkeys;

namespace Juvin.App.Tests.Fakes;

/// <summary>A desktop-shortcut backend that records starts and removals; removing fails with <see cref="RemoveFailure" /> when that is set.</summary>
public sealed class FakeDesktopShortcutBackend(bool requiresSessionRestart) : IHotkeyBackend, IDesktopShortcutInfo
{
    public string? RemoveFailure { get; set; }
    public int RemoveCount { get; private set; }
    public int StartCount { get; private set; }

    public bool RequiresSessionRestartToApply { get; } = requiresSessionRestart;

    public Task RemoveAsync(CancellationToken ct)
    {
        RemoveCount++;
        return RemoveFailure is null ? Task.CompletedTask : throw new HotkeyBackendException(RemoveFailure);
    }

    public string Id => "desktop-shortcut";
    public bool IsAvailable => true;

    // Presses arrive through the control socket, never from this backend.
    public event Action? Pressed
    {
        add { }
        remove { }
    }

    public event Action<string>? Stopped
    {
        add { }
        remove { }
    }

    public Task StartAsync(KeyChord chord, CancellationToken ct)
    {
        StartCount++;
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;
}
