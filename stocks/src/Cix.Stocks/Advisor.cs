using Cix.Domain.Exchange;
using Cix.Domain.MarketData;

namespace Cix.Stocks;

/// <summary>Daily bars for one instrument, adjusted for splits and dividends, in ascending order.</summary>
public sealed record PriceHistory(Instrument Instrument, IReadOnlyList<Candle> Bars);

/// <summary>Everything the advisor looks at for one day's advice.</summary>
/// <param name="Today">The day the advice is for; bars older than <see cref="AdvisorSettings.MaxBarAgeDays"/> before it are stale.</param>
/// <param name="BaseCurrency">The allocation's currency, for example <c>EUR</c>.</param>
/// <param name="Capital">The allocation: what sizing is a fraction of, in the base currency.</param>
/// <param name="Cash">Cash free for new positions, in the base currency.</param>
/// <param name="FxToBase">Units of the base currency per unit of each other currency, for example <c>USD → 0.92</c>.</param>
/// <param name="Watchlist">The instruments the owner chose, with their bars. Held instruments must be in it to get advice.</param>
/// <param name="BenchmarkBars">The bars of <see cref="AdvisorSettings.BenchmarkSymbol"/>, for the market regime.</param>
/// <param name="Holdings">What the owner recorded as held.</param>
public sealed record AdvisorInput(
    DateOnly Today,
    string BaseCurrency,
    decimal Capital,
    decimal Cash,
    IReadOnlyDictionary<string, decimal> FxToBase,
    IReadOnlyList<PriceHistory> Watchlist,
    IReadOnlyList<Candle> BenchmarkBars,
    IReadOnlyList<Holding> Holdings);

public enum MarketRegime
{
    /// <summary>The benchmark's data is missing or stale. Treated as risk-off: no new entries, the tighter trail.</summary>
    Unknown,
    RiskOn,
    RiskOff,
}

/// <summary>What the owner should do at the broker. Listed in this order: protecting what is held comes before buying.</summary>
public enum ActionKind
{
    Sell,
    SetStop,
    RaiseStop,
    Buy,
}

/// <summary>One action for the owner to carry out by hand.</summary>
/// <param name="Quantity">Shares to buy or sell, or the shares a stop covers.</param>
/// <param name="ReferencePrice">The last close the advice was computed from, in the instrument's currency.</param>
/// <param name="Stop">The stop to place (buy, set, raise), or the one already there (sell).</param>
/// <param name="Reason">One of <see cref="Reasons"/>.</param>
public sealed record Recommendation(ActionKind Kind, string Symbol, decimal Quantity, decimal ReferencePrice, decimal? Stop, string Reason);

/// <summary>Why the advisor said nothing about a symbol (or the market, under the benchmark's symbol).</summary>
public sealed record AdvisorNote(string Symbol, string Reason);

public sealed record Advice(DateOnly AsOf, MarketRegime Regime, IReadOnlyList<Recommendation> Actions, IReadOnlyList<AdvisorNote> Notes);

/// <summary>Reason codes for recommendations and notes: stable strings for the journal and the dashboard.</summary>
public static class Reasons
{
    public const string Breakout = "breakout";
    public const string TrendBroken = "trend-broken";
    public const string BelowStop = "below-stop";
    public const string BelowTrailingStop = "below-trailing-stop";
    public const string NoStop = "no-stop";
    public const string Trail = "trail";
    public const string RiskOffTighten = "risk-off-tighten";

    public const string RegimeRiskOff = "regime-risk-off";
    public const string RegimeUnknown = "regime-unknown";
    public const string NoData = "no-data";
    public const string StaleData = "stale-data";
    public const string NotEnoughData = "not-enough-data";
    public const string NoFxRate = "no-fx-rate";
    public const string TooSmall = "too-small";
    public const string NoCash = "no-cash";
    public const string MaxPositions = "max-positions";
    public const string CheckHolding = "check-holding";
    public const string DuplicateHolding = "duplicate-holding";
}

/// <summary>
/// The deterministic core of the stocks advisory (ADR-0023): from daily bars, the market regime and the owner's recorded
/// holdings, it lists what to do at the broker. It never places anything; the owner does.
/// <list type="bullet">
/// <item>Held positions: sell when the close falls under the trend line or the stop; otherwise keep a stop under each one,
/// trailing <see cref="AdvisorSettings.TrailingStopAtr"/> ATRs below the highest close since entry (tighter while
/// risk-off). A stop is only ever raised, never lowered.</item>
/// <item>Entries, only while risk-on: the close above the slow average, the fast average above the slow one, and a new
/// <see cref="AdvisorSettings.BreakoutDays"/>-day closing high. Strongest momentum first, sized so the loss at the stop
/// plus a gap stays within <see cref="AdvisorSettings.RiskPerTrade"/>, within the position cap and the free cash.</item>
/// </list>
/// Stale or missing data never produces an action, only a note; while the benchmark's data is missing, held positions keep
/// the normal trail and nothing is bought.
/// </summary>
public static class Advisor
{
    public static Advice Evaluate(AdvisorInput input, AdvisorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (input.Capital <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), input.Capital, "Capital must be positive.");
        if (input.Cash < 0)
            throw new ArgumentOutOfRangeException(nameof(input), input.Cash, "Cash can't be negative.");

