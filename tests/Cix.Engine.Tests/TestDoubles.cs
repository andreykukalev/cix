using Cix.Domain;
using Cix.Domain.Exchange;

namespace Cix.Engine.Tests;

/// <summary>Market data only: pairs from the spike's BTC-EUR settings. Order calls must never reach it.</summary>
internal sealed class FakeMarketData : IExchangeClient
{
    public Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PairInfo>>(
        [
            new PairInfo("BTC-EUR", "BTC", "EUR", 0.00000001m, 0.01m, 0.00000001m, 200m, 0.1m, Active: true),
        ]);

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Candle>>([]);

    public Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken) => throw new NotSupportedException("Paper must not read real balances.");

    public Task<PlacedOrder> PlaceLimitOrderAsync(OrderIntent approvedOrder, bool postOnly, CancellationToken cancellationToken) => throw new NotSupportedException("Paper must not place real orders.");

    public Task<ExchangeOrder?> GetOrderAsync(string venueOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> CancelOrderAsync(string venueOrderId, CancellationToken cancellationToken) => throw new NotSupportedException("Paper must not cancel real orders.");
}

internal sealed class MutableTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}
