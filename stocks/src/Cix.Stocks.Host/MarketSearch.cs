namespace Cix.Stocks.Host;

/// <summary>The market-wide search between runs: the last session it searched, and the EODHD calls it spent on <see cref="CallsDay"/>.</summary>
public sealed record SearchState(DateOnly? LastDay, DateOnly? CallsDay, int Calls);

/// <summary>The exchange's common stocks as last fetched, refetched every <see cref="ScreenerSettings.UniverseDays"/> days.</summary>
public sealed record StoredUniverse(DateOnly FetchedOn, IReadOnlyList<ListedStock> Stocks);

/// <summary>The search's outcome, as the document writes it.</summary>
public static class SearchStatus
{
    /// <summary>Searched: the candidates are this session's.</summary>
    public const string Done = "done";

    /// <summary>The search is off in config/stocks.json (it needs a paid EODHD plan).</summary>
    public const string Off = "off";

    /// <summary>The day's search calls would not fit: nothing fetched.</summary>
    public const string CallBudget = "call-budget";

    /// <summary>EODHD refused or failed the download or the symbol list (a free key is refused).</summary>
    public const string DataError = "data-error";

    /// <summary>EODHD's download isn't for the advised session yet.</summary>
    public const string NotReady = "not-ready";
}

/// <summary>One candidate as the Stocks page shows it. <see cref="Status"/> is <c>breakout</c> or <c>near-high</c>.</summary>
public sealed record CandidateLine(
    string Symbol, string? Name, string? Venue, string Status, decimal Close, decimal BreakoutHigh, decimal TrendAverage, decimal Stop,
    decimal Shares, decimal Momentum, decimal? MarketCap);

/// <summary>
/// The market-wide search's result for one session (<c>candidates-latest</c> in the advice container, stocks/README.md has
/// the contract): stocks outside the watchlist and the holdings that pass the advisor's entry rule, sized on the capital.
/// </summary>
public sealed record CandidatesDocument(
    DateOnly? AsOf, DateTimeOffset GeneratedAt, string Status, string Regime, string BaseCurrency, decimal Capital, int Listed,
    int Shortlisted, IReadOnlyList<CandidateLine> Candidates);

