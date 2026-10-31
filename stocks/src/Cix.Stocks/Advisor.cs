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
    public const string TooExpensive = "too-expensive";
    public const string ThemeLimit = "theme-limit";
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
/// <summary>Where an instrument stands against the entry rule (<see cref="Advisor.Entry"/>), in the order it is checked.</summary>
public enum EntryState
{
    /// <summary>No bars at all.</summary>
    NoData,

    /// <summary>The latest bar is older than <see cref="AdvisorSettings.MaxBarAgeDays"/> (or after today).</summary>
    StaleData,

    /// <summary>One share costs more than <see cref="AdvisorSettings.MaxSharePrice"/> in the base currency.</summary>
    TooExpensive,

    /// <summary>Too few bars for the averages, the prior high or the ATR.</summary>
    NotEnoughData,

    /// <summary>The close is at or under the slow average, or the fast average is at or under it.</summary>
    BelowTrend,

    /// <summary>In an uptrend, but the close isn't above the prior closing high.</summary>
    Uptrend,

    /// <summary>In an uptrend and above the prior closing high: the entry signal.</summary>
    Breakout,
}

/// <summary>
/// An instrument against the entry rule: its state and, as far as they could be computed, the figures it was judged on.
/// </summary>
public sealed record EntryCheck(EntryState State, decimal? Close, decimal? SlowAverage, decimal? FastAverage, decimal? PriorHigh, decimal? Atr);

public static class Advisor
{
    /// <summary>
    /// The entry rule, the one place it is written: fresh bars, a share within the price cap (checked only when
    /// <paramref name="fxToBase"/> is known), enough history, the close above the slow average with the fast one above it, and
    /// the close above the prior closing high. The watchlist on the Stocks page shows the same verdicts.
    /// </summary>
    public static EntryCheck Entry(IReadOnlyList<Candle> bars, DateOnly today, decimal? fxToBase, AdvisorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(settings);
        if (bars.Count == 0)
            return new(EntryState.NoData, null, null, null, null, null);
        var close = bars[^1].Close;
        var slow = TrendIndicators.Sma(bars, settings.SlowSmaDays);
        var fast = TrendIndicators.Sma(bars, settings.FastSmaDays);
        var high = TrendIndicators.PriorHighestClose(bars, settings.BreakoutDays);
        var atr = Indicators.Atr(bars, settings.AtrDays);
        EntryCheck Check(EntryState state) => new(state, close, slow, fast, high, atr);

        if (!IsFresh(bars, today, settings))
            return Check(EntryState.StaleData);
        if (settings.MaxSharePrice is { } maxPrice && fxToBase is { } rate && close * rate > maxPrice)
            return Check(EntryState.TooExpensive);
        if (bars.Count < settings.MinBars || slow is null || fast is null || high is null || atr is not > 0)
            return Check(EntryState.NotEnoughData);
        if (close <= slow || fast <= slow)
            return Check(EntryState.BelowTrend);
        return Check(close > high ? EntryState.Breakout : EntryState.Uptrend);
    }

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
            // Too-expensive is noted on every risk-on run, not only on a breakout, so the owner sees why it is never bought.
            var entry = Entry(bars, input.Today, FxRate(input, history.Instrument.Currency), settings);
            var skip = entry.State switch
            {
                EntryState.NoData => Reasons.NoData,
                EntryState.StaleData => Reasons.StaleData,
                EntryState.TooExpensive => Reasons.TooExpensive,
                EntryState.NotEnoughData => Reasons.NotEnoughData,
                _ => null,
            };
            if (skip is not null)
            {
                notes.Add(new(symbol, skip));
                continue;
            }

