namespace Cix.Engine.Persistence;

/// <summary>
/// Which engine may trade in a mode. With a shared database two engines of the same mode can run for a while: during a
/// deployment (the new replica starts before the old one stops), after a crash that the host hasn't noticed yet, or while
/// moving from the PC to Azure. Only the engine holding the lease loads the state and acts on the exchange (ADR-0013).
/// </summary>
public interface IEngineLease
{
    /// <summary>Whether this engine holds the lease and renewed it recently enough to act on it.</summary>
    bool IsHeld { get; }

    /// <summary>Renews the lease. False once it is lost, either taken by another engine or not renewed in time.</summary>
    Task<bool> RenewAsync(CancellationToken cancellationToken);

    /// <summary>Gives the lease up at shutdown, so the next engine can take it at once.</summary>
    Task ReleaseAsync(CancellationToken cancellationToken);
}

/// <summary>Without a shared database there is nothing to share: one engine, one machine, always held.</summary>
public sealed class NoEngineLease : IEngineLease
{
    public static readonly NoEngineLease Instance = new();

    public bool IsHeld => true;

    public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <param name="Holder">The engine that holds it, or null once released.</param>
public sealed record LeaseRecord(string? Holder, DateTimeOffset? AcquiredAt, DateTimeOffset UpdatedAt);

public sealed record StoredLease(LeaseRecord Record, string ETag);

/// <summary>One lease document, changed only if nobody else changed it since it was read (an ETag check).</summary>
public interface ILeaseStore
{
    Task<StoredLease?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Creates the document; returns its ETag, or null if it already exists.</summary>
    Task<string?> TryCreateAsync(LeaseRecord record, CancellationToken cancellationToken);

    /// <summary>Replaces the document if its ETag is still <paramref name="etag"/>; returns the new ETag, or null if it changed.</summary>
    Task<string?> TryReplaceAsync(LeaseRecord record, string etag, CancellationToken cancellationToken);
}

/// <summary>
/// The lease, held by renewing a document every <see cref="RenewInterval"/>. No two clocks are compared: a waiting engine
/// takes the lease only after watching the document stay unchanged for <see cref="Expiry"/> on its own clock, while the
/// holder stops acting once it has gone <see cref="HoldLimit"/> without a successful renewal, also on its own clock. The
/// difference between the two is the margin for the holder to shut down. Every change is an ETag check, so two engines
/// can never both believe they took it.
/// </summary>
public sealed partial class EngineLease : IEngineLease
{
    public static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(120);

    private readonly ILeaseStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<EngineLease> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();

    private LeaseRecord? _held;
    private string? _etag;
    private long _renewedAt;

    // What a waiting engine last saw, and when it first saw it.
    private string? _observedETag;
    private long _observedAt;

    public EngineLease(ILeaseStore store, TimeProvider time, ILogger<EngineLease> logger, string? holder = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _store = store;
        _time = time;
        _logger = logger;
        _delay = delay ?? ((d, ct) => Task.Delay(d, time, ct));
        Holder = holder ?? $"{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    /// <summary>This process: the host name (a replica's name in Azure) and a random suffix, so a restart is a new holder.</summary>
    public string Holder { get; }

    public bool IsHeld
    {
        get
        {
            lock (_gate)
                return _etag is not null && _time.GetElapsedTime(_renewedAt) < HoldLimit;
        }
    }

    /// <summary>Waits until this engine holds the lease. Call before loading the engine state.</summary>
    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        var waiting = false;
        while (!await TryAcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!waiting)
            {
                LogWaiting(_logger);
                waiting = true;
            }

            await _delay(RenewInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One attempt: takes the lease if nobody holds it, it was released, or its holder stopped renewing it.</summary>
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (IsHeld)
            return true;

        var now = _time.GetUtcNow();
        var record = new LeaseRecord(Holder, now, now);
        var sentAt = _time.GetTimestamp(); // the hold limit counts from before the write, never from its slow reply
        var current = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current is null)
            return Took(await _store.TryCreateAsync(record, cancellationToken).ConfigureAwait(false), record, sentAt, previous: null);

        var free = current.Record.Holder is null || string.Equals(current.Record.Holder, Holder, StringComparison.Ordinal);
        if (!free)
        {
            lock (_gate)
            {
                if (!string.Equals(_observedETag, current.ETag, StringComparison.Ordinal))
                {
                    _observedETag = current.ETag;
                    _observedAt = _time.GetTimestamp();
                    return false;
                }

                if (_time.GetElapsedTime(_observedAt) < Expiry)
                    return false;
            }
        }

        return Took(await _store.TryReplaceAsync(record, current.ETag, cancellationToken).ConfigureAwait(false), record, sentAt, current.Record.Holder);
    }

