using Cix.Domain.Exchange;

namespace Cix.Domain.Risk;

/// <summary>
/// The active strategy's own limits on new buys, from its spec. They only ever tighten: an order that passes them still
/// meets the <see cref="RiskGate"/> and its <see cref="RiskLimits"/>, which stay final. Null means the spec sets no such limit.
/// </summary>
/// <param name="OrderQuote">The strategy's order size. A buy worth more than <see cref="OrderQuoteTolerance"/> times it is reduced to it.</param>
/// <param name="MaxPositionQuotePerSymbol">Most one coin may hold, counting buys still working.</param>
/// <param name="MaxExposureQuote">Most all coins together may hold, counting buys still working.</param>
/// <param name="DailyLossStopQuote">No new buys once today's loss reaches this.</param>
/// <param name="MaxNewPositionsPerRun">Buys one agent run may place.</param>
/// <param name="EntryCooldown">Least time between two buys.</param>
/// <param name="OrderFraction">The order size as a share of the capital (ADR-0030); with <paramref name="OrderQuote"/> the smaller applies.</param>
/// <param name="MaxPositionFractionPerSymbol">Most in one coin as a share of the capital.</param>
/// <param name="MaxExposureFraction">Most in all coins together as a share of the capital.</param>
/// <param name="DailyLossStopFraction">The daily stop as a share of the day's starting capital.</param>
/// <param name="CapitalCeiling">The most that counts as capital (the risk limits' allocation); null means no ceiling.</param>
public sealed record StrategyLimits(
    decimal? OrderQuote = null,
    decimal? MaxPositionQuotePerSymbol = null,
    decimal? MaxExposureQuote = null,
    decimal? DailyLossStopQuote = null,
    int? MaxNewPositionsPerRun = null,
    TimeSpan? EntryCooldown = null,
    decimal? OrderFraction = null,
    decimal? MaxPositionFractionPerSymbol = null,
    decimal? MaxExposureFraction = null,
    decimal? DailyLossStopFraction = null,
    decimal? CapitalCeiling = null)
{
    /// <summary>Room for rounding to the exchange's steps: the scanner sizes orders to the order size, not under it.</summary>
    public const decimal OrderQuoteTolerance = 1.10m;

    public static readonly StrategyLimits None = new();

    /// <summary>True when any limit is a share of the capital, so it moves with the account's value.</summary>
    public bool ScalesWithCapital => OrderFraction is not null || MaxPositionFractionPerSymbol is not null
        || MaxExposureFraction is not null || DailyLossStopFraction is not null;

    /// <summary>The capital an equity counts as: the equity, never below 0 and never above <see cref="CapitalCeiling"/>.</summary>
    public decimal CapitalOf(decimal equity) => Math.Max(0m, CapitalCeiling is { } ceiling ? Math.Min(equity, ceiling) : equity);

    /// <summary>
    /// These limits in money for an account worth <paramref name="equity"/>, whose day started at <paramref name="dayStartEquity"/>:
    /// each share of the capital becomes an amount (rounded down to the cent), and where the spec also names an amount the
    /// smaller applies. The result has no shares left, so it doesn't move again.
    /// </summary>
    public StrategyLimits At(decimal equity, decimal dayStartEquity)
    {
        if (!ScalesWithCapital)
            return this;
        var capital = CapitalOf(equity);
        return new StrategyLimits(
            Smaller(OrderQuote, OrderFraction, capital),
            Smaller(MaxPositionQuotePerSymbol, MaxPositionFractionPerSymbol, capital),
            Smaller(MaxExposureQuote, MaxExposureFraction, capital),
            Smaller(DailyLossStopQuote, DailyLossStopFraction, CapitalOf(dayStartEquity)),
            MaxNewPositionsPerRun,
            EntryCooldown);
    }

    /// <summary>These limits in money for <paramref name="portfolio"/>: its equity, and its equity at the start of the day.</summary>
    public StrategyLimits At(PortfolioSnapshot portfolio)
    {
        ArgumentNullException.ThrowIfNull(portfolio);
        return At(portfolio.Equity, portfolio.Equity - portfolio.PnlToday);
    }

    private static decimal? Smaller(decimal? quote, decimal? fraction, decimal capital)
    {
        if (fraction is not { } f)
            return quote;
        var scaled = Math.Round(f * capital, 2, MidpointRounding.ToZero);
        return quote is { } q ? Math.Min(q, scaled) : scaled;
    }
}

