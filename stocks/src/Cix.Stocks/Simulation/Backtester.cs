using Cix.Domain.Exchange;

namespace Cix.Stocks.Simulation;

public sealed record EquityPoint(DateOnly Day, decimal Equity, decimal Benchmark, int Positions);

/// <summary>Summary of an equity curve. Rates are fractions: 0.12 is 12%.</summary>
/// <param name="Cagr">Compound annual growth over the calendar span.</param>
/// <param name="MaxDrawdown">The largest fall from a peak, as a positive fraction of the peak.</param>
/// <param name="RoundTrips">Positions opened and closed.</param>
/// <param name="WinRate">Round trips closed with a profit after fees, or null without any.</param>
/// <param name="TimeInMarket">Days with at least one position, as a fraction of all days.</param>
public sealed record BacktestStats(decimal TotalReturn, decimal Cagr, decimal MaxDrawdown, int RoundTrips, decimal? WinRate, decimal TimeInMarket);

public sealed record BacktestResult(
    IReadOnlyList<EquityPoint> Equity,
    IReadOnlyList<PaperTrade> Trades,
    IReadOnlyList<SkippedAction> Skipped,
    BacktestStats Strategy,
    BacktestStats Benchmark);

/// <summary>
/// Replays the advisor over history: each trading day the <see cref="PaperPortfolio"/> carries out the previous close's
/// advice at the open and its stops against the low, then the advisor sees that day's close, the portfolio's holdings and
/// its cash. The benchmark is bought and held from the first day's close, without fees. What it doesn't model: FX moves
/// (rates are fixed), dividends beyond the adjusted prices, partial fills, and days the owner doesn't act.
/// </summary>
public static class Backtester
{
    /// <summary>The advisor sees at most this many of each instrument's latest bars (about two years), which keeps a long
    /// replay linear; every indicator it uses needs fewer, and a trail since entry is cut off only for older holdings.</summary>
    internal const int Window = 500;

    /// <param name="watchlist">Each instrument's full history, from well before <paramref name="from"/> so indicators are warm.</param>
    /// <param name="benchmark">The benchmark's full history, likewise.</param>
    public static BacktestResult Run(
        IReadOnlyList<PriceHistory> watchlist,
        IReadOnlyList<Candle> benchmark,
        DateOnly from,
        DateOnly to,
        string baseCurrency,
        decimal capital,
        IReadOnlyDictionary<string, decimal> fxToBase,
        AdvisorSettings advisorSettings,
        SimulationSettings simulationSettings)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        ArgumentNullException.ThrowIfNull(benchmark);
        if (to < from)
            throw new ArgumentException("The range ends before it starts.", nameof(to));
        advisorSettings.Validate();

        var portfolio = new PaperPortfolio(baseCurrency, capital, watchlist.Select(h => h.Instrument), fxToBase, simulationSettings);
        var byDay = watchlist.ToDictionary(
            h => h.Instrument.Symbol,
            h => h.Bars.ToDictionary(TrendIndicators.TradingDay),
            StringComparer.Ordinal);
        var days = watchlist.SelectMany(h => h.Bars).Concat(benchmark).Select(TrendIndicators.TradingDay)
            .Where(d => d >= from && d <= to).Distinct().Order().ToList();

        var cursors = watchlist.ToDictionary(h => h.Instrument.Symbol, _ => 0, StringComparer.Ordinal);
        var benchmarkCursor = 0;
        var lastClose = new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal? benchmarkStart = null;
        var pending = (IReadOnlyList<Recommendation>)[];
        var curve = new List<EquityPoint>();

