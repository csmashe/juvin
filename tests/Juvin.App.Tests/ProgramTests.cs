using Avalonia.Logging;
using Juvin.App.Tests.Fakes;

namespace Juvin.App.Tests;

public sealed class ProgramTests
{
    // Avalonia's binding warnings can quote bound values, so only their metadata may reach Trace, the log file, or the console.
    [Fact]
    public void AvaloniaDiagnostics_ReachTraceAsMetadataOnly()
    {
        const string sentinel = "sentinel draft text";
        var previous = Logger.Sink;
        var previousOut = Console.Out;
        using var console = new StringWriter();
        try
        {
            Console.SetOut(console);
            Program.BuildAvaloniaApp();
            using var recorder = new TraceRecorder();

            var log = Logger.TryGet(LogEventLevel.Warning, LogArea.Binding);
            Assert.NotNull(log);
            log.Value.Log(null, "Could not convert {Value}", sentinel);

            Assert.DoesNotContain(sentinel, console.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(recorder.Lines, line => line.Contains(sentinel, StringComparison.Ordinal));
            Assert.Contains(recorder.Lines, line => line.Contains($"[Avalonia] Warning in {LogArea.Binding}", StringComparison.Ordinal));
        }
        finally
        {
            Logger.Sink = previous;
            Console.SetOut(previousOut);
        }
    }
}
