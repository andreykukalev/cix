using Cix.Domain.Exchange;
using Cix.Domain.MarketData;

namespace Cix.Domain.Scanning;

/// <summary>
/// The daily swing rules (ADR-0028), for strategies with <see cref="ScannerParameters.Timeframe"/> <c>1d</c>: the same
/// filters, sizing and signals as the hourly scan, measured on daily candles, with a breakout entry, a BTC regime filter, a
/// trend exit on the daily close and an optional trailing stop. The live quote still prices every order.
/// </summary>
public static partial class MarketScanner
{
    /// <summary>Daily candles are behind when the newest one closed longer ago than this: a day is missing.</summary>
    public static readonly TimeSpan MaxDailyCandleAge = TimeSpan.FromHours(27);

    /// <summary>Days of daily volume averaged for the liquidity filter.</summary>
    public const int DailyVolumeDays = 7;

    private const int AtrPeriod = 14;

    /// <summary>
    /// A daily candle's nominal length. The exchange's days follow its local midnight, so one a year lasts 23 hours and one 25;
    /// <see cref="MaxDailyCandleAge"/> leaves room for both.
    /// </summary>
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>The figures of a symbol's daily candles that the entry and the ranking read.</summary>
    /// <param name="PriorHigh">The highest high of the <see cref="ScannerParameters.BreakoutCandles"/> candles before the last.</param>
    /// <param name="Return">The last close against the close <see cref="ScannerParameters.BreakoutCandles"/> candles before it.</param>
    internal sealed record DailyFigures(decimal LastClose, decimal PriorHigh, decimal? FastEma, decimal SlowEma, decimal? Return);

    /// <summary>Whether BTC's daily trend lets the strategy buy (<see cref="ScannerParameters.RegimeEmaPeriod"/>).</summary>
    internal sealed record Regime(bool Open, string Reason, string Detail);

    /// <summary>
    /// The input with its EMAs, trend and ATR from its daily candles, so the ranking and the status show the figures the
    /// strategy trades on. Price, spread and the 1h and 24h moves stay those of the live quote and the hourly candles.
    /// </summary>
    internal static ScanInput WithDailyFeatures(ScanInput input, ScannerParameters p)
    {
        var daily = input.Daily ?? [];
        var closes = daily.Select(c => c.Close).ToList();
        var fast = Indicators.Ema(closes, p.FastEmaPeriod);
        var slow = Indicators.Ema(closes, p.SlowEmaPeriod);
        var atr = Indicators.Atr(daily, AtrPeriod);
        var price = input.Features.Price;
        return input with
        {
            Features = input.Features with
            {
                Ema20 = Round(fast, 8),
                Ema50 = Round(slow, 8),
                Trend = FeatureCalculator.TrendOf(fast, slow),
                Atr14 = Round(atr, 8),
                AtrFraction = atr is null || price is not > 0 ? null : Round(atr / price, 6),
            },
        };
    }

    private static (Metrics? Metrics, ScanExclusion? Exclusion) EvaluateDaily(ScanInput input, ScannerParameters p, DateTimeOffset now)
    {
        ScanExclusion Out(string reason, string detail) => new(input.Symbol, reason, detail);
        var f = input.Features;
        if (Unusable(input) is { } unusable)
            return (null, unusable);

        var daily = input.Daily ?? [];
        var needed = Math.Max(Math.Max(p.SlowEmaPeriod, p.BreakoutCandles + 1), Math.Max(AtrPeriod + 1, DailyVolumeDays));
        if (daily.Count < needed)
            return (null, Out("insufficient-history", Inv($"Only {daily.Count} daily candles; {needed} needed.")));
        var last = daily[^1];
        if (now - (last.Start + Day) > MaxDailyCandleAge)
            return (null, Out("stale", Inv($"The newest daily candle is from {last.Start:yyyy-MM-dd}; daily data is behind.")));
        if (f.Price is not > 0 || f.AtrFraction is not > 0 || f.Atr14 is not > 0 || f.SpreadFraction is null || f.Ema50 is null)
            return (null, Out("insufficient-history", "Not enough candles yet for EMA, ATR or spread."));

        // Assumes candle volume is in the base currency, as the hourly filter does.
        var week = daily.Skip(daily.Count - DailyVolumeDays).ToList();
        var quoteVolume = week.Average(c => c.Volume * c.Close);
        var averageVolume = week.Average(c => c.Volume);
        decimal? volumeRatio = averageVolume > 0 ? last.Volume / averageVolume : null;
        var cost = RoundTripCost(f.SpreadFraction.Value, p);

        if (quoteVolume < p.MinQuoteVolume24h)
            return (null, Out("low-volume", Inv($"Average daily volume over {DailyVolumeDays} days {quoteVolume:N0} is below the minimum {p.MinQuoteVolume24h:N0}.")));
        if (f.SpreadFraction.Value > p.MaxSpreadFraction)
            return (null, Out("wide-spread", Inv($"Spread {f.SpreadFraction.Value:0.##%} is above the maximum {p.MaxSpreadFraction:0.##%}.")));
        if (f.AtrFraction.Value > p.MaxAtrFraction)
            return (null, Out("too-volatile", Inv($"Daily ATR {f.AtrFraction.Value:0.##%} is above the maximum {p.MaxAtrFraction:0.##%}.")));
        if (f.AtrFraction.Value < cost * p.MinAtrToCostRatio)
            return (null, Out("costs-exceed-volatility", Inv($"Daily ATR {f.AtrFraction.Value:0.##%} is under {p.MinAtrToCostRatio:0.##}× the round-trip cost {cost:0.##%}.")));

        var closes = daily.Select(c => c.Close).ToList();
        var priorHigh = daily.Skip(daily.Count - 1 - p.BreakoutCandles).Take(p.BreakoutCandles).Max(c => c.High);
        var back = closes[^(p.BreakoutCandles + 1)];
        var figures = new DailyFigures(
            last.Close, priorHigh, Indicators.Ema(closes, p.FastEmaPeriod), Indicators.Ema(closes, p.SlowEmaPeriod)!.Value,
            back > 0 ? (last.Close - back) / back : null);
        return (new Metrics(quoteVolume, volumeRatio, cost, figures), null);
    }

