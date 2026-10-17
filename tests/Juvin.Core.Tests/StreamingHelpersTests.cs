using System.Runtime.CompilerServices;
using Juvin.Core.Providers;
using Juvin.Core.Providers.Streaming;

namespace Juvin.Core.Tests;

public sealed class StreamingHelpersTests
{
    [Fact]
    public async Task SseDecoder_JoinsMultiLineDataAndSkipsComments()
    {
        const string input = ": keep-alive\n\ndata: one\ndata: two\n\nevent: custom\nid: 7\ndata: three\n\nevent: dropped\n\ndata: four\n\n";

        using var reader = new StringReader(input);
        var events = await Collect(SseEventDecoder.DecodeAsync(reader));

        Assert.Equal(["one\ntwo", "three", "four"], events.Select(e => e.Data));
        Assert.Equal("custom", events[1].EventType);
        Assert.Equal("7", events[1].LastEventId);
        Assert.Equal("message", events[2].EventType);
    }

    [Fact]
    public async Task SseDecoder_DropsAnUnterminatedEvent()
    {
        using var reader = new StringReader("data: one\n\ndata: partial");
        var events = await Collect(SseEventDecoder.DecodeAsync(reader));

        Assert.Equal("one", Assert.Single(events).Data);
    }

    [Fact]
    public async Task SseDecoder_MapsBodyReadFailuresToUnreachable()
    {
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Collect(SseEventDecoder.DecodeAsync(new FailingReader()))
        );

        Assert.Equal(ProviderFailureKind.Unreachable, ex.Kind);
    }

    [Theory]
    [InlineData(new[] { "<think>plan</think>Hello" }, "Hello")]
    [InlineData(new[] { "<thi", "nk>plan</th", "ink>Hel", "lo" }, "Hello")]
    [InlineData(new[] { "<", "think>a", "<", "/think>", "\n\nHi" }, "Hi")]
    [InlineData(new[] { "Use a < b and ", "<thin" }, "Use a < b and <thin")]
    [InlineData(new[] { "<THINK>x</THINK>ok" }, "ok")]
    [InlineData(new[] { "before <think>never closed" }, "before ")]
    public void ThinkFilter_RemovesBlocksSplitAcrossDeltas(string[] deltas, string expected)
    {
        var filter = new ThinkingBlockStreamFilter();

        var visible = string.Concat(deltas.Select(filter.Push)) + filter.Flush();

        Assert.Equal(expected, visible);
    }

    [Theory]
    [InlineData("<think>never closed", true)]
    [InlineData("<think>closed</think>Hi", false)]
    [InlineData("Hi <thi", false)]
    public void ThinkFilter_ReportsTextThatEndsInsideABlock(string delta, bool expected)
    {
        var filter = new ThinkingBlockStreamFilter();

        filter.Push(delta);
        filter.Flush();

        Assert.Equal(expected, filter.EndedInsideBlock);
    }

    [Fact]
    public async Task Pump_CoalescesDeltasAndAlwaysFlushesTheFinalText()
    {
        var emitted = new List<string>();
        var pump = new StreamPump(emitted.Add, TimeSpan.FromHours(1));

        var text = await pump.RunAsync(Yield("a", "", "b", "c"), CancellationToken.None);

        Assert.Equal("abc", text);
        Assert.Equal(["a", "abc"], emitted);
    }

    [Fact]
    public async Task Pump_FlushesThePartialAndRethrowsAFault()
    {
        var emitted = new List<string>();
        var pump = new StreamPump(emitted.Add, TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<ProviderException>(() =>
            pump.RunAsync(Faulting("par", "tial"), CancellationToken.None)
        );

        Assert.Equal("partial", emitted.Last());
    }

    [Fact]
    public async Task Pump_ReportsCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        // ReSharper disable once AccessToDisposedClosure -- the pump finishes before the scope ends
        var pump = new StreamPump(_ => cts.Cancel(), TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(Hanging(cts.Token), cts.Token));
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }

    private static async IAsyncEnumerable<string> Yield(params string[] deltas)
    {
        foreach (var delta in deltas)
        {
            await Task.Yield();
            yield return delta;
        }
    }

    private static async IAsyncEnumerable<string> Faulting(params string[] deltas)
    {
        await foreach (var delta in Yield(deltas))
        {
            yield return delta;
        }

        throw new ProviderException(ProviderFailureKind.Unreachable, "lost");
    }

    private static async IAsyncEnumerable<string> Hanging([EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return "a";
        await Task.Delay(Timeout.Infinite, ct);
        yield return "b";
    }

    private sealed class FailingReader : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            throw new IOException("reset");
    }
}
