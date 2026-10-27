using Cix.Domain.Risk;
using Cix.Engine.Analysis;
using Cix.Engine.Execution;
using Cix.Engine.MarketData;
using Cix.Engine.Scanning;
using Cix.Engine.Strategy;

namespace Cix.Engine.Status;

/// <summary>
/// The engine's status for people and the dashboard, rewritten every minute as the <c>engine-status-&lt;mode&gt;</c> document
/// in the state container (docs/persistence.md). Built only from what the engine already holds in memory: building it makes
/// no exchange call and changes no state, and nothing reads it back, so it can't affect trading. It holds fixed codes and
/// categories, never free text that could carry order IDs or account amounts (those stay in the journal); the scan rows' details
/// are the scanner's own sentences, from market data and the strategy's settings.
/// </summary>
/// <param name="UpdatedAt">When this was written: the engine's heartbeat, and its last successful database write.</param>
/// <param name="HaltedBy">What engaged the kill switch: <c>owner</c>, <c>reconciler</c>, <c>safety-monitor</c>, <c>execution</c> or <c>engine</c>.</param>
/// <param name="HaltCategory">A fixed description of <paramref name="HaltedBy"/>; the exact reason is in the journal.</param>
/// <param name="LastReconciledAt">The last full comparison with the exchange: when the portfolio was last in sync.</param>
/// <param name="LastReconcileProblem">A code from <see cref="ReconcileStatus"/>, or null.</param>
/// <param name="OldestQuoteAt">The oldest latest quote among the tradable coins that have one.</param>
/// <param name="UnquotedSymbols">Tradable coins with no quote yet.</param>
/// <param name="StrategySelection">What the engine made of the strategy selected in the database (#105); null when switching is off.</param>
public sealed record EngineStatus(
    string Mode,
    DateTimeOffset UpdatedAt,
    DateTimeOffset StartedAt,
    string? Strategy,
    bool Halted,
    string? HaltedBy,
    string? HaltCategory,
    DateTimeOffset? HaltedSince,
    DateTimeOffset? LastReconciledAt,
    DateTimeOffset? LastReconcileAttemptAt,
    string? LastReconcileProblem,
    ScanStatus? LastScan,
    MarketViewStatus? MarketView,
    DateTimeOffset? OldestQuoteAt,
    IReadOnlyList<string> UnquotedSymbols,
    PortfolioStatus? Portfolio,
    FiltersStatus? Filters = null,
    ManualRequestsStatus? ManualRequests = null,
    StrategySelectionStatus? StrategySelection = null);

/// <summary>
/// The coin filters the scanner applies, from the active strategy spec (a change is a new spec version). Fractions as
/// fractions (0.004 = 0.4%), volume in the quote currency.
/// </summary>
/// <param name="Since">When these filters were first applied, from the database record; null until the first scan records them.</param>
public sealed record FiltersStatus(
    string? Spec,
    DateTimeOffset? Since,
    decimal MinQuoteVolume24h,
    decimal MaxSpreadFraction,
    decimal MaxAtrFraction,
    decimal MinAtrToCostRatio,
    int MinHourlyCandles24h,
    bool RequireUptrendPullback,
    bool TrendExits,
    int MaxEntrySignals,
    decimal OrderQuote);

/// <param name="State"><c>ok</c>, <c>off</c>, <c>unavailable</c> (no requests container yet) or <c>not-used</c> (no database).</param>
/// <param name="AwaitingAgent">Agent-assisted requests waiting for the trader.</param>
public sealed record ManualRequestsStatus(string State, int AwaitingAgent, DateTimeOffset? LastPollAt);

/// <param name="Market">Every scanned symbol from the latest scan, for the owner's market overview.</param>
public sealed record ScanStatus(
    DateTimeOffset At, int Ranked, int Excluded, int EntrySignals, int ExitSignals, string SignalKey, IReadOnlyList<MarketRow> Market);

