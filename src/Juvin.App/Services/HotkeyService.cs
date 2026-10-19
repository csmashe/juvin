using System.Diagnostics;
using Juvin.Core.Hotkeys;
using Juvin.Core.Interfaces;
using Juvin.Core.Models;
using Juvin.Platform.Hotkeys;

namespace Juvin.App.Services;

public enum HotkeyStatusKind
{
    Stopped,
    Active,
    NoBackend,
    Failed,

    /// <summary>The backend stopped on its own after starting; selection runs again shortly.</summary>
    Lost,
}

/// <param name="BackendId">The backend listening, or the one that failed or was lost; null otherwise.</param>
/// <param name="Message">The backend's own reason for a failure or loss; null when it gave none.</param>
public sealed record HotkeyStatus(HotkeyStatusKind Kind, string? BackendId = null, string? Message = null)
{
    public static HotkeyStatus Stopped { get; } = new(HotkeyStatusKind.Stopped);
}

/// <summary>
///     Listens for the rewrite shortcut through the user's chosen hotkey backend, or the first one
///     that starts in registration order, and turns each press into a capture. Restarts when the
///     shortcut or the backend choice changes, and selects again shortly after a backend stops on its
///     own. A saved shortcut that is not a valid chord falls back to the default. Traces carry backend
///     ids and status only.
/// </summary>
/// <remarks>
///     Members are safe to call from any thread. Events are raised on backend or thread-pool
///     threads, so UI subscribers must marshal to the UI thread. Awaits never resume on the UI
///     thread, because app exit blocks on <see cref="StopAsync" /> there.
/// </remarks>
public sealed class HotkeyService : IHotkeyRestart
{
    private static readonly TimeSpan s_reselectDelay = TimeSpan.FromSeconds(3);

    private static readonly KeyChord s_defaultChord = KeyChord.TryParse(AppSettings.DefaultRewriteShortcutFor(OperatingSystem.IsMacOS()), out var chord)
        ? chord
        : throw new InvalidOperationException("The default shortcut is not a valid chord.");

    private readonly IReadOnlyList<IHotkeyBackend> _backends;
    private readonly ConnectorService _connector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _reselectDelay;
    private readonly ISettingsService _settings;

    // Written under _gate; HandleTrigger reads it without the gate.
    private volatile IHotkeyBackend? _active;

    // Guarded by _gate.
    private Action<string>? _activeStopped;
    private (string Shortcut, string? PhysicalKey, string? Backend)? _applied;
    private int _generation;
    private bool _started;

    private volatile HotkeyStatus _status = HotkeyStatus.Stopped;
    private volatile bool _suspended;

    /// <param name="backends">Every backend for this OS, most preferred first.</param>
    /// <param name="settings">Supplies the shortcut and the backend choice.</param>
    /// <param name="connector">Captures on each press.</param>
    public HotkeyService(IEnumerable<IHotkeyBackend> backends, ISettingsService settings, ConnectorService connector)
        : this(backends, settings, connector, s_reselectDelay)
    {
    }

    internal HotkeyService(IEnumerable<IHotkeyBackend> backends, ISettingsService settings, ConnectorService connector, TimeSpan reselectDelay)
    {
        _backends = [.. backends];
        _settings = settings;
        _connector = connector;
        _reselectDelay = reselectDelay;
        _settings.SettingsChanged += changed => _ = RestartIfChangedAsync(changed);
    }

    public HotkeyStatus Status => _status;

    public bool IsListening => _status.Kind == HotkeyStatusKind.Active;

    /// <summary>The backends that can run in this session, most preferred first.</summary>
    public IReadOnlyList<IHotkeyBackend> AvailableBackends => [.. _backends.Where(backend => backend.IsAvailable)];

    /// <summary>While true, presses are ignored, so recording a new shortcut does not start a capture.</summary>
    public bool Suspended
    {
        get => _suspended;
        set => _suspended = value;
    }

    public event Action? StatusChanged;

    /// <summary>Raised on every press before the capture starts, for the settings "press to test" row.</summary>
    public event Action? Pressed;

    /// <summary>The chord a saved shortcut listens for: the shortcut itself, or the default when it is not a valid chord.</summary>
    public static KeyChord EffectiveShortcut(string shortcut) => KeyChord.TryParse(shortcut, out var chord) ? chord : s_defaultChord;

