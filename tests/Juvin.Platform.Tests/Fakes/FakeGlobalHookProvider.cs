using SharpHook.Data;
using SharpHook.Native;
using SharpHook.Providers;

namespace Juvin.Platform.Tests.Fakes;

/// <summary>
///     Stands in for libuiohook's process-wide hook: one dispatch callback for the process, a run that
///     reports enabled only after a short delay, and a stop that only signals the run, which
///     then takes <see cref="TeardownDelay" /> to return, as the native hook does.
/// </summary>
public sealed class FakeGlobalHookProvider : IGlobalHookProvider
{
    private static readonly TimeSpan s_enableDelay = TimeSpan.FromMilliseconds(150);

    private readonly Lock _gate = new();
    private DispatchProc? _dispatch;
    private nint _userData;
    private ManualResetEventSlim? _stop;
    private int _runs;

    public TimeSpan TeardownDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    public int Runs => Volatile.Read(ref _runs);

    public bool KeyTypedEnabled { get; set; }

    public void SetDispatchProc(DispatchProc? dispatchProc, nint userData)
    {
        lock (_gate)
        {
            _dispatch = dispatchProc;
            _userData = userData;
        }
    }

    public UioHookResult Run()
    {
        Interlocked.Increment(ref _runs);
        using var stop = new ManualResetEventSlim();
        lock (_gate)
        {
            _stop = stop;
        }

        Thread.Sleep(s_enableDelay);
        Dispatch(new UioHookEvent { Type = EventType.HookEnabled });
        stop.Wait();
        Thread.Sleep(TeardownDelay);
        return UioHookResult.Success;
    }

    public UioHookResult RunKeyboard() => Run();

    public UioHookResult RunMouse() => Run();

    public UioHookResult Stop()
    {
        lock (_gate)
        {
            _stop?.Set();
            _stop = null;
        }

        return UioHookResult.Success;
    }

    public void Press(KeyCode key, EventMask mask) =>
        Dispatch(new UioHookEvent { Type = EventType.KeyPressed, Mask = mask, Keyboard = new KeyboardEventData { KeyCode = key } });

    public void Release(KeyCode key) =>
        Dispatch(new UioHookEvent { Type = EventType.KeyReleased, Keyboard = new KeyboardEventData { KeyCode = key } });

    private void Dispatch(UioHookEvent e)
    {
        DispatchProc? dispatch;
        nint userData;
        lock (_gate)
        {
            dispatch = _dispatch;
            userData = _userData;
        }

        dispatch?.Invoke(ref e, userData);
    }
}
