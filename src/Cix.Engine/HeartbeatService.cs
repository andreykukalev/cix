namespace Cix.Engine;

/// <summary>Announces the trading mode at startup and logs a heartbeat until the pollers land (milestone 1).</summary>
public sealed partial class HeartbeatService(ILogger<HeartbeatService> logger, TimeProvider time, TradingContext trading) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mode = trading.Mode;
        if (mode == TradingMode.Live)
            LogLive(logger);
        else
            LogPaper(logger);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            LogHeartbeat(logger, mode, time.GetUtcNow());
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "CIX engine started in LIVE mode: orders use REAL MONEY on Revolut X")]
    private static partial void LogLive(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "CIX engine started in PAPER mode: orders are simulated")]
    private static partial void LogPaper(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Heartbeat ({Mode}) at {Now:O}")]
    private static partial void LogHeartbeat(ILogger logger, TradingMode mode, DateTimeOffset now);
}
