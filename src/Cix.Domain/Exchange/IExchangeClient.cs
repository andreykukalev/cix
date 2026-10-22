namespace Cix.Domain.Exchange;

public sealed record Balance(string Currency, decimal Available, decimal Reserved, decimal Total);

public sealed record Candle(DateTimeOffset Start, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>Trading rules for one symbol. Symbols are always <c>BASE-QUOTE</c>, for example <c>BTC-EUR</c>.</summary>
public sealed record PairInfo(
    string Symbol,
    string Base,
    string Quote,
    decimal BaseStep,
    decimal QuoteStep,
    decimal MinOrderSize,
    decimal MaxOrderSize,
    decimal MinOrderSizeQuote,
    bool Active);

public sealed record PlacedOrder(string VenueOrderId, string ClientOrderId);

public enum OrderStatus
{
    Unknown,
    New,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
}

/// <summary>An order as the exchange reports it, normalised to domain conventions.</summary>
/// <param name="RawStatus">The exchange's own status text, kept for the journal.</param>
public sealed record ExchangeOrder(
    string VenueOrderId,
    string ClientOrderId,
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal FilledQuantity,
    decimal FilledAmountQuote,
    decimal? Price,
    decimal TotalFee,
    string? FeeCurrency,
    OrderStatus Status,
    string RawStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsOpen => Status is OrderStatus.New or OrderStatus.PartiallyFilled;
}

/// <summary>The exchange returned an error response.</summary>
public class ExchangeException(string message, int? statusCode = null, string? errorId = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public string? ErrorId { get; } = errorId;
}

/// <summary>
/// An order submission may or may not have reached the exchange (timeout, connection drop or 5xx).
/// Never resubmit blindly: reconcile by <see cref="ClientOrderId"/> first.
/// </summary>
public sealed class OrderPlacementUncertainException(string clientOrderId, Exception? inner = null)
    : ExchangeException($"Order placement outcome unknown for client order ID {clientOrderId}; reconcile before retrying.")
{
    public string ClientOrderId { get; } = clientOrderId;
    public Exception? Cause { get; } = inner;
}

/// <summary>Exchange operations the engine needs. Implemented per venue and by the paper engine.</summary>
public interface IExchangeClient
{
    Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Places an order that has already passed the risk gate.</summary>
    /// <exception cref="OrderPlacementUncertainException">The outcome is unknown; reconcile before retrying.</exception>
    Task<PlacedOrder> PlaceLimitOrderAsync(OrderIntent approvedOrder, bool postOnly, CancellationToken cancellationToken);

    /// <summary>Returns the order, or null if the exchange doesn't (yet) know it.</summary>
    Task<ExchangeOrder?> GetOrderAsync(string venueOrderId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken cancellationToken);

    /// <summary>Requests cancellation. Returns false if the exchange doesn't know the order.</summary>
    Task<bool> CancelOrderAsync(string venueOrderId, CancellationToken cancellationToken);
}
