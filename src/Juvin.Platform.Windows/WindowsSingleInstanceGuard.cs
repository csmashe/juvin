namespace Juvin.Platform.Windows;

/// <summary>
///     Single-instance guard backed by a session-local named mutex. Holding the handle open is
///     what claims the slot; the mutex is never waited on, so thread affinity does not matter.
/// </summary>
public sealed class WindowsSingleInstanceGuard : ISingleInstanceGuard, IDisposable
{
    // Also the AppMutex in scripts/windows/juvin.iss; change both together.
    private const string DefaultMutexName = @"Local\Juvin.SingleInstance";

    private readonly Lock _gate = new();
    private readonly string _mutexName;
    private Mutex? _mutex;

    public WindowsSingleInstanceGuard()
        : this(DefaultMutexName)
    {
    }

    internal WindowsSingleInstanceGuard(string mutexName)
    {
        _mutexName = mutexName;
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_mutex is not null)
            {
                return true;
            }

            Mutex mutex;
            bool createdNew;
            try
            {
                mutex = new Mutex(initiallyOwned: false, _mutexName, out createdNew);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
            {
                // An elevated instance holds the mutex and this token cannot open it.
                return false;
            }

            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            _mutex = mutex;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}
