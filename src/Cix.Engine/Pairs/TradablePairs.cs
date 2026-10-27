using System.Globalization;
using System.Text.Json.Nodes;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.Manual;
using Cix.Engine.MarketData;
using Cix.Engine.Persistence;
using Cix.Engine.Scanning;

namespace Cix.Engine.Pairs;

/// <summary>The owner's list of tradable pairs, as saved in <c>tradable-pairs-&lt;mode&gt;</c>.</summary>
public sealed record TradablePairsDocument(IReadOnlyList<string> Symbols, DateTimeOffset UpdatedAt, string? UpdatedBy = null);

/// <summary>One pair the exchange lists in the permitted quote currency, for the dashboard's picker.</summary>
/// <param name="Permitted">Whether the mode's limits file (<c>config/risk-limits.json</c> or <c>config/risk-limits.live.json</c>) lets the owner add it.</param>
public sealed record CatalogPair(string Symbol, string Base, bool Tradable, bool Permitted);

/// <summary>
/// What the dashboard needs to add and remove pairs (<c>pair-catalog-&lt;mode&gt;</c>): the pairs tradable now, the most there
/// may be, and the exchange's active pairs in the quote currency the limits permit.
/// </summary>
public sealed record PairCatalogDocument(
    IReadOnlyList<string> Tradable,
    int Cap,
    string? QuoteCurrency,
    IReadOnlyList<string> AlwaysPermitted,
    IReadOnlyList<CatalogPair> Pairs,
    DateTimeOffset UpdatedAt);

/// <summary>Where the tradable list and the catalog live (the <c>state</c> container in Cosmos DB).</summary>
public interface ITradablePairsRepository
{
    Task<TradablePairsDocument?> ReadAsync(TradingMode mode, CancellationToken cancellationToken);

    Task SaveAsync(TradingMode mode, TradablePairsDocument document, CancellationToken cancellationToken);

    Task SaveCatalogAsync(TradingMode mode, PairCatalogDocument catalog, CancellationToken cancellationToken);
}

/// <summary>For tests, and to show the shape the documents hold.</summary>
public sealed class InMemoryTradablePairsRepository : ITradablePairsRepository
{
    public Dictionary<TradingMode, TradablePairsDocument> Saved { get; } = [];

    public PairCatalogDocument? Catalog { get; private set; }

    /// <summary>When set, every call throws it: a database outage.</summary>
    public Exception? Failure { get; set; }

    public Task<TradablePairsDocument?> ReadAsync(TradingMode mode, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult(Saved.GetValueOrDefault(mode));
    }

    public Task SaveAsync(TradingMode mode, TradablePairsDocument document, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Saved[mode] = document;
        return Task.CompletedTask;
    }

    public Task SaveCatalogAsync(TradingMode mode, PairCatalogDocument catalog, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Catalog = catalog;
        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (Failure is { } failure)
            throw failure;
    }
}

/// <summary>The Cosmos DB documents behind <see cref="ITradablePairsRepository"/>.</summary>
public sealed class CosmosTradablePairsRepository(CosmosDb db) : ITradablePairsRepository
{
    public const string ListType = "tradable-pairs";
    public const string CatalogType = "pair-catalog";

    public const string ListAbout =
        "The pairs the engine trades now: the owner adds and removes them from the dashboard (through requests), within the mode's limits file (config/risk-limits.json, or config/risk-limits.live.json for live). Written by the engine only.";
    public const string CatalogAbout =
        "The exchange's active pairs in the permitted quote currency and which are tradable, written by the engine for the dashboard's pair picker.";

    public static string ListId(TradingMode mode) => $"tradable-pairs-{mode.ToString().ToLowerInvariant()}";

    public static string CatalogId(TradingMode mode) => $"pair-catalog-{mode.ToString().ToLowerInvariant()}";

    public Task<TradablePairsDocument?> ReadAsync(TradingMode mode, CancellationToken cancellationToken) =>
        CosmosDb.ReadStateAsync<TradablePairsDocument>(db.State, ListId(mode), cancellationToken);

    public Task SaveAsync(TradingMode mode, TradablePairsDocument document, CancellationToken cancellationToken) =>
        CosmosDb.WriteStateAsync(db.State, ListId(mode), document, ListType, ListAbout, cancellationToken);

