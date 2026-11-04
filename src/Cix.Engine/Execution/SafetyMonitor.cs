using Cix.Domain;
using Cix.Domain.Risk;
using Cix.Engine.MarketData;

namespace Cix.Engine.Execution;

/// <summary>What one round of checks did.</summary>
/// <param name="KillSwitchReason">Why the kill switch was engaged this round, or null.</param>
/// <param name="BuysCancelled">Open buys cancelled because drawdown was at the configured limit after the owner resumed.</param>
public sealed record SafetyCheckResult(string? KillSwitchReason, int BuysCancelled);

/// <summary>
/// Every minute: values the portfolio (so the peak and 00:00 UTC baseline follow the market), acts on drawdown and on
/// market data stale for too long, and reconciles with the exchange every <see cref="SafetyOptions.ReconcileInterval"/>.
/// At the configured drawdown limit it engages the kill switch once (pre-live assessment F-10), as the owner's Pause would:
/// nothing new until the owner resumes, while <see cref="ProtectionService"/>'s stop-loss sells and the owner's own sells go on.
/// After the owner resumes it doesn't pause again until the drawdown has been back under the limit (the gate still refuses
/// buys meanwhile, and open buys are cancelled). At the hard ceiling (<see cref="HardCeilings.MaxDrawdownFraction"/>) it
/// engages the kill switch as the critical state. Nothing is sold off automatically. The risk-gate reject streak trigger
/// lives in <see cref="ExecutionService"/>.
/// </summary>
public sealed partial class SafetyMonitor : BackgroundService
{
    public const string By = "safety-monitor";

    /// <summary>Who engages the kill switch at the hard drawdown ceiling: the critical state.</summary>
    public const string CriticalBy = "risk-critical";

