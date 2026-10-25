using System.Net;
using System.Net.Sockets;
using Microsoft.Azure.Cosmos;

namespace Cix.Engine;

/// <summary>
/// Runs a startup step until it succeeds, waiting longer between attempts (2, 4, 8, 16, then every 30 seconds), as long as
/// the failure is one that time can fix: the database still starting, a dropped connection, a timeout. Any other failure
/// (a bad setting, an unreadable saved state) stops startup at once, as before. Used so the engine waits for Cosmos DB
/// after a reboot instead of crashing and being restarted by Docker.
/// </summary>
public static partial class StartupRetry
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(
        Func<CancellationToken, Task> step,
        string what,
        ILogger logger,
        TimeProvider time,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        delay ??= (wait, ct) => Task.Delay(wait, time, ct);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await step(cancellationToken).ConfigureAwait(false);
                if (attempt > 1)
                    LogReady(logger, what, attempt);
                return;
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                var wait = Backoff(attempt);
                LogWaiting(logger, what, attempt, (int)wait.TotalSeconds, Describe(ex));
                await delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static TimeSpan Backoff(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var seconds = attempt >= 5 ? MaxWait.TotalSeconds : Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxWait.TotalSeconds));
    }

    /// <summary>Failures that waiting can fix. A cancellation the caller asked for is never one.</summary>
    public static bool IsTransient(Exception ex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (cancellationToken.IsCancellationRequested)
            return false;
        return ex switch
        {
            CosmosException cosmos => cosmos.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
            HttpRequestException or SocketException or TimeoutException or OperationCanceledException => true,
            IOException when ex.GetType() == typeof(IOException) => ex.InnerException is null || IsTransient(ex.InnerException, cancellationToken),
            AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(e => IsTransient(e, cancellationToken)),
            _ => false,
        };
    }

    /// <summary>The kind of failure and its status, never the full message (it can carry endpoint details).</summary>
    private static string Describe(Exception ex) => ex switch
    {
        CosmosException cosmos => $"Cosmos DB {(int)cosmos.StatusCode} {cosmos.StatusCode}",
        HttpRequestException http => $"{nameof(HttpRequestException)} ({http.HttpRequestError})",
        _ => ex.GetType().Name,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waiting for {What}: attempt {Attempt} failed ({Error}); retrying in {Seconds}s")]
    private static partial void LogWaiting(ILogger logger, string what, int attempt, int seconds, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "{What} ready after {Attempts} attempts")]
    private static partial void LogReady(ILogger logger, string what, int attempts);
}
