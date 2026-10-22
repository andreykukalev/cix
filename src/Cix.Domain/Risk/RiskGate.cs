using Cix.Domain.Exchange;

namespace Cix.Domain.Risk;

public enum RiskOutcome
{
    Accepted,
    Resized,
    Rejected,
}

/// <summary>
/// The gate's decision. <see cref="Order"/> is what may be sent; null when rejected. Only the gate can build an accepting
/// verdict, because only it can create an <see cref="ApprovedOrder"/>.
/// </summary>
/// <param name="Rule">Short machine-readable rule name, for example <c>allocation-cap</c>.</param>
/// <param name="Reason">Human- and agent-readable explanation.</param>
public sealed record RiskVerdict(RiskOutcome Outcome, ApprovedOrder? Order, string Rule, string Reason)
{
    internal static RiskVerdict Accept(ApprovedOrder order) => new(RiskOutcome.Accepted, order, "ok", "Within all limits.");
    internal static RiskVerdict Resize(ApprovedOrder order, string rule, string reason) => new(RiskOutcome.Resized, order, rule, reason);
    public static RiskVerdict Reject(string rule, string reason) => new(RiskOutcome.Rejected, null, rule, reason);
}

/// <summary>
/// Final authority on every order. Deterministic and side-effect free.
/// Risk-reducing sells stay allowed during drawdown and daily-loss halts; the kill switch blocks everything.
/// </summary>
public sealed class RiskGate
{
    private readonly RiskLimits _limits;
    private readonly TimeProvider _time;

