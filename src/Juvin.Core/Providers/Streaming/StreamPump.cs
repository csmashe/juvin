using System.Diagnostics;
using System.Text;

namespace Juvin.Core.Providers.Streaming;

/// <summary>
///     Consumes a text-delta stream, accumulates the full text, and calls <c>onAccumulated</c>
///     at most once per <c>flushInterval</c> (~30 Hz) plus a final flush. Rate-limiting keeps fast
///     providers from flooding the UI dispatcher. Any fault is rethrown after the final flush, so
///     the caller has seen the partial text.
/// </summary>
internal sealed class StreamPump
{
    private static readonly TimeSpan s_defaultFlushInterval = TimeSpan.FromMilliseconds(33);

    private readonly TimeSpan _flushInterval;
    private readonly Action<string> _onAccumulated;
    private readonly StringBuilder _sb = new();

    private string? _lastEmitted;

    public StreamPump(Action<string> onAccumulated, TimeSpan? flushInterval = null)
    {
        _onAccumulated = onAccumulated;
        _flushInterval = flushInterval ?? s_defaultFlushInterval;
    }

    public async Task<string> RunAsync(IAsyncEnumerable<string> source, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastFlushMs = -_flushInterval.TotalMilliseconds;

        try
        {
            await foreach (var delta in source.WithCancellation(ct))
            {
                if (string.IsNullOrEmpty(delta))
                {
                    continue;
                }

                _sb.Append(delta);

                if (stopwatch.Elapsed.TotalMilliseconds - lastFlushMs < _flushInterval.TotalMilliseconds)
                {
                    continue;
                }

                Emit();
                lastFlushMs = stopwatch.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // Caller cancellation wins if a dependency fault completes in the same race window.
            EmitFinal();
            throw new OperationCanceledException(ct);
        }
        catch
        {
            EmitFinal();
            throw;
        }

        EmitFinal();
        return _sb.ToString();
    }

    private void Emit()
    {
        var text = _sb.ToString();
        if (text == _lastEmitted)
        {
            return;
        }

        _lastEmitted = text;
        _onAccumulated(text);
    }

    private void EmitFinal()
    {
        if (_sb.Length == 0)
        {
            return;
        }

        Emit();
    }
}
