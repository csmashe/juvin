using Juvin.Core.Hotkeys;
using Juvin.Platform.Hotkeys;

namespace Juvin.App.Tests.Fakes;

/// <summary>A desktop-shortcut backend that records whether the shortcut was removed.</summary>
public sealed class FakeDesktopShortcutBackend(bool requiresSessionRestart) : IHotkeyBackend, IDesktopShortcutInfo
{
    public int RemoveCount { get; private set; }

    public bool RequiresSessionRestartToApply { get; } = requiresSessionRestart;

    public Task RemoveAsync(CancellationToken ct)
    {
        RemoveCount++;
        return Task.CompletedTask;
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

    public Task StartAsync(KeyChord chord, CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;
}