            if (entry is not { State: EntryState.Breakout, Close: { } close, Atr: { } atr })
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
        var themes = ThemeHoldings(input, histories);
        var themeCap = input.Capital * settings.MaxThemeFraction;
        foreach (var c in candidates.OrderByDescending(c => c.Momentum).ThenBy(c => c.History.Instrument.Symbol, StringComparer.Ordinal))
        {
            var instrument = c.History.Instrument;
            if (slots <= 0)
            {
                notes.Add(new(instrument.Symbol, Reasons.MaxPositions));
                continue;
            }

            var stop = InitialStop(c.Close, c.Atr, instrument.PriceTick, settings);
            if (stop <= 0)
            {
                notes.Add(new(instrument.Symbol, Reasons.NotEnoughData));
                continue;
            }

            var pricePerShare = c.Close * c.Fx;
            var sized = Sized(input.Capital, c.Close, stop, c.Atr, c.Fx, settings);
            var byCash = cash / pricePerShare;
            var theme = instrument.Theme;
            var byTheme = theme is null ? decimal.MaxValue : Math.Max(0m, themeCap - themes.GetValueOrDefault(theme)) / pricePerShare;
            var quantity = FloorTo(Math.Min(Math.Min(sized, byCash), byTheme), instrument.QuantityStep);
            if (quantity <= 0)
            {
                notes.Add(new(instrument.Symbol, byTheme < Math.Min(sized, byCash) ? Reasons.ThemeLimit : byCash < sized ? Reasons.NoCash : Reasons.TooSmall));
                continue;
            }

            actions.Add(new(ActionKind.Buy, instrument.Symbol, quantity, c.Close, stop, Reasons.Breakout));
            cash -= quantity * pricePerShare;
            if (theme is not null)
                themes[theme] = themes.GetValueOrDefault(theme) + (quantity * pricePerShare);
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

    /// <summary>What each theme holds now, at the last close in the base currency; holdings that can't be valued count nothing.</summary>
    private static Dictionary<string, decimal> ThemeHoldings(AdvisorInput input, IEnumerable<PriceHistory> histories)
    {
        var bySymbol = histories.ToDictionary(h => h.Instrument.Symbol, StringComparer.Ordinal);
        var themes = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var holding in input.Holdings.Where(h => h.Quantity > 0).DistinctBy(h => h.Symbol, StringComparer.Ordinal))
        {
            if (!bySymbol.TryGetValue(holding.Symbol, out var history) || history.Instrument.Theme is not { } theme)
                continue;
            var price = history.Bars.Count > 0 ? history.Bars[^1].Close : holding.AverageCost;
            if (FxRate(input, history.Instrument.Currency) is { } fx)
                themes[theme] = themes.GetValueOrDefault(theme) + (holding.Quantity * price * fx);
        }

        return themes;
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

    /// <summary>A new position's stop: <see cref="AdvisorSettings.InitialStopAtr"/> ATRs under the close, rounded down to the tick.</summary>
    public static decimal InitialStop(decimal close, decimal atr, decimal priceTick, AdvisorSettings settings) =>
        FloorTo(close - (settings.InitialStopAtr * atr), priceTick);

    /// <summary>
    /// A new position's size before cash, slots and themes, unrounded: the shares whose loss to the stop plus a gap of
    /// <see cref="AdvisorSettings.GapAtr"/> ATRs is <see cref="AdvisorSettings.RiskPerTrade"/> of the capital, at most
    /// <see cref="AdvisorSettings.MaxPositionFraction"/> of it in value. Prices in the instrument's currency, capital in the base.
    /// </summary>
    public static decimal Sized(decimal capital, decimal close, decimal stop, decimal atr, decimal fxToBase, AdvisorSettings settings)
    {
        var riskPerShare = (close - stop + (settings.GapAtr * atr)) * fxToBase;
        var pricePerShare = close * fxToBase;
        return Math.Min(capital * settings.RiskPerTrade / riskPerShare, capital * settings.MaxPositionFraction / pricePerShare);
    }

    /// <summary>Rounds down to a multiple of <paramref name="step"/>: quantities never exceed the budget, stops never sit above their level.</summary>
    public static decimal FloorTo(decimal value, decimal step) =>
        step <= 0 ? value : Math.Floor(value / step) * step;
}
