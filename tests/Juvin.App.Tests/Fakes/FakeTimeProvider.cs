namespace Juvin.App.Tests.Fakes;

/// <summary>A clock that only moves when the test advances it; timestamps follow the same clock.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _now += by;
}