        var actions = new List<Recommendation>();
        var notes = new List<AdvisorNote>();
        var histories = new Dictionary<string, PriceHistory>(StringComparer.Ordinal);
        foreach (var history in input.Watchlist)
            histories.TryAdd(history.Instrument.Symbol, history);

        var regime = Regime(input, settings, notes);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var holding in input.Holdings)
        {
            if (holding.Quantity <= 0)
                continue;
            if (!seen.Add(holding.Symbol))
            {
                notes.Add(new(holding.Symbol, Reasons.DuplicateHolding));
                continue;
            }

            if (!histories.TryGetValue(holding.Symbol, out var history))
            {
                notes.Add(new(holding.Symbol, Reasons.NoData));
                continue;
            }

            AdviseHolding(holding, history, regime, input.Today, settings, actions, notes);
        }

        AdviseEntries(input, histories.Values, regime, settings, actions, notes);

        return new Advice(input.Today, regime, actions.OrderBy(a => a.Kind).ToList(), notes);
    }

    private static MarketRegime Regime(AdvisorInput input, AdvisorSettings settings, List<AdvisorNote> notes)
    {
        var bars = input.BenchmarkBars;
        if (!IsFresh(bars, input.Today, settings) || TrendIndicators.Sma(bars, settings.RegimeSmaDays) is not { } sma)
        {
            notes.Add(new(settings.BenchmarkSymbol, Reasons.RegimeUnknown));
            return MarketRegime.Unknown;
        }

        return bars[^1].Close > sma ? MarketRegime.RiskOn : MarketRegime.RiskOff;
    }

    private static void AdviseHolding(
        Holding holding, PriceHistory history, MarketRegime regime, DateOnly today, AdvisorSettings settings,
        List<Recommendation> actions, List<AdvisorNote> notes)
    {
        var bars = history.Bars;
        if (!IsFresh(bars, today, settings))
        {
            notes.Add(new(holding.Symbol, bars.Count == 0 ? Reasons.NoData : Reasons.StaleData));
            return;
        }

        if (TrendIndicators.Sma(bars, settings.SlowSmaDays) is not { } trend || Indicators.Atr(bars, settings.AtrDays) is not { } atr || atr <= 0)
        {
            notes.Add(new(holding.Symbol, Reasons.NotEnoughData));
            return;
        }

        var close = bars[^1].Close;
        var tick = history.Instrument.PriceTick;
        Recommendation Act(ActionKind kind, decimal? stop, string reason) =>
            new(kind, holding.Symbol, holding.Quantity, close, stop, reason);

        if (close < trend)
        {
            actions.Add(Act(ActionKind.Sell, holding.Stop, Reasons.TrendBroken));
            return;
        }

        if (holding.Stop is { } current && close <= current)
        {
            // A stop the previous close was already under would have sold at the broker by now: the record is off (a typo,
            // or a split, since recorded stops are in raw prices and bars are adjusted). Ask rather than sell.
            if (bars.Count >= 2 && bars[^2].Close <= current)
                notes.Add(new(holding.Symbol, Reasons.CheckHolding));
            else
                actions.Add(Act(ActionKind.Sell, current, Reasons.BelowStop));
            return;
        }

        // Only a confirmed risk-off tightens: a stop is never lowered again, so missing data mustn't move it.
        var riskOff = regime == MarketRegime.RiskOff;
        var trail = riskOff ? settings.RiskOffTrailingStopAtr : settings.TrailingStopAtr;
        var high = TrendIndicators.HighestCloseSince(bars, holding.OpenedOn) ?? close;
        var stop = FloorTo(high - (trail * atr), tick);

        if (stop >= close)
        {
            actions.Add(Act(ActionKind.Sell, holding.Stop, Reasons.BelowTrailingStop));
            return;
        }

        if (stop <= 0)
        {
            notes.Add(new(holding.Symbol, Reasons.NotEnoughData));
            return;
        }

        if (holding.Stop is not { } existing)
            actions.Add(Act(ActionKind.SetStop, stop, Reasons.NoStop));
        else if (stop > existing * (1 + settings.MinStopRaiseFraction))
            actions.Add(Act(ActionKind.RaiseStop, stop, riskOff ? Reasons.RiskOffTighten : Reasons.Trail));
    }

    private static void AdviseEntries(
        AdvisorInput input, IEnumerable<PriceHistory> histories, MarketRegime regime, AdvisorSettings settings,
        List<Recommendation> actions, List<AdvisorNote> notes)
    {
        if (regime != MarketRegime.RiskOn)
        {
            if (regime == MarketRegime.RiskOff)
                notes.Add(new(settings.BenchmarkSymbol, Reasons.RegimeRiskOff));
            return;
        }

        var held = input.Holdings.Where(h => h.Quantity > 0).Select(h => h.Symbol).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<(PriceHistory History, decimal Close, decimal Atr, decimal Momentum, decimal Fx)>();
        foreach (var history in histories)
        {
            var symbol = history.Instrument.Symbol;
            var bars = history.Bars;
            if (held.Contains(symbol))
                continue;
            if (!IsFresh(bars, input.Today, settings))
            {
                notes.Add(new(symbol, bars.Count == 0 ? Reasons.NoData : Reasons.StaleData));
                continue;
            }

            if (bars.Count < settings.MinBars
                || TrendIndicators.Sma(bars, settings.SlowSmaDays) is not { } slow
                || TrendIndicators.Sma(bars, settings.FastSmaDays) is not { } fast
                || TrendIndicators.PriorHighestClose(bars, settings.BreakoutDays) is not { } priorHigh
                || Indicators.Atr(bars, settings.AtrDays) is not { } atr
                || atr <= 0)
            {
                notes.Add(new(symbol, Reasons.NotEnoughData));
                continue;
            }

            var close = bars[^1].Close;
            if (close <= slow || fast <= slow || close <= priorHigh)
                continue;

            if (FxRate(input, history.Instrument.Currency) is not { } fx)
            {
                notes.Add(new(symbol, Reasons.NoFxRate));
                continue;
            }

            var from = bars[Math.Max(0, bars.Count - 1 - settings.MomentumDays)].Close;
            candidates.Add((history, close, atr, Indicators.Change(from, close) ?? 0m, fx));
        }

        var slots = settings.MaxPositions - held.Count;
        if (Budget(input, histories, notes) is not { } cash)
            return;
        foreach (var c in candidates.OrderByDescending(c => c.Momentum).ThenBy(c => c.History.Instrument.Symbol, StringComparer.Ordinal))
        {
            var instrument = c.History.Instrument;
            if (slots <= 0)
            {
                notes.Add(new(instrument.Symbol, Reasons.MaxPositions));
                continue;
            }

            var stop = FloorTo(c.Close - (settings.InitialStopAtr * c.Atr), instrument.PriceTick);
            if (stop <= 0)
            {
                notes.Add(new(instrument.Symbol, Reasons.NotEnoughData));
                continue;
            }

            var riskPerShare = (c.Close - stop + (settings.GapAtr * c.Atr)) * c.Fx;
            var pricePerShare = c.Close * c.Fx;
            var byRisk = input.Capital * settings.RiskPerTrade / riskPerShare;
            var byCap = input.Capital * settings.MaxPositionFraction / pricePerShare;
            var byCash = cash / pricePerShare;
            var quantity = FloorTo(Math.Min(Math.Min(byRisk, byCap), byCash), instrument.QuantityStep);
            if (quantity <= 0)
            {
                notes.Add(new(instrument.Symbol, byCash < Math.Min(byRisk, byCap) ? Reasons.NoCash : Reasons.TooSmall));
                continue;
            }

            actions.Add(new(ActionKind.Buy, instrument.Symbol, quantity, c.Close, stop, Reasons.Breakout));
            cash -= quantity * pricePerShare;
            slots--;
        }
    }

    /// <summary>
    /// What new positions may use: the free cash, but never more than the allocation minus what is held (at the last close,
    /// or the cost without bars), so cash the account holds beyond the allocation is never spent. Null when a holding
    /// can't be valued for want of an FX rate.
    /// </summary>
    private static decimal? Budget(AdvisorInput input, IEnumerable<PriceHistory> histories, List<AdvisorNote> notes)
    {
        var bySymbol = histories.ToDictionary(h => h.Instrument.Symbol, StringComparer.Ordinal);
        var invested = 0m;
        foreach (var holding in input.Holdings.Where(h => h.Quantity > 0).DistinctBy(h => h.Symbol, StringComparer.Ordinal))
        {
            bySymbol.TryGetValue(holding.Symbol, out var history);
            var price = history is { Bars.Count: > 0 } ? history.Bars[^1].Close : holding.AverageCost;
            // Without bars the currency is unknown: the base currency is assumed only when nothing else is known.
            var currency = history?.Instrument.Currency ?? input.BaseCurrency;
            if (FxRate(input, currency) is not { } fx)
            {
                notes.Add(new(holding.Symbol, Reasons.NoFxRate));
                return null;
            }

            invested += holding.Quantity * price * fx;
        }

        return Math.Max(0m, Math.Min(input.Cash, input.Capital - invested));
    }

    private static bool IsFresh(IReadOnlyList<Candle> bars, DateOnly today, AdvisorSettings settings)
    {
        if (bars.Count == 0)
            return false;
        var last = TrendIndicators.TradingDay(bars[^1]);
        return last <= today && last >= today.AddDays(-settings.MaxBarAgeDays);
    }

    private static decimal? FxRate(AdvisorInput input, string currency)
    {
        if (string.Equals(currency, input.BaseCurrency, StringComparison.Ordinal))
            return 1m;
        return input.FxToBase.TryGetValue(currency, out var rate) && rate > 0 ? rate : null;
    }

    /// <summary>Rounds down to a multiple of <paramref name="step"/>: quantities never exceed the budget, stops never sit above their level.</summary>
    internal static decimal FloorTo(decimal value, decimal step) =>
        step <= 0 ? value : Math.Floor(value / step) * step;
}
