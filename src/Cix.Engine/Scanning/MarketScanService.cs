using System.Globalization;
using System.Text.Json;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Domain.Scanning;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Strategy;

namespace Cix.Engine.Scanning;

/// <summary>Bound from the "Scanner" section (ADR-0006).</summary>
public sealed class ScannerOptions
{
    public const string SectionName = "Scanner";

    /// <summary>How often the engine scans in the background and journals new signals.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    public ScannerParameters Parameters { get; set; } = new();

    /// <summary>
    /// When true (the default), the engine itself sells positions that reach their stop-loss or take-profit, through the risk
    /// gate, without waiting for an agent run. Trend exits stay with the trader.
    /// </summary>
    public bool ProtectiveExits { get; set; } = true;

    /// <summary>
    /// Refuses a <c>Scanner:Universe</c> list: ADR-0021 removed the curated universe, and no pair list may live in configuration.
    /// The scan covers the watched coins the owner adds from the dashboard.
    /// </summary>
    public static void ThrowIfUniverseConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.GetSection(SectionName + ":Universe").Exists())
            throw new InvalidOperationException("Scanner:Universe was removed (ADR-0021): the scan covers the coins added on the dashboard's Coins tab. Remove the setting.");
    }

    public void Validate()
    {
        if (Interval < TimeSpan.FromMinutes(1) || Interval > TimeSpan.FromHours(1))
            throw new InvalidOperationException("Scanner:Interval must be between 1 minute and 1 hour.");
        Parameters.Validate();
    }
}

/// <summary>
/// What the engine watches, scans and may trade (ADR-0021): the watched list the owner edits on the Coins tab, plus the coins
/// the account holds. There is no curated universe in configuration: the scan covers exactly the watched coins, and the
/// strategy's filters decide which of them trade. The exchange's active pairs are checked at every scan.
/// </summary>
public sealed class MarketUniverse
{
    private readonly Func<IReadOnlySet<string>> _watched;
    private Snapshot? _cache;

    /// <summary>A fixed list, for tests.</summary>
    public MarketUniverse(IReadOnlySet<string> watched)
        : this(() => watched)
    {
        ArgumentNullException.ThrowIfNull(watched);
    }

    /// <summary>With the run-time list the owner changes from the dashboard: <see cref="Tradable"/> follows it.</summary>
    public MarketUniverse(Cix.Domain.Risk.TradableSymbols tradable)
        : this(() => tradable.Current)
    {
        ArgumentNullException.ThrowIfNull(tradable);
    }

    private MarketUniverse(Func<IReadOnlySet<string>> watched) => _watched = watched;

    /// <summary>What the scan covers: the watched coins.</summary>
    public IReadOnlyList<string> Candidates => Lists().Symbols;

    /// <summary>The watched coins, polled and valued; orders in them still pass the strategy's filters and the risk gate.</summary>
    public IReadOnlyList<string> Tradable => Lists().Symbols;

    public bool IsTradable(string symbol) => Tradable.Contains(symbol, StringComparer.Ordinal);

    /// <summary>The list from one read of the watched set, recomputed only when it changes.</summary>
    private Snapshot Lists()
    {
        var source = _watched();
        if (Volatile.Read(ref _cache) is { } cached && ReferenceEquals(cached.Source, source))
            return cached;
        var lists = new Snapshot(source, MarketDataPoller.NormaliseSymbols(source));
        Volatile.Write(ref _cache, lists);
        return lists;
    }

    private sealed record Snapshot(IReadOnlySet<string> Source, IReadOnlyList<string> Symbols);
}