        foreach (var day in days)
        {
            var today = new Dictionary<string, Candle>(StringComparer.Ordinal);
            foreach (var (symbol, bars) in byDay)
            {
                if (bars.TryGetValue(day, out var bar))
                    today[symbol] = bar;
            }

            var waiting = portfolio.ExecuteDay(day, today, pending);

            foreach (var history in watchlist)
            {
                var symbol = history.Instrument.Symbol;
                cursors[symbol] = Advance(history.Bars, cursors[symbol], day);
                if (cursors[symbol] > 0)
                    lastClose[symbol] = history.Bars[cursors[symbol] - 1].Close;
            }

            benchmarkCursor = Advance(benchmark, benchmarkCursor, day);
            var equity = portfolio.Equity(lastClose);
            var advice = Advisor.Evaluate(
                new AdvisorInput(
                    day, baseCurrency, equity, portfolio.Cash, fxToBase,
                    watchlist.Select(h => new PriceHistory(h.Instrument, Tail(h.Bars, cursors[h.Instrument.Symbol]))).ToList(),
                    Tail(benchmark, benchmarkCursor),
                    portfolio.Holdings),
                advisorSettings);
            // Advice for a symbol that had no bar today and is still waiting is replaced by the newer advice for it.
            var advised = advice.Actions.Select(a => a.Symbol).ToHashSet(StringComparer.Ordinal);
            pending = advice.Actions.Concat(waiting.Where(w => !advised.Contains(w.Symbol))).ToList();

            var benchmarkClose = benchmarkCursor > 0 ? benchmark[benchmarkCursor - 1].Close : (decimal?)null;
            benchmarkStart ??= benchmarkClose;
            var benchmarkEquity = benchmarkClose is { } close && benchmarkStart is { } start ? capital * close / start : capital;
            curve.Add(new EquityPoint(day, equity, benchmarkEquity, portfolio.Holdings.Count));
        }

        return new BacktestResult(
            curve, portfolio.Trades, portfolio.Skipped,
            Stats(curve.Select(p => (p.Day, p.Equity)).ToList(), portfolio.Trades, curve.Count(p => p.Positions > 0)),
            Stats(curve.Select(p => (p.Day, p.Benchmark)).ToList(), null, curve.Count));
    }

    /// <summary>The number of bars on or before <paramref name="day"/>, moving forward from <paramref name="cursor"/>.</summary>
    private static int Advance(IReadOnlyList<Candle> bars, int cursor, DateOnly day)
    {
        while (cursor < bars.Count && TrendIndicators.TradingDay(bars[cursor]) <= day)
            cursor++;
        return cursor;
    }

    private static Slice Tail(IReadOnlyList<Candle> bars, int count)
    {
        var start = Math.Max(0, count - Window);
        return new Slice(bars, start, count - start);
    }

    internal static BacktestStats Stats(IReadOnlyList<(DateOnly Day, decimal Value)> curve, IReadOnlyList<PaperTrade>? trades, int daysInMarket)
    {
        if (curve.Count == 0 || curve[0].Value <= 0)
            return new BacktestStats(0m, 0m, 0m, 0, null, 0m);

        var first = curve[0].Value;
        var last = curve[^1].Value;
        var totalReturn = (last / first) - 1;
        var years = (curve[^1].Day.DayNumber - curve[0].Day.DayNumber) / 365.25;
        // A growth rate, not an amount of money: computed in double because decimal has no fractional power, then stored as decimal.
        var cagr = years > 0 && last > 0 ? (decimal)(Math.Pow((double)(last / first), 1 / years) - 1) : totalReturn;

        var peak = first;
        var drawdown = 0m;
        foreach (var (_, value) in curve)
        {
            peak = Math.Max(peak, value);
            if (peak > 0)
                drawdown = Math.Max(drawdown, (peak - value) / peak);
        }

        var closed = trades?.Where(t => t.ProfitBase is not null).ToList() ?? [];
        decimal? winRate = closed.Count > 0 ? (decimal)closed.Count(t => t.ProfitBase > 0) / closed.Count : null;
        return new BacktestStats(
            Math.Round(totalReturn, 4), Math.Round(cagr, 4), Math.Round(drawdown, 4), closed.Count,
            winRate is { } w ? Math.Round(w, 4) : null, Math.Round((decimal)daysInMarket / curve.Count, 4));
    }

    /// <summary>A read-only view of part of a list, so the replay never copies histories.</summary>
    private sealed class Slice(IReadOnlyList<Candle> source, int start, int count) : IReadOnlyList<Candle>
    {
        public int Count => count;

        public Candle this[int index] =>
            (uint)index < (uint)count ? source[start + index] : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<Candle> GetEnumerator()
        {
            for (var i = 0; i < count; i++)
                yield return source[start + i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
