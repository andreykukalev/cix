using Cix.Domain.Risk;
using Cix.Engine.MarketData;

namespace Cix.Engine.Execution;

/// <summary>What one round of checks did.</summary>
/// <param name="KillSwitchReason">Why the kill switch was engaged this round, or null.</param>
/// <param name="BuysCancelled">Open buys cancelled because drawdown reached the configured limit.</param>
public sealed record SafetyCheckResult(string? KillSwitchReason, int BuysCancelled);

/// <summary>
/// Every minute: values the portfolio (so the peak and 00:00 UTC baseline follow the market), acts on drawdown and on
/// market data stale for too long, and reconciles with the exchange every <see cref="SafetyOptions.ReconcileInterval"/>.
/// At the configured drawdown limit it cancels open buys (the gate already refuses new ones) but leaves sells working, so
/// positions can still be reduced; at the hard ceiling (<see cref="HardCeilings.MaxDrawdownFraction"/>) it engages the
/// kill switch, the critical state: nothing new until the owner resumes, while <see cref="ProtectionService"/>'s stop-loss sells
/// go on (no halt blocks them, and nothing is sold off automatically). The risk-gate reject streak trigger lives in
/// <see cref="ExecutionService"/>.
/// </summary>
public sealed partial class SafetyMonitor : BackgroundService
{
    public const string By = "safety-monitor";

    /// <summary>Who engages the kill switch at the hard drawdown ceiling: the critical state.</summary>
    public const string CriticalBy = "risk-critical";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ExecutionService _execution;
    private readonly KillSwitch _killSwitch;
    private readonly MarketState _market;
    private readonly RiskLimits _limits;
    private readonly SafetyOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SafetyMonitor> _logger;
    private readonly TradableSymbols? _tradable;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset _lastReconcile = DateTimeOffset.MinValue;

    public SafetyMonitor(
        ExecutionService execution,
        KillSwitch killSwitch,
        MarketState market,
        RiskLimits limits,
        SafetyOptions options,
        TimeProvider time,
        ILogger<SafetyMonitor> logger,
        TradableSymbols? tradable = null)
    {
        options.Validate(limits);
        _execution = execution;
        _killSwitch = killSwitch;
        _market = market;
        _limits = limits;
        _options = options;
        _time = time;
        _logger = logger;
        _tradable = tradable;
        _startedAt = time.GetUtcNow();
    }

    /// <summary>One round of checks.</summary>
    public async Task<SafetyCheckResult> CheckOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        string? reason = null;
        var by = By;
        var buysCancelled = 0;

        var snapshot = await _execution.SampleAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is { PeakEquity: > 0 })
        {
            var drawdown = (snapshot.PeakEquity - snapshot.Equity) / snapshot.PeakEquity;
            if (drawdown >= HardCeilings.MaxDrawdownFraction)
            {
                by = CriticalBy;
                // The same text each minute, so a halt already in place records it beside once, not every minute.
                reason = $"Critical: drawdown reached the {HardCeilings.MaxDrawdownFraction:P0} hard ceiling. Buys, the strategy and the agents stop until the owner resumes; stop-loss sells go on.";
                LogCritical(_logger, drawdown);
            }
            else if (drawdown >= _limits.MaxDrawdownFraction && !_killSwitch.IsEngaged)
            {
                buysCancelled = await _execution.CancelOpenBuysAsync(cancellationToken).ConfigureAwait(false);
                if (buysCancelled > 0)
                    LogDrawdownBuysCancelled(_logger, buysCancelled, drawdown);
            }
        }

        // Give the poller time to deliver first quotes after a start. Then stale data halts everything only where it protects
        // something (ADR-0021): a coin the account holds, whose stop can't be watched, or every watched coin, when the feed is
        // down. A quiet coin nobody holds is refused by the gate's own stale-data rule and filtered out by the scan by itself.
        if (reason is null && now - _startedAt >= _options.StaleDataKillAfter)
        {
            var held = (snapshot ?? _execution.LatestSample?.Snapshot)?.PositionQuoteBySymbol
                .Where(p => p.Value > 0m).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var checkedCount = 0;
            var stale = new List<string>();
            foreach (var symbol in (_tradable?.Current ?? TradableSymbols.None).Order(StringComparer.Ordinal))
            {
                // A pair the owner just added gets the same time as the engine's start to deliver its first quotes.
                if (_tradable?.AddedAt(symbol) is { } added && now - added < _options.StaleDataKillAfter)
                    continue;

                checkedCount++;
                var quote = _market.Quote(symbol);
                if (quote is null || now - quote.AsOf > _options.StaleDataKillAfter)
                    stale.Add(symbol);
            }

            // Without a sample yet the engine can't tell what is held: every watched coin counts as held.
            var heldStale = stale.FirstOrDefault(s => held is null || held.Contains(s));
            if (heldStale is not null)
                reason = $"No fresh market data for {heldStale} for over {_options.StaleDataKillAfter.TotalMinutes:0} minutes.";
            else if (checkedCount > 0 && stale.Count == checkedCount)
                reason = $"No fresh market data for any watched coin for over {_options.StaleDataKillAfter.TotalMinutes:0} minutes.";
            else if (stale.Count > 0)
                LogStaleNotHeld(_logger, string.Join(", ", stale), stale.Count);
        }

        // The critical state is recorded even over another halt (beside it), so Resume has to name it too; once recorded, it
        // isn't re-engaged every minute (each engagement cancels the open orders again).
        var halt = _killSwitch.State;
        if (reason is not null && (!halt.Engaged || (by == CriticalBy && halt.By != CriticalBy && halt.AlsoBy != CriticalBy)))
            await _execution.EngageKillSwitchAsync(reason, by, cancellationToken).ConfigureAwait(false);

        if (now - _lastReconcile >= _options.ReconcileInterval)
        {
            _lastReconcile = now;
            var report = await _execution.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            reason ??= report.KillSwitchReason;
        }

        return new SafetyCheckResult(reason, buysCancelled);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCheckFailed(_logger, ex.GetType().Name, ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No fresh market data for {Symbols} ({Count}); not held, so no halt: the gate and the scan leave them out")]
    private static partial void LogStaleNotHeld(ILogger logger, string symbols, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drawdown {Drawdown} reached the limit: cancelled {Count} open buys; sells stay allowed")]
    private static partial void LogDrawdownBuysCancelled(ILogger logger, int count, decimal drawdown);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Drawdown {Drawdown} is at or above the hard ceiling: critical state, stop-loss sells go on")]
    private static partial void LogCritical(ILogger logger, decimal drawdown);

    [LoggerMessage(Level = LogLevel.Error, Message = "Safety check failed ({Error}): {Detail}")]
    private static partial void LogCheckFailed(ILogger logger, string error, string detail);
}
