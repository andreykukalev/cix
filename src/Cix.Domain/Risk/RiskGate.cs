namespace Cix.Domain.Risk;

public enum RiskOutcome
{
    Accepted,
    Resized,
    Rejected,
}

/// <summary>The gate's decision. <see cref="Order"/> is what may be sent; null when rejected.</summary>
/// <param name="Rule">Short machine-readable rule name, for example <c>allocation-cap</c>.</param>
/// <param name="Reason">Human- and agent-readable explanation.</param>
public sealed record RiskVerdict(RiskOutcome Outcome, OrderIntent? Order, string Rule, string Reason)
{
    public static RiskVerdict Accept(OrderIntent order) => new(RiskOutcome.Accepted, order, "ok", "Within all limits.");
    public static RiskVerdict Resize(OrderIntent order, string rule, string reason) => new(RiskOutcome.Resized, order, rule, reason);
    public static RiskVerdict Reject(string rule, string reason) => new(RiskOutcome.Rejected, null, rule, reason);
}

/// <summary>
/// Final authority on every order. Deterministic and side-effect free.
/// Risk-reducing sells stay allowed during drawdown and daily-loss halts; the kill switch blocks everything.
/// </summary>
public sealed class RiskGate
{
    private const int SizeDecimals = 8;
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

    public RiskVerdict Evaluate(OrderIntent intent, PortfolioSnapshot portfolio, MarketSnapshot market)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(market);

        if (portfolio.KillSwitchEngaged)
            return RiskVerdict.Reject("kill-switch", "The kill switch is engaged; no orders are allowed.");

        if (!_limits.AllowedSymbols.Contains(intent.Symbol))
            return RiskVerdict.Reject("symbol-allowlist", $"{intent.Symbol} is not in the allowed symbols.");

        if (intent.BaseSize <= 0 || intent.LimitPrice <= 0)
            return RiskVerdict.Reject("invalid-order", "Size and limit price must be positive.");

        if (!string.Equals(market.Symbol, intent.Symbol, StringComparison.Ordinal) || market.ReferencePrice <= 0)
            return RiskVerdict.Reject("market-data", "No valid market data for this symbol.");

        if (_time.GetUtcNow() - market.AsOf > HardCeilings.MaxMarketDataAge)
            return RiskVerdict.Reject("stale-market-data", "Market data is too old to price this order.");

        var deviation = Math.Abs(intent.LimitPrice - market.ReferencePrice) / market.ReferencePrice;
        if (deviation > _limits.MaxPriceDeviationFraction)
            return RiskVerdict.Reject("price-band", $"Limit price is {deviation:P2} from the reference price; the maximum is {_limits.MaxPriceDeviationFraction:P2}.");

        if (portfolio.OrdersInLastMinute >= _limits.MaxOrdersPerMinute)
            return RiskVerdict.Reject("order-rate", $"At most {_limits.MaxOrdersPerMinute} orders per minute.");

        return intent.Side == OrderSide.Sell
            ? EvaluateSell(intent, portfolio)
            : EvaluateBuy(intent, portfolio);
    }

    private RiskVerdict EvaluateSell(OrderIntent intent, PortfolioSnapshot portfolio)
    {
        var positionQuote = portfolio.PositionQuoteBySymbol.GetValueOrDefault(intent.Symbol);
        if (positionQuote <= 0)
            return RiskVerdict.Reject("no-position", "Spot only: there is no position to sell.");

        if (intent.NotionalQuote <= positionQuote)
            return RiskVerdict.Accept(intent);

        var resized = intent with { BaseSize = RoundDown(positionQuote / intent.LimitPrice) };
        return resized.BaseSize <= 0
            ? RiskVerdict.Reject("no-position", "The position is too small to sell at this price.")
            : RiskVerdict.Resize(resized, "position-size", "Sell reduced to the current position size.");
    }

    private RiskVerdict EvaluateBuy(OrderIntent intent, PortfolioSnapshot portfolio)
    {
        if (portfolio.PeakEquity > 0)
        {
            var drawdown = (portfolio.PeakEquity - portfolio.Equity) / portfolio.PeakEquity;
            if (drawdown >= _limits.MaxDrawdownFraction)
                return RiskVerdict.Reject("drawdown-halt", $"Drawdown is {drawdown:P2}; new buys are halted until the owner resumes trading.");
        }

        if (-portfolio.RealizedPnlToday >= _limits.MaxDailyLossQuote)
            return RiskVerdict.Reject("daily-loss", "The daily loss limit is reached; new buys are halted until tomorrow (UTC).");

        var positionQuote = portfolio.PositionQuoteBySymbol.GetValueOrDefault(intent.Symbol);
        var room = new[]
        {
            ("max-order", _limits.MaxOrderQuote),
            ("position-cap", _limits.MaxPositionQuotePerSymbol - positionQuote),
            ("allocation-cap", _limits.AllocationQuote - portfolio.TotalExposureQuote),
        }.MinBy(r => r.Item2);

        if (room.Item2 <= 0)
            return RiskVerdict.Reject(room.Item1, $"No room left under {room.Item1}.");

        if (intent.NotionalQuote <= room.Item2)
            return RiskVerdict.Accept(intent);

        var resized = intent with { BaseSize = RoundDown(room.Item2 / intent.LimitPrice) };
        return resized.BaseSize <= 0
            ? RiskVerdict.Reject(room.Item1, $"Remaining room under {room.Item1} is too small for one unit.")
            : RiskVerdict.Resize(resized, room.Item1, $"Order reduced to {room.Item2:0.##} quote to stay within {room.Item1}.");
    }

    private static decimal RoundDown(decimal value) =>
        Math.Round(value, SizeDecimals, MidpointRounding.ToZero);
}
