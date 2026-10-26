using Cix.Engine.Execution;
using Cix.Engine.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>A lease document with ETags, like the one in Cosmos DB; it can be made unreachable.</summary>
internal sealed class InMemoryLeaseStore : ILeaseStore
{
    private readonly object _gate = new();
    private StoredLease? _current;
    private int _version;

    public bool Unreachable { get; set; }

    public LeaseRecord? Record
    {
        get
        {
            lock (_gate)
                return _current?.Record;
        }
    }

    public Task<StoredLease?> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnreachable();
        lock (_gate)
            return Task.FromResult(_current);
    }

    public Task<string?> TryCreateAsync(LeaseRecord record, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable();
        lock (_gate)
        {
            if (_current is not null)
                return Task.FromResult<string?>(null);
            _current = new StoredLease(record, $"v{++_version}");
            return Task.FromResult<string?>(_current.ETag);
        }
    }

    public Task<string?> TryReplaceAsync(LeaseRecord record, string etag, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable();
        lock (_gate)
        {
            if (_current is null || _current.ETag != etag)
                return Task.FromResult<string?>(null);
            _current = new StoredLease(record, $"v{++_version}");
            return Task.FromResult<string?>(_current.ETag);
        }
    }

    private void ThrowIfUnreachable()
    {
        if (Unreachable)
            throw new HttpRequestException("database unreachable");
    }
}

internal sealed class FakeLifetime : IHostApplicationLifetime
{
    public bool StopRequested { get; private set; }

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => StopRequested = true;
}

/// <summary>A lease whose state the test sets directly.</summary>
internal sealed class FixedLease(bool held) : IEngineLease
{
    public bool IsHeld { get; set; } = held;

    public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(IsHeld);