/// <summary>
/// One symbol in the latest scan: how it moved and what the scanner made of it. Moves and volatility are fractions
/// (0.05 = 5%); no prices. Excluded symbols carry the filter's code and its sentence.
/// </summary>
/// <param name="Volatility">Hourly ATR as a fraction of the price.</param>
/// <param name="VolumeRatio">The last hour's volume against the 24h hourly average.</param>
/// <param name="Signal"><c>buy</c>, <c>sell (stop|target|trend-down)</c>, or null.</param>
/// <param name="Note">Why there is no entry (for example <c>trend down</c>), or the code of the filter that excluded the symbol.</param>
/// <param name="Detail">
/// The scanner's sentence behind <paramref name="Note"/>: the figure and the threshold it was held to (for example
/// <c>Spread 0.31% is above the maximum 0.2%.</c>). Written by the scanner from market data and the strategy's settings only.
/// </param>
public sealed record MarketRow(
    string Symbol,
    int? Rank,
    string? Trend,
    decimal? Return1h,
    decimal? Return24h,
    decimal? Volatility,
    decimal? VolumeRatio,
    string? Signal,
    string? Note,
    string? Detail = null);

public sealed record MarketViewStatus(string Regime, DateTimeOffset RecordedAt, DateTimeOffset ValidUntil, bool Active, IReadOnlyList<string> Avoid);

/// <param name="Cash">Equity minus the value of holdings: free quote currency plus what open buys have reserved.</param>
public sealed record PortfolioStatus(
    DateTimeOffset At,
    string Currency,
    decimal Equity,
    decimal Cash,
    decimal PeakEquity,
    decimal PnlToday,
    decimal ExposureQuote,
    IReadOnlyList<HoldingStatus> Holdings);

public sealed record HoldingStatus(
    string Symbol,
    decimal ValueQuote,
    decimal? SellableBase,
    string? PlanStatus,
    decimal? EntryPrice,
    decimal? StopLoss,
    decimal? TakeProfit);

/// <summary>Where the status is written. Only the latest matters, so a write replaces the previous one.</summary>
public interface IEngineStatusStore
{
    /// <summary>True when the status goes to the database, so a successful write also proves the database is reachable.</summary>
    bool UsesDatabase { get; }

    Task SaveAsync(EngineStatus status, CancellationToken cancellationToken);
}

/// <summary>Without Cosmos DB: kept in memory only (and for tests).</summary>
public sealed class InMemoryEngineStatusStore : IEngineStatusStore
{
    public EngineStatus? Last { get; private set; }

    public bool UsesDatabase => false;

    public Task SaveAsync(EngineStatus status, CancellationToken cancellationToken)
    {
        Last = status;
        return Task.CompletedTask;
    }
}

/// <summary>The database's health at one moment: <c>ok</c>, <c>unreachable</c>, <c>unknown</c> (not checked yet) or <c>not-used</c>.</summary>
public sealed record DatabaseHealth(string State, DateTimeOffset? OkAt, DateTimeOffset? FailedAt, string? Problem)
{
    public static readonly DatabaseHealth Unknown = new("unknown", null, null, null);
}

/// <summary>
/// Whether the engine's dependencies answer, for <c>/healthz</c> and the status record. The database is judged by the
/// status write each minute; anything else that fails to reach it (journal, state) fails on its own terms. Published as one
/// immutable record, so a reader never sees half an update.
/// </summary>
public sealed class DependencyHealth
{
    private DatabaseHealth _database = DatabaseHealth.Unknown;

    public DatabaseHealth Database => Volatile.Read(ref _database);

    public void DatabaseNotUsed() => Volatile.Write(ref _database, new DatabaseHealth("not-used", null, null, null));

    public void DatabaseOk(DateTimeOffset at) => Volatile.Write(ref _database, Database with { State = "ok", OkAt = at, Problem = null });

    public void DatabaseFailed(DateTimeOffset at, string problem) =>
        Volatile.Write(ref _database, Database with { State = "unreachable", FailedAt = at, Problem = problem });
}

