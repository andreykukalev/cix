namespace Cix.Engine.Execution;

/// <summary>
/// Values the portfolio every minute, so the drawdown peak and the 00:00 UTC daily-loss baseline track the market
/// even when no order is attempted (otherwise an overnight loss would become part of the next day's baseline).
/// </summary>
public sealed class PortfolioSampler(ExecutionService execution, TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await execution.SampleAsync(stoppingToken).ConfigureAwait(false);
    }
}