    public Task SaveCatalogAsync(TradingMode mode, PairCatalogDocument catalog, CancellationToken cancellationToken) =>
        CosmosDb.WriteStateAsync(db.State, CatalogId(mode), catalog, CatalogType, CatalogAbout, cancellationToken);
}

/// <summary>
/// Adds and removes tradable pairs at run time from the owner's dashboard requests (<see cref="RequestType"/> documents in the
/// <c>requests</c> container), so a new coin needs no configuration change or redeploy. The limits in
/// the mode's limits file (<c>config/risk-limits.json</c>, or <c>config/risk-limits.live.json</c> for live) still decide which
/// pairs may ever be traded and how many at once, and every order still
/// goes through the risk gate, which reads the same list. A pair is added only if the exchange lists it as active, in the
/// account's quote currency, with the 24h traded value the running strategy's scanner asks for; it is removed only when
/// nothing is held, reserved or working in it, not even a residual below the exchange minimum, so equity and exits never lose
/// sight of a position. Each change is journaled (kind
/// <see cref="Kind"/>) before it applies and saved as <c>tradable-pairs-&lt;mode&gt;</c>, which a restart restores.
/// Paper and live alike (ADR-0015, ADR-0018): each mode keeps its own list, within its own limits.
/// </summary>
public sealed partial class TradablePairsService(
    TradableSymbols tradable,
    ITradablePairsRepository repository,
    PairCatalog pairs,
    IMarketDataSource market,
    ExecutionService execution,
    IJournal journal,
    TradingContext trading,
    TimeProvider time,
    ILogger<TradablePairsService> logger,
    IManualRequestStore? requests = null,
    MarketScanService? scanner = null,
    IEngineLease? lease = null,
    Func<decimal>? liquidityFloor = null) : BackgroundService
{
    /// <summary>The owner's limits file for this mode, as the dashboard and the owner know it.</summary>
    private string LimitsFile => trading.Mode == TradingMode.Live ? "config/risk-limits.live.json" : "config/risk-limits.json";

    /// <summary>The least 24h traded value a pair needs to be added: the running strategy's scanner filter.</summary>
    private decimal LiquidityFloor => liquidityFloor?.Invoke() ?? scanner?.Parameters.MinQuoteVolume24h ?? new Domain.Scanning.ScannerParameters().MinQuoteVolume24h;

    /// <summary>The <c>type</c> of a dashboard request to add or remove a pair, in the <c>requests</c> container.</summary>
    public const string RequestType = "tradable-pair";

    public const string Kind = "pairs";
    public const string Actor = "engine";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>A request older than this when the engine first sees it expires instead of acting on a stale intent.</summary>
    public static readonly TimeSpan RequestMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>How often the catalog for the dashboard is written again (the exchange's pairs rarely change).</summary>
    public static readonly TimeSpan CatalogInterval = TimeSpan.FromHours(1);

    /// <summary>Who engages the kill switch when the saved pairs can't all be restored.</summary>
    public const string By = "tradable-pairs";

    private DateTimeOffset _catalogAt = DateTimeOffset.MinValue;

    /// <summary>Why the saved list couldn't be restored as saved; the kill switch is engaged for it at the first poll.</summary>
    private string? _restoreProblem;

    /// <summary>Outcomes of requests already acted on, by id: a request whose status write failed is closed, never applied twice.</summary>
    private readonly Dictionary<string, (string Status, string Detail)> _decided = new(StringComparer.Ordinal);

    /// <summary>
    /// At startup, before the engine scans or serves tools: restores the saved list (dropping pairs the limits no longer
    /// permit, or keeping the configured list if the saved one can't be used) and writes the catalog.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var saved = await repository.ReadAsync(trading.Mode, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            var symbols = (saved.Symbols ?? []).Where(s => s is not null).Select(MarketDataPoller.NormaliseSymbol).ToHashSet(StringComparer.Ordinal);
            var dropped = symbols.Where(s => !tradable.Limits.Permits(s)).Order(StringComparer.Ordinal).ToList();
            symbols.ExceptWith(dropped);
            if (dropped.Count > 0)
                LogDropped(logger, string.Join(", ", dropped));
            var problems = tradable.TrySet(symbols, now);
            if (problems.Count > 0)
                LogRestoreRefused(logger, string.Join(" ", problems));

            // Save what is tradable now, so the dashboard shows it, not the list as it was saved.
            if (!tradable.Current.SetEquals(saved.Symbols ?? []))
                await repository.SaveAsync(trading.Mode, new TradablePairsDocument(tradable.Current.Order(StringComparer.Ordinal).ToList(), now, Actor), cancellationToken).ConfigureAwait(false);

            // A pair left out may still be held: it would drop out of equity and its exits would be refused. Halt until the owner looks.
            if (dropped.Count > 0 || problems.Count > 0)
                _restoreProblem = problems.Count > 0
                    ? $"The saved trading pairs couldn't be restored ({problems[0]}); only {string.Join(", ", tradable.Current.Order(StringComparer.Ordinal))} are tradable. Check for holdings in the others before resuming."
                    : $"Saved trading pairs no longer permitted by {LimitsFile} were dropped: {string.Join(", ", dropped)}. Check for holdings in them before resuming.";
        }

        await WriteCatalogAsync(now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes new requests, then refreshes the catalog when due. Tests call this instead of running the loop.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (lease is { IsHeld: false })
            return; // only the engine that trades changes the list
        if (_restoreProblem is { } restore)
        {
            await execution.EngageKillSwitchAsync(restore, By, cancellationToken).ConfigureAwait(false);
            _restoreProblem = null;
        }

        var now = time.GetUtcNow();
        var changed = await TakeRequestsAsync(now, cancellationToken).ConfigureAwait(false);
        if (changed || now - _catalogAt >= CatalogInterval)
            await WriteCatalogAsync(now, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPollFailed(logger, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Handles each new request in the order it was made. Returns whether the list changed.</summary>
    private async Task<bool> TakeRequestsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (requests is null || requests.State != "ok")
            return false;
        var mode = trading.Mode.ToString().ToLowerInvariant();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        IReadOnlyList<StoredRequest> found;
        try
        {
            found = await requests.ListNewAsync(today.AddDays(-1), today.AddDays(1), mode, cancellationToken, RequestType).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRequestsFailed(logger, ex.GetType().Name);
            return false;
        }

        var changed = false;
        foreach (var (stored, request) in found.Select(r => (Stored: r, Parsed: ParseRequest(r.Document, now)))
            .OrderBy(p => p.Parsed.CreatedAt ?? DateTimeOffset.MinValue).ThenBy(p => p.Stored.Id, StringComparer.Ordinal))
        {
            string status, detail;
            if (_decided.TryGetValue(stored.Id, out var decided))
            {
                (status, detail) = decided; // acted on already; only its status is written again
            }
            else if (request.Change is not { } change)
            {
                (status, detail) = (request.Expired ? ManualRequestStatus.Expired : ManualRequestStatus.Invalid, request.Problem!);
            }
            else
            {
                var outcome = await ApplyAsync(change, now, cancellationToken).ConfigureAwait(false);
                if (outcome is null)
                    continue; // not decided (the journal or the database failed): still new, taken at the next poll
                (status, detail) = outcome.Value;
                changed |= status == "accepted";
                _decided[stored.Id] = outcome.Value;
            }

            try
            {
                var basis = ManualRequestDocument.ShapeProblem(stored.Document) is null
                    ? stored.Document
                    : new JsonObject { ["id"] = stored.Id, ["day"] = stored.Day, ["type"] = RequestType };
                await requests.TryReplaceAsync(stored, ManualRequestDocument.WithStatus(basis, status, now, Actor, detail), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogRequestsFailed(logger, ex.GetType().Name); // read again next time; adding or removing twice changes nothing
            }
        }

        return changed;
    }

    /// <summary>A change the owner asked for.</summary>
    internal sealed record PairChange(bool Add, string Symbol, DateTimeOffset RequestedAt, string? Note);

    internal sealed record ParsedRequest(PairChange? Change, DateTimeOffset? CreatedAt, string? Problem, bool Expired = false);

    /// <summary>A dashboard request as a change, or why it can't be one. The engine trusts nothing in the document.</summary>
    internal static ParsedRequest ParseRequest(JsonObject document, DateTimeOffset now)
    {
        if (ManualRequestDocument.ShapeProblem(document) is { } shape)
            return new ParsedRequest(null, null, shape);
        var createdAt = ManualRequestDocument.Field(document, "createdAt") is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
        if (createdAt is null)
            return new ParsedRequest(null, null, "The request has no valid createdAt.");
        if (ManualRequestDocument.Day(document) != createdAt.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            return new ParsedRequest(null, createdAt, "day must be the UTC date of createdAt.");
        if (createdAt - now > TimeSpan.FromMinutes(1))
            return new ParsedRequest(null, createdAt, "The request's time is in the future.");
        var action = ManualRequestDocument.Field(document, "action");
        if (action is not ("add" or "remove"))
            return new ParsedRequest(null, createdAt, "action must be add or remove.");
        var symbol = ManualRequestDocument.Field(document, "symbol");
        if (symbol is null || !SymbolFormat().IsMatch(symbol))
            return new ParsedRequest(null, createdAt, "symbol must be a pair such as SOL-EUR.");
        if (now - createdAt > RequestMaxAge)
            return new ParsedRequest(null, createdAt, "The request was more than 5 minutes old when the engine saw it; send it again.", Expired: true);
        return new ParsedRequest(new PairChange(action == "add", symbol, createdAt.Value, ManualRequestDocument.Clean(ManualRequestDocument.Field(document, "note"), 280)), createdAt, null);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z0-9]{1,20}-[A-Z]{3,5}$")]
    private static partial System.Text.RegularExpressions.Regex SymbolFormat();

    /// <summary>
    /// Checks and applies one change: (status, detail), or null when it should be tried again at the next poll. A removal is
    /// checked and applied while no order can be placed, so nothing can be bought or left working in the pair in between.
    /// </summary>
    private Task<(string Status, string Detail)?> ApplyAsync(PairChange change, DateTimeOffset now, CancellationToken cancellationToken) =>
        change.Add
            ? DecideAsync(change, null, now, cancellationToken)
            : execution.RunExclusiveAsync(view => DecideAsync(change, view, now, cancellationToken), cancellationToken);

    private async Task<(string Status, string Detail)?> DecideAsync(PairChange change, ExclusiveView? view, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = tradable.Current;
        var symbol = change.Symbol;
        if (change.Add == current.Contains(symbol))
            return ("accepted", change.Add ? $"{symbol} is already tradable." : $"{symbol} is not tradable; nothing to remove.");

        var problem = change.Add
            ? await AddProblemAsync(symbol, current, now, cancellationToken).ConfigureAwait(false)
            : await RemoveProblemAsync(symbol, current, view!, cancellationToken).ConfigureAwait(false);
        var next = change.Add ? current.Append(symbol).ToHashSet(StringComparer.Ordinal) : current.Where(s => s != symbol).ToHashSet(StringComparer.Ordinal);
        if (problem is null && tradable.Problems(next) is { Count: > 0 } limits)
            problem = limits[0];

        var verb = change.Add ? "add" : "remove";
        if (problem is not null)
        {
            if (!await AppendAsync("refused", $"Pair {symbol} not {(change.Add ? "added" : "removed")}: {problem}", change, current, null, problem).ConfigureAwait(false))
                return null;
            LogRefused(logger, verb, symbol, problem);
            return (ManualRequestStatus.Rejected, problem);
        }

        // Journal first: a change the journal doesn't record doesn't happen. Then save it, so a restart keeps it, then apply.
        if (!await AppendAsync(change.Add ? "added" : "removed", $"Pair {symbol} {(change.Add ? "added" : "removed")}", change, current, next, null).ConfigureAwait(false))
            return null;
        try
        {
            await repository.SaveAsync(trading.Mode, new TradablePairsDocument(next.Order(StringComparer.Ordinal).ToList(), now, "owner"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogSaveFailed(logger, ex.GetType().Name);
            return null;
        }

        if (tradable.TrySet(next, now) is { Count: > 0 } late)
            return (ManualRequestStatus.Rejected, late[0]); // can't happen after the check above; kept so a list is never half-applied
        LogApplied(logger, verb, symbol);
        return ("accepted", change.Add
            ? $"{symbol} added. Its prices load now; the scanner can signal it once it has a day of hourly candles, and every order still goes through the risk gate."
            : $"{symbol} removed: no new orders in it.");
    }

    /// <summary>Why <paramref name="symbol"/> can't be added; null when it can.</summary>
    private async Task<string?> AddProblemAsync(string symbol, IReadOnlySet<string> current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!tradable.Limits.Permits(symbol))
            return tradable.Limits.AllowedQuoteCurrency is { } quote
                ? $"Only {quote} pairs are permitted by {LimitsFile}."
                : $"{LimitsFile} doesn't permit it; only the owner can widen that list.";
        if (current.Count >= tradable.Limits.TradableCap)
            return $"At most {tradable.Limits.TradableCap} pairs can be tradable at once; remove one first.";

        PairInfo? pair;
        IReadOnlyList<Candle> candles;
        try
        {
            pair = await pairs.GetAsync(symbol, cancellationToken).ConfigureAwait(false);
            candles = pair is { Active: true }
                ? await market.GetCandlesAsync(symbol, MarketDataPoller.HourInterval, now - TimeSpan.FromHours(25), cancellationToken).ConfigureAwait(false)
                : [];
        }
        catch (Exception ex) when (ex is ExchangeException or HttpRequestException)
        {
            return "The exchange couldn't be asked about this pair right now; send the request again later.";
        }

        if (pair is null || !pair.Active)
            return "Revolut X doesn't list it as an active pair.";
        var quoteOf = current.Select(s => s.Split('-')[^1]).Distinct(StringComparer.Ordinal).ToList();
        if (quoteOf.Count == 1 && !string.Equals(pair.Quote, quoteOf[0], StringComparison.OrdinalIgnoreCase))
            return $"Every pair must be quoted in {quoteOf[0]}.";

        // The same liquidity floor the running strategy's scanner applies: thin markets stay out.
        var day = candles.Where(c => c.Start >= now - TimeSpan.FromHours(24) && c.Start + TimeSpan.FromHours(1) <= now).ToList();
        var traded = day.Sum(c => c.Volume * c.Close);
        var floor = LiquidityFloor;
        if (traded < floor)
            return string.Create(CultureInfo.InvariantCulture, $"It traded {traded:0} {pair.Quote} in the last 24 hours; the running strategy needs at least {floor:0}.");
        return null;
    }

    /// <summary>Why <paramref name="symbol"/> can't be removed; null when it can. Reads the account fresh, under the order lock.</summary>
    private static async Task<string?> RemoveProblemAsync(string symbol, IReadOnlySet<string> current, ExclusiveView view, CancellationToken cancellationToken)
    {
        if (current.Count <= 1)
            return "At least one pair must stay tradable.";
        if (view.Pending.Any(p => MarketDataPoller.NormaliseSymbol(p.Order.Symbol) == symbol))
            return "An order in it was just sent and isn't settled yet; send the request again in a minute.";
        IReadOnlyList<ExchangeOrder> open;
        PortfolioSnapshot? portfolio;
        try
        {
            open = await view.OpenOrdersAsync(cancellationToken).ConfigureAwait(false);
            portfolio = await view.PortfolioAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExchangeException or HttpRequestException or PortfolioUnavailableException)
        {
            portfolio = null;
            open = [];
        }

        if (portfolio is null)
            return "The portfolio can't be valued right now, so the engine can't tell whether you hold it; send the request again later.";
        if (open.Any(o => o.IsOpen && MarketDataPoller.NormaliseSymbol(o.Symbol) == symbol))
            return "It has an open order; cancel it or let it fill first.";
        // Any balance in it, however small, keeps the pair: once removed, it would drop out of equity and of the exits. That
        // covers what an order reserves and a residual below the exchange minimum, which can't be sold on its own.
        if (portfolio.SellableBaseBySymbol?.ContainsKey(symbol) == true)
            return string.Create(CultureInfo.InvariantCulture,
                $"You still hold it (about {portfolio.PositionQuoteBySymbol.GetValueOrDefault(symbol):0.##} {symbol.Split('-')[^1]}); sell it first.");
        // What is left is a residual below the exchange minimum (an order reserving it was refused above). The owner asked for
        // a pair to go only at a zero balance; it can be cleared by buying up to the minimum and selling all.
        if (portfolio.PositionQuoteBySymbol.GetValueOrDefault(symbol) > 0m)
            return string.Create(CultureInfo.InvariantCulture,
                $"A residual of it is still on the account (about {portfolio.PositionQuoteBySymbol[symbol]:0.##} {symbol.Split('-')[^1]}), below the exchange minimum, so it can't be sold on its own; a pair can be removed only when its balance is zero. To clear it, buy enough to reach the minimum, then sell all.");
        if (portfolio.OpenBuyQuoteBySymbol?.GetValueOrDefault(symbol) > 0m)
            return "A buy in it is still being settled; send the request again in a minute.";
        return null;
    }

    /// <summary>Writes the dashboard's catalog; a failure is logged and tried again at the next poll.</summary>
    private async Task WriteCatalogAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = tradable.Current;
        var limits = tradable.Limits;
        var quote = limits.AllowedQuoteCurrency ?? current.Select(s => s.Split('-')[^1]).FirstOrDefault();
        try
        {
            var listed = await market.GetPairsAsync(cancellationToken).ConfigureAwait(false);
            var items = listed
                .Where(p => p.Active && string.Equals(p.Quote, quote, StringComparison.OrdinalIgnoreCase))
                .Select(p => (Pair: p, Symbol: MarketDataPoller.NormaliseSymbol(p.Symbol)))
                .Select(p => new CatalogPair(p.Symbol, p.Pair.Base.ToUpperInvariant(), current.Contains(p.Symbol), limits.Permits(p.Symbol)))
                .Where(p => p.Permitted || p.Tradable)
                .OrderBy(p => p.Symbol, StringComparer.Ordinal)
                .Take(500)
                .ToList();
            await repository.SaveCatalogAsync(trading.Mode, new PairCatalogDocument(
                current.Order(StringComparer.Ordinal).ToList(), limits.TradableCap, quote,
                limits.AllowedSymbols.Order(StringComparer.Ordinal).ToList(), items, now), cancellationToken).ConfigureAwait(false);
            _catalogAt = now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCatalogFailed(logger, ex.GetType().Name);
        }
    }

    private async Task<bool> AppendAsync(string action, string summary, PairChange change, IReadOnlySet<string> from, IReadOnlySet<string>? to, string? problem)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = action,
            ["mode"] = trading.Mode.ToString().ToLowerInvariant(),
            ["symbol"] = change.Symbol,
            ["requested"] = change.Add ? "add" : "remove",
            ["requestedBy"] = "owner",
            ["requestedAt"] = change.RequestedAt.ToString("O", CultureInfo.InvariantCulture),
            ["from"] = string.Join(",", from.Order(StringComparer.Ordinal)),
            ["note"] = change.Note ?? string.Empty,
        };
        if (to is not null)
            data["to"] = string.Join(",", to.Order(StringComparer.Ordinal));
        if (problem is not null)
            data["problem"] = problem;
        var now = time.GetUtcNow();
        try
        {
            await journal.AppendAsync(new JournalEntry(JournalIds.New(now, Kind), now, Kind, Actor, null, null, summary, data), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogJournalFailed(logger, ex.Message);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tradable pairs: {Verb} {Symbol} done")]
    private static partial void LogApplied(ILogger logger, string verb, string symbol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tradable pairs: {Verb} {Symbol} refused: {Problem}")]
    private static partial void LogRefused(ILogger logger, string verb, string symbol, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saved tradable pairs no longer permitted by the risk limits, dropped: {Symbols}")]
    private static partial void LogDropped(ILogger logger, string symbols);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saved tradable pairs not restored ({Problems}); the configured allowlist is tradable")]
    private static partial void LogRestoreRefused(ILogger logger, string problems);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pair requests could not be read or updated ({Error}); retried at the next poll")]
    private static partial void LogRequestsFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tradable pairs could not be saved ({Error}); the change is retried at the next poll")]
    private static partial void LogSaveFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pair catalog could not be written ({Error}); retried at the next poll")]
    private static partial void LogCatalogFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pair poll failed ({Error}); retried at the next poll")]
    private static partial void LogPollFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed for a pair change: {Detail}; retried at the next poll")]
    private static partial void LogJournalFailed(ILogger logger, string detail);
}
