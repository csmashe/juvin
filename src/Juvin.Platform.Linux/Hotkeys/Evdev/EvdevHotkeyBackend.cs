using System.Diagnostics;
using Juvin.Core.Hotkeys;
using Juvin.Core.Localization;
using Juvin.Platform.Hotkeys;

namespace Juvin.Platform.Linux.Hotkeys.Evdev;

/// <summary>
///     Reads keyboards from <c>/dev/input</c>, which works on every Wayland desktop once the
///     keyboard-access setup task grants access. The chord fires on its key's key-down while exactly
///     its modifiers are held, on either side and on any keyboard.
/// </summary>
public sealed class EvdevHotkeyBackend : IHotkeyBackend
{
    private const string BackendId = "evdev";

    private static readonly TimeSpan s_rescanInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly HashSet<(string Device, int Code)> _heldModifiers = [];
    private readonly Localizer _localizer;
    private readonly Dictionary<string, IEvdevDeviceReader> _readers = new(StringComparer.Ordinal);
    private readonly TimeSpan _rescanInterval;
    private readonly IEvdevDeviceSource _source;
    private long _generation;
    private int _keyCode;
    private ChordModifiers _modifiers;
    private CancellationTokenSource? _rescan;

    public EvdevHotkeyBackend(Localizer localizer)
        : this(localizer, new EvdevDeviceSource(), s_rescanInterval)
    {
    }

    internal EvdevHotkeyBackend(Localizer localizer, IEvdevDeviceSource source, TimeSpan rescanInterval)
    {
        _localizer = localizer;
        _source = source;
        _rescanInterval = rescanInterval;
    }

    public string Id => BackendId;

    public bool IsAvailable => OperatingSystem.IsLinux() && _source.EnumerateKeyboards().Count > 0;

    public event Action? Pressed;

    // Never raised: with every keyboard gone it keeps listening and the rescan reattaches them.
    public event Action<string>? Stopped
    {
        add { }
        remove { }
    }

    public async Task StartAsync(KeyChord chord, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chord);
        await StopAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!LinuxKeyMap.KeyCodes.TryGetValue(chord.PhysicalKey, out var keyCode))
        {
            throw new HotkeyBackendException(_localizer["Hotkey.Linux.UnsupportedKey"]);
        }

        var keyboards = _source.EnumerateKeyboards();
        int attached;
        lock (_gate)
        {
            _keyCode = keyCode;
            _modifiers = chord.Modifiers;
            AttachNew_NoLock(keyboards);
            attached = _readers.Count;
        }

        if (attached == 0)
        {
            await StopAsync().ConfigureAwait(false);
            Trace.WriteLine("[EvdevHotkeyBackend] No keyboard could be opened.");
            throw new HotkeyBackendException(_localizer["Hotkey.Linux.NoKeyboardAccess"]);
        }

        StartRescan();
        Trace.WriteLine($"[EvdevHotkeyBackend] Listening on {attached} keyboard(s).");
    }

    public Task StopAsync()
    {
        List<IEvdevDeviceReader> readers;
        CancellationTokenSource? rescan;
        lock (_gate)
        {
            _generation++;
            readers = [.. _readers.Values];
            _readers.Clear();
            _heldModifiers.Clear();
            rescan = _rescan;
            _rescan = null;
        }

        rescan?.Cancel();
        rescan?.Dispose();
        foreach (var reader in readers)
        {
            reader.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>Attaches keyboards that appeared since the last scan; failed readers were already dropped.</summary>
    internal void Rescan()
    {
        var keyboards = _source.EnumerateKeyboards();
        lock (_gate)
        {
            if (_rescan is null)
            {
                return;
            }

            var before = _readers.Count;
            AttachNew_NoLock(keyboards);
            if (_readers.Count != before)
            {
                Trace.WriteLine($"[EvdevHotkeyBackend] Now listening on {_readers.Count} keyboard(s).");
            }
        }
    }

    private void StartRescan()
    {
        var rescan = new CancellationTokenSource();
        lock (_gate)
        {
            _rescan = rescan;
        }

        var ct = rescan.Token;
        _ = Task.Run(
            async () =>
            {
                using var timer = new PeriodicTimer(_rescanInterval);
                try
                {
                    while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    {
                        Rescan();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopped.
                }
            },
            CancellationToken.None
        );
    }

    private void AttachNew_NoLock(IReadOnlyList<string> keyboards)
    {
        var generation = _generation;
        foreach (var path in keyboards.Where(path => !_readers.ContainsKey(path)))
        {
            var reader = _source.TryOpen(
                path,
                (device, input) => OnEvent(generation, device, input),
                device => OnFailed(generation, device)
            );
            if (reader is not null)
            {
                _readers[path] = reader;
            }
        }
    }

    private void OnEvent(long generation, string device, InputEvent input)
    {
        lock (_gate)
        {
            if (generation != _generation || !Matches_NoLock(device, input))
            {
                return;
            }
        }

        Pressed?.Invoke();
    }

    private bool Matches_NoLock(string device, InputEvent input)
    {
        if (input is { Type: InputEvent.EvSyn, Code: InputEvent.SynDropped })
        {
            // The kernel dropped events; what this device holds is unknown until its keys are pressed again.
            _heldModifiers.RemoveWhere(held => held.Device == device);
            return false;
        }

        if (input.Type != InputEvent.EvKey || input.Value == InputEvent.Repeated)
        {
            return false;
        }

        // ReSharper disable once InvertIf -- modifier bookkeeping stays its own branch ahead of the chord check
        if (LinuxKeyMap.ModifierOf(input.Code) != ChordModifiers.None)
        {
            if (input.Value == InputEvent.Pressed)
            {
                _heldModifiers.Add((device, input.Code));
            }
            else
            {
                _heldModifiers.Remove((device, input.Code));
            }

            return false;
        }

        return input.Code == _keyCode && input.Value == InputEvent.Pressed && HeldModifiers_NoLock() == _modifiers;
    }

    private ChordModifiers HeldModifiers_NoLock() =>
        _heldModifiers.Aggregate(ChordModifiers.None, (modifiers, held) => modifiers | LinuxKeyMap.ModifierOf(held.Code));

    private void OnFailed(long generation, string device)
    {
        IEvdevDeviceReader? reader;
        lock (_gate)
        {
            if (generation != _generation || !_readers.Remove(device, out reader))
            {
                return;
            }

            _heldModifiers.RemoveWhere(held => held.Device == device);
            Trace.WriteLine($"[EvdevHotkeyBackend] Lost {device}; listening on {_readers.Count} keyboard(s).");
        }

        reader.Dispose();
    }
}
