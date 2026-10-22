namespace Cix.Domain.Exchange;

public sealed record Balance(string Currency, decimal Available, decimal Reserved, decimal Total);

public sealed record Candle(DateTimeOffset Start, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

public sealed record PlacedOrder(string VenueOrderId, string ClientOrderId);

/// <summary>Exchange operations the engine needs. Implemented per venue and by the paper engine.</summary>
public interface IExchangeClient
{
    Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Places an order that has already passed the risk gate.</summary>
    Task<PlacedOrder> PlaceLimitOrderAsync(OrderIntent approvedOrder, CancellationToken cancellationToken);

    Task CancelOrderAsync(string venueOrderId, CancellationToken cancellationToken);
}
