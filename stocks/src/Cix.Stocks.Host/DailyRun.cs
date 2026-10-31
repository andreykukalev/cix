using Cix.Domain.Exchange;
using Cix.Stocks.Simulation;

namespace Cix.Stocks.Host;

public enum RunOutcome
{
    /// <summary>New advice for a new close was written.</summary>
    Advised,

    /// <summary>The latest close was already advised: nothing changed but the call count.</summary>
    AlreadyAdvised,

    /// <summary>No bars at all: nothing to advise on.</summary>
    NoData,
}

public sealed record RunReport(RunOutcome Outcome, DateOnly? AsOf, int Calls, int Trades, int Actions, int Notes);

/// <summary>
/// One run after the US close (ADR-0023):
/// <list type="number">
/// <item>Bars for the benchmark, then the held symbols, then the rest of the watchlist: from the cache when fetched after
/// the latest session's data was ready, otherwise from EODHD within the day's call budget. A symbol over budget or whose
/// fetch fails keeps its older bars, with a note.</item>
/// <item>The advice of the previous close is carried out in the paper portfolio at the next session's open, and its stops
/// checked on every session since.</item>
/// <item>The advisor runs on the latest close, sized on the portfolio's equity, and the advice, the run's trades and the
/// portfolio are saved. A close already advised is never carried out twice.</item>
/// </list>
/// The run never places an order anywhere: the owner carries the advice out by hand.
/// </summary>
public sealed class DailyRun(StocksConfig config, IDailyBarSource source, IStocksStore store, TimeProvider time)
{
    /// <summary>A change of the adjustment factor beyond this is a split (the quantity changes), not a dividend.</summary>
    private const decimal SplitThreshold = 0.1m;

    public async Task<RunReport> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var saved = await store.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var state = saved?.Value ?? PaperState.Start(config);
        if (!string.Equals(state.BaseCurrency, config.BaseCurrency, StringComparison.Ordinal))
            throw new InvalidOperationException($"The saved paper portfolio is in {state.BaseCurrency}, the configuration in {config.BaseCurrency}: start a new portfolio to change it.");

        var instruments = config.Watchlist.ToDictionary(w => w.Symbol, w => w.ToInstrument(), StringComparer.Ordinal);
        var notes = new List<AdvisorNote>();
        var (bars, factors, calls, spent) = await FetchAsync(state, today, now, notes, cancellationToken).ConfigureAwait(false);

        var lastDays = bars.Values.Where(b => b.Count > 0).Select(b => TrendIndicators.TradingDay(b[^1])).ToList();
        if (lastDays.Count == 0)
        {
            await store.SaveStateAsync(state with { CallsDay = today, Calls = calls }, saved?.ETag, cancellationToken).ConfigureAwait(false);
            return new(RunOutcome.NoData, null, spent, 0, 0, notes.Count);
        }

        var asOf = lastDays.Max();
        if (state.LastAdvisedDay is { } advised && advised >= asOf)
        {
            await store.SaveStateAsync(state with { CallsDay = today, Calls = calls }, saved?.ETag, cancellationToken).ConfigureAwait(false);
            return new(RunOutcome.AlreadyAdvised, asOf, spent, 0, 0, notes.Count);
        }

        // Splits and dividends re-adjust the history: the saved position and the pending advice move into the new terms.
        var cash = state.Cash;
        var positions = new List<PaperPosition>();
        foreach (var position in state.Positions)
        {
            var symbol = position.Holding.Symbol;
            if (!factors.TryGetValue(symbol, out var adjustment))
            {
                positions.Add(position);
                continue;
            }

            var (factor, cachedClose) = adjustment;
            var step = instruments.TryGetValue(symbol, out var i) ? i.QuantityStep : 1m;
            var holding = position.Holding with { AverageCost = position.Holding.AverageCost * factor, Stop = position.Holding.Stop * factor };
            if (Math.Abs(factor - 1m) > SplitThreshold)
                holding = holding with { Quantity = Advisor.FloorTo(holding.Quantity / factor, step) };
            else if (factor < 1m)
                cash += holding.Quantity * cachedClose * (1m - factor) * Fx(instruments, symbol); // the dividend, paid in cash
            if (holding.Quantity > 0)
                positions.Add(position with { Holding = holding });
        }

