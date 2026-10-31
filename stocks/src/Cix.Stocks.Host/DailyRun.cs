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
/// One run after the US close (ADR-0023). It must start after <see cref="StocksConfig.DataReadyUtc"/>, when the session's
/// bars are complete.
/// <list type="number">
/// <item>The day's EODHD calls are reserved in the saved portfolio before any is made, so an overlapping run can't spend
/// them twice: it fails on the portfolio's version instead.</item>
/// <item>Bars for the benchmark, then the held symbols, then the rest of the watchlist, the longest unfetched first: from the
/// cache when fetched after the latest session's data was ready, otherwise from EODHD. A symbol over budget, or whose fetch
/// fails or comes back empty, keeps its older bars, with a note.</item>
/// <item>A split or dividend since the last run (the bars' terms moved against the closes saved with the portfolio) moves
/// the held position and the pending advice into the new terms.</item>
/// <item>The advice of the previous close is carried out in the paper portfolio at the next session's open, and the stops
/// are checked on every session since, including a held symbol's sessions that came late.</item>
/// <item>The advisor runs on the latest close, sized on the portfolio's equity, and the run's trades, the dated advice, the
/// portfolio and the page are saved, in that order. A close already advised is never carried out twice; a page a failed run
/// left behind is republished from the dated advice.</item>
/// </list>
/// The advice is for the paper portfolio's holdings: the owner, following it at the broker, holds the same. The run never
/// places an order anywhere.
/// </summary>
public sealed class DailyRun(StocksConfig config, IDailyBarSource source, IStocksStore store, TimeProvider time)
{
    /// <summary>A change of the adjustment factor beyond this is a split (the quantity changes), not a dividend.</summary>
    private const decimal SplitThreshold = 0.1m;

    /// <summary>Closes saved per symbol to tell a split or dividend from a revised bar.</summary>
    private const int ReferenceDays = 3;

    /// <summary>The rounding of the provider's adjusted prices: a smaller change is no adjustment.</summary>
    private const decimal Tolerance = 0.0005m;

    public async Task<RunReport> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var saved = await store.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var state = saved?.Value ?? PaperState.Start(config);
        var etag = saved?.ETag;
        if (!string.Equals(state.BaseCurrency, config.BaseCurrency, StringComparison.Ordinal))
            throw new InvalidOperationException($"The saved paper portfolio is in {state.BaseCurrency}, the configuration in {config.BaseCurrency}: start a new portfolio to change it.");

        var instruments = config.Watchlist.ToDictionary(w => w.Symbol, w => w.ToInstrument(), StringComparer.Ordinal);
        var terms = state.Terms ?? new Dictionary<string, SymbolTerms>();
        var notes = new List<AdvisorNote>();

        // 1. What needs fetching, and the reservation of its calls.
        var cutoff = config.DataCutoff(now);
        var cache = new Dictionary<string, CachedBars?>(StringComparer.Ordinal);
        foreach (var symbol in Symbols(state))
            cache[symbol] = await store.GetBarsAsync(symbol, cancellationToken).ConfigureAwait(false);
        var order = Order(state, cache);
        var stale = order.Where(s => cache[s] is not { } c || c.FetchedAt < cutoff).ToList();
        var calls = state.CallsDay == today ? state.Calls : 0;
        var reserved = Math.Min(stale.Count, Math.Max(0, Math.Min(config.MaxCallsPerRun, config.MaxCallsPerDay - calls)));
        if (reserved > 0)
        {
            state = state with { CallsDay = today, Calls = calls + reserved };
            etag = await store.SaveStateAsync(state, etag, cancellationToken).ConfigureAwait(false);
        }

