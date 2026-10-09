namespace Juvin.Core.Rewrite;

/// <summary>
///     Reports on the calling thread, in order. <see cref="Progress{T}" /> posts to the thread pool
///     when there is no synchronization context, which can reorder preview updates.
/// </summary>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public InlineProgress(Action<T> handler)
    {
        _handler = handler;
    }

    public void Report(T value)
    {
        _handler(value);
    }
}