/// <summary>
/// The market-wide search (ADR-0025), after the daily run has advised a close: EODHD's whole-market download (100 calls),
/// the symbol list when it is older than <see cref="ScreenerSettings.UniverseDays"/> (1 call), then each shortlisted stock's
/// bars (1 call each, or the cache). Its calls are reserved in its own state before any is made, so an overlapping run fails
/// on the version instead of spending them twice. It never touches the paper portfolio and never places an order.
/// </summary>
public sealed class MarketSearch(StocksConfig config, IMarketSource market, IDailyBarSource source, IStocksStore store, TimeProvider time)
{
    public async Task<CandidatesDocument?> RunAsync(CancellationToken cancellationToken)
    {
        var settings = config.Screener;
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var advice = await store.GetAdviceAsync("advice-latest", cancellationToken).ConfigureAwait(false);
        if (advice is null)
            return null; // nothing advised yet: no session to search, no capital to size on

        CandidatesDocument Page(string status, int listed = 0, int shortlisted = 0, IReadOnlyList<CandidateLine>? lines = null) =>
            new(status == SearchStatus.Done ? advice.AsOf : null, now, status, advice.Regime, advice.BaseCurrency, advice.Equity, listed, shortlisted, lines ?? []);

        if (!settings.Enabled)
        {
            var off = Page(SearchStatus.Off);
            await store.SaveLatestCandidatesAsync(off, cancellationToken).ConfigureAwait(false);
            return off;
        }

        var saved = await store.GetSearchStateAsync(cancellationToken).ConfigureAwait(false);
        var state = saved?.Value ?? new SearchState(null, null, 0);
        if (state.LastDay is { } last && last >= advice.AsOf)
            return null; // this session is searched: the page stands

        // The calls: the download, the symbol list when due, and each shortlisted stock's bars at most.
        var universe = await store.GetUniverseAsync(cancellationToken).ConfigureAwait(false);
        var universeDue = universe is null || universe.FetchedOn.AddDays(settings.UniverseDays) <= today;
        var needed = 100 + (universeDue ? 1 : 0) + settings.ShortlistSize;
        var calls = state.CallsDay == today ? state.Calls : 0;
        if (calls + needed > settings.MaxCallsPerDay)
        {
            var spent = Page(SearchStatus.CallBudget);
            await store.SaveLatestCandidatesAsync(spent, cancellationToken).ConfigureAwait(false);
            return spent;
        }

        state = state with { CallsDay = today, Calls = calls + needed };
        var etag = await store.SaveSearchStateAsync(state, saved?.ETag, cancellationToken).ConfigureAwait(false);
        var used = 0;

        IReadOnlyList<MarketRow> rows;
        try
        {
            if (universeDue)
            {
                used++;
                var listed = await market.GetListedStocksAsync(settings.Exchange, cancellationToken).ConfigureAwait(false);
                if (listed.Count == 0)
                    throw new StockDataException("EODHD's symbol list came back empty.");
                universe = new StoredUniverse(today, listed);
                await store.SaveUniverseAsync(universe, cancellationToken).ConfigureAwait(false);
            }

            used += 100;
            rows = await market.GetMarketDayAsync(settings.Exchange, cancellationToken).ConfigureAwait(false);
        }
        catch (StockDataException)
        {
            await GiveBackAsync(used).ConfigureAwait(false);
            var failed = Page(SearchStatus.DataError);
            await store.SaveLatestCandidatesAsync(failed, cancellationToken).ConfigureAwait(false);
            return failed;
        }

        // The download is for the advised session, or the provider hasn't updated yet: then the next run searches again.
        if (rows.Count == 0 || rows.Max(r => r.Day) < advice.AsOf)
        {
            await GiveBackAsync(used).ConfigureAwait(false);
            var early = Page(SearchStatus.NotReady);
            await store.SaveLatestCandidatesAsync(early, cancellationToken).ConfigureAwait(false);
            return early;
        }

        var bySymbol = universe!.Stocks.DistinctBy(s => s.Symbol, StringComparer.Ordinal).ToDictionary(s => s.Symbol, StringComparer.Ordinal);
        var exclude = config.Watchlist.Select(w => w.Symbol).Concat(advice.Holdings.Select(h => h.Symbol)).Append(config.Benchmark)
            .ToHashSet(StringComparer.Ordinal);
        var shortlist = Screener.Shortlist(rows, bySymbol, advice.AsOf, config.AdvisorSettings.MaxSharePrice, exclude, settings);

        var cutoff = config.DataCutoff(now);
        var found = new List<Candidate>();
        foreach (var row in shortlist)
        {
            var cached = await store.GetBarsAsync(row.Symbol, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<Cix.Domain.Exchange.Candle> bars;
            if (cached is { } c && c.FetchedAt >= cutoff)
            {
                bars = c.Bars.Select(b => b.ToCandle()).ToList();
            }
            else
            {
                used++;
                try
                {
                    bars = await source.GetDailyBarsAsync(row.Symbol, today.AddDays(-config.HistoryDays), today, cancellationToken).ConfigureAwait(false);
                }
                catch (StockDataException)
                {
                    continue; // one stock's bars failing doesn't stop the search
                }

                if (bars.Count == 0)
                    continue;
                await store.SaveBarsAsync(new CachedBars(row.Symbol, now, bars.Select(StoredBar.From).ToList()), cancellationToken).ConfigureAwait(false);
            }

            var listed = bySymbol[row.Symbol];
            if (Screener.Evaluate(row.Symbol, listed.Name, listed.Venue, row.MarketCap, bars, today, advice.Equity, config.AdvisorSettings, settings) is { } candidate)
                found.Add(candidate);
        }

        var lines = Screener.Rank(found, settings).Select(c => new CandidateLine(
            c.Symbol, c.Name, c.Venue, c.State == EntryState.Breakout ? "breakout" : "near-high", c.Close, c.PriorHigh, c.SlowAverage, c.Stop,
            c.Shares, c.Momentum, c.MarketCap)).ToList();
        var page = Page(SearchStatus.Done, bySymbol.Count, shortlist.Count, lines);
        await store.AddCandidatesAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"candidates-{advice.AsOf:yyyy-MM-dd}"), page, cancellationToken).ConfigureAwait(false);
        state = state with { LastDay = advice.AsOf, Calls = calls + used };
        await store.SaveSearchStateAsync(state, etag, cancellationToken).ConfigureAwait(false);
        await store.SaveLatestCandidatesAsync(page, cancellationToken).ConfigureAwait(false);
        return page;

        // The reserved calls not made go back, so a retry the same day isn't refused for calls never spent.
        async Task GiveBackAsync(int spent)
        {
            state = state with { Calls = calls + spent };
            etag = await store.SaveSearchStateAsync(state, etag, cancellationToken).ConfigureAwait(false);
        }
    }
}
