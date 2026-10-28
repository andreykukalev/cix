using Cix.Domain;
using Cix.Domain.Risk;
using Cix.Domain.Exchange;

namespace Cix.Engine.Tests;

/// <summary>Market data only, with the spike's BTC-EUR settings. Being an IMarketDataSource, it has no order methods.</summary>
internal sealed class FakeMarketData : IMarketDataSource
{
    public int PairCalls { get; private set; }

    public Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken)
    {
        PairCalls++;
        return Task.FromResult<IReadOnlyList<PairInfo>>(
        [
            new PairInfo("BTC-EUR", "BTC", "EUR", 0.00000001m, 0.01m, 0.00000001m, 200m, 0.1m, Active: true),
        ]);
    }

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Candle>>([]);
}

/// <summary>A clock the test moves. Safe to read and advance from several tasks at once (concurrent exits advance it from their delays).</summary>
internal sealed class MutableTime(DateTimeOffset start) : TimeProvider
{
    private long _utcTicks = start.UtcTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    // Elapsed time follows the same clock, so durations measured with timestamps advance with the test.
    public override long GetTimestamp() => Interlocked.Read(ref _utcTicks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}

/// <summary>Builds an <see cref="ApprovedOrder"/> without the gate, to test how clients handle orders the gate would refuse.</summary>
internal static class TestApproval
{
    public static ApprovedOrder Approved(this OrderIntent order) => new(order, DateTimeOffset.UnixEpoch);
}