    private bool Took(string? etag, LeaseRecord record, long sentAt, string? previous)
    {
        lock (_gate)
        {
            _observedETag = null;
            if (etag is null)
                return false; // another engine changed it first
            _held = record;
            _etag = etag;
            _renewedAt = sentAt;
        }

        LogAcquired(_logger, Holder, previous ?? "nobody");
        return true;
    }

    public async Task<bool> RenewAsync(CancellationToken cancellationToken)
    {
        LeaseRecord held;
        string etag;
        lock (_gate)
        {
            if (_held is null || _etag is null)
                return false;
            held = _held;
            etag = _etag;
        }

        var next = held with { UpdatedAt = _time.GetUtcNow() };
        var sentAt = _time.GetTimestamp();
        string? renewed;
        try
        {
            renewed = await _store.TryReplaceAsync(next, etag, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The database may be briefly unreachable: still held until the hold limit runs out.
            LogRenewFailed(_logger, ex.GetType().Name);
            return IsHeld;
        }

        lock (_gate)
        {
            if (renewed is null)
            {
                _held = null;
                _etag = null;
            }
            else
            {
                _held = next;
                _etag = renewed;
                _renewedAt = sentAt;
            }
        }

        if (renewed is null)
            LogLost(_logger, Holder);
        return renewed is not null;
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        LeaseRecord held;
        string etag;
        lock (_gate)
        {
            if (_held is null || _etag is null)
                return;
            held = _held;
            etag = _etag;
            _held = null;
            _etag = null; // stop acting first, then tell the others
        }

        try
        {
            if (await _store.TryReplaceAsync(held with { Holder = null, UpdatedAt = _time.GetUtcNow() }, etag, cancellationToken).ConfigureAwait(false) is not null)
                LogReleased(_logger, Holder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRenewFailed(_logger, ex.GetType().Name); // the next engine waits for the lease to expire instead
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Another engine holds the lease for this trading mode; waiting until it stops or its lease expires")]
    private static partial void LogWaiting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine lease taken by {Holder} (previously {Previous})")]
    private static partial void LogAcquired(ILogger logger, string holder, string previous);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine lease could not be written ({Error})")]
    private static partial void LogRenewFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Engine lease lost by {Holder}: another engine took it; this engine stops")]
    private static partial void LogLost(ILogger logger, string holder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine lease released by {Holder}")]
    private static partial void LogReleased(ILogger logger, string holder);
}

/// <summary>
/// Renews the lease, and stops the engine when it is lost so the host restarts it as a waiting engine. Registered before
/// the other background services, so it stops after them and releases the lease only once they have finished.
/// </summary>
public sealed partial class EngineLeaseService(IEngineLease lease, IHostApplicationLifetime lifetime, TimeProvider time, ILogger<EngineLeaseService> logger)
    : BackgroundService
{
    /// <summary>How long the release at shutdown may take, whatever time the other services' shutdown left.</summary>
    public static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One renewal, given at most <see cref="EngineLease.RenewInterval"/> so a hung database call can't keep a lease that ran
    /// out from stopping the engine. Stops the application when the lease is lost. Returns whether it is still held.
    /// </summary>
    public async Task<bool> RenewOnceAsync(CancellationToken cancellationToken)
    {
        bool held;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(EngineLease.RenewInterval);
            try
            {
                held = await lease.RenewAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                held = lease.IsHeld; // timed out: like any failed renewal, held only until the hold limit
            }
        }

        if (held)
            return true;
        LogStopping(logger);
        lifetime.StopApplication();
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (lease is NoEngineLease)
            return;
        using var timer = new PeriodicTimer(EngineLease.RenewInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!await RenewOnceAsync(stoppingToken).ConfigureAwait(false))
                    return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Its own time limit: the host's token may already have run out if other services were slow to stop.
        using var release = new CancellationTokenSource(ReleaseTimeout);
        try
        {
            await lease.ReleaseAsync(release.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Not released: the next engine waits for the lease to expire instead.
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Engine lease lost or not renewed in time: stopping so that only one engine trades")]
    private static partial void LogStopping(ILogger logger);
}