    public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EngineLeaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static EngineLease Engine(InMemoryLeaseStore store, MutableTime time, string holder) =>
        new(store, time, NullLogger<EngineLease>.Instance, holder, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task FirstEngineTakesTheLease_ASecondWaitsWhileItIsRenewed()
    {
        var time = new MutableTime(T0);
        var store = new InMemoryLeaseStore();
        var first = Engine(store, time, "a");
        var second = Engine(store, time, "b");

        Assert.True(await first.TryAcquireAsync(CancellationToken.None));
        Assert.True(first.IsHeld);
        Assert.Equal("a", store.Record!.Holder);

        // Ten minutes of renewals every 15 seconds: the second engine never sees the lease stand still long enough.
        for (var i = 0; i < 40; i++)
        {
            Assert.False(await second.TryAcquireAsync(CancellationToken.None));
            time.Advance(EngineLease.RenewInterval);
            Assert.True(await first.RenewAsync(CancellationToken.None));
        }

        Assert.False(second.IsHeld);
        Assert.Equal("a", store.Record!.Holder);
    }

    [Fact]
    public async Task AHolderThatStopsRenewing_LosesTheLeaseOnlyAfterItExpires_AndStopsActingFirst()
    {
        var time = new MutableTime(T0);
        var store = new InMemoryLeaseStore();
        var first = Engine(store, time, "a");
        var second = Engine(store, time, "b");
        Assert.True(await first.TryAcquireAsync(CancellationToken.None));

        // The first engine hangs: no more renewals.
        Assert.False(await second.TryAcquireAsync(CancellationToken.None)); // starts watching
        time.Advance(EngineLease.HoldLimit);
        Assert.False(first.IsHeld); // it stops acting at the hold limit...
        Assert.False(await second.TryAcquireAsync(CancellationToken.None)); // ...long before anyone may take over

        time.Advance(EngineLease.Expiry - EngineLease.HoldLimit - TimeSpan.FromSeconds(1));
        Assert.False(await second.TryAcquireAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await second.TryAcquireAsync(CancellationToken.None));
        Assert.Equal("b", store.Record!.Holder);

        // The first engine wakes up: its renewal fails, so it knows it lost the lease.
        Assert.False(await first.RenewAsync(CancellationToken.None));
        Assert.False(first.IsHeld);
        Assert.True(second.IsHeld);
    }

    [Fact]
    public async Task AReleasedLease_IsTakenAtOnce()
    {
        var time = new MutableTime(T0);
        var store = new InMemoryLeaseStore();
        var first = Engine(store, time, "a");
        var second = Engine(store, time, "b");
        Assert.True(await first.TryAcquireAsync(CancellationToken.None));
        Assert.False(await second.TryAcquireAsync(CancellationToken.None));

        await first.ReleaseAsync(CancellationToken.None);

        Assert.False(first.IsHeld);
        Assert.Null(store.Record!.Holder);
        Assert.True(await second.TryAcquireAsync(CancellationToken.None));
        Assert.Equal("b", store.Record!.Holder);
    }

    [Fact]
    public async Task ADatabaseOutage_KeepsTheLeaseUntilTheHoldLimit_ThenTheEngineStops()
    {
        var time = new MutableTime(T0);
        var store = new InMemoryLeaseStore();
        var lease = Engine(store, time, "a");
        var lifetime = new FakeLifetime();
        var service = new EngineLeaseService(lease, lifetime, time, NullLogger<EngineLeaseService>.Instance);
        Assert.True(await lease.TryAcquireAsync(CancellationToken.None));

        store.Unreachable = true;
        for (var elapsed = TimeSpan.Zero; elapsed + EngineLease.RenewInterval < EngineLease.HoldLimit; elapsed += EngineLease.RenewInterval)
        {
            time.Advance(EngineLease.RenewInterval);
            Assert.True(await service.RenewOnceAsync(CancellationToken.None)); // still within the hold limit
        }

        Assert.False(lifetime.StopRequested);
        time.Advance(EngineLease.RenewInterval);
        Assert.False(await service.RenewOnceAsync(CancellationToken.None));
        Assert.True(lifetime.StopRequested);
        Assert.False(lease.IsHeld);
    }

    [Fact]
    public async Task ALostLease_StopsTheEngine()
    {
        var time = new MutableTime(T0);
        var store = new InMemoryLeaseStore();
        var first = Engine(store, time, "a");
        var second = Engine(store, time, "b");
        var lifetime = new FakeLifetime();
        var service = new EngineLeaseService(first, lifetime, time, NullLogger<EngineLeaseService>.Instance);
        Assert.True(await first.TryAcquireAsync(CancellationToken.None));
        Assert.False(await second.TryAcquireAsync(CancellationToken.None));
        time.Advance(EngineLease.Expiry);
        Assert.True(await second.TryAcquireAsync(CancellationToken.None));

        Assert.False(await service.RenewOnceAsync(CancellationToken.None));
        Assert.True(lifetime.StopRequested);
    }

    [Fact]
    public async Task TwoEnginesRacingForAFreeLease_OnlyOneGetsIt()
    {
        var time = new MutableTime(T0);
        var store = new InMemoryLeaseStore();
        var engines = Enumerable.Range(0, 5).Select(i => Engine(store, time, $"e{i}")).ToList();

        var results = await Task.WhenAll(engines.Select(e => e.TryAcquireAsync(CancellationToken.None)));

        Assert.Single(results, r => r);
        Assert.Single(engines, e => e.IsHeld);
    }

    [Fact]
    public async Task WithoutTheLease_NoOrderIsSent_AndNoOrderIsCancelled()
    {
        var lease = new FixedLease(held: true);
        var rig = new Rig(lease: lease);
        var resting = await rig.Buy(0.0005m, 59_950m, "c-1");
        Assert.Equal(ExecutionStatus.Placed, resting.Status);

        lease.IsHeld = false;
        var refused = await rig.Buy(0.0005m, 59_950m, "c-2");
        Assert.Equal(ExecutionStatus.Rejected, refused.Status);
        Assert.Equal(ExecutionService.LeaseRule, refused.Rule);
        Assert.Single(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None)); // only the first one reached the exchange

        var (outcome, _) = await rig.Execution.CancelOwnOrderAsync(resting.Placed!.VenueOrderId, CancellationToken.None);
        Assert.Equal(CancelOutcome.Failed, outcome);
        Assert.Equal(0, await rig.Execution.CancelOpenBuysAsync(CancellationToken.None));
        Assert.Single(await rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None)); // still working: it may be the other engine's now
    }
}