    /// <summary>
    /// Momentum over the breakout window: the return over <see cref="ScannerParameters.BreakoutCandles"/> days in daily ATRs,
    /// plus the trend, unusual volume and costs as in the hourly score.
    /// </summary>
    internal static decimal DailyScore(FeatureSet f, Metrics m, ScannerParameters p)
    {
        var atr = f.AtrFraction!.Value;
        var momentum = m.Daily?.Return is { } r ? r / atr : 0m;
        var trend = f.Trend switch { Trend.Up => 1m, Trend.Down => -1m, _ => 0m };
        var volume = m.VolumeRatio is { } v ? Math.Clamp(v - 1m, 0m, 2m) : 0m;
        return (p.MomentumWeight * momentum) + (p.TrendWeight * trend) + (p.VolumeWeight * volume) - (p.CostWeight * m.RoundTripCost / atr);
    }

    /// <summary>
    /// BTC's daily trend in <paramref name="input"/>'s quote currency, judged once per scan, or null when the strategy has no
    /// regime filter. Without fresh BTC daily candles (BTC isn't watched, or its data is behind) the strategy doesn't buy.
    /// </summary>
    private static Regime? RegimeFor(ScanInput input, IReadOnlyList<ScanInput> inputs, ScannerParameters p, DateTimeOffset now, Dictionary<string, Regime> known)
    {
        if (p.RegimeEmaPeriod == 0 || input.Pair is null)
            return null;
        var quote = input.Pair.Quote;
        if (known.TryGetValue(quote, out var regime))
            return regime;

        var symbol = $"BTC-{quote}";
        var btc = inputs.FirstOrDefault(i => string.Equals(i.Symbol, symbol, StringComparison.Ordinal))?.Daily ?? [];
        var ema = Indicators.Ema(btc.Select(c => c.Close).ToList(), p.RegimeEmaPeriod);
        if (ema is not > 0 || now - (btc[^1].Start + Day) > MaxDailyCandleAge)
            regime = new Regime(false, "regime unknown", Inv($"Buys wait for {symbol}'s daily trend (its close against its EMA{p.RegimeEmaPeriod}), and its daily candles are missing or behind; {symbol} must be on the watched list."));
        else if (btc[^1].Close < ema)
            regime = new Regime(false, Inv($"regime: BTC below EMA{p.RegimeEmaPeriod}"), Inv($"{symbol}'s last daily close is {(ema.Value - btc[^1].Close) / ema.Value:0.##%} below its EMA{p.RegimeEmaPeriod}; the strategy buys only while BTC is above it."));
        else
            regime = new Regime(true, "regime open", Inv($"{symbol} closed above its EMA{p.RegimeEmaPeriod}."));
        known[quote] = regime;
        return regime;
    }