    public async Task StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _started = true;
            await ApplyAsync(_settings.Current, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _started = false;
            _applied = null;
            _generation++;
            await StopActiveAsync().ConfigureAwait(false);
            SetStatus(HotkeyStatus.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A press from the active backend, or from <c>juvin --trigger</c>, which a desktop shortcut runs.</summary>
    public void HandlePress()
    {
        if (_suspended)
        {
            Trace.WriteLine("[HotkeyService] Press ignored while suspended.");
            return;
        }

        Pressed?.Invoke();
        _ = _connector.TriggerCaptureAsync();
    }

    /// <summary>Removes the desktop shortcut; if it was active, listening stops until the next shortcut or backend change.</summary>
    public async Task RemoveDesktopShortcutAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_active is IDesktopShortcutInfo)
            {
                await StopActiveAsync().ConfigureAwait(false);
                // Before the removal, which can throw: the backend is already stopped either way.
                SetStatus(HotkeyStatus.Stopped);
            }

            foreach (var desktop in _backends.OfType<IDesktopShortcutInfo>())
            {
                await desktop.RemoveAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-runs backend selection, for a setup task that just granted the access a better backend needs.</summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                await ApplyAsync(_settings.Current, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Honoured only while the desktop shortcut or no backend is active, so a stale binding (KDE keeps
    ///     one live until the next sign-in) cannot fire an old chord once another backend listens.
    /// </summary>
    public void HandleTrigger()
    {
        var active = _active;
        if (active is null or IDesktopShortcutInfo)
        {
            HandlePress();
            return;
        }

        Trace.WriteLine($"[HotkeyService] Trigger ignored while {active.Id} listens.");
    }

    private async Task RestartIfChangedAsync(AppSettings settings)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_started && _applied != (settings.RewriteShortcut, settings.RewriteShortcutPhysicalKey, settings.HotkeyBackend))
            {
                await ApplyAsync(settings, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyAsync(AppSettings settings, CancellationToken ct)
    {
        await StopActiveAsync().ConfigureAwait(false);
        _generation++;
        _applied = (settings.RewriteShortcut, settings.RewriteShortcutPhysicalKey, settings.HotkeyBackend);
        KeyChord chord;
        if (KeyChord.TryParse(settings.RewriteShortcut, out var parsed))
        {
            chord = parsed.WithPhysicalKey(settings.RewriteShortcutPhysicalKey);
        }
        else
        {
            Trace.WriteLine($"[HotkeyService] The shortcut setting is not a valid chord ({settings.RewriteShortcut.Length} chars); using the default.");
            chord = s_defaultChord;
        }

        var available = AvailableBackends;
        var preferred = available.FirstOrDefault(backend => backend.Id == settings.HotkeyBackend);
        if (preferred is null && settings.HotkeyBackend is { } chosen)
        {
            Trace.WriteLine($"[HotkeyService] The chosen backend {chosen} is not available; selecting automatically.");
        }

        var candidates = preferred is null ? available : [preferred];
        if (candidates.Count == 0)
        {
            Trace.WriteLine($"[HotkeyService] None of {_backends.Count} backends is available.");
            SetStatus(new HotkeyStatus(HotkeyStatusKind.NoBackend));
            return;
        }

        HotkeyStatus? failure = null;
        foreach (var backend in candidates)
        {
            var generation = _generation;
            Action<string> stopped = reason => _ = ReselectAfterStopAsync(backend, generation, reason);
            backend.Pressed += HandlePress;
            backend.Stopped += stopped;
            try
            {
                await backend.StartAsync(chord, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                backend.Pressed -= HandlePress;
                backend.Stopped -= stopped;
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                Trace.WriteLine($"[HotkeyService] {backend.Id} did not start ({ex.GetType().Name}).");
                failure ??= new HotkeyStatus(HotkeyStatusKind.Failed, backend.Id, (ex as HotkeyBackendException)?.Message);
                continue;
            }

            _active = backend;
            _activeStopped = stopped;
            Trace.WriteLine($"[HotkeyService] Listening with {backend.Id}.");
            SetStatus(new HotkeyStatus(HotkeyStatusKind.Active, backend.Id));
            await RemoveIdleDesktopShortcutsAsync(backend, ct).ConfigureAwait(false);
            return;
        }

        SetStatus(failure!);
    }

    /// <summary>A desktop shortcut left behind after a backend switch would keep firing the old chord.</summary>
    private async Task RemoveIdleDesktopShortcutsAsync(IHotkeyBackend active, CancellationToken ct)
    {
        foreach (var backend in _backends)
        {
            if (ReferenceEquals(backend, active) || backend is not IDesktopShortcutInfo desktop || !backend.IsAvailable)
            {
                continue;
            }

            try
            {
                await desktop.RemoveAsync(ct).ConfigureAwait(false);
            }
            catch (HotkeyBackendException ex)
            {
                Trace.WriteLine($"[HotkeyService] Could not remove the idle {backend.Id} shortcut ({ex.GetType().Name}).");
            }
        }
    }

    private async Task StopActiveAsync()
    {
        if (_active is not { } active)
        {
            return;
        }

        _active = null;
        active.Pressed -= HandlePress;
        active.Stopped -= _activeStopped;
        _activeStopped = null;
        try
        {
            await active.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HotkeyService] {active.Id} did not stop cleanly ({ex.GetType().Name}).");
        }
    }

    /// <summary>A backend that stopped on its own is reported as lost, then selection runs again unless something restarted it meanwhile.</summary>
    private async Task ReselectAfterStopAsync(IHotkeyBackend backend, int generation, string reason)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != _generation || !ReferenceEquals(_active, backend))
            {
                return;
            }

            Trace.WriteLine($"[HotkeyService] {backend.Id} stopped listening; selecting again in {_reselectDelay.TotalSeconds:0} seconds.");
            await StopActiveAsync().ConfigureAwait(false);
            SetStatus(new HotkeyStatus(HotkeyStatusKind.Lost, backend.Id, reason));
        }
        finally
        {
            _gate.Release();
        }

        await Task.Delay(_reselectDelay).ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_started && generation == _generation)
            {
                await ApplyAsync(_settings.Current, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetStatus(HotkeyStatus status)
    {
        _status = status;
        StatusChanged?.Invoke();
    }
}