/// <summary>
/// Gathers features, candles, pair rules, open orders and positions, runs the deterministic <see cref="MarketScanner"/>, and
/// journals each new signal once. Reads only: it never places or cancels orders.
/// </summary>
public sealed partial class MarketScanService(
    MarketUniverse universe,
    FeatureService features,
    ICandleStore candles,
    PairCatalog pairs,
    ExecutionService execution,
    KillSwitch killSwitch,
    IJournal journal,
    IStrategySpecStore specs,
    ScannerOptions options,
    TimeProvider time,
    ILogger<MarketScanService> logger,
    Analysis.MarketViewStore? views = null,
    ScannerFilterRecorder? filters = null,
    Cix.Domain.Risk.RiskLimits? riskLimits = null,
    ProtectionOptions? protection = null)
{
    public const string SignalKind = "scan-signal";
    public const string Actor = "scanner";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string> _lastSignals = new(StringComparer.Ordinal);

    /// <summary>The most recent scan, or null before the first.</summary>
    public ScanResult? Latest { get; private set; }

    /// <summary>
    /// Why a strategy buy in <paramref name="symbol"/> is refused now, or null when the latest scan let it pass every market
    /// filter of the active strategy (ADR-0021): a watched coin trades only while it passes. A coin not scanned yet is refused
    /// too, as its data is still loading.
    /// </summary>
    public string? BuyRefusal(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        symbol = MarketDataPoller.NormaliseSymbol(symbol);
        if (Latest is not { } scan)
            return $"{symbol} hasn't been scanned yet; the strategy's filters decide once it has.";
        if (scan.Excluded.FirstOrDefault(e => string.Equals(e.Symbol, symbol, StringComparison.Ordinal)) is { } excluded)
            return $"{symbol} is filtered out by the strategy ({excluded.Reason}): {excluded.Detail}";
        return scan.Ranked.Any(r => string.Equals(r.Symbol, symbol, StringComparison.Ordinal))
            ? null
            : $"{symbol} hasn't been scanned yet; the strategy's filters decide once it has.";
    }

    private TaskCompletionSource _scanned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _scans;

    /// <summary>How many scans have finished since start.</summary>
    public long Scans => Interlocked.Read(ref _scans);

    /// <summary>Completes once a scan has finished after <paramref name="seen"/> (a <see cref="Scans"/>): the status is written then.</summary>
    public Task WaitForScanAsync(long seen, CancellationToken cancellationToken)
    {
        var scanned = Volatile.Read(ref _scanned); // before the count: a scan in between shows in the count
        return Scans != seen ? Task.CompletedTask : scanned.Task.WaitAsync(cancellationToken);
    }

    public bool Halted => killSwitch.State.Engaged;

    /// <summary>True when the active spec lets agents call scan_market: the trader then runs on signals, not on a timer.</summary>
    public bool SignalDriven => specs.Active?.Allows("scan_market") ?? false;

    /// <summary>
    /// The scanner settings in force: the active spec's <c>scanner</c> parameters (with its <c>orderQuote</c>), or configuration
    /// when the spec has none. Program reads this at startup, so a bad spec stops the engine instead of trading on defaults.
    /// </summary>
    public ScannerParameters Parameters => ParametersFor(specs.Active);

    private ScannerParameters ParametersFor(StrategySpec? spec)
    {
        var cached = Volatile.Read(ref _parameters);
        if (cached is not null && cached.Label == spec?.Label)
            return cached.Parameters;
        var resolved = FromSpec(spec, options.Parameters);
        Volatile.Write(ref _parameters, new CachedParameters(spec?.Label, resolved));
        return resolved;
    }

    private sealed record CachedParameters(string? Label, ScannerParameters Parameters);

    private CachedParameters? _parameters;

    /// <summary>
    /// A short, stable description of the latest scan's signals (for example <c>buy:SOL-EUR|sell:BTC-EUR:stop</c>), empty when
    /// there are none. The trader loop starts an agent run when it changes to something non-empty.
    /// </summary>
    public string SignalKey => Latest is { } scan
        ? KeyOf(options.ProtectiveExits ? scan with { Exits = scan.Exits.Where(x => !ProtectiveReasons.Contains(x.Reason)).ToList() } : scan, StaleOrderSymbols)
        : string.Empty; // stop and target exits are the engine's own job when protective exits are on: no agent run for them

    /// <summary>Orders resting longer than this need a run to cancel them (the specs' 50-minute tidy rule).</summary>
    public static readonly TimeSpan StaleOrderAge = TimeSpan.FromMinutes(50);

    /// <summary>
    /// Symbols with an open order older than <see cref="StaleOrderAge"/> at the latest scan. They count as signals, so an exit
    /// hidden behind an unfilled order, or a forgotten buy, gets a run within the hour rather than at the next heartbeat.
    /// </summary>
    public IReadOnlySet<string> StaleOrderSymbols { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    public static string KeyOf(ScanResult scan, IEnumerable<string>? staleOrderSymbols = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return string.Join('|', scan.Exits.Select(x => $"sell:{x.Symbol}:{x.Reason}")
            .Concat(scan.Entries.Select(e => $"buy:{e.Symbol}"))
            .Concat((staleOrderSymbols ?? []).Select(s => $"stale-order:{s}"))
            .Order(StringComparer.Ordinal));
    }

    private static readonly JsonSerializerOptions SpecJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, // a typo fails loudly
    };

    /// <summary>
    /// Scanner settings from a spec's <c>scanner</c> object, which replaces configuration entirely (unlisted values take the
    /// code defaults, so a spec version alone says what ran), plus its top-level <c>orderQuote</c>. Without a <c>scanner</c>
    /// object, <paramref name="fallback"/> (configuration) applies.
    /// </summary>
    public static ScannerParameters FromSpec(StrategySpec? spec, ScannerParameters fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (spec is null)
            return fallback;

        var result = fallback;
        if (spec.Parameters.TryGetValue("scanner", out var scanner))
        {
            if (scanner.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{spec.Label}: parameters.scanner must be an object.");
            if (scanner.EnumerateObject().Any(p => string.Equals(p.Name, "orderQuote", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{spec.Label}: set orderQuote once, at the top of parameters, not inside scanner.");
            try
            {
                result = scanner.Deserialize<ScannerParameters>(SpecJson)
                    ?? throw new InvalidOperationException($"{spec.Label}: parameters.scanner is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{spec.Label}: parameters.scanner is not valid ({ex.Message}).", ex);
            }
        }

        // One orderQuote per spec: the scanner's size cap and the order-size hook read the same number.
        if (spec.Parameters.TryGetValue("orderQuote", out var orderQuote))
        {
            if (orderQuote.ValueKind != JsonValueKind.Number || !orderQuote.TryGetDecimal(out var quote))
                throw new InvalidOperationException($"{spec.Label}: parameters.orderQuote must be a number.");
            result = result with { OrderQuote = quote };
        }

        result.Validate();
        return result;
    }

    /// <summary>Scans now and journals signals that weren't in the previous scan.</summary>
    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One read of the spec per scan: it can switch between scans (#105), never within one.
            var spec = specs.Active;
            var parameters = ParametersFor(spec);
            var result = await RunAsync(spec, parameters, cancellationToken).ConfigureAwait(false);
            await RecordNewSignalsAsync(result, spec).ConfigureAwait(false);
            Latest = result;
            Interlocked.Increment(ref _scans);
            Interlocked.Exchange(ref _scanned, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            await RecordFiltersAsync(result, parameters, spec).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The coin filters in force and which coins pass them, as recorded in the database (null before the first scan).</summary>
    public ScannerFilterState? RecordedFilters => filters?.Current;

    /// <summary>Records filter and eligibility changes; a failure is logged and retried at the next scan, never fails the scan.</summary>
    private async Task RecordFiltersAsync(ScanResult result, ScannerParameters parameters, StrategySpec? spec)
    {
        if (filters is null)
            return;
        try
        {
            await filters.RecordAsync(result, parameters, spec?.Label, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // anything, timeouts included: a record that failed must never stop the scan or the protective exits after it
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFiltersFailed(logger, ex.GetType().Name, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Coin filter record not updated ({Error}): {Detail}; retried at the next scan")]
    private static partial void LogFiltersFailed(ILogger logger, string error, string detail);

    private async Task<ScanResult> RunAsync(StrategySpec? spec, ScannerParameters parameters, CancellationToken cancellationToken)
    {
        var plans = await PositionPlans.LoadAsync(execution, journal, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The portfolio can't be valued right now.");
        var open = await execution.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        var withOpenOrder = open.Where(o => o.IsOpen).Select(o => MarketDataPoller.NormaliseSymbol(o.Symbol)).ToHashSet(StringComparer.Ordinal);
        var now = time.GetUtcNow();
        StaleOrderSymbols = open.Where(o => o.IsOpen && now - o.CreatedAt > StaleOrderAge)
            .Select(o => MarketDataPoller.NormaliseSymbol(o.Symbol)).ToHashSet(StringComparer.Ordinal);

        var inputs = new List<ScanInput>();
        foreach (var symbol in universe.Candidates)
        {
            var allowed = universe.IsTradable(symbol);
            PairInfo? pair = allowed ? await pairs.GetAsync(symbol, cancellationToken).ConfigureAwait(false) : null;
            var hourly = allowed ? candles.Recent(symbol, MarketDataPoller.HourInterval, FeatureService.HourlyCandlesNeeded) : Array.Empty<Candle>();
            inputs.Add(new ScanInput(symbol, features.GetFeatures(symbol), hourly, pair, allowed, withOpenOrder.Contains(symbol)));
        }

        var held = plans.Select(p => new HeldPosition(p.Symbol, p.SellableBaseSize, p.StopLoss, p.TakeProfit)).ToList();
        return MarketScanner.Scan(inputs, held, parameters, now, allowEntries: !Halted, view: views?.Current, entriesPausedReason: EntriesPausedReason(spec, now));
    }

    /// <summary>
    /// Why the strategy's own limits allow no buy now, so the scanner suggests none the engine would refuse: its cooldown
    /// since the last buy, or its total exposure cap (from the safety monitor's latest valuation). Null when buys are open.
    /// </summary>
    private string? EntriesPausedReason(StrategySpec? spec, DateTimeOffset now)
    {
        if (riskLimits is null)
            return null;
        var limits = Strategy.StrategySchema.LimitsOf(spec, riskLimits);
        if (limits.EntryCooldown is { } cooldown && execution.LastBuyAt is { } last && now - last < cooldown)
            return Inv($"cooldown until {(last + cooldown).UtcDateTime:HH:mm} UTC");
        if (limits.MaxExposureQuote is { } cap && execution.LatestSample?.Snapshot.TotalExposureQuote >= cap)
            return "strategy exposure cap reached";
        return null;
    }

    /// <summary>Exit reasons the engine acts on by itself: price levels set when the position was bought, no judgement needed.</summary>
    public static readonly IReadOnlySet<string> ProtectiveReasons = new HashSet<string>(StringComparer.Ordinal) { "stop", "target" };

    /// <summary>An unfilled protective sell older than this is cancelled, so the next scan re-prices it at the current bid.</summary>
    public static readonly TimeSpan ProtectiveRepriceAfter = TimeSpan.FromMinutes(4);

    /// <summary>After the gate refuses a protective sell for a symbol, wait this long before trying that symbol again.</summary>
    public static readonly TimeSpan ProtectiveBackoff = TimeSpan.FromMinutes(15);

    private readonly HashSet<string> _protectiveOrderIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _protectiveBackoff = new(StringComparer.Ordinal);
    private bool _protectiveRestored;

    /// <summary>
    /// Places a sell for every target exit in <paramref name="scan"/>, and every stop exit unless <see cref="ProtectionService"/>
    /// sells stops itself (<see cref="StopsOnEveryQuote"/>), through <see cref="ExecutionService"/> and so the
    /// risk gate. Positions stay protected while agents can't run (usage limit, outage, expired token). Returns the orders placed.
    /// </summary>
    public async Task<int> ApplyProtectiveExitsAsync(ScanResult scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (!options.ProtectiveExits || Halted)
            return 0;

        var placed = 0;
        foreach (var exit in scan.Exits.Where(x => ProtectiveReasons.Contains(x.Reason) && !(x.Reason == "stop" && StopsOnEveryQuote)))
        {
            var now = time.GetUtcNow();
            lock (_protectiveBackoff)
            {
                if (_protectiveBackoff.TryGetValue(exit.Symbol, out var until) && now < until)
                    continue; // refused recently: don't hammer the gate or the journal every scan
            }

            var clientOrderId = Guid.NewGuid().ToString();
            var result = await execution.PlaceAsync(
                new OrderIntent(exit.Symbol, OrderSide.Sell, exit.BaseSize, exit.LimitPrice, clientOrderId), postOnly: false, cancellationToken,
                countsTowardRejectStreak: false, context: new OrderContext("protective-exit", Strategy: specs.Active?.Label)).ConfigureAwait(false);
            if (result.Status == ExecutionStatus.Rejected)
            {
                lock (_protectiveBackoff)
                    _protectiveBackoff[exit.Symbol] = now + ProtectiveBackoff;
            }
            else
            {
                // Placed, or sent with an unknown outcome: track it either way so a resting sell gets re-priced.
                if (result.Status == ExecutionStatus.Placed)
                    placed++;
                lock (_protectiveOrderIds)
                    _protectiveOrderIds.Add(clientOrderId);
                lock (_protectiveBackoff)
                    _protectiveBackoff.Remove(exit.Symbol);
            }

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tool"] = "protective-exit",
                ["clientOrderId"] = clientOrderId,
                ["symbol"] = exit.Symbol,
                ["side"] = "sell",
                ["requestedSize"] = exit.BaseSize.ToString(CultureInfo.InvariantCulture),
                ["requestedPrice"] = exit.LimitPrice.ToString(CultureInfo.InvariantCulture),
                ["status"] = result.Status.ToString().ToLowerInvariant(),
                ["rule"] = result.Rule,
                ["reason"] = result.Reason,
                ["rationale"] = $"protective exit {exit.Symbol} {exit.Reason}: {exit.Detail}",
            };
            await AppendSafelyAsync(new JournalEntry(
                JournalIds.New(time.GetUtcNow(), "order"), time.GetUtcNow(), "order", "engine", null, specs.Active?.Label,
                Inv($"protective {exit.Reason} sell {exit.BaseSize} {exit.Symbol} @ {exit.LimitPrice}: {result.Status} ({result.Rule})"), data))
                .ConfigureAwait(false);
            LogProtectiveExit(logger, exit.Reason, exit.Symbol, exit.BaseSize, exit.LimitPrice, result.Status.ToString(), result.Rule);
        }

        return placed;
    }

    /// <summary>Stops are sold by <see cref="ProtectionService"/> on every quote, so the scan leaves them alone and sells only targets.</summary>
    public bool StopsOnEveryQuote => protection is { Enabled: true };

    /// <summary>The order source of the sells a strategy switch makes to bring holdings within the new strategy's caps.</summary>
    public const string TrimSource = "strategy-trim";

    /// <summary>
    /// After a strategy switch: sells the part of each holding above the new strategy's per-coin and total exposure caps
    /// (<see cref="StrategyTrim"/>), at the bid, through <see cref="ExecutionService"/> and so the risk gate. Once per switch:
    /// a sell that is refused isn't tried again, and one still resting after <see cref="ProtectiveRepriceAfter"/> is cancelled
    /// like a protective sell, so it never keeps a stop from selling. Skips coins with an open order or a protective sell in
    /// <paramref name="scan"/>, so nothing is sold twice. Returns the orders placed, or null when the trim couldn't be judged
    /// (halted, or the portfolio can't be valued): the caller tries again at the next scan.
    /// </summary>
    public async Task<int?> TrimToStrategyAsync(ScanResult scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (Halted)
            return null;
        if (riskLimits is null || specs.Active is not { } spec)
            return 0;
        var limits = Strategy.StrategySchema.LimitsOf(spec, riskLimits);
        if (limits.MaxPositionQuotePerSymbol is null && limits.MaxExposureQuote is null)
            return 0;
        if (await execution.GetPortfolioAsync(cancellationToken).ConfigureAwait(false) is not { } portfolio)
        {
            LogTrimSkipped(logger, spec.Label, "the portfolio can't be valued now");
            return null;
        }

        if (portfolio.Snapshot.SellableBaseBySymbol is not { } sellable)
        {
            LogTrimSkipped(logger, spec.Label, "the amounts free to sell are unknown");
            return null;
        }

        var open = await execution.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        var protective = scan.Exits.Where(x => ProtectiveReasons.Contains(x.Reason))
            .GroupBy(x => x.Symbol, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(x => x.BaseSize * x.LimitPrice), StringComparer.Ordinal);
        var busy = open.Where(o => o.IsOpen).Select(o => MarketDataPoller.NormaliseSymbol(o.Symbol)).Concat(protective.Keys)
            .ToHashSet(StringComparer.Ordinal);
        var holdings = new List<TrimHolding>();
        foreach (var (symbol, value) in portfolio.Snapshot.PositionQuoteBySymbol)
        {
            // A coin being sold still counts toward the exposure, but isn't sold again.
            if (value <= 0 || busy.Contains(symbol) || !universe.IsTradable(symbol))
                continue;
            if (features.GetFeatures(symbol).Bid is not { } bid || await pairs.GetAsync(symbol, cancellationToken).ConfigureAwait(false) is not { } pair)
                continue;
            holdings.Add(new TrimHolding(symbol, value, sellable.GetValueOrDefault(symbol), bid, pair));
        }

        // Coins left out (busy, unpriced) still count toward the total cap, less what their protective sells take, and so do
        // working buys, as the strategy gate counts them: only the rest is trimmed for it.
        var positions = portfolio.Snapshot.PositionQuoteBySymbol.Where(p => p.Value > 0).ToList();
        var leftOut = positions.Where(p => holdings.All(h => h.Symbol != p.Key))
            .Sum(p => Math.Max(0m, p.Value - protective.GetValueOrDefault(p.Key)));
        var workingBuys = Math.Max(0m, portfolio.Snapshot.TotalExposureQuote - positions.Sum(p => p.Value));
        var trimLimits = limits.MaxExposureQuote is { } cap ? limits with { MaxExposureQuote = Math.Max(0m, cap - leftOut - workingBuys) } : limits;
        var placed = 0;
        foreach (var sell in StrategyTrim.Plan(holdings, trimLimits))
        {
            var clientOrderId = Guid.NewGuid().ToString();
            var result = await execution.PlaceAsync(
                new OrderIntent(sell.Symbol, OrderSide.Sell, sell.BaseSize, sell.LimitPrice, clientOrderId), postOnly: false, cancellationToken,
                countsTowardRejectStreak: false, context: new OrderContext(TrimSource, Strategy: spec.Label)).ConfigureAwait(false);
            if (result.Status != ExecutionStatus.Rejected)
            {
                lock (_protectiveOrderIds)
                    _protectiveOrderIds.Add(clientOrderId); // cancelled if it rests, so it can't keep a stop from selling
            }

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tool"] = TrimSource,
                ["clientOrderId"] = clientOrderId,
                ["symbol"] = sell.Symbol,
                ["side"] = "sell",
                ["requestedSize"] = sell.BaseSize.ToString(CultureInfo.InvariantCulture),
                ["requestedPrice"] = sell.LimitPrice.ToString(CultureInfo.InvariantCulture),
                ["status"] = result.Status.ToString().ToLowerInvariant(),
                ["rule"] = result.Rule,
                ["reason"] = result.Reason,
                ["rationale"] = $"strategy trim {sell.Symbol} {sell.Rule}: {sell.Reason}",
            };
            await AppendSafelyAsync(new JournalEntry(
                JournalIds.New(time.GetUtcNow(), "order"), time.GetUtcNow(), "order", "engine", null, spec.Label,
                Inv($"trim to {spec.Label} sell {sell.BaseSize} {sell.Symbol} @ {sell.LimitPrice}: {result.Status} ({result.Rule})"), data))
                .ConfigureAwait(false);
            LogTrim(logger, sell.Symbol, sell.BaseSize, spec.Label, result.Status.ToString(), sell.Reason);
            if (result.Status == ExecutionStatus.Placed)
                placed++;
        }

        return placed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Trim {Symbol} {Size} for {Strategy}: {Status}. {Reason}")]
    private static partial void LogTrim(ILogger logger, string symbol, decimal size, string strategy, string status, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No trim to {Strategy}: {Reason}")]
    private static partial void LogTrimSkipped(ILogger logger, string strategy, string reason);

    /// <summary>Cancels this engine's protective sells that have rested unfilled too long; the next scan sells again at the bid.</summary>
    public async Task<int> CancelStaleProtectiveExitsAsync(CancellationToken cancellationToken)
    {
        if (!_protectiveRestored)
            await RestoreProtectiveOrdersAsync(cancellationToken).ConfigureAwait(false);

        string[] ids;
        lock (_protectiveOrderIds)
            ids = [.. _protectiveOrderIds];
        if (ids.Length == 0)
            return 0;

        var now = time.GetUtcNow();
        var open = await execution.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        var openIds = open.Where(o => o.IsOpen).Select(o => o.ClientOrderId).ToHashSet(StringComparer.Ordinal);
        var cancelled = 0;
        foreach (var order in open.Where(o => o.IsOpen && ids.Contains(o.ClientOrderId) && now - o.CreatedAt >= ProtectiveRepriceAfter))
        {
            var (outcome, _) = await execution.CancelOwnOrderAsync(order.VenueOrderId, cancellationToken).ConfigureAwait(false);
            if (outcome == CancelOutcome.Cancelled)
                cancelled++;
        }

        lock (_protectiveOrderIds)
            _protectiveOrderIds.RemoveWhere(id => !openIds.Contains(id)); // filled or gone: stop tracking
        return cancelled;
    }

    /// <summary>After a restart, picks up protective sells placed in the last day from the journal, so they are re-priced too.</summary>
    private async Task RestoreProtectiveOrdersAsync(CancellationToken cancellationToken)
    {
        var since = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1);
        var orders = await journal.QueryAsync(new JournalQuery(From: since, Kind: "order", Limit: FileJournal.MaxQueryLimit), cancellationToken)
            .ConfigureAwait(false);
        lock (_protectiveOrderIds)
        {
            foreach (var entry in orders.Where(e => e.Data?.GetValueOrDefault("tool") is "protective-exit" or TrimSource && e.Data.GetValueOrDefault("status") != "rejected"))
            {
                if (entry.Data!.GetValueOrDefault("clientOrderId") is { Length: > 0 } id)
                    _protectiveOrderIds.Add(id);
            }
        }

        _protectiveRestored = true;
    }

    private async Task AppendSafelyAsync(JournalEntry entry)
    {
        try
        {
            await journal.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOrderJournalFailed(logger, entry.Kind, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed for an engine {Kind} entry: {Detail}")]
    private static partial void LogOrderJournalFailed(ILogger logger, string kind, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Protective {Reason} exit: sell {Size} {Symbol} at {Price}: {Status} ({Rule})")]
    private static partial void LogProtectiveExit(ILogger logger, string reason, string symbol, decimal size, decimal price, string status, string rule);

    /// <summary>One journal entry per signal when it first appears, so reports can compare signals taken and not taken.</summary>
    private async Task RecordNewSignalsAsync(ScanResult result, StrategySpec? active)
    {
        var current = new Dictionary<string, (string Summary, Dictionary<string, string> Data)>(StringComparer.Ordinal);
        foreach (var e in result.Entries)
        {
            current[$"buy:{e.Symbol}"] = (
                Inv($"buy {e.Symbol} #{e.Rank}: {e.BaseSize} at {e.LimitPrice}, stop {e.StopLoss}, target {e.TakeProfit} ({e.Reason})"),
                Data(e.Symbol, "buy", "entry", e.LimitPrice, e.BaseSize, ("stopLoss", e.StopLoss), ("takeProfit", e.TakeProfit), ("riskQuote", e.RiskQuote), ("rank", e.Rank)));
        }

        foreach (var x in result.Exits)
        {
            current[$"sell:{x.Symbol}:{x.Reason}"] = (
                Inv($"sell {x.Symbol} {x.BaseSize} at {x.LimitPrice}: {x.Reason} ({x.Detail})"),
                Data(x.Symbol, "sell", x.Reason, x.LimitPrice, x.BaseSize));
        }

        var spec = active?.Label;
        foreach (var (key, (summary, data)) in current.Where(c => !_lastSignals.Contains(c.Key)).ToList())
        {
            try
            {
                await journal.AppendAsync(
                    new JournalEntry(JournalIds.New(result.AsOf, SignalKind), result.AsOf, SignalKind, Actor, null, spec, summary, data),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogJournalFailed(logger, key, ex.Message);
                current.Remove(key); // try again at the next scan
            }
        }

        _lastSignals = current.Keys.ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> Data(
        string symbol, string side, string reason, decimal price, decimal size, params (string Key, decimal Value)[] extra)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["symbol"] = symbol,
            ["side"] = side,
            ["reason"] = reason,
            ["limitPrice"] = price.ToString(CultureInfo.InvariantCulture),
            ["baseSize"] = size.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (key, value) in extra)
            data[key] = value.ToString(CultureInfo.InvariantCulture);
        return data;
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed for scan signal {Signal}: {Detail}")]
    private static partial void LogJournalFailed(ILogger logger, string signal, string detail);
}

/// <summary>
/// Scans every <see cref="ScannerOptions.Interval"/>, and at once when the strategy switches, so sizes, signals and the coin
/// filters follow the new strategy without waiting for the next tick. A failed scan is logged and retried at the next tick.
/// </summary>
public sealed partial class MarketScanBackgroundService(
    MarketScanService scanner,
    ScannerOptions options,
    TimeProvider time,
    ILogger<MarketScanBackgroundService> logger,
    RuntimeStrategySpecs? specs = null,
    TradableSymbols? tradable = null) : BackgroundService
{
    /// <summary>What starts the next scan.</summary>
    internal enum ScanTrigger { Tick, Switch, Pairs, Stopped }

    /// <summary>
    /// After the owner adds or removes a pair, the scan waits this long: time for the market data poller to load a new pair's
    /// quote and hourly candles, so the scan judges it on data rather than reporting it stale.
    /// </summary>
    public static readonly TimeSpan PairsSettle = TimeSpan.FromSeconds(20);

    /// <summary>The spec version the last scan started after; a newer one means a switch to scan for.</summary>
    private long _seen = specs?.Version ?? 0;

    /// <summary>The wait for a switch after <see cref="_seen"/>, kept across ticks so waits don't pile up.</summary>
    private Task? _changed;

    /// <summary>The tradable list's version the last scan started after; a newer one means pairs to scan.</summary>
    private long _pairsSeen = tradable?.Version ?? 0;

    /// <summary>The wait for a pair change after <see cref="_pairsSeen"/>.</summary>
    private Task? _pairsChanged;

    /// <summary>A switch whose trim hasn't been judged yet (the scan failed, or trading was halted): the next scans try it.</summary>
    private bool _trimPending;

    /// <summary>
    /// Waits for <paramref name="tick"/> (false once the timer stops), a strategy switch or a change to the tradable pairs,
    /// whichever comes first. A change is known by its version, not by which wait ended first, so one made while the tick was
    /// also due isn't missed. A switch comes before a pair change: its scan covers the new pairs too.
    /// </summary>
    internal async Task<ScanTrigger> NextAsync(Task<bool> tick, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tick);
        if (specs is null && tradable is null)
            return await tick.ConfigureAwait(false) ? ScanTrigger.Tick : ScanTrigger.Stopped;
        while (true)
        {
            if (specs is not null)
                _changed ??= specs.WaitForChangeAsync(_seen, cancellationToken);
            if (tradable is not null)
                _pairsChanged ??= tradable.WaitForChangeAsync(_pairsSeen, cancellationToken);
            await Task.WhenAny(new[] { tick, _changed, _pairsChanged }.OfType<Task>()).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (specs is not null && specs.Version != _seen)
            {
                _seen = specs.Version;
                _changed = null;
                _trimPending = true;
                if (tradable is not null)
                    (_pairsSeen, _pairsChanged) = (tradable.Version, null); // this scan covers them
                LogSwitchScan(logger, specs.Active?.Label ?? "none");
                return ScanTrigger.Switch;
            }

            if (tradable is not null && tradable.Version != _pairsSeen)
            {
                _pairsSeen = tradable.Version;
                _pairsChanged = null;
                LogPairsScan(logger, PairsSettle.TotalSeconds);
                return ScanTrigger.Pairs;
            }

            if (tick.IsCompleted)
                return await tick.ConfigureAwait(false) ? ScanTrigger.Tick : ScanTrigger.Stopped;
            // Ended without a newer version: wait again.
            if (_changed?.IsCompleted == true)
                _changed = null;
            if (_pairsChanged?.IsCompleted == true)
                _pairsChanged = null;
        }
    }

    /// <summary>
    /// One scan; returns false if it failed. Tests call this instead of running the loop. After a strategy switch, holdings
    /// above the new strategy's caps are trimmed after the protective exits, once the trim can be judged.
    /// </summary>
    public async Task<bool> ScanOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await scanner.CancelStaleProtectiveExitsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogScanFailed(logger, ex.GetType().Name, "re-pricing protective sells: " + ex.Message); // the scan still runs
            }

            var result = await scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            LogScanned(logger, result.Ranked.Count, result.Excluded.Count, result.Entries.Count, result.Exits.Count, Describe(result));
            await scanner.ApplyProtectiveExitsAsync(result, cancellationToken).ConfigureAwait(false);
            if (_trimPending && await scanner.TrimToStrategyAsync(result, cancellationToken).ConfigureAwait(false) is not null)
                _trimPending = false;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogScanFailed(logger, ex.GetType().Name, ex.Message);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First scan after a minute (time for quotes and candles to load), then every interval.
        await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken).ConfigureAwait(false);
        await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(options.Interval, time);
        Task<bool>? tick = null;
        while (true)
        {
            tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
            var trigger = await NextAsync(tick, stoppingToken).ConfigureAwait(false);
            if (trigger == ScanTrigger.Stopped)
                return;
            if (trigger == ScanTrigger.Tick)
                tick = null; // a switch or a pair change leaves the pending tick for the next round
            if (trigger == ScanTrigger.Pairs)
                await Task.Delay(PairsSettle, time, stoppingToken).ConfigureAwait(false);
            await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One line an owner can read in the logs: why each symbol did or didn't produce a signal.</summary>
    public static string Describe(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var ranked = result.Ranked.Select(r => string.Create(CultureInfo.InvariantCulture,
            $"#{r.Rank} {r.Symbol} {r.Score:0.##} ({r.NoEntryReason ?? "signal"})"));
        var excluded = result.Excluded.Select(e => $"{e.Symbol} {e.Reason}");
        var exits = result.Exits.Select(x => $"{x.Symbol} {x.Reason}");
        return $"ranked [{string.Join("; ", ranked)}] excluded [{string.Join("; ", excluded)}] exits [{string.Join("; ", exits)}]";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan: {Ranked} ranked, {Excluded} excluded, {Entries} entry and {Exits} exit signals: {Detail}")]
    private static partial void LogScanned(ILogger logger, int ranked, int excluded, int entries, int exits, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scan failed ({Error}): {Detail}")]
    private static partial void LogScanFailed(ILogger logger, string error, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Strategy switched to {Strategy}: scanning now")]
    private static partial void LogSwitchScan(ILogger logger, string strategy);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tradable pairs changed: scanning in {Seconds} seconds")]
    private static partial void LogPairsScan(ILogger logger, double seconds);
}

public static class ScannerRegistration
{
    /// <summary>Registers the universe (the watched pairs), the scanner and its background loop.</summary>
    public static IServiceCollection AddCixScanner(this IServiceCollection services, ScannerOptions options, MarketUniverse universe)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(universe);
        services.AddSingleton<IScannerFilterStore, InMemoryScannerFilterStore>();
        services.AddSingleton<ScannerFilterRecorder>();
        services.AddSingleton<MarketScanService>();
        services.AddHostedService<MarketScanBackgroundService>();
        return services;
    }
}
