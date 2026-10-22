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

internal sealed class MutableTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>Builds an <see cref="ApprovedOrder"/> without the gate, to test how clients handle orders the gate would refuse.</summary>
internal static class TestApproval
{
    public static ApprovedOrder Approved(this OrderIntent order) => new(order, DateTimeOffset.UnixEpoch);
}