        IReadOnlyList<Recommendation> pending = state.Pending
            .Select(a => factors.TryGetValue(a.Symbol, out var f) ? Adjust(a, f.Factor, instruments) : a)
            .Where(a => a.Quantity > 0)
            .ToList();

        var portfolio = new PaperPortfolio(config.BaseCurrency, cash, positions, instruments.Values, config.Fx, config.Costs);
        var byDay = bars.ToDictionary(
            kv => kv.Key, kv => kv.Value.ToDictionary(TrendIndicators.TradingDay), StringComparer.Ordinal);
        if (state.LastAdvisedDay is { } last)
        {
            var sessions = byDay.Values.SelectMany(d => d.Keys).Where(d => d > last && d <= asOf).Distinct().Order();
            var advice = pending;
            foreach (var day in sessions)
            {
                var dayBars = byDay.Where(kv => kv.Value.ContainsKey(day)).ToDictionary(kv => kv.Key, kv => kv.Value[day], StringComparer.Ordinal);
                // Advice for a symbol without a bar that day waits for its next bar; a buy that waited is dropped, as the
                // newer advice decides again.
                advice = portfolio.ExecuteDay(day, dayBars, advice).Where(a => a.Kind != ActionKind.Buy).ToList();
            }
        }

        var lastClose = bars.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value[^1].Close, StringComparer.Ordinal);
        var equity = portfolio.Equity(lastClose);
        var input = new AdvisorInput(
            today, config.BaseCurrency, equity, portfolio.Cash, config.Fx,
            config.Watchlist.Select(w => new PriceHistory(instruments[w.Symbol], bars.GetValueOrDefault(w.Symbol) ?? [])).ToList(),
            bars.GetValueOrDefault(config.Benchmark) ?? [], portfolio.Holdings);
        var result = Advisor.Evaluate(input, config.AdvisorSettings);
        notes.AddRange(result.Notes);

        var trades = portfolio.Trades.Select(t => new TradeLine(t.Day, t.Symbol, StocksJson.Word(t.Kind), t.Quantity, t.Price, t.FeesBase, t.ProfitBase, t.Reason)).ToList();
        var document = new AdviceDocument(
            asOf, now, StocksJson.Word(result.Regime), config.Benchmark, config.BaseCurrency, state.Capital, portfolio.Cash, equity,
            result.Actions.Select(a => new ActionLine(
                StocksJson.Word(a.Kind), a.Symbol, instruments.GetValueOrDefault(a.Symbol)?.Name, instruments.GetValueOrDefault(a.Symbol)?.Currency,
                a.Quantity, a.ReferencePrice, a.Stop, a.Reason)).ToList(),
            portfolio.Holdings.Select(h => new HoldingLine(
                h.Symbol, instruments.GetValueOrDefault(h.Symbol)?.Name, instruments.GetValueOrDefault(h.Symbol)?.Currency,
                h.Quantity, h.AverageCost, lastClose.TryGetValue(h.Symbol, out var c) ? c : null, h.Stop, h.OpenedOn)).ToList(),
            notes.Distinct().Select(n => new NoteLine(n.Symbol, n.Reason)).ToList(),
            trades,
            portfolio.Skipped.Select(s => new SkippedLine(s.Day, s.Symbol, StocksJson.Word(s.Kind), s.Reason)).ToList());

        // Append-only records first, then the portfolio (which fails if another run got there first), then the page.
        foreach (var trade in trades)
            await store.AddTradeAsync($"{trade.Day:yyyy-MM-dd}-{trade.Symbol}-{trade.Kind}", trade, cancellationToken).ConfigureAwait(false);
        await store.AddAdviceAsync($"advice-{asOf:yyyy-MM-dd}", document, cancellationToken).ConfigureAwait(false);
        var next = new PaperState(config.BaseCurrency, state.Capital, portfolio.Cash, portfolio.Positions, asOf, result.Actions, today, calls);
        await store.SaveStateAsync(next, saved?.ETag, cancellationToken).ConfigureAwait(false);
        await store.SaveLatestAdviceAsync(document, cancellationToken).ConfigureAwait(false);
        return new(RunOutcome.Advised, asOf, spent, trades.Count, result.Actions.Count, document.Notes.Count);
    }

    private async Task<(Dictionary<string, IReadOnlyList<Candle>> Bars, Dictionary<string, (decimal Factor, decimal LastClose)> Factors, int Calls, int Spent)> FetchAsync(
        PaperState state, DateOnly today, DateTimeOffset now, List<AdvisorNote> notes, CancellationToken cancellationToken)
    {
        var calls = state.CallsDay == today ? state.Calls : 0;
        var budget = Math.Max(0, Math.Min(config.MaxCallsPerRun, config.MaxCallsPerDay - calls));
        var cutoff = config.DataCutoff(now);
        var held = state.Positions.Select(p => p.Holding.Symbol).ToHashSet(StringComparer.Ordinal);
        var watch = config.Watchlist.Select(w => w.Symbol).ToList();
        // Held symbols no longer watched still need bars, for their stops.
        var order = new[] { config.Benchmark }
            .Concat(watch.Where(held.Contains))
            .Concat(held.Where(s => !watch.Contains(s, StringComparer.Ordinal)).Order(StringComparer.Ordinal))
            .Concat(watch.Where(s => !held.Contains(s)))
            .Distinct(StringComparer.Ordinal);

        var bars = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal);
        var factors = new Dictionary<string, (decimal, decimal)>(StringComparer.Ordinal);
        var spent = 0;
        foreach (var symbol in order)
        {
            var cached = await store.GetBarsAsync(symbol, cancellationToken).ConfigureAwait(false);
            var old = cached?.Bars.Select(b => b.ToCandle()).ToList() ?? [];
            if (cached is not null && cached.FetchedAt >= cutoff)
            {
                bars[symbol] = old;
                continue;
            }

            if (budget == 0)
            {
                notes.Add(new(symbol, HostReasons.CallBudget));
                bars[symbol] = old;
                continue;
            }

            budget--;
            calls++;
            spent++;
            try
            {
                var fresh = await source.GetDailyBarsAsync(symbol, today.AddDays(-config.HistoryDays), today, cancellationToken).ConfigureAwait(false);
                if (cached is not null && AdjustmentFactor(cached.Bars, fresh) is { } factor)
                    factors[symbol] = factor;
                await store.SaveBarsAsync(new CachedBars(symbol, now, fresh.Select(StoredBar.From).ToList()), cancellationToken).ConfigureAwait(false);
                bars[symbol] = fresh;
            }
            catch (StockDataException)
            {
                notes.Add(new(symbol, HostReasons.DataError));
                bars[symbol] = old;
            }
        }

        return (bars, factors, calls, spent);
    }

    /// <summary>
    /// How the history's terms changed since the cached fetch: the new close over the cached close on the latest day both
    /// have, with that cached close. Null when unchanged (or nothing to compare).
    /// </summary>
    internal static (decimal Factor, decimal LastClose)? AdjustmentFactor(IReadOnlyList<StoredBar> cached, IReadOnlyList<Candle> fresh)
    {
        var freshByDay = fresh.ToDictionary(TrendIndicators.TradingDay);
        for (var i = cached.Count - 1; i >= 0; i--)
        {
            if (!freshByDay.TryGetValue(cached[i].Day, out var bar) || cached[i].Close <= 0)
                continue;
            var factor = bar.Close / cached[i].Close;
            // Rounding of the provider's adjusted prices is not an adjustment.
            return Math.Abs(factor - 1m) < 0.0005m ? null : (factor, cached[i].Close);
        }

        return null;
    }

    private static Recommendation Adjust(Recommendation a, decimal factor, Dictionary<string, Instrument> instruments)
    {
        var step = instruments.TryGetValue(a.Symbol, out var i) ? i.QuantityStep : 1m;
        var quantity = Math.Abs(factor - 1m) > SplitThreshold ? Advisor.FloorTo(a.Quantity / factor, step) : a.Quantity;
        return a with { Quantity = quantity, ReferencePrice = a.ReferencePrice * factor, Stop = a.Stop * factor };
    }

    private decimal Fx(Dictionary<string, Instrument> instruments, string symbol) =>
        !instruments.TryGetValue(symbol, out var i) || string.Equals(i.Currency, config.BaseCurrency, StringComparison.Ordinal) ? 1m
        : config.Fx.TryGetValue(i.Currency, out var rate) ? rate : 0m;
}
