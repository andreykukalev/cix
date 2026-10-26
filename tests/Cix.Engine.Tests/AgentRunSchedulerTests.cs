using System.Text.Json;
using Cix.Engine.AgentRuns;
using Cix.Engine.Journal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

internal sealed class RecordingRunQueue(IAgentRunStateStore store) : IAgentRunQueue
{
    public List<(AgentRun Run, TimeSpan TimeToLive, bool SavedFirst)> Sent { get; } = [];

    public bool Fail { get; set; }

    public async Task SendAsync(AgentRun run, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        if (Fail)
            throw new HttpRequestException("queue unreachable");
        var saved = await store.LoadAsync(cancellationToken);
        Sent.Add((run, timeToLive, saved?.InFlight.Any(r => r.Id == run.Id) == true));
    }
}

internal sealed class FixedRunSignals : IAgentRunSignals
{
    public AgentRunInputs Inputs { get; set; } = new(true, true, string.Empty, string.Empty);

    public AgentRunInputs Read() => Inputs;
}

/// <summary>The scheduler around the run rules: lease, saving before sending, send failures, claims, journal.</summary>
public sealed class AgentRunSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    internal sealed class Setup
    {
        public Setup(bool analyst = false, bool lease = true, InMemoryAgentRunStateStore? store = null)
        {
            Store = store ?? new InMemoryAgentRunStateStore();
            Queue = new RecordingRunQueue(Store);
            Lease = new FixedLease(lease);
            Scheduler = New(analyst);
        }

        public MutableTime Time { get; } = new(T0);
        public InMemoryAgentRunStateStore Store { get; }
        public RecordingRunQueue Queue { get; }
        public FixedRunSignals Signals { get; } = new();
        public InMemoryJournal Journal { get; } = new();
        public FixedLease Lease { get; }
        public AgentRunScheduler Scheduler { get; }

        /// <summary>Another engine over the same saved state, as after a restart or a deployment.</summary>
        public AgentRunScheduler New(bool analyst = false) => new(
            new AgentRunOptions { Analyst = analyst }, Signals, Store, Queue, Journal, Time, NullLogger<AgentRunScheduler>.Instance, Lease);

        public IReadOnlyList<JournalEntry> Steps(string step) =>
            Journal.Entries.Where(e => e.Kind == AgentRunScheduler.Kind && e.Data?["step"] == step).ToList();
    }

    [Fact]
    public async Task DueRuns_AreSavedBeforeTheyAreQueued_AndJournaled()
    {
        var setup = new Setup(analyst: true);

        Assert.Equal(2, await setup.Scheduler.PollOnceAsync(CancellationToken.None));

        Assert.Equal(new[] { AgentRunKind.Trader, AgentRunKind.Analyst }, setup.Queue.Sent.Select(s => s.Run.Kind));
        Assert.All(setup.Queue.Sent, s => Assert.True(s.SavedFirst));
        Assert.All(setup.Queue.Sent, s => Assert.Equal(TimeSpan.FromMinutes(10), s.TimeToLive)); // expires when it would be given up
        var queued = setup.Steps("queued");
        Assert.Equal(setup.Queue.Sent.Select(s => s.Run.Id), queued.Select(e => e.RunId));
        Assert.All(queued, e => Assert.Equal(AgentRunScheduler.Actor, e.Actor));
        Assert.Equal("trader", queued[0].Data!["job"]);

        var saves = setup.Store.Saves;
        Assert.Equal(0, await setup.Scheduler.PollOnceAsync(CancellationToken.None));
        Assert.Equal(saves, setup.Store.Saves); // nothing changed, nothing written
    }

    [Fact]
    public async Task WithoutTheLease_NothingIsQueuedOrClaimed()
    {
        var setup = new Setup(lease: false);

        Assert.False(setup.Scheduler.IsActive);
        Assert.Equal(0, await setup.Scheduler.PollOnceAsync(CancellationToken.None));
        Assert.Empty(setup.Queue.Sent);
        Assert.Equal(0, setup.Store.Saves);
        Assert.Null(await setup.Scheduler.ClaimAsync("run-20261026-080000-abcdef", "trader", CancellationToken.None));
    }

    [Fact]
    public async Task ARunThatCannotBeQueued_CountsAsNotRun_AndIsTriedAgain()
    {
        var setup = new Setup();
        setup.Queue.Fail = true;
        setup.Signals.Inputs = new AgentRunInputs(true, true, "buy:SOL-EUR", string.Empty);

        Assert.Equal(0, await setup.Scheduler.PollOnceAsync(CancellationToken.None));
        var state = (await setup.Store.LoadAsync(CancellationToken.None))!;
        Assert.Empty(state.InFlight);
        Assert.Equal((0, 1), (state.Runs, state.Refunds));
        Assert.Single(setup.Steps("not-sent"));

        setup.Queue.Fail = false;
        setup.Time.Advance(TimeSpan.FromMinutes(15)); // the minimum gap after the attempt
        Assert.Equal(1, await setup.Scheduler.PollOnceAsync(CancellationToken.None));
        Assert.Equal("buy:SOL-EUR", setup.Queue.Sent.Single().Run.Key);
    }

    [Fact]
    public async Task OnlyItsOwnJob_ClaimsARun_Once_AndReportsItOnce()
    {
        var setup = new Setup();
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = setup.Queue.Sent.Single().Run;

        Assert.Null(await setup.Scheduler.ClaimAsync(run.Id, "analyst", CancellationToken.None));
        Assert.Null(await setup.Scheduler.ClaimAsync(run.Id, null, CancellationToken.None));
        Assert.Null(await setup.Scheduler.ClaimAsync("../" + run.Id, "trader", CancellationToken.None));
        Assert.Equal(run.Id, (await setup.Scheduler.ClaimAsync(run.Id, "trader", CancellationToken.None))?.Id);
        Assert.Null(await setup.Scheduler.ClaimAsync(run.Id, "trader", CancellationToken.None));

        Assert.False(await setup.Scheduler.CompleteAsync(run.Id, "trader-requests", AgentRunOutcome.Done, CancellationToken.None));
        Assert.True(await setup.Scheduler.CompleteAsync(run.Id, "trader", AgentRunOutcome.Done, CancellationToken.None));
        Assert.False(await setup.Scheduler.CompleteAsync(run.Id, "trader", AgentRunOutcome.NotRun, CancellationToken.None));

        Assert.Empty((await setup.Store.LoadAsync(CancellationToken.None))!.InFlight);
        Assert.Equal(new[] { "queued", "claimed", "done" }, setup.Journal.Entries.Where(e => e.RunId == run.Id).Select(e => e.Data!["step"]));
    }

    [Fact]
    public async Task ANewEngine_CarriesOnWithTheSavedState()
    {
        var setup = new Setup();
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = setup.Queue.Sent.Single().Run;

        var next = setup.New();
        Assert.Equal(0, await next.PollOnceAsync(CancellationToken.None)); // the heartbeat is in flight: not queued again
        Assert.NotNull(await next.ClaimAsync(run.Id, "trader", CancellationToken.None));
        Assert.True(await next.CompleteAsync(run.Id, "trader", AgentRunOutcome.Done, CancellationToken.None));
        Assert.Equal(T0 + TimeSpan.FromHours(4), next.State!.NextHeartbeat);
    }

    [Fact]
    public async Task RunsNeverClaimed_AreGivenUp_AndJournaled()
    {
        var setup = new Setup();
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);

        setup.Time.Advance(TimeSpan.FromMinutes(10));
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);

        var expired = Assert.Single(setup.Steps("not-run"));
        Assert.Equal(setup.Queue.Sent.Single().Run.Id, expired.RunId);
        Assert.Empty(setup.Scheduler.State!.InFlight);
    }

    [Fact]
    public void TheState_RoundTripsThroughTheDatabaseJson()
    {
        var state = new AgentRunState
        {
            Day = new DateOnly(2026, 10, 26),
            Runs = 3,
            Refunds = 1,
            CappedOn = new DateOnly(2026, 10, 26),
            LastKey = "buy:SOL-EUR",
            NextHeartbeat = T0,
            InFlight = [new AgentRun("run-20261026-080000-abcdef", AgentRunKind.Requests, AgentRunPurpose.Requests, "manual:a", T0, T0.AddMinutes(1))],
        };

        var json = JsonSerializer.Serialize(state, Persistence.CosmosDb.Json);
        var back = JsonSerializer.Deserialize<AgentRunState>(json, Persistence.CosmosDb.Json)!;

        Assert.Equal(json, JsonSerializer.Serialize(back, Persistence.CosmosDb.Json));
        Assert.Equal(state.InFlight.Single(), back.InFlight.Single());
    }

    [Fact]
    public void TheMessage_SaysWhichRun_AndNothingElse()
    {
        var run = new AgentRun("run-20261026-080000-abcdef", AgentRunKind.Requests, AgentRunPurpose.Requests, "manual:3f2a", T0);
        using var message = JsonDocument.Parse(StorageAgentRunQueue.MessageFor(run));

        Assert.Equal(
            new[] { ("runId", "run-20261026-080000-abcdef"), ("job", "trader-requests"), ("purpose", "requests") },
            message.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetString())));
    }
}
