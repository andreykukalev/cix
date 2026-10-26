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
public sealed record StrategyLimits(
    decimal? OrderQuote = null,
    decimal? MaxPositionQuotePerSymbol = null,
    decimal? MaxExposureQuote = null,
    decimal? DailyLossStopQuote = null,
    int? MaxNewPositionsPerRun = null,
    TimeSpan? EntryCooldown = null)
{
    /// <summary>Room for rounding to the exchange's steps: the scanner sizes orders to the order size, not under it.</summary>
    public const decimal OrderQuoteTolerance = 1.10m;

    public static readonly StrategyLimits None = new();
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