    public RiskGate(RiskLimits limits, TimeProvider? time = null)
    {
        var errors = limits.Validate();
        if (errors.Count > 0)
            throw new ArgumentException("Invalid risk limits: " + string.Join(" ", errors), nameof(limits));

        _limits = limits;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Decides whether <paramref name="intent"/> may be sent. With <paramref name="pair"/>, also enforces the exchange's
    /// rules: the pair must be active, size and price must sit on its steps (round before calling), resizes round down to
    /// its base step, and the final order must meet its minimum and maximum sizes.
    /// </summary>
    public RiskVerdict Evaluate(OrderIntent intent, PortfolioSnapshot portfolio, MarketSnapshot market, PairInfo? pair = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(market);

        if (portfolio.KillSwitchEngaged)
            return RiskVerdict.Reject("kill-switch", "The kill switch is engaged; no orders are allowed.");

        if (!_limits.AllowedSymbols.Contains(intent.Symbol))
            return RiskVerdict.Reject("symbol-allowlist", $"{intent.Symbol} is not in the allowed symbols.");

        if (intent.BaseSize <= 0 || intent.LimitPrice <= 0 || string.IsNullOrWhiteSpace(intent.ClientOrderId))
            return RiskVerdict.Reject("invalid-order", "Size and limit price must be positive and the client order ID set.");

        if (pair is not null)
        {
            if (!string.Equals(pair.Symbol, intent.Symbol, StringComparison.Ordinal) || !pair.Active)
                return RiskVerdict.Reject("pair-inactive", $"{intent.Symbol} is not tradable on the exchange.");
            if (!IsMultiple(intent.LimitPrice, pair.QuoteStep) || !IsMultiple(intent.BaseSize, pair.BaseStep))
                return RiskVerdict.Reject("step", $"Size must be a multiple of {pair.BaseStep} and price of {pair.QuoteStep}.");
        }

        if (!string.Equals(market.Symbol, intent.Symbol, StringComparison.Ordinal) || market.ReferencePrice <= 0)
            return RiskVerdict.Reject("market-data", "No valid market data for this symbol.");

        var age = _time.GetUtcNow() - market.AsOf;
        if (age > HardCeilings.MaxMarketDataAge || age < TimeSpan.Zero)
            return RiskVerdict.Reject("stale-market-data", "Market data is too old (or from the future) to price this order.");

        var deviation = Math.Abs(intent.LimitPrice - market.ReferencePrice) / market.ReferencePrice;
        if (deviation > _limits.MaxPriceDeviationFraction)
            return RiskVerdict.Reject("price-band", $"Limit price is {deviation:P2} from the reference price; the maximum is {_limits.MaxPriceDeviationFraction:P2}.");

        if (portfolio.OrdersInLastMinute >= _limits.MaxOrdersPerMinute)
            return RiskVerdict.Reject("order-rate", $"At most {_limits.MaxOrdersPerMinute} orders per minute.");

        var step = pair?.BaseStep ?? DefaultSizeStep;
        var sized = intent.Side == OrderSide.Sell
            ? SizeSell(intent, portfolio, step)
            : SizeBuy(intent, portfolio, step);
        if (sized.Order is null)
            return RiskVerdict.Reject(sized.Rule, sized.Reason);

        var order = sized.Order;
        var (rule, reason, resized) = (sized.Rule, sized.Reason, sized.Resized);
        if (pair is not null)
        {
            if (order.BaseSize > pair.MaxOrderSize)
            {
                order = order with { BaseSize = RoundDown(pair.MaxOrderSize, step) };
                (rule, reason, resized) = ("exchange-max-size", $"Order reduced to the exchange maximum of {pair.MaxOrderSize}.", true);
            }

            if (order.BaseSize < pair.MinOrderSize || order.NotionalQuote < pair.MinOrderSizeQuote)
            {
                return RiskVerdict.Reject(
                    "exchange-min-size",
                    resized
                        ? $"After {rule}, the order is below the exchange minimum ({pair.MinOrderSize} base, {pair.MinOrderSizeQuote} quote)."
                        : $"The order is below the exchange minimum ({pair.MinOrderSize} base, {pair.MinOrderSizeQuote} quote).");
            }
        }

        var approved = new ApprovedOrder(order, _time.GetUtcNow());
        return resized ? RiskVerdict.Resize(approved, rule, reason) : RiskVerdict.Accept(approved);
    }

    private const decimal DefaultSizeStep = 0.00000001m;

    private readonly record struct Sizing(OrderIntent? Order, bool Resized, string Rule, string Reason)
    {
        public static Sizing Keep(OrderIntent order) => new(order, false, "ok", "Within all limits.");
        public static Sizing Shrink(OrderIntent order, string rule, string reason) => new(order, true, rule, reason);
        public static Sizing No(string rule, string reason) => new(null, false, rule, reason);
    }

    private static Sizing SizeSell(OrderIntent intent, PortfolioSnapshot portfolio, decimal step)
    {
        var positionQuote = portfolio.PositionQuoteBySymbol.GetValueOrDefault(intent.Symbol);
        if (positionQuote <= 0)
            return Sizing.No("no-position", "Spot only: there is no position to sell.");

        if (intent.NotionalQuote <= positionQuote)
            return Sizing.Keep(intent);

        var resized = intent with { BaseSize = RoundDown(positionQuote / intent.LimitPrice, step) };
        return resized.BaseSize <= 0
            ? Sizing.No("no-position", "The position is too small to sell at this price.")
            : Sizing.Shrink(resized, "position-size", "Sell reduced to the current position size.");
    }

    private Sizing SizeBuy(OrderIntent intent, PortfolioSnapshot portfolio, decimal step)
    {
        if (portfolio.PeakEquity > 0)
        {
            var drawdown = (portfolio.PeakEquity - portfolio.Equity) / portfolio.PeakEquity;
            if (drawdown >= _limits.MaxDrawdownFraction)
                return Sizing.No("drawdown-halt", $"Drawdown is {drawdown:P2}; new buys are halted until the owner resumes trading.");
        }

        if (-portfolio.PnlToday >= _limits.MaxDailyLossQuote)
            return Sizing.No("daily-loss", "The daily loss limit is reached; new buys are halted until tomorrow (UTC).");

        // Buys already working count toward the position they will become.
        var positionQuote = portfolio.PositionQuoteBySymbol.GetValueOrDefault(intent.Symbol)
            + (portfolio.OpenBuyQuoteBySymbol?.GetValueOrDefault(intent.Symbol) ?? 0m);
        var room = new[]
        {
            ("max-order", _limits.MaxOrderQuote),
            ("position-cap", _limits.MaxPositionQuotePerSymbol - positionQuote),
            ("allocation-cap", _limits.AllocationQuote - portfolio.TotalExposureQuote),
        }.MinBy(r => r.Item2);

        if (room.Item2 <= 0)
            return Sizing.No(room.Item1, $"No room left under {room.Item1}.");

        if (intent.NotionalQuote <= room.Item2)
            return Sizing.Keep(intent);

        var resized = intent with { BaseSize = RoundDown(room.Item2 / intent.LimitPrice, step) };
        return resized.BaseSize <= 0
            ? Sizing.No(room.Item1, $"Remaining room under {room.Item1} is too small for one unit.")
            : Sizing.Shrink(resized, room.Item1, $"Order reduced to {room.Item2:0.##} quote to stay within {room.Item1}.");
    }

    private static decimal RoundDown(decimal value, decimal step) => Steps.Floor(value, step);

    private static bool IsMultiple(decimal value, decimal step) => Steps.IsMultiple(value, step);
}
