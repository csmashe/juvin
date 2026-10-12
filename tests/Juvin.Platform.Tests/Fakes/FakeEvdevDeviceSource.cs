using System.Runtime.Versioning;
using Juvin.Platform.Linux.Hotkeys.Evdev;

namespace Juvin.Platform.Tests.Fakes;

[SupportedOSPlatform("linux")]
internal sealed class FakeEvdevDeviceSource : IEvdevDeviceSource
{
    private readonly Dictionary<string, Reader> _open = new(StringComparer.Ordinal);

    public List<string> Keyboards { get; } = [];

    public HashSet<string> Unopenable { get; } = [];

    public IReadOnlyCollection<string> OpenDevices => _open.Keys;

    public IReadOnlyList<string> EnumerateKeyboards() => [.. Keyboards];

    public IEvdevDeviceReader? TryOpen(string path, Action<string, InputEvent> onEvent, Action<string> onFailed)
    {
        if (Unopenable.Contains(path))
        {
            return null;
        }

        var reader = new Reader(this, path, onEvent, onFailed);
        _open[path] = reader;
        return reader;
    }

    public void Key(string path, int code, int value) => _open[path].OnEvent(path, InputEvent.Key(code, value));

    public void Send(string path, InputEvent input) => _open[path].OnEvent(path, input);

    public void Fail(string path) => _open[path].OnFailed(path);

    private sealed class Reader(FakeEvdevDeviceSource owner, string path, Action<string, InputEvent> onEvent, Action<string> onFailed)
        : IEvdevDeviceReader
    {
        public Action<string, InputEvent> OnEvent { get; } = onEvent;

        public Action<string> OnFailed { get; } = onFailed;

        public string Path => path;

        public void Dispose() => owner._open.Remove(path);
    }
}
