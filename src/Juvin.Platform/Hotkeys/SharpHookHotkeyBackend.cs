using System.Diagnostics;
using Juvin.Core.Hotkeys;
using Juvin.Core.Localization;
using SharpHook;
using SharpHook.Native;

namespace Juvin.Platform.Hotkeys;

/// <summary>
///     A libuiohook global hook through SharpHook: the backend on Windows and macOS, and on Linux
///     only under X11, because it sees nothing from Wayland clients. Press-only.
/// </summary>
public sealed class SharpHookHotkeyBackend : IHotkeyBackend
{
    private const string BackendId = "sharphook";

    private static readonly TimeSpan s_startTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_stopTimeout = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Localizer _localizer;
    private TaskPoolGlobalHook? _hook;
    private KeyCode _key;
    private bool _keyDown;
    private ChordModifiers _modifiers;

    public SharpHookHotkeyBackend(Localizer localizer)
    {
        _localizer = localizer;
    }

    public string Id => BackendId;

    public bool IsAvailable =>
        OperatingSystem.IsWindows()
        || OperatingSystem.IsMacOS()
        || (OperatingSystem.IsLinux() && IsX11Session(Environment.GetEnvironmentVariable));

    public event Action? Pressed;

    public event Action<string>? Stopped;

    /// <summary>Every <see cref="KeyChord.KeyNames" /> entry and its SharpHook key code.</summary>
    internal static IReadOnlyDictionary<string, KeyCode> KeyCodes { get; } = BuildKeyCodes();

    public async Task StartAsync(KeyChord chord, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chord);
        await StopAsync().ConfigureAwait(false);
        if (!KeyCodes.TryGetValue(KeyToMatch(chord, OperatingSystem.IsMacOS()), out var key))
        {
            throw new HotkeyBackendException(_localizer["Hotkey.SharpHook.UnsupportedKey"]);
        }

        // A background thread: a hung libuiohook must not keep the process alive after the bounded stop.
        var hook = new TaskPoolGlobalHook(runAsyncOnBackgroundThread: true);
        var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hook.HookEnabled += (_, _) => enabled.TrySetResult();
        hook.KeyPressed += OnKeyPressed;
        hook.KeyReleased += OnKeyReleased;
        lock (_gate)
        {
            _key = key;
            _modifiers = chord.Modifiers;
            _keyDown = false;
            _hook = hook;
        }

        var run = hook.RunAsync();
        var first = await Task.WhenAny(enabled.Task, run, Task.Delay(s_startTimeout, ct)).ConfigureAwait(false);
        if (first == run)
        {
            await StopAsync().ConfigureAwait(false);
            var result = run.Exception?.GetBaseException() is HookException failure ? failure.Result : UioHookResult.Failure;
            Trace.WriteLine($"[SharpHookHotkeyBackend] Hook did not start ({result}).");
            throw new HotkeyBackendException(
                result == UioHookResult.ErrorAxApiDisabled
                    ? _localizer["Hotkey.SharpHook.AccessibilityNeeded"]
                    : _localizer.Format("Hotkey.SharpHook.StartFailed", result)
            );
        }

        if (ct.IsCancellationRequested)
        {
            await StopAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        if (first != enabled.Task)
        {
            await StopAsync().ConfigureAwait(false);
            Trace.WriteLine("[SharpHookHotkeyBackend] Hook did not report enabled in time.");
            throw new HotkeyBackendException(_localizer["Hotkey.SharpHook.NoResponse"]);
        }

        _ = run.ContinueWith(
            task => OnHookEnded(hook, task),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default
        );
    }

