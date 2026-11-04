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

    /// <summary>EODHD's download is for a later session than the advice (its bars came late): the next run searches.</summary>
    public const string AdviceBehind = "advice-behind";
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
/// bars (1 call each, or the cache). Its calls are reserved in its own state, under its version, before any is made: two
/// runs reserving at once conflict, and a later run counts the earlier one's calls. A provider failure gives back the calls
/// not made; any other failure keeps the whole reservation, erring on the side of the key's limit. Bars are fetched only
/// until <c>deadline</c>, so the job ends inside its timeout with what it found. It never touches the paper portfolio and
/// never places an order.
/// </summary>
public sealed class MarketSearch(
    StocksConfig config, IMarketSource market, IDailyBarSource source, IStocksStore store, TimeProvider time, DateTimeOffset? deadline = null)
{
    /// <summary>The universe document's largest size: Cosmos DB refuses an item over 2 MB.</summary>
    private const int MaxUniverseBytes = 1_800_000;

    /// <summary>The most stocks kept in the universe document: about 1.5 MB, under Cosmos DB's 2 MB item limit.</summary>
    public const int MaxUniverse = 15_000;

    public async Task<CandidatesDocument?> RunAsync(CancellationToken cancellationToken)
    {
        var settings = config.Screener;
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var advice = await store.GetAdviceAsync("advice-latest", cancellationToken).ConfigureAwait(false);
        if (advice is null)
            return null; // nothing advised yet: no session to search, no capital to size on

        // Sized on the owner's real capital once there is one (ADR-0026), the paper portfolio's equity before.
        var capital = advice.Owner?.Capital ?? advice.Equity;
        CandidatesDocument Page(string status, int listed = 0, int shortlisted = 0, IReadOnlyList<CandidateLine>? lines = null) =>
            new(status == SearchStatus.Done ? advice.AsOf : null, now, status, advice.Regime, advice.BaseCurrency, capital, listed, shortlisted, lines ?? []);

        if (!settings.Enabled)
        {
            var off = Page(SearchStatus.Off);
            await store.SaveLatestCandidatesAsync(off, cancellationToken).ConfigureAwait(false);
            return off;
        }

        var saved = await store.GetSearchStateAsync(cancellationToken).ConfigureAwait(false);
        var state = saved?.Value ?? new SearchState(null, null, 0);
        if (state.LastDay is { } last && last >= advice.AsOf)
        {
            // Searched already. A run that failed after saving its state but before the page left the page behind: copy it.
            var latest = await store.GetCandidatesAsync("candidates-latest", cancellationToken).ConfigureAwait(false);
            if (latest?.AsOf is not { } shown || shown < last)
            {
                var dated = await store.GetCandidatesAsync(DatedId(last), cancellationToken).ConfigureAwait(false);
                if (dated is not null)
                    await store.SaveLatestCandidatesAsync(dated, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

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
                // Only what the search can use is kept: the whole list, over-the-counter venues included, would not fit one document.
                var venues = settings.Venues.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var listed = (await market.GetListedStocksAsync(settings.Exchange, cancellationToken).ConfigureAwait(false))
                    .Where(l => venues.Contains(l.Venue) && string.Equals(l.Type, settings.Type, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (listed.Count == 0)
                    throw new StockDataException("EODHD's symbol list has no stock on the screener's venues.");
                if (listed.Count > MaxUniverse)
                    throw new StockDataException($"EODHD's symbol list has {listed.Count} stocks on the screener's venues, over {MaxUniverse}.");
                universe = new StoredUniverse(today, listed);
                if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(universe, StocksJson.Options).Length > MaxUniverseBytes)
                    throw new StockDataException("EODHD's symbol list is too large to keep in one document.");
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

        // The download must be for the advised session: older, the provider hasn't updated yet; newer, the advice's own bars
        // came late. Either way the next run searches again.
        var marketDay = rows.Count == 0 ? (DateOnly?)null : rows.Max(r => r.Day);
        if (marketDay != advice.AsOf)
        {
            await GiveBackAsync(used).ConfigureAwait(false);
            var wait = Page(marketDay > advice.AsOf ? SearchStatus.AdviceBehind : SearchStatus.NotReady);
            await store.SaveLatestCandidatesAsync(wait, cancellationToken).ConfigureAwait(false);
            return wait;
        }

        var bySymbol = universe!.Stocks.DistinctBy(s => s.Symbol, StringComparer.Ordinal).ToDictionary(s => s.Symbol, StringComparer.Ordinal);
        var exclude = config.Watchlist.Select(w => w.Symbol).Concat(advice.Holdings.Select(h => h.Symbol))
            .Concat(advice.Owner?.Holdings.Select(h => h.Symbol) ?? []).Append(config.Benchmark)
            .ToHashSet(StringComparer.Ordinal);
        var shortlist = Screener.Shortlist(rows, bySymbol, advice.AsOf, config.AdvisorSettings.MaxSharePrice, exclude, settings);

        var cutoff = config.DataCutoff(now);
        var found = new List<Candidate>();
        foreach (var row in shortlist)
        {
            if (deadline is { } end && time.GetUtcNow() >= end)
                break; // out of time: list what was found
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
            // Judged on the advised session only: a stock whose own bars lag would be judged on an older close.
            if (Screener.Evaluate(row.Symbol, listed.Name, listed.Venue, row.MarketCap, bars, today, capital, config.AdvisorSettings, settings) is { } candidate
                && candidate.Day == advice.AsOf)
                found.Add(candidate);
        }

        var lines = Screener.Rank(found, settings).Select(c => new CandidateLine(
            c.Symbol, c.Name, c.Venue, c.State == EntryState.Breakout ? "breakout" : "near-high", c.Close, c.PriorHigh, c.SlowAverage, c.Stop,
            c.Shares, c.Momentum, c.MarketCap)).ToList();
        var page = Page(SearchStatus.Done, bySymbol.Count, shortlist.Count, lines);
        await store.AddCandidatesAsync(DatedId(advice.AsOf), page, cancellationToken).ConfigureAwait(false);
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

    private static string DatedId(DateOnly day) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"candidates-{day:yyyy-MM-dd}");
}
