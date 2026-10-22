namespace Cix.Engine;

/// <summary>Placeholder until the market-data poller and paper engine land (milestone 1).</summary>
public sealed partial class HeartbeatService(ILogger<HeartbeatService> logger, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, "paper");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            LogHeartbeat(logger, time.GetUtcNow());
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "CIX engine started in {Mode} mode")]
    private static partial void LogStarted(ILogger logger, string mode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Heartbeat at {Now:O}")]
    private static partial void LogHeartbeat(ILogger logger, DateTimeOffset now);
}