    private static (EntrySignal? Signal, string? NoEntryReason, string? Detail) DailyEntry(ScanInput input, Metrics m, int rank, ScannerParameters p, Regime? regime)
    {
        var f = input.Features;
        var d = m.Daily!;
        var n = p.BreakoutCandles;
        if (regime is { Open: false })
            return (null, regime.Reason, regime.Detail);
        if (input.LastSoldAt is { } sold && input.Daily is { Count: > 0 } daily && sold >= daily[^1].Start)
            return (null, "sold since the breakout", Inv($"The engine sold {input.Symbol} at {sold.UtcDateTime:yyyy-MM-dd HH:mm} UTC, after the last daily candle began; the strategy waits for the next daily close before buying it again, so a stop-out isn't bought back on the same signal."));
        if (p.RequireUptrendPullback)
        {
            if (d.FastEma is not { } fast || fast <= d.SlowEma || d.LastClose <= d.SlowEma)
                return (null, "no daily uptrend", Inv($"The strategy buys only in a daily uptrend: EMA{p.FastEmaPeriod} above EMA{p.SlowEmaPeriod} and the last daily close above EMA{p.SlowEmaPeriod}; the close is {d.LastClose / d.SlowEma - 1:+0.##%;-0.##%;0%} from EMA{p.SlowEmaPeriod}."));
            if (d.LastClose <= d.PriorHigh)
                return (null, "no breakout", Inv($"The last daily close is {(d.PriorHigh - d.LastClose) / d.PriorHigh:0.##%} below the highest high of the {n} days before it; the strategy buys a close above it."));
        }

        if (f.Ask is not > 0)
            return (null, "no ask", "The exchange shows no ask price right now.");
        if (p.RequireUptrendPullback && f.Ask.Value <= d.PriorHigh)
            return (null, "back below the breakout", Inv($"The ask has fallen back to or under the {n}-day high the last close broke; the strategy buys only while the breakout holds."));
        var atr = f.Atr14!.Value;
        var above = (f.Ask.Value - d.LastClose) / atr;
        if (p.RequireUptrendPullback && above > p.EntryMaxAboveBreakoutAtr)
            return (null, Inv($"extended: {above:0.##} ATR above the breakout close"),
                Inv($"The ask is {above:0.##} daily ATR above the breakout's close; the strategy buys at most {p.EntryMaxAboveBreakoutAtr:0.##} ATR above it, so it doesn't chase."));

        var setup = p.RequireUptrendPullback ? Inv($"{n}-day breakout") : Inv($"test entry (daily trend {f.Trend.ToString().ToLowerInvariant()})");
        var reason = Inv($"{setup}: close {d.LastClose / d.PriorHigh - 1:+0.##%;-0.##%;0%} over the {n}-day high, {n}-day {(d.Return ?? 0m):+0.##%;-0.##%;0%}, ask {above:0.##} ATR above the close");
        return Sized(input, m, rank, p, f.Ask.Value, atr, reason);
    }

    /// <summary>The trend exit's detail when the last daily close is below the slow EMA; null when it isn't, or the daily data can't tell.</summary>
    private static string? DailyTrendDown(ScanInput input, ScannerParameters p, DateTimeOffset now)
    {
        var daily = input.Daily ?? [];
        if (daily.Count < p.SlowEmaPeriod || now - (daily[^1].Start + Day) > MaxDailyCandleAge)
            return null;
        var slow = Indicators.Ema(daily.Select(c => c.Close).ToList(), p.SlowEmaPeriod);
        return slow is { } ema && daily[^1].Close < ema
            ? Inv($"the daily close fell {(ema - daily[^1].Close) / ema:0.##%} below EMA{p.SlowEmaPeriod}")
            : null;
    }

    /// <summary>
    /// For each held position with a stop and a fill time: the highest daily close since the buy filled, less
    /// <see cref="ScannerParameters.TrailAtr"/> daily ATRs, when that is above the stop. Left out: a coin with an exit signal
    /// (it is being sold), and one whose quote or daily candles are stale. A raise stays at least
    /// <see cref="MinExitDistanceFraction"/> below the bid, so a raise alone never sells (a bad daily print can't force a sale
    /// at once); the price then has to fall to it.
    /// </summary>
    private static List<StopRaise> Trail(
        IReadOnlyList<HeldPosition> held, IReadOnlyDictionary<string, ScanInput> inputs, IReadOnlyList<ExitSignal> exits, ScannerParameters p, DateTimeOffset now)
    {
        var raises = new List<StopRaise>();
        foreach (var h in held.OrderBy(h => h.Symbol, StringComparer.Ordinal))
        {
            if (h.StopLoss is not { } stop || h.FilledAt is not { } filled || exits.Any(x => string.Equals(x.Symbol, h.Symbol, StringComparison.Ordinal)))
                continue;
            if (inputs.GetValueOrDefault(h.Symbol) is not { Pair: { } pair, Daily: { Count: > 0 } daily } input || input.Features.IsStale
                || input.Features.Bid is not > 0)
                continue;
            if (now - (daily[^1].Start + Day) > MaxDailyCandleAge || Indicators.Atr(daily, AtrPeriod) is not { } atr || atr <= 0)
                continue;
            // The candles that closed after the fill: a candle closes when the next one starts (23 or 25 hours on a clock change).
            var since = daily.Where((c, i) => (i + 1 < daily.Count ? daily[i + 1].Start : c.Start + Day) > filled).ToList();
            if (since.Count == 0)
                continue;
            var highest = since.Max(c => c.Close);
            var level = Math.Min(
                FloorToStep(highest - (p.TrailAtr * atr), pair.QuoteStep),
                FloorToStep(input.Features.Bid.Value * (1 - MinExitDistanceFraction), pair.QuoteStep));
            if (level > stop)
                raises.Add(new StopRaise(h.Symbol, stop, level, Inv($"trailing stop: highest daily close since the buy less {p.TrailAtr:0.##} daily ATR")));
        }

        return raises;
    }
}
