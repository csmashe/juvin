namespace Juvin.Platform;

public interface ISingleInstanceGuard
{
    /// <summary>
    ///     Claims the single-instance slot for this process. Returns false when another instance
    ///     holds it. The slot is held until the guard is disposed or the process exits.
    /// </summary>
    bool TryAcquire();
}
