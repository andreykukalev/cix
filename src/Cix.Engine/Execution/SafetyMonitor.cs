using Cix.Domain.Risk;
using Cix.Engine.MarketData;

namespace Cix.Engine.Execution;

/// <summary>
/// Every minute: values the portfolio (so the peak and 00:00 UTC baseline follow the market), engages the kill switch
/// on a drawdown at the limit or market data stale for too long, and reconciles with the exchange every
/// <see cref="SafetyOptions.ReconcileInterval"/>. The risk-gate reject streak trigger lives in <see cref="ExecutionService"/>.
/// </summary>
public sealed partial class SafetyMonitor : BackgroundService
{
    public const string By = "safety-monitor";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ExecutionService _execution;
    private readonly KillSwitch _killSwitch;
    private readonly MarketState _market;
    private readonly RiskLimits _limits;
    private readonly SafetyOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SafetyMonitor> _logger;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset _lastReconcile = DateTimeOffset.MinValue;

    public SafetyMonitor(
        ExecutionService execution,
        KillSwitch killSwitch,
        MarketState market,
        RiskLimits limits,
        SafetyOptions options,
        TimeProvider time,
        ILogger<SafetyMonitor> logger)
    {
        options.Validate();
        _execution = execution;
        _killSwitch = killSwitch;
        _market = market;
        _limits = limits;
        _options = options;
        _time = time;
        _logger = logger;
        _startedAt = time.GetUtcNow();
    }

    /// <summary>One round of checks. Returns the reason the kill switch was engaged, or null.</summary>
    public async Task<string?> CheckOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        string? reason = null;

        var snapshot = await _execution.SampleAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is { PeakEquity: > 0 })
        {
            var drawdown = (snapshot.PeakEquity - snapshot.Equity) / snapshot.PeakEquity;
            if (drawdown >= _limits.MaxDrawdownFraction)
                reason = $"Drawdown is {drawdown:P2}, at or above the {_limits.MaxDrawdownFraction:P0} limit.";
        }

        // Give the poller time to deliver first quotes after a start.
        if (reason is null && now - _startedAt >= _options.StaleDataKillAfter)
        {
            foreach (var symbol in _limits.AllowedSymbols.Order(StringComparer.Ordinal))
            {
                var quote = _market.Quote(symbol);
                if (quote is null || now - quote.AsOf > _options.StaleDataKillAfter)
                {
                    reason = $"No fresh market data for {symbol} for over {_options.StaleDataKillAfter.TotalMinutes:0} minutes.";
                    break;
                }
            }
        }

        if (reason is not null && !_killSwitch.IsEngaged)
            await _execution.EngageKillSwitchAsync(reason, By, cancellationToken).ConfigureAwait(false);

        if (now - _lastReconcile >= _options.ReconcileInterval)
        {
            _lastReconcile = now;
            var report = await _execution.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            reason ??= report.KillSwitchReason;
        }

        return reason;
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Safety check failed ({Error}): {Detail}")]
    private static partial void LogCheckFailed(ILogger logger, string error, string detail);
}