/// <summary>Writes <see cref="EngineStatus"/> every minute. A failed write is logged and marks the database unreachable; it never stops trading.</summary>
public sealed partial class EngineStatusService(
    ExecutionService execution,
    KillSwitch killSwitch,
    MarketState market,
    RiskLimits limits,
    TradingContext trading,
    IEngineStatusStore store,
    DependencyHealth health,
    TimeProvider time,
    ILogger<EngineStatusService> logger,
    MarketScanService? scanner = null,
    MarketViewStore? views = null,
    IStrategySpecStore? specs = null,
    Manual.ManualRequestService? manual = null,
    StrategySelectionService? selection = null,
    TradableSymbols? tradable = null,
    Journal.IJournal? journal = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly DateTimeOffset _startedAt = time.GetUtcNow();

    // The hourly history of the portfolio this status shows (#70), when there is a journal to keep it in.
    private BalanceSnapshots? _snapshots;

    /// <summary>Fixed text for each component that can engage the kill switch; the exact reason stays in the journal.</summary>
    public static string HaltCategoryFor(string? by) => by switch
    {
        "owner" => "Paused by the owner.",
        ExecutionService.ReconcilerBy => "Reconciliation found the account changed outside the engine.",
        SafetyMonitor.By => "A safety limit was reached (drawdown, daily loss or stale market data).",
        ExecutionService.RejectStreakBy => "Too many orders in a row were rejected by the risk gate.",
        "engine" => "Waiting for the owner to confirm before trading starts.",
        Pairs.TradablePairsService.By => "The saved trading pairs couldn't all be restored; check holdings before resuming.",
        _ => "Halted.",
    };

    /// <summary>The latest scan, one row per symbol: ranked ones by rank, then the excluded ones.</summary>
    public static IReadOnlyList<MarketRow> MarketRows(Cix.Domain.Scanning.ScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var buys = scan.Entries.Select(e => e.Symbol).ToHashSet(StringComparer.Ordinal);
        var sells = scan.Exits.GroupBy(x => x.Symbol, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.Ordinal);
        string? Signal(string symbol) => buys.Contains(symbol) ? "buy" : sells.TryGetValue(symbol, out var reason) ? $"sell ({reason})" : null;
        static decimal? Round(decimal? value, int digits = 4) => value is { } v ? Math.Round(v, digits, MidpointRounding.AwayFromZero) : null;

        return scan.Ranked.OrderBy(r => r.Rank)
            .Select(r => new MarketRow(
                r.Symbol, r.Rank, r.Trend.ToString().ToLowerInvariant(), Round(r.Return1h), Round(r.Return24h), Round(r.AtrFraction),
                Round(r.VolumeRatio, 2), Signal(r.Symbol), r.NoEntryReason, r.NoEntryDetail))
            .Concat(scan.Excluded.OrderBy(x => x.Symbol, StringComparer.Ordinal)
                .Select(x => new MarketRow(x.Symbol, null, null, Round(x.Return1h), Round(x.Return24h), null, null, Signal(x.Symbol), x.Reason, x.Detail)))
            .ToList();
    }

    /// <summary>The status from what the engine holds in memory: no exchange call, no state change.</summary>
    public EngineStatus Build()
    {
        var now = time.GetUtcNow();
        var kill = killSwitch.State;
        var scan = scanner?.Latest;
        var view = views?.Current;
        var reconcile = execution.LastReconcile;
        var quoteTimes = market.QuoteTimes();
        var symbols = (tradable?.Current ?? limits.AllowedSymbols).Select(MarketDataPoller.NormaliseSymbol).Order(StringComparer.Ordinal).ToList();
        var quoted = symbols.Where(quoteTimes.ContainsKey).ToList();

        return new EngineStatus(
            trading.Mode.ToString().ToLowerInvariant(),
            now,
            _startedAt,
            specs?.Active?.Label,
            kill.Engaged,
            kill.Engaged ? kill.By : null,
            kill.Engaged ? HaltCategoryFor(kill.By) : null,
            kill.Engaged ? kill.Since : null,
            reconcile?.ReconciledAt,
            reconcile?.AttemptedAt,
            reconcile?.Problem,
            scan is null ? null : new ScanStatus(scan.AsOf, scan.Ranked.Count, scan.Excluded.Count, scan.Entries.Count, scan.Exits.Count, scanner!.SignalKey, MarketRows(scan)),
            view is null
                ? null
                : new MarketViewStatus(view.Regime.ToString(), view.RecordedAt, view.ValidUntil, view.IsActive(now), view.Flags.Select(f => f.Symbol).ToList()),
            quoted.Count == 0 ? null : quoted.Min(s => quoteTimes[s]),
            symbols.Where(s => !quoteTimes.ContainsKey(s)).ToList(),
            Portfolio(),
            Filters(),
            manual is null ? null : new ManualRequestsStatus(manual.State, manual.Pending.Count, manual.LastPollAt),
            selection?.Status);
    }

    private FiltersStatus? Filters()
    {
        if (scanner is null)
            return null;
        var p = scanner.Parameters;
        var recorded = scanner.RecordedFilters;
        return new FiltersStatus(
            specs?.Active?.Label, recorded is not null && recorded.Filters == p ? recorded.Since : null,
            p.MinQuoteVolume24h, p.MaxSpreadFraction, p.MaxAtrFraction, p.MinAtrToCostRatio, p.MinHourlyCandles24h,
            p.RequireUptrendPullback, p.TrendExits, p.MaxEntrySignals, p.OrderQuote);
    }

    private PortfolioStatus? Portfolio()
    {
        if (execution.LatestSample is not { } sample)
            return null; // not valued yet (or never could be: stale prices): the status says so by leaving it out

        var (snapshot, at) = (sample.Snapshot, sample.At);
        var plans = execution.PositionPlanStates;
        var holdings = snapshot.PositionQuoteBySymbol
            .Where(p => p.Value > 0m)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p =>
            {
                plans.TryGetValue(p.Key, out var plan);
                return new HoldingStatus(
                    p.Key, Math.Round(p.Value, 2), snapshot.SellableBaseBySymbol?.GetValueOrDefault(p.Key),
                    plan?.Status, plan?.EntryPrice, plan?.StopLoss, plan?.TakeProfit);
            })
            .ToList();
        var currency = limits.AllowedSymbols.Select(s => MarketDataPoller.NormaliseSymbol(s).Split('-').Last()).FirstOrDefault() ?? "EUR";
        var positions = snapshot.PositionQuoteBySymbol.Values.Sum();
        return new PortfolioStatus(
            at, currency, Math.Round(snapshot.Equity, 2), Math.Round(snapshot.Equity - positions, 2), Math.Round(snapshot.PeakEquity, 2),
            Math.Round(snapshot.PnlToday, 2), Math.Round(snapshot.TotalExposureQuote, 2), holdings);
    }

    /// <summary>
    /// Builds and saves the status once, and the hour's balance snapshot when it is due. Returns whether the status was saved.
    /// Only a failed status save counts against the database.
    /// </summary>
    public async Task<bool> WriteOnceAsync(CancellationToken cancellationToken)
    {
        EngineStatus status;
        try
        {
            status = Build();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBuildFailed(logger, ex.GetType().Name);
            return false;
        }

        var saved = false;
        try
        {
            await store.SaveAsync(status, cancellationToken).ConfigureAwait(false);
            if (store.UsesDatabase)
                health.DatabaseOk(status.UpdatedAt);
            saved = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (store.UsesDatabase)
                health.DatabaseFailed(time.GetUtcNow(), ex.GetType().Name);
            LogWriteFailed(logger, ex.GetType().Name);
        }

        if (journal is not null)
        {
            _snapshots ??= new BalanceSnapshots(journal, trading, time, logger);
            await _snapshots.WriteIfDueAsync(status.Portfolio, status.Strategy, cancellationToken).ConfigureAwait(false);
        }
        return saved;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.UsesDatabase)
            health.DatabaseNotUsed();
        await WriteOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await NextAsync(timer, stoppingToken).ConfigureAwait(false))
                await WriteOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private Task<bool>? _tick;
    private Task? _scanned;
    private long _scansSeen = scanner?.Scans ?? 0;

    /// <summary>
    /// Waits for the next minute or the end of a scan, whichever comes first (false once the timer stops): a scan's result,
    /// a newly added pair's first one among them, reaches the dashboard at once rather than up to a minute later.
    /// </summary>
    internal async Task<bool> NextAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        _tick ??= timer.WaitForNextTickAsync(cancellationToken).AsTask();
        if (scanner is not null)
            _scanned ??= scanner.WaitForScanAsync(_scansSeen, cancellationToken);
        await Task.WhenAny(new[] { _tick, _scanned }.OfType<Task>()).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (scanner is not null && _scanned?.IsCompleted == true)
        {
            _scansSeen = scanner.Scans;
            _scanned = null;
        }

        if (!_tick.IsCompleted)
            return true;
        var ticked = await _tick.ConfigureAwait(false);
        _tick = null;
        return ticked;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Engine status could not be built ({Error}); trading is unaffected")]
    private static partial void LogBuildFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine status could not be written ({Error}); trading is unaffected")]
    private static partial void LogWriteFailed(ILogger logger, string error);
}

public static class StatusRegistration
{
    /// <summary>The status record and dependency health. Cosmos DB, when enabled, replaces the in-memory store (AddCixCosmos).</summary>
    public static IServiceCollection AddCixStatus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<DependencyHealth>();
        services.AddSingleton<IEngineStatusStore, InMemoryEngineStatusStore>();
        services.AddSingleton<EngineStatusService>();
        services.AddHostedService(sp => sp.GetRequiredService<EngineStatusService>());
        return services;
    }
}
