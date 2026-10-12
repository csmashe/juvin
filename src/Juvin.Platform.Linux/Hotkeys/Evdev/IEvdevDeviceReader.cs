namespace Juvin.Platform.Linux.Hotkeys.Evdev;

/// <summary>Reads one keyboard node on its own thread. Disposing stops it without waiting; no callback follows.</summary>
internal interface IEvdevDeviceReader : IDisposable
{
    // ReSharper disable once UnusedMemberInSuper.Global -- part of the contract; a reader is identified by its node
    string Path { get; }
}

/// <summary>The device seam of <see cref="EvdevHotkeyBackend" />; tests replace it to feed events.</summary>
internal interface IEvdevDeviceSource
{
    IReadOnlyList<string> EnumerateKeyboards();

    /// <summary>Opens a keyboard node and starts reading, or returns null when it cannot be opened.</summary>
    /// <param name="path">The <c>/dev/input/event*</c> node.</param>
    /// <param name="onEvent">Called on the reader's thread for every event read.</param>
    /// <param name="onFailed">Called once on the reader's thread when the device goes away or a read fails.</param>
    IEvdevDeviceReader? TryOpen(string path, Action<string, InputEvent> onEvent, Action<string> onFailed);
}

internal sealed class EvdevDeviceSource : IEvdevDeviceSource
{
    public IReadOnlyList<string> EnumerateKeyboards() => KeyboardDeviceDiscovery.EnumerateKeyboards();

    public IEvdevDeviceReader? TryOpen(string path, Action<string, InputEvent> onEvent, Action<string> onFailed) =>
        EvdevDeviceReader.TryOpen(path, onEvent, onFailed);
}