    public async Task StopAsync()
    {
        TaskPoolGlobalHook? hook;
        lock (_gate)
        {
            hook = _hook;
            _hook = null;
            _keyDown = false;
        }

        if (hook is null)
        {
            return;
        }

        hook.KeyPressed -= OnKeyPressed;
        hook.KeyReleased -= OnKeyReleased;
        // libuiohook's Dispose blocks until its thread stops; never let it hang the caller.
        try
        {
            await Task.Run(hook.Dispose).WaitAsync(s_stopTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or HookException)
        {
            Trace.WriteLine($"[SharpHookHotkeyBackend] Stopping the hook failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>A hook that ends while it is still the current one stopped on its own, not through <see cref="StopAsync" />.</summary>
    private void OnHookEnded(TaskPoolGlobalHook hook, Task run)
    {
        // The subscriber is taken under the same lock as the identity check, so a restart that swaps
        // the hook and the subscription in between cannot have the old hook's end stop the new one.
        Action<string>? stopped;
        lock (_gate)
        {
            if (!ReferenceEquals(_hook, hook))
            {
                return;
            }

            stopped = Stopped;
        }

        Trace.WriteLine($"[SharpHookHotkeyBackend] Hook stopped on its own ({run.Exception?.GetBaseException().GetType().Name ?? run.Status.ToString()}).");
        stopped?.Invoke(_localizer["Hotkey.SharpHook.Stopped"]);
    }

    /// <summary>libuiohook reports layout keys (Windows virtual keys, X11 keysyms); only macOS gives the hardware key code.</summary>
    internal static string KeyToMatch(KeyChord chord, bool isMac) => isMac ? chord.PhysicalKey : chord.Key;

    internal static bool IsX11Session(Func<string, string?> getEnvironment) =>
        !string.Equals(getEnvironment("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(getEnvironment("WAYLAND_DISPLAY"));

    /// <summary>The chord's modifiers as SharpHook reports them. On macOS the primary modifier is Command (Meta) and Super is Control.</summary>
    internal static ChordModifiers ModifiersOf(ModifierMask mask, bool isMac)
    {
        var modifiers = ChordModifiers.None;
        if ((mask & ModifierMask.Ctrl) != 0)
        {
            modifiers |= isMac ? ChordModifiers.Super : ChordModifiers.Ctrl;
        }

        if ((mask & ModifierMask.Meta) != 0)
        {
            modifiers |= isMac ? ChordModifiers.Ctrl : ChordModifiers.Super;
        }

        if ((mask & ModifierMask.Alt) != 0)
        {
            modifiers |= ChordModifiers.Alt;
        }

        if ((mask & ModifierMask.Shift) != 0)
        {
            modifiers |= ChordModifiers.Shift;
        }

        return modifiers;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        bool matched;
        lock (_gate)
        {
            if (e.Data.KeyCode != _key || _keyDown)
            {
                return;
            }

            // Held keys repeat KeyPressed; only the first press until the release counts.
            _keyDown = true;
            matched = ModifiersOf(e.RawEvent.Mask, OperatingSystem.IsMacOS()) == _modifiers;
        }

        if (matched)
        {
            Pressed?.Invoke();
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        lock (_gate)
        {
            if (e.Data.KeyCode == _key)
            {
                _keyDown = false;
            }
        }
    }

    private static Dictionary<string, KeyCode> BuildKeyCodes()
    {
        var codes = new Dictionary<string, KeyCode>(StringComparer.Ordinal)
        {
            ["Space"] = KeyCode.VcSpace,
            ["Escape"] = KeyCode.VcEscape,
            ["Enter"] = KeyCode.VcEnter,
            ["Tab"] = KeyCode.VcTab,
            ["Backspace"] = KeyCode.VcBackspace,
            ["Delete"] = KeyCode.VcDelete,
            ["Insert"] = KeyCode.VcInsert,
            ["Home"] = KeyCode.VcHome,
            ["End"] = KeyCode.VcEnd,
            ["PageUp"] = KeyCode.VcPageUp,
            ["PageDown"] = KeyCode.VcPageDown,
            ["Up"] = KeyCode.VcUp,
            ["Down"] = KeyCode.VcDown,
            ["Left"] = KeyCode.VcLeft,
            ["Right"] = KeyCode.VcRight,
            ["Minus"] = KeyCode.VcMinus,
            ["Equal"] = KeyCode.VcEquals,
            ["BracketLeft"] = KeyCode.VcOpenBracket,
            ["BracketRight"] = KeyCode.VcCloseBracket,
            ["Semicolon"] = KeyCode.VcSemicolon,
            ["Quote"] = KeyCode.VcQuote,
            ["Backquote"] = KeyCode.VcBackQuote,
            ["Backslash"] = KeyCode.VcBackslash,
            ["Comma"] = KeyCode.VcComma,
            ["Period"] = KeyCode.VcPeriod,
            ["Slash"] = KeyCode.VcSlash,
        };

        // Letters, digits, and F1 to F24 are named VcA, Vc0, VcF1 in SharpHook.
        foreach (var name in KeyChord.KeyNames.Where(name => !codes.ContainsKey(name)))
        {
            codes[name] = Enum.Parse<KeyCode>("Vc" + name);
        }

        return codes;
    }
}
