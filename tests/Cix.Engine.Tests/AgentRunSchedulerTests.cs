using System.Text.Json;
using Cix.Engine.AgentRuns;
using Cix.Engine.Journal;
using Cix.Engine.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

internal sealed class RecordingRunQueue(IAgentRunStateStore store) : IAgentRunQueue
{
    public List<(AgentRun Run, TimeSpan TimeToLive, bool SavedFirst)> Sent { get; } = [];

    public bool Fail { get; set; }

    /// <summary>The send's own timeout firing, with the caller's token still live.</summary>
    public bool TimeOut { get; set; }

    public async Task SendAsync(AgentRun run, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        if (Fail)
            throw new HttpRequestException("queue unreachable");
        if (TimeOut)
            throw new TaskCanceledException("send timed out");
        var saved = await store.LoadAsync(cancellationToken);
        Sent.Add((run, timeToLive, saved?.State.InFlight.Any(r => r.Id == run.Id) == true));
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
        private readonly Action<AgentRunOptions>? _change;

        public Setup(bool analyst = false, bool lease = true, Action<AgentRunOptions>? change = null)
        {
            _change = change;
            Store = new InMemoryAgentRunStateStore();
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
        public AgentRunScheduler New(bool analyst = false)
        {
            var options = new AgentRunOptions { Analyst = analyst };
            _change?.Invoke(options);
            return new(options, Signals, Store, Queue, Journal, Time, NullLogger<AgentRunScheduler>.Instance, Lease);
        }

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
        Assert.Equal(AgentRunAnswer.Unavailable, (await setup.Scheduler.ClaimAsync("run-20261026-080000-abcdef", "trader", null, CancellationToken.None)).Answer);
    }

    [Fact]
    public async Task AnEngineThatLostTheLease_OrWasOvertaken_ChangesNothing()
    {
        var setup = new Setup();
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = setup.Queue.Sent.Single().Run;

        var next = setup.New(); // took over, read the same state
        Assert.Equal(AgentRunAnswer.Yes, (await next.ClaimAsync(run.Id, "trader", null, CancellationToken.None)).Answer);
        var saves = setup.Store.Saves;

        // The old engine still has the state from before the claim: its write (a second claim) must not undo it.
        Assert.Equal(AgentRunAnswer.Unavailable, (await setup.Scheduler.ClaimAsync(run.Id, "trader", null, CancellationToken.None)).Answer);
        Assert.Equal(saves, setup.Store.Saves);
        Assert.Null(setup.Scheduler.State); // read again next time
        Assert.Equal(AgentRunAnswer.Yes, await setup.Scheduler.CompleteAsync(run.Id, "trader", AgentRunOutcome.Done, CancellationToken.None));

        setup.Lease.IsHeld = false;
        Assert.Equal(AgentRunAnswer.Unavailable, await next.CompleteAsync(run.Id, "trader", AgentRunOutcome.Done, CancellationToken.None));
        setup.Time.Advance(TimeSpan.FromHours(4)); // a heartbeat is due, but not this engine's to queue
        Assert.Equal(0, await next.PollOnceAsync(CancellationToken.None));
        Assert.Equal(saves + 1, setup.Store.Saves);
    }

    [Fact]
    public async Task ARunThatCannotBeQueued_CountsAsNotRun_AndIsTriedAgain()
    {
        var setup = new Setup();
        setup.Queue.Fail = true;
        setup.Signals.Inputs = new AgentRunInputs(true, true, "buy:SOL-EUR", string.Empty);

        Assert.Equal(0, await setup.Scheduler.PollOnceAsync(CancellationToken.None));
        var state = (await setup.Store.LoadAsync(CancellationToken.None))!.State;
        Assert.Empty(state.InFlight);
        Assert.Equal((0, 1), (state.Runs, state.Refunds));
        Assert.Single(setup.Steps("not-sent"));

        setup.Queue.Fail = false;
        setup.Time.Advance(TimeSpan.FromMinutes(15)); // the minimum gap after the attempt
        Assert.Equal(1, await setup.Scheduler.PollOnceAsync(CancellationToken.None));
        Assert.Equal("buy:SOL-EUR", setup.Queue.Sent.Single().Run.Key);
    }

    [Fact]
    public async Task ASendThatTimesOut_IsNotSent()
    {
        var setup = new Setup();
        setup.Queue.TimeOut = true;

        Assert.Equal(0, await setup.Scheduler.PollOnceAsync(CancellationToken.None));
        Assert.Single(setup.Steps("not-sent"));
        Assert.Empty(setup.Scheduler.State!.InFlight);
    }

    [Fact]
    public async Task TheDailyCap_IsJournaledOnce()
    {
        var setup = new Setup(change: o => o.MaxRunsPerDay = 1);
        setup.Signals.Inputs = new AgentRunInputs(true, true, "buy:SOL-EUR", string.Empty);
        var run = (await PollAndTakeAsync(setup)).Run;
        Assert.Equal(AgentRunAnswer.Yes, await setup.Scheduler.CompleteAsync(run.Id, "trader", AgentRunOutcome.Done, CancellationToken.None));

        setup.Time.Advance(TimeSpan.FromMinutes(15));
        setup.Signals.Inputs = new AgentRunInputs(true, true, "buy:SOL-EUR|buy:ETH-EUR", string.Empty);
        Assert.Equal(0, await setup.Scheduler.PollOnceAsync(CancellationToken.None));
        Assert.Equal(0, await setup.Scheduler.PollOnceAsync(CancellationToken.None));

        var capped = Assert.Single(setup.Steps("capped"));
        Assert.Equal(AgentRunScheduler.Actor, capped.Actor);
        Assert.Null(capped.RunId);
    }

    [Fact]
    public async Task AnExecution_CanRepeatAClaimWhoseAnswerItLost()
    {
        var setup = new Setup();
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = setup.Queue.Sent.Single().Run;

        Assert.Equal(AgentRunAnswer.Yes, (await setup.Scheduler.ClaimAsync(run.Id, "trader", "cix-trader-x7k2p", CancellationToken.None)).Answer);
        Assert.Equal(AgentRunAnswer.Yes, (await setup.Scheduler.ClaimAsync(run.Id, "trader", "cix-trader-x7k2p", CancellationToken.None)).Answer);
        Assert.Equal(AgentRunAnswer.No, (await setup.Scheduler.ClaimAsync(run.Id, "trader", "cix-trader-other", CancellationToken.None)).Answer);
        Assert.Equal(AgentRunAnswer.No, (await setup.Scheduler.ClaimAsync(run.Id, "trader", "not a label", CancellationToken.None)).Answer);
        Assert.Single(setup.Steps("claimed")); // journaled once
    }

    [Fact]
    public async Task RunAnalystNow_SavesBeforeItQueues_AndARunThatCannotBeQueued_DoesNotHoldBackTheNextPress()
    {
        var setup = new Setup(analyst: true);

        var (answer, detail) = await setup.Scheduler.RunAnalystNowAsync(CancellationToken.None);
        Assert.Equal(AgentRunAnswer.Yes, answer);
        var sent = Assert.Single(setup.Queue.Sent);
        Assert.True(sent.SavedFirst);
        Assert.Equal(AgentRunPurpose.Owner, sent.Run.Purpose);
        Assert.Contains(sent.Run.Id, detail, StringComparison.Ordinal);
        Assert.Equal(AgentRunAnswer.No, (await setup.Scheduler.RunAnalystNowAsync(CancellationToken.None)).Answer); // one at a time
        Assert.Equal(AgentRunAnswer.Yes, (await setup.Scheduler.ClaimAsync(sent.Run.Id, "analyst", null, CancellationToken.None)).Answer);
        Assert.Equal(AgentRunAnswer.Yes, await setup.Scheduler.CompleteAsync(sent.Run.Id, "analyst", AgentRunOutcome.Done, CancellationToken.None));

        setup.Time.Advance(TimeSpan.FromMinutes(30));
        setup.Queue.Fail = true;
        Assert.Equal(AgentRunAnswer.No, (await setup.Scheduler.RunAnalystNowAsync(CancellationToken.None)).Answer);
        Assert.Single(setup.Steps("not-sent"));
        setup.Queue.Fail = false;
        Assert.Equal(AgentRunAnswer.Yes, (await setup.Scheduler.RunAnalystNowAsync(CancellationToken.None)).Answer); // no cooldown after it
        Assert.Equal(2, (await setup.Store.LoadAsync(CancellationToken.None))!.State.OwnerAnalystRuns);

        setup.Lease.IsHeld = false;
        Assert.Equal(AgentRunAnswer.Unavailable, (await setup.Scheduler.RunAnalystNowAsync(CancellationToken.None)).Answer);
    }

    [Fact]
    public void WithCosmos_TheRunStateIsInTheDatabase_WhicheverIsRegisteredFirst()
    {
        var options = new AgentRunOptions { Mode = AgentRunMode.Queue, Queues = { ServiceUri = "https://cixruns.queue.core.windows.net" } };
        var cosmos = new CosmosOptions { Enabled = true, Emulator = true, Endpoint = "https://localhost:8081", Database = "cix" };

        var cosmosLast = new ServiceCollection()
            .AddCixAgentRuns(options, Tools.ToolServerAuth.Entra, durableState: true)
            .AddCixCosmos(cosmos, TradingMode.Paper);
        var cosmosFirst = new ServiceCollection()
            .AddCixCosmos(cosmos, TradingMode.Paper)
            .AddCixAgentRuns(options, Tools.ToolServerAuth.Entra, durableState: true);

        foreach (var services in new[] { cosmosLast, cosmosFirst })
        {
            var store = services.Last(d => d.ServiceType == typeof(IAgentRunStateStore));
            Assert.NotNull(store.ImplementationFactory); // the Cosmos store's factory, not the in-memory instance
            Assert.Null(store.ImplementationInstance);
        }
    }

    private static async Task<(AgentRunAnswer Answer, AgentRun Run)> PollAndTakeAsync(Setup setup)
    {
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = setup.Queue.Sent.Last().Run;
        var (answer, _) = await setup.Scheduler.ClaimAsync(run.Id, run.Kind.Role(), null, CancellationToken.None);
        return (answer, run);
    }

    [Fact]
    public async Task OnlyItsOwnJob_ClaimsARun_Once_AndReportsItOnce()
    {
        var setup = new Setup();
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = setup.Queue.Sent.Single().Run;

        Assert.Equal(AgentRunAnswer.No, (await setup.Scheduler.ClaimAsync(run.Id, "analyst", null, CancellationToken.None)).Answer);
        Assert.Equal(AgentRunAnswer.No, (await setup.Scheduler.ClaimAsync(run.Id, null, null, CancellationToken.None)).Answer);
        Assert.Equal(AgentRunAnswer.No, (await setup.Scheduler.ClaimAsync("../" + run.Id, "trader", null, CancellationToken.None)).Answer);
        var claimed = await setup.Scheduler.ClaimAsync(run.Id, "trader", null, CancellationToken.None);
        Assert.Equal((AgentRunAnswer.Yes, run.Id), (claimed.Answer, claimed.Run?.Id));
        Assert.Equal(AgentRunAnswer.No, (await setup.Scheduler.ClaimAsync(run.Id, "trader", null, CancellationToken.None)).Answer);

        Assert.Equal(AgentRunAnswer.No, await setup.Scheduler.CompleteAsync(run.Id, "trader-requests", AgentRunOutcome.Done, CancellationToken.None));
        Assert.Equal(AgentRunAnswer.Yes, await setup.Scheduler.CompleteAsync(run.Id, "trader", AgentRunOutcome.Done, CancellationToken.None));
        Assert.Equal(AgentRunAnswer.No, await setup.Scheduler.CompleteAsync(run.Id, "trader", AgentRunOutcome.NotRun, CancellationToken.None));

        Assert.Empty((await setup.Store.LoadAsync(CancellationToken.None))!.State.InFlight);
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
        Assert.Equal(AgentRunAnswer.Yes, (await next.ClaimAsync(run.Id, "trader", null, CancellationToken.None)).Answer);
        Assert.Equal(AgentRunAnswer.Yes, await next.CompleteAsync(run.Id, "trader", AgentRunOutcome.Done, CancellationToken.None));
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
            LastAnalystRun = T0,
            OwnerAnalystRuns = 2,
            InFlight = [new AgentRun("run-20261026-080000-abcdef", AgentRunKind.Requests, AgentRunPurpose.Requests, "manual:a", T0, T0.AddMinutes(1))],
        };

        var json = JsonSerializer.Serialize(state, CosmosDb.Json);
        var back = JsonSerializer.Deserialize<AgentRunState>(json, CosmosDb.Json)!;

        Assert.Equal(json, JsonSerializer.Serialize(back, CosmosDb.Json));
        Assert.Equal(state.InFlight.Single(), back.InFlight.Single());
        Assert.Equal((T0, 2), (back.LastAnalystRun, back.OwnerAnalystRuns));
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