/// <summary>What the strategy limits depend on besides the portfolio: the engine's last buy, and the buys in this agent run.</summary>
public sealed record StrategyActivity(DateTimeOffset? LastBuyAt, int BuysThisRun);

/// <param name="Order">What may go on to the risk gate; null when refused.</param>
public sealed record StrategyVerdict(OrderIntent? Order, bool Resized, string Rule, string Reason);

/// <summary>
/// Applies <see cref="StrategyLimits"/> to a buy before the risk gate sees it. Sells always pass: they reduce risk, and a
/// strategy change must never trap a position. Deterministic and side-effect free, like the gate.
/// </summary>
public static class StrategyGate
{
    public static StrategyVerdict CheckBuy(
        OrderIntent intent, PortfolioSnapshot portfolio, StrategyLimits limits, StrategyActivity activity, DateTimeOffset now, decimal baseStep)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(activity);
        if (intent.Side != OrderSide.Buy)
            return new StrategyVerdict(intent, false, "ok", "Sells aren't limited by the strategy.");
        limits = limits.At(portfolio); // shares of the capital, in money for this account now (ADR-0030)

        if (limits.DailyLossStopQuote is { } stop && -portfolio.PnlToday >= stop)
            return No("strategy-daily-loss-stop", $"Today's loss has reached the strategy's daily stop of {stop:0.##}; no new buys until tomorrow (UTC).");
        if (limits.MaxNewPositionsPerRun is { } perRun && activity.BuysThisRun >= perRun)
            return No("strategy-positions-per-run", $"This run already placed {activity.BuysThisRun} buy(s); the strategy allows {perRun} per run.");
        if (limits.EntryCooldown is { } cooldown && activity.LastBuyAt is { } last && now - last < cooldown)
            return No("strategy-cooldown", $"The strategy waits {cooldown.TotalMinutes:0} minutes between buys; the last was {(now - last).TotalMinutes:0} minutes ago.");

        var positionQuote = portfolio.PositionQuoteBySymbol.GetValueOrDefault(intent.Symbol)
            + (portfolio.OpenBuyQuoteBySymbol?.GetValueOrDefault(intent.Symbol) ?? 0m);
        var rooms = new List<(string Rule, decimal Room, string What)>();
        if (limits.OrderQuote is { } orderQuote && intent.NotionalQuote > orderQuote * StrategyLimits.OrderQuoteTolerance)
            rooms.Add(("strategy-order-size", orderQuote, $"the strategy's order size of {orderQuote:0.##}"));
        if (limits.MaxPositionQuotePerSymbol is { } perSymbol)
            rooms.Add(("strategy-position-cap", perSymbol - positionQuote, $"the strategy's cap of {perSymbol:0.##} per coin"));
        if (limits.MaxExposureQuote is { } exposure)
            rooms.Add(("strategy-exposure-cap", exposure - portfolio.TotalExposureQuote, $"the strategy's total exposure cap of {exposure:0.##}"));

        if (rooms.Count == 0)
            return new StrategyVerdict(intent, false, "ok", "Within the strategy's limits.");
        var (rule, room, what) = rooms.MinBy(r => r.Room);
        if (room <= 0)
            return No(rule, $"No room left under {what}.");
        if (intent.NotionalQuote <= room)
            return new StrategyVerdict(intent, false, "ok", "Within the strategy's limits.");

        var resized = intent with { BaseSize = Steps.Floor(room / intent.LimitPrice, baseStep > 0 ? baseStep : 0.00000001m) };
        return resized.BaseSize <= 0
            ? No(rule, $"The room left under {what} is too small for one unit.")
            : new StrategyVerdict(resized, true, rule, $"Order reduced to {room:0.##} to stay within {what}.");
    }

    private static StrategyVerdict No(string rule, string reason) => new(null, false, rule, reason);
}
