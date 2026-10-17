using System.Collections.Concurrent;
using System.Diagnostics;

namespace Juvin.Core.Tests.Fakes;

/// <summary>Records <see cref="Trace" /> output while it is alive. Trace is process-wide, so other tests' lines may appear too.</summary>
internal sealed class TraceRecorder : TraceListener
{
    private readonly ConcurrentQueue<string> _lines = new();

    public TraceRecorder()
    {
        Trace.Listeners.Add(this);
    }

    public IReadOnlyCollection<string> Lines => _lines;

    public override void Write(string? message)
    {
        _lines.Enqueue(message ?? "");
    }

    public override void WriteLine(string? message)
    {
        _lines.Enqueue(message ?? "");
    }

    protected override void Dispose(bool disposing)
    {
        Trace.Listeners.Remove(this);
        base.Dispose(disposing);
    }
}
