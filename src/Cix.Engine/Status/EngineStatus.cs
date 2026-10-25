using Cix.Domain.Risk;
using Cix.Engine.Analysis;
using Cix.Engine.Execution;
using Cix.Engine.MarketData;
using Cix.Engine.Scanning;
using Cix.Engine.Strategy;

namespace Cix.Engine.Status;

/// <summary>
/// The engine's status for people and the dashboard, rewritten every minute as the <c>engine-status-&lt;mode&gt;</c> document
/// in the state container (docs/persistence.md). Read-only: nothing in the engine reads it back, so it can't affect trading.
/// </summary>
/// <param name="UpdatedAt">When this was written: the engine's heartbeat, and its last successful database write.</param>
/// <param name="LastReconciledAt">The last successful comparison with the exchange: when the portfolio was last in sync.</param>
public sealed record EngineStatus(
    string Mode,
    DateTimeOffset UpdatedAt,
    DateTimeOffset StartedAt,
    string? Strategy,
    bool Halted,
    string? HaltReason,
    DateTimeOffset? HaltedSince,
    DateTimeOffset? LastReconciledAt,
    string? LastReconcileProblem,
    ScanStatus? LastScan,
    MarketViewStatus? MarketView,
    DateTimeOffset? OldestQuoteAt,
    PortfolioStatus? Portfolio);

public sealed record ScanStatus(DateTimeOffset At, int Ranked, int Excluded, int EntrySignals, int ExitSignals, string SignalKey);

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

/// <summary>
/// Whether the engine's dependencies answer, for <c>/healthz</c> and the status record. The database is judged by the
/// status write each minute; anything else that fails to reach it (journal, state) fails on its own terms.
/// </summary>
public sealed class DependencyHealth
{
    private readonly Lock _gate = new();

    public DateTimeOffset? DatabaseOkAt { get; private set; }

    public DateTimeOffset? DatabaseFailedAt { get; private set; }

    public string? DatabaseProblem { get; private set; }

    /// <summary><c>ok</c>, <c>unreachable</c>, <c>unknown</c> (not checked yet) or <c>not-used</c> (no database configured).</summary>
    public string Database { get; private set; } = "unknown";

    public void DatabaseNotUsed()
    {
        lock (_gate)
            Database = "not-used";
    }

    public void DatabaseOk(DateTimeOffset at)
    {
        lock (_gate)
        {
            DatabaseOkAt = at;
            DatabaseProblem = null;
            Database = "ok";
        }
    }

    public void DatabaseFailed(DateTimeOffset at, string problem)
    {
        lock (_gate)
        {
            DatabaseFailedAt = at;
            DatabaseProblem = problem;
            Database = "unreachable";
        }
    }
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
    IStrategySpecStore? specs = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly DateTimeOffset _startedAt = time.GetUtcNow();

    public async Task<EngineStatus> BuildAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var kill = killSwitch.State;
        var scan = scanner?.Latest;
        var view = views?.Current;
        var quoteTimes = market.QuoteTimes();
        var tradable = limits.AllowedSymbols.Select(MarketDataPoller.NormaliseSymbol).ToList();
        DateTimeOffset? oldestQuote = tradable.Count == 0 || tradable.Any(s => !quoteTimes.ContainsKey(s))
            ? null
            : tradable.Min(s => quoteTimes[s]);

        return new EngineStatus(
            trading.Mode.ToString().ToLowerInvariant(),
            now,
            _startedAt,
            specs?.Active?.Label,
            kill.Engaged,
            kill.Engaged ? kill.Reason : null,
            kill.Engaged ? kill.Since : null,
            execution.LastReconciledAt,
            execution.LastReconcileProblem,
            scan is null ? null : new ScanStatus(scan.AsOf, scan.Ranked.Count, scan.Excluded.Count, scan.Entries.Count, scan.Exits.Count, scanner!.SignalKey),
            view is null
                ? null
                : new MarketViewStatus(view.Regime.ToString(), view.RecordedAt, view.ValidUntil, view.IsActive(now), view.Flags.Select(f => f.Symbol).ToList()),
            oldestQuote,
            await PortfolioAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<PortfolioStatus?> PortfolioAsync(CancellationToken cancellationToken)
    {
        if (await execution.GetPortfolioAsync(cancellationToken).ConfigureAwait(false) is not { } valued)
            return null; // can't be valued now (stale prices): the status says so by leaving it out

        var (snapshot, at) = valued;
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

    /// <summary>Builds and saves the status once. Returns whether it was saved.</summary>
    public async Task<bool> WriteOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await BuildAsync(cancellationToken).ConfigureAwait(false);
            await store.SaveAsync(status, cancellationToken).ConfigureAwait(false);
            if (store.UsesDatabase)
                health.DatabaseOk(status.UpdatedAt);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (store.UsesDatabase)
                health.DatabaseFailed(time.GetUtcNow(), ex.GetType().Name);
            LogWriteFailed(logger, ex.GetType().Name);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.UsesDatabase)
            health.DatabaseNotUsed();
        await WriteOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await WriteOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

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