    /// <summary>Who engages the kill switch at the configured drawdown limit.</summary>
    public const string DrawdownBy = "risk-drawdown";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long the running checks may go without progress (a round, or a step of one, starting or finishing) before the engine
    /// counts as stalled: then <c>/healthz</c> fails and the platform restarts it. Far above a slow step (each exchange call gives
    /// up within about a minute), so an exchange or database outage alone should not restart it.
    /// </summary>
    public static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(15);

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
    private bool _drawdownPaused; // the drawdown pause engaged (or a halt covered it) and the drawdown isn't back under the limit since
    private long _progressTicks; // when a round last started or finished, or the loop started; 0 until it starts

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
        _drawdownPaused = execution.DrawdownPaused; // saved, so a restart doesn't pause again for the same drawdown
    }

    private void SetDrawdownPaused(bool paused)
    {
        _drawdownPaused = paused;
        if (_execution.DrawdownPaused != paused)
            _execution.SaveDrawdownPaused(paused); // a write that fails is tried again next round
    }

    /// <summary>
    /// Whether the running checks have stopped making progress for <see cref="StalledAfter"/>. False until the loop starts, so
    /// a monitor that isn't hosted (tests, a standby engine) never counts as stalled.
    /// </summary>
    public bool IsStalled
    {
        get
        {
            var ticks = Volatile.Read(ref _progressTicks);
            return ticks != 0 && _time.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero) > StalledAfter;
        }
    }

    private void MarkProgress()
    {
        if (Volatile.Read(ref _progressTicks) != 0)
            Volatile.Write(ref _progressTicks, _time.GetUtcNow().UtcTicks);
    }

    /// <summary>
    /// One round of checks. Each step runs even when an earlier one fails (a valuation that can't save its baselines still
    /// leaves the stale-data check and the reconciliation to run); a failed step is logged.
    /// </summary>
    public async Task<SafetyCheckResult> CheckOnceAsync(CancellationToken cancellationToken)
    {
        MarkProgress();
        try
        {
            return await CheckStepsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MarkProgress();
        }
    }

    private async Task<SafetyCheckResult> CheckStepsAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        string? reason = null;
        var by = By;
        var buysCancelled = 0;

        PortfolioSnapshot? snapshot = null;
        try
        {
            snapshot = await _execution.SampleAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is { PeakEquity: > 0 })
            {
                var drawdown = (snapshot.PeakEquity - snapshot.Equity) / snapshot.PeakEquity;
                if (drawdown >= HardCeilings.MaxDrawdownFraction)
                {
                    by = CriticalBy;
                    // The same text each minute, so a halt already in place records it beside once, not every minute.
                    reason = $"Critical: drawdown reached the {HardCeilings.MaxDrawdownFraction:P0} hard ceiling. Buys, the strategy and the agents stop until the owner resumes; stop-loss sells go on.";
                    LogCritical(_logger, drawdown);
                    SetDrawdownPaused(true); // the owner's Resume of the critical halt covers the drawdown limit too
                }
                else if (drawdown >= _limits.MaxDrawdownFraction)
                {
                    if (!_drawdownPaused)
                    {
                        // Over another halt it is recorded beside it, so the owner sees it before resuming.
                        by = DrawdownBy;
                        reason = $"Drawdown reached the {_limits.MaxDrawdownFraction:P0} limit. Buys, the strategy and the agents stop until the owner resumes; stop-loss sells and the owner's own sells go on.";
                        LogDrawdownHalt(_logger, drawdown);
                    }
                    else if (!_killSwitch.IsEngaged)
                    {
                        // Resumed while still down: the gate refuses buys until the drawdown is back under the limit.
                        buysCancelled = await _execution.CancelOpenBuysAsync(cancellationToken).ConfigureAwait(false);
                        if (buysCancelled > 0)
                            LogDrawdownBuysCancelled(_logger, buysCancelled, drawdown);
                    }
                }
                else
                {
                    SetDrawdownPaused(false); // back under the limit: the next fall to it pauses again
                }
            }
        }
        catch (Exception ex) when (Failed(ex, cancellationToken))
        {
            LogCheckFailed(_logger, "valuation", ex.GetType().Name, ex.Message);
        }

        MarkProgress(); // each step counts: a slow exchange makes a round long, not stalled

        // Give the poller time to deliver first quotes after a start. Then stale data halts everything only where it protects
        // something (ADR-0021): a coin the account holds, whose stop can't be watched, or every watched coin, when the feed is
        // down. A quiet coin nobody holds is refused by the gate's own stale-data rule and filtered out by the scan by itself.
        if (reason is null && now - _startedAt >= _options.StaleDataKillAfter)
        {
            // Held: positions, plus coins with a working buy or an order sent in the last minutes, which may have filled
            // since the last sample that could be valued.
            var held = (snapshot ?? _execution.LatestSample?.Snapshot)?.PositionQuoteBySymbol
                .Where(p => p.Value > 0m).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            held?.UnionWith(_execution.ActiveSymbols);
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
        // isn't re-engaged every minute (each engagement cancels the open orders again). The drawdown pause is recorded beside
        // another halt the same way, unless the critical state already is (it says more).
        var halt = _killSwitch.State;
        var critical = halt.By == CriticalBy || halt.AlsoBy == CriticalBy;
        if (reason is not null && (!halt.Engaged || (by == CriticalBy && !critical)
            || (by == DrawdownBy && !critical && halt.By != DrawdownBy && halt.AlsoBy != DrawdownBy)))
        {
            try
            {
                await _execution.EngageKillSwitchAsync(reason, by, cancellationToken).ConfigureAwait(false);
                if (by == DrawdownBy)
                    SetDrawdownPaused(true);
            }
            catch (Exception ex) when (Failed(ex, cancellationToken))
            {
                LogCheckFailed(_logger, "kill switch", ex.GetType().Name, ex.Message);
            }

            MarkProgress();
        }
        else if (by == DrawdownBy)
        {
            SetDrawdownPaused(true); // the halt in place names it already, or the critical state does
        }

        if (now - _lastReconcile >= _options.ReconcileInterval)
        {
            _lastReconcile = now;
            try
            {
                var report = await _execution.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                reason ??= report.KillSwitchReason;
            }
            catch (Exception ex) when (Failed(ex, cancellationToken))
            {
                LogCheckFailed(_logger, "reconciliation", ex.GetType().Name, ex.Message);
            }
        }

        return new SafetyCheckResult(reason, buysCancelled);
    }

    /// <summary>
    /// Compares the account with the exchange as the engine starts, rather than a minute later, since orders wait for the
    /// first complete reconciliation (F-12). One that can't complete yet (no prices for the holdings check) is done again in
    /// the first round.
    /// </summary>
    public async Task ReconcileAtStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _execution.ReconcileAsync(cancellationToken).ConfigureAwait(false); // engages the kill switch on a finding
            if (_execution.LastReconciledAt is not null)
                _lastReconcile = _time.GetUtcNow();
        }
        catch (Exception ex) when (Failed(ex, cancellationToken))
        {
            LogCheckFailed(_logger, "reconciliation", ex.GetType().Name, ex.Message);
        }

        MarkProgress();
    }

    /// <summary>A step's failure, not the engine stopping (an SDK timeout is an OperationCanceledException too).</summary>
    private static bool Failed(Exception ex, CancellationToken cancellationToken) =>
        ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    /// <summary>Starts the loop, and from now on <see cref="IsStalled"/> watches it.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _progressTicks, _time.GetUtcNow().UtcTicks);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileAtStartAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCheckFailed(_logger, "round", ex.GetType().Name, ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No fresh market data for {Symbols} ({Count}); not held, so no halt: the gate and the scan leave them out")]
    private static partial void LogStaleNotHeld(ILogger logger, string symbols, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drawdown {Drawdown} reached the limit: trading paused until the owner resumes; stop-loss sells go on")]
    private static partial void LogDrawdownHalt(ILogger logger, decimal drawdown);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drawdown {Drawdown} still at the limit after the owner resumed: cancelled {Count} open buys; sells stay allowed")]
    private static partial void LogDrawdownBuysCancelled(ILogger logger, int count, decimal drawdown);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Drawdown {Drawdown} is at or above the hard ceiling: critical state, stop-loss sells go on")]
    private static partial void LogCritical(ILogger logger, decimal drawdown);

    [LoggerMessage(Level = LogLevel.Error, Message = "Safety check failed at {Step} ({Error}): {Detail}")]
    private static partial void LogCheckFailed(ILogger logger, string step, string error, string detail);
}
