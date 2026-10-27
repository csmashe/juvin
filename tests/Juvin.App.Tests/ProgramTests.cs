using Avalonia.Logging;
using Juvin.App.Tests.Fakes;

namespace Juvin.App.Tests;

public sealed class ProgramTests
{
    // Avalonia's binding warnings can quote bound values, so they must never reach Trace and the log file.
    [Fact]
    public void AvaloniaDiagnostics_StayOutOfTrace()
    {
        const string sentinel = "sentinel draft text";
        var previous = Logger.Sink;
        try
        {
            Program.BuildAvaloniaApp();
            using var recorder = new TraceRecorder();

            var log = Logger.TryGet(LogEventLevel.Warning, LogArea.Binding);
            Assert.NotNull(log);
            log.Value.Log(null, "Could not convert {Value}", sentinel);

            Assert.DoesNotContain(recorder.Lines, line => line.Contains(sentinel, StringComparison.Ordinal));
        }
        finally
        {
            Logger.Sink = previous;
        }
    }
}