        // 2. The bars.
        var bars = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal);
        var spent = 0;
        foreach (var symbol in order)
        {
            var cached = cache[symbol];
            var old = cached?.Bars.Select(b => b.ToCandle()).ToList() ?? [];
            bars[symbol] = old;
            if (!stale.Contains(symbol))
                continue;
            if (spent == reserved)
            {
                notes.Add(new(symbol, HostReasons.CallBudget));
                continue;
            }

            spent++;
            try
            {
                var fresh = await source.GetDailyBarsAsync(symbol, today.AddDays(-config.HistoryDays), today, cancellationToken).ConfigureAwait(false);
                // An empty or much shorter answer (a symbol the key doesn't cover today, a provider hiccup) is no reason to
                // forget the history: the older bars stay.
                if (fresh.Count == 0 || fresh.Count < old.Count / 2)
                    throw new StockDataException($"EODHD answered {fresh.Count} bars for {symbol}, {old.Count} before.");
                await store.SaveBarsAsync(new CachedBars(symbol, now, fresh.Select(StoredBar.From).ToList()), cancellationToken).ConfigureAwait(false);
                bars[symbol] = fresh;
            }
            catch (StockDataException)
            {
                notes.Add(new(symbol, HostReasons.DataError));
            }
        }

        var lastDays = bars.Values.Where(b => b.Count > 0).Select(b => TrendIndicators.TradingDay(b[^1])).ToList();
        if (lastDays.Count == 0)
            return new(RunOutcome.NoData, null, spent, 0, 0, notes.Count);
        var asOf = lastDays.Max();
        if (state.LastAdvisedDay is { } advised && advised >= asOf)
        {
            await RepublishAsync(advised, cancellationToken).ConfigureAwait(false);
            return new(RunOutcome.AlreadyAdvised, asOf, spent, 0, 0, notes.Count);
        }

        // 3. Splits and dividends since the last run.
        var factors = new Dictionary<string, (decimal Factor, decimal Close)>(StringComparer.Ordinal);
        foreach (var symbol in state.Positions.Select(p => p.Holding.Symbol).Concat(state.Pending.Select(a => a.Symbol)).Distinct(StringComparer.Ordinal))
        {
            if (terms.TryGetValue(symbol, out var t) && AdjustmentFactor(t.References, bars.GetValueOrDefault(symbol) ?? []) is { } f)
                factors[symbol] = f;
        }

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

            var (factor, referenceClose) = adjustment;
            var instrument = instruments.GetValueOrDefault(symbol);
            // Rounded as the broker would show them: the stop down to the tick, so it never sits above its level.
            var holding = position.Holding with
            {
                AverageCost = Math.Round(position.Holding.AverageCost * factor, 6, MidpointRounding.ToEven),
                Stop = position.Holding.Stop is { } stop ? Advisor.FloorTo(stop * factor, instrument?.PriceTick ?? 0.01m) : null,
            };
            if (Math.Abs(factor - 1m) > SplitThreshold)
                holding = holding with { Quantity = Advisor.FloorTo(holding.Quantity / factor, instrument?.QuantityStep ?? 1m) };
            else if (factor < 1m)
                cash += Math.Round(holding.Quantity * referenceClose * (1m - factor) * Fx(instruments, symbol), 6, MidpointRounding.ToEven); // the dividend, paid in cash
            notes.Add(new(symbol, HostReasons.Adjusted));
            if (holding.Quantity > 0)
                positions.Add(position with { Holding = holding });
        }

        IReadOnlyList<Recommendation> advice = state.Pending
            .Select(a => factors.TryGetValue(a.Symbol, out var f) ? Adjust(a, f.Factor, instruments) : a)
            .Where(a => a.Quantity > 0)
            .ToList();

        // 4. The sessions since the last run: the advice at the first one's open, the stops on each. A held symbol whose
        // bars came late (over budget, a failed fetch) gets its stops checked on the sessions it missed too.
        var portfolio = new PaperPortfolio(config.BaseCurrency, cash, positions, instruments.Values, config.Fx, config.Costs);
        var byDay = bars.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(TrendIndicators.TradingDay), StringComparer.Ordinal);
        var held = positions.Select(p => p.Holding.Symbol).ToHashSet(StringComparer.Ordinal);
        if (state.LastAdvisedDay is { } last)
        {
            bool Due(string symbol, DateOnly day) =>
                day > last || (held.Contains(symbol) && terms.TryGetValue(symbol, out var t) && day > t.LastBarDay);
            var sessions = byDay.SelectMany(kv => kv.Value.Keys.Where(d => d <= asOf && Due(kv.Key, d))).Distinct().Order().ToList();
            foreach (var day in sessions)
            {
                var dayBars = byDay.Where(kv => kv.Value.ContainsKey(day) && Due(kv.Key, day)).ToDictionary(kv => kv.Key, kv => kv.Value[day], StringComparer.Ordinal);
                if (day <= last)
                {
                    portfolio.ExecuteDay(day, dayBars, []); // a late session: its stops only, the advice came after it
                    continue;
                }

                // Advice for a symbol without a bar that day waits for its next bar; a buy that waited is dropped, as the
                // newer advice decides again.
                advice = portfolio.ExecuteDay(day, dayBars, advice).Where(a => a.Kind != ActionKind.Buy).ToList();
            }
        }

        // 5. The advice on the latest close.
        var lastClose = bars.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value[^1].Close, StringComparer.Ordinal);
        var equity = portfolio.Equity(lastClose);
        var input = new AdvisorInput(
            today, config.BaseCurrency, equity, portfolio.Cash, config.Fx,
            config.Watchlist.Select(w => new PriceHistory(instruments[w.Symbol], bars.GetValueOrDefault(w.Symbol) ?? [])).ToList(),
            bars.GetValueOrDefault(config.Benchmark) ?? [], portfolio.Holdings);
        var result = Advisor.Evaluate(input, config.AdvisorSettings);
        notes.AddRange(result.Notes);
        // Advice still waiting for its symbol's bar (a sell, a stop) stays pending unless the new advice says otherwise.
        var stillHeld = portfolio.Holdings.Select(h => h.Symbol).ToHashSet(StringComparer.Ordinal);
        var pending = result.Actions
            .Concat(advice.Where(a => stillHeld.Contains(a.Symbol) && !result.Actions.Any(r => r.Symbol == a.Symbol)))
            .ToList();

        var trades = portfolio.Trades.Select(t => new TradeLine(t.Day, t.Symbol, StocksJson.Word(t.Kind), t.Quantity, t.Price, Money(t.FeesBase), t.ProfitBase is { } p ? Money(p) : null, t.Reason)).ToList();
        var document = new AdviceDocument(
            asOf, now, StocksJson.Word(result.Regime), config.Benchmark, config.BaseCurrency, state.Capital, Money(portfolio.Cash), Money(equity),
            result.Actions.Select(a => new ActionLine(
                StocksJson.Word(a.Kind), a.Symbol, instruments.GetValueOrDefault(a.Symbol)?.Name, instruments.GetValueOrDefault(a.Symbol)?.Currency,
                a.Quantity, a.ReferencePrice, a.Stop, a.Reason)).ToList(),
            portfolio.Holdings.Select(h => new HoldingLine(
                h.Symbol, instruments.GetValueOrDefault(h.Symbol)?.Name, instruments.GetValueOrDefault(h.Symbol)?.Currency,
                h.Quantity, Math.Round(h.AverageCost, 4, MidpointRounding.ToEven), lastClose.TryGetValue(h.Symbol, out var c) ? c : null, h.Stop, h.OpenedOn)).ToList(),
            notes.Distinct().Select(n => new NoteLine(n.Symbol, n.Reason)).ToList(),
            trades,
            portfolio.Skipped.Select(s => new SkippedLine(s.Day, s.Symbol, StocksJson.Word(s.Kind), s.Reason)).ToList());

        // The terms the positions and the pending advice are now in, to recognise the next split or dividend.
        var nextTerms = new Dictionary<string, SymbolTerms>(StringComparer.Ordinal);
        foreach (var symbol in stillHeld.Concat(pending.Select(a => a.Symbol)).Distinct(StringComparer.Ordinal))
        {
            if (bars.GetValueOrDefault(symbol) is { Count: > 0 } b)
                nextTerms[symbol] = Terms(b);
            else if (terms.TryGetValue(symbol, out var t))
                nextTerms[symbol] = t;
        }

        // 6. Append-only records first, then the portfolio (which fails if another run got there first), then the page.
        foreach (var trade in trades)
            await store.AddTradeAsync($"{trade.Day:yyyy-MM-dd}-{trade.Symbol}-{trade.Kind}", trade, cancellationToken).ConfigureAwait(false);
        await store.AddAdviceAsync($"advice-{asOf:yyyy-MM-dd}", document, cancellationToken).ConfigureAwait(false);
        var next = new PaperState(
            config.BaseCurrency, state.Capital, portfolio.Cash, portfolio.Positions, asOf, pending, today, calls + spent, nextTerms);
        await store.SaveStateAsync(next, etag, cancellationToken).ConfigureAwait(false);
        await store.SaveLatestAdviceAsync(document, cancellationToken).ConfigureAwait(false);
        return new(RunOutcome.Advised, asOf, spent, trades.Count, result.Actions.Count, document.Notes.Count);
    }

    /// <summary>The benchmark, the held symbols (watched or not: their stops need bars), then the rest of the watchlist.</summary>
    private IReadOnlyList<string> Symbols(PaperState state)
    {
        var held = state.Positions.Select(p => p.Holding.Symbol).ToList();
        return new[] { config.Benchmark }.Concat(held).Concat(config.Watchlist.Select(w => w.Symbol)).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The fetch order: the benchmark and the held symbols first, then the rest, the longest unfetched first, so with too
    /// small a budget no symbol waits for ever.
    /// </summary>
    private List<string> Order(PaperState state, Dictionary<string, CachedBars?> cache)
    {
        var first = new[] { config.Benchmark }.Concat(state.Positions.Select(p => p.Holding.Symbol).Order(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        var rest = config.Watchlist.Select(w => w.Symbol).Where(s => !first.Contains(s))
            .Select((s, i) => (Symbol: s, Index: i))
            .OrderBy(x => cache[x.Symbol]?.FetchedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Symbol);
        return first.Concat(rest).ToList();
    }

    /// <summary>The last bar's day, and the closes of the bars before it (the last may still be revised).</summary>
    private static SymbolTerms Terms(IReadOnlyList<Candle> bars) => new(
        TrendIndicators.TradingDay(bars[^1]),
        bars.Take(bars.Count - 1).TakeLast(ReferenceDays).Select(b => new StoredClose(TrendIndicators.TradingDay(b), b.Close)).ToList());

    /// <summary>
    /// How the history's terms changed since the closes were saved (a split or a dividend re-adjusts every earlier bar by one
    /// factor): the bars' close over the saved close, the same on each saved day, with the latest saved close. Null when
    /// unchanged, when the days disagree (a revised bar, not an adjustment), or with fewer than two days to compare.
    /// </summary>
    internal static (decimal Factor, decimal Close)? AdjustmentFactor(IReadOnlyList<StoredClose> references, IReadOnlyList<Candle> bars)
    {
        var byDay = bars.ToDictionary(TrendIndicators.TradingDay);
        var ratios = references
            .Where(r => r.Close > 0 && byDay.ContainsKey(r.Day))
            .OrderByDescending(r => r.Day)
            .Select(r => (Factor: byDay[r.Day].Close / r.Close, r.Close))
            .ToList();
        if (ratios.Count < 2 || ratios.Any(r => Math.Abs(r.Factor - ratios[0].Factor) > Tolerance * ratios[0].Factor))
            return null;
        var factor = ratios.Average(r => r.Factor);
        return Math.Abs(factor - 1m) < Tolerance ? null : (factor, ratios[0].Close);
    }

    /// <summary>
    /// A run that saved the portfolio but stopped before <c>advice-latest</c> (a crash, a lost connection) would leave the
    /// page on the close before: the dated advice is copied there.
    /// </summary>
    private async Task RepublishAsync(DateOnly advised, CancellationToken cancellationToken)
    {
        var latest = await store.GetAdviceAsync("advice-latest", cancellationToken).ConfigureAwait(false);
        if (latest is not null && latest.AsOf >= advised)
            return;
        if (await store.GetAdviceAsync($"advice-{advised:yyyy-MM-dd}", cancellationToken).ConfigureAwait(false) is { } dated)
            await store.SaveLatestAdviceAsync(dated, cancellationToken).ConfigureAwait(false);
    }

    private static Recommendation Adjust(Recommendation a, decimal factor, Dictionary<string, Instrument> instruments)
    {
        var instrument = instruments.GetValueOrDefault(a.Symbol);
        var quantity = Math.Abs(factor - 1m) > SplitThreshold ? Advisor.FloorTo(a.Quantity / factor, instrument?.QuantityStep ?? 1m) : a.Quantity;
        return a with
        {
            Quantity = quantity,
            ReferencePrice = Math.Round(a.ReferencePrice * factor, 6, MidpointRounding.ToEven),
            Stop = a.Stop is { } stop ? Advisor.FloorTo(stop * factor, instrument?.PriceTick ?? 0.01m) : null,
        };
    }

    /// <summary>Money on the page, to the cent; the saved portfolio keeps every digit.</summary>
    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.ToEven);

    private decimal Fx(Dictionary<string, Instrument> instruments, string symbol) =>
        !instruments.TryGetValue(symbol, out var i) || string.Equals(i.Currency, config.BaseCurrency, StringComparison.Ordinal) ? 1m
        : config.Fx.TryGetValue(i.Currency, out var rate) ? rate : 0m;
}
