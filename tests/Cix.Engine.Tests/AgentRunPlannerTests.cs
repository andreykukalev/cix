using Cix.Engine.AgentRuns;
using Cix.Engine.Tools;

namespace Cix.Engine.Tests;

/// <summary>The run rules carried over from tools/trader/run-trader.sh and run-analyst.sh (ADR-0013).</summary>
public sealed class AgentRunPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    private static AgentRunOptions Options(Action<AgentRunOptions>? change = null)
    {
        var options = new AgentRunOptions { Analyst = false };
        change?.Invoke(options);
        return options;
    }

    private static AgentRunInputs Signals(string key = "", string requests = "", bool scanned = true) => new(scanned, true, key, requests);

    private static AgentRunInputs NoScanner => new(false, false, string.Empty, string.Empty);

    /// <summary>The planner with a clock and a state, the way the scheduler drives it.</summary>
    private sealed class Sim(AgentRunOptions options, DateTimeOffset? start = null)
    {
        public AgentRunPlanner Planner { get; } = new(options);

        public AgentRunState State { get; private set; } = new();

        public DateTimeOffset Now { get; private set; } = start ?? T0;

        public List<string> Notices { get; } = [];

        public List<ExpiredRun> Expired { get; } = [];

        public void Advance(TimeSpan by) => Now += by;

        public IReadOnlyList<AgentRun> Tick(AgentRunInputs inputs)
        {
            var plan = Planner.Tick(State, inputs, Now);
            State = plan.State;
            Notices.AddRange(plan.Notices);
            Expired.AddRange(plan.Expired);
            return plan.Dispatch;
        }

        public AgentRun Single(AgentRunInputs inputs) => Assert.Single(Tick(inputs));

        public AgentRun? Claim(AgentRun run, AgentRunKind? kind = null)
        {
            var (state, claimed) = Planner.Claim(State, run.Id, kind ?? run.Kind, Now);
            State = state;
            return claimed;
        }

        public AgentRun? Complete(AgentRun run, AgentRunOutcome outcome, AgentRunKind? kind = null)
        {
            var (state, ended) = Planner.Complete(State, run.Id, kind ?? run.Kind, outcome, Now);
            State = state;
            return ended;
        }

        /// <summary>The job claims the run and reports how it ended.</summary>
        public void Finish(AgentRun run, AgentRunOutcome outcome)
        {
            Assert.NotNull(Claim(run));
            Assert.NotNull(Complete(run, outcome));
        }
    }

    [Fact]
    public void SignalDriven_WaitsForTheFirstScan_ThenReviewsAtOnce_AndEveryHeartbeat()
    {
        var sim = new Sim(Options());

        Assert.Empty(sim.Tick(Signals(scanned: false)));
        var first = sim.Single(Signals());
        Assert.Equal((AgentRunKind.Trader, AgentRunPurpose.Heartbeat), (first.Kind, first.Purpose));
        Assert.Empty(sim.Tick(Signals())); // one run at a time
        sim.Advance(TimeSpan.FromMinutes(5));
        sim.Finish(first, AgentRunOutcome.Done);

        sim.Advance(TimeSpan.FromMinutes(239));
        Assert.Empty(sim.Tick(Signals()));
        sim.Advance(TimeSpan.FromMinutes(1)); // 240 minutes after the run ended
        Assert.Equal(AgentRunPurpose.Heartbeat, sim.Single(Signals()).Purpose);
    }

    [Fact]
    public void NewSignals_RunOnce_AfterTheMinimumGap_AndOnlyRemovalsDoNot()
    {
        var sim = new Sim(Options());
        sim.Finish(sim.Single(Signals()), AgentRunOutcome.Done); // the first heartbeat, at T0

        sim.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(sim.Tick(Signals("buy:SOL-EUR"))); // within 15 minutes of the last run
        sim.Advance(TimeSpan.FromMinutes(10));
        var signal = sim.Single(Signals("buy:SOL-EUR"));
        Assert.Equal((AgentRunPurpose.Signals, "buy:SOL-EUR"), (signal.Purpose, signal.Key));
        sim.Finish(signal, AgentRunOutcome.Done);

        sim.Advance(TimeSpan.FromMinutes(20));
        Assert.Empty(sim.Tick(Signals("buy:SOL-EUR"))); // handled
        var more = sim.Single(Signals("buy:SOL-EUR|sell:BTC-EUR:stop"));
        sim.Finish(more, AgentRunOutcome.Done);

        sim.Advance(TimeSpan.FromMinutes(20));
        Assert.Empty(sim.Tick(Signals("sell:BTC-EUR:stop"))); // only a removal
        Assert.Equal("sell:BTC-EUR:stop", sim.State.LastKey);
        Assert.Equal(AgentRunPurpose.Signals, sim.Single(Signals("buy:SOL-EUR|sell:BTC-EUR:stop")).Purpose); // came back: new
    }

    [Fact]
    public void FailedRuns_AreRetriedAfterTheGap_NotRunIsRefunded_AndTheCapHoldsUntilMidnight()
    {
        var sim = new Sim(Options(o => o.MaxRunsPerDay = 2));

        var first = sim.Single(Signals("buy:SOL-EUR")); // something new at once: no heartbeat first
        Assert.Equal(AgentRunPurpose.Signals, first.Purpose);
        sim.Finish(first, AgentRunOutcome.Failed);
        sim.Advance(TimeSpan.FromMinutes(14));
        Assert.Empty(sim.Tick(Signals("buy:SOL-EUR")));
        sim.Advance(TimeSpan.FromMinutes(1));
        var second = sim.Single(Signals("buy:SOL-EUR")); // still unhandled
        Assert.Equal(2, sim.State.Runs);
        sim.Finish(second, AgentRunOutcome.NotRun);
        Assert.Equal((1, 1), (sim.State.Runs, sim.State.Refunds)); // Claude wasn't used: not counted

        sim.Advance(TimeSpan.FromMinutes(15));
        sim.Finish(sim.Single(Signals("buy:SOL-EUR")), AgentRunOutcome.Done);
        sim.Advance(TimeSpan.FromMinutes(15));
        Assert.Empty(sim.Tick(Signals("buy:SOL-EUR|buy:ETH-EUR"))); // capped
        Assert.Empty(sim.Tick(Signals("buy:SOL-EUR|buy:ETH-EUR")));
        Assert.Single(sim.Notices); // said once

        sim.Advance(new DateTimeOffset(2026, 10, 27, 0, 0, 30, TimeSpan.Zero) - sim.Now);
        Assert.Equal("buy:SOL-EUR|buy:ETH-EUR", sim.Single(Signals("buy:SOL-EUR|buy:ETH-EUR")).Key);
        Assert.Equal(1, sim.State.Runs);
    }

    [Fact]
    public void Refunds_AreCapped_AndBelongToTheDayTheRunWasCounted()
    {
        var sim = new Sim(Options(), new DateTimeOffset(2026, 10, 26, 23, 55, 0, TimeSpan.Zero));
        var late = sim.Single(Signals());
        sim.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(sim.Claim(late));
        sim.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(sim.Complete(late, AgentRunOutcome.NotRun)); // reported on the next day
        Assert.Equal((new DateOnly(2026, 10, 27), 0, 0), (sim.State.Day, sim.State.Runs, sim.State.Refunds));

        sim = new Sim(Options(o => o.MinGapMinutes = 1));
        for (var i = 0; i < AgentRunOptions.MaxRefundsPerDay + 2; i++)
        {
            sim.Finish(sim.Single(Signals("buy:SOL-EUR")), AgentRunOutcome.NotRun);
            sim.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal((2, AgentRunOptions.MaxRefundsPerDay), (sim.State.Runs, sim.State.Refunds)); // past the ceiling, runs count
    }

    [Fact]
    public void RequestRuns_GoFirst_HaveTheirOwnCap_RetryAfterFiveMinutes_AndRunOncePerRequest()
    {
        var sim = new Sim(Options(o => o.MaxRequestRunsPerDay = 2));

        var both = sim.Tick(Signals("buy:SOL-EUR", requests: "manual:a"));
        Assert.Equal(new[] { AgentRunKind.Requests, AgentRunKind.Trader }, both.Select(r => r.Kind));
        var first = both[0];
        Assert.Equal((AgentRunPurpose.Requests, "manual:a"), (first.Purpose, first.Key));
        Assert.Equal((1, 1), (sim.State.Runs, sim.State.RequestRuns)); // counted apart
        sim.Finish(both[1], AgentRunOutcome.Done);

        sim.Finish(first, AgentRunOutcome.Failed);
        sim.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(sim.Tick(Signals("buy:SOL-EUR", requests: "manual:a")));
        sim.Advance(TimeSpan.FromMinutes(1));
        var second = sim.Single(Signals("buy:SOL-EUR", requests: "manual:a")); // no minimum gap for the owner
        sim.Finish(second, AgentRunOutcome.NotRun);
        Assert.Equal(1, sim.State.RequestRuns);

        sim.Advance(TimeSpan.FromMinutes(5));
        sim.Finish(sim.Single(Signals("buy:SOL-EUR", requests: "manual:a")), AgentRunOutcome.Done);
        Assert.Empty(sim.Tick(Signals("buy:SOL-EUR", requests: "manual:a"))); // answered: not run again
        Assert.Empty(sim.Tick(Signals("buy:SOL-EUR", requests: "manual:a|manual:b"))); // capped at 2
        Assert.Single(sim.Notices);
        Assert.Equal(1, sim.State.Runs); // the strategy's runs untouched
    }

    [Fact]
    public void WithoutTheScanner_RunsOnAFixedCadence_WithoutCatchingUp()
    {
        var sim = new Sim(Options());

        var first = sim.Single(NoScanner);
        Assert.Equal(AgentRunPurpose.Scheduled, first.Purpose);
        sim.Advance(TimeSpan.FromMinutes(5));
        sim.Finish(first, AgentRunOutcome.Done);
        sim.Advance(TimeSpan.FromMinutes(54));
        Assert.Empty(sim.Tick(NoScanner));
        sim.Advance(TimeSpan.FromMinutes(1));
        sim.Finish(sim.Single(NoScanner), AgentRunOutcome.Done); // at 60 minutes

        sim.Advance(TimeSpan.FromMinutes(140)); // 200 minutes: the runs at 120 and 180 were missed
        sim.Finish(sim.Single(NoScanner), AgentRunOutcome.Done);
        Assert.Empty(sim.Tick(NoScanner)); // one run, not three
        Assert.Equal(sim.Now + TimeSpan.FromMinutes(60), sim.State.NextScheduled);
    }

    [Fact]
    public void SwitchingToAStrategyWithoutTheScanner_WaitsAnIntervalAfterTheLastRun()
    {
        var sim = new Sim(Options());
        sim.Finish(sim.Single(Signals()), AgentRunOutcome.Done);
        sim.Advance(TimeSpan.FromMinutes(20));

        Assert.Empty(sim.Tick(NoScanner));
        Assert.Equal(T0 + TimeSpan.FromMinutes(60), sim.State.NextScheduled);
        sim.Advance(TimeSpan.FromMinutes(40));
        Assert.Equal(AgentRunPurpose.Scheduled, sim.Single(NoScanner).Purpose);
    }

    [Fact]
    public void Analyst_RunsAtStart_KeepsItsCadence_AndRetriesARunThatCouldNotUseClaude()
    {
        var sim = new Sim(Options(o => o.Analyst = true));
        var idle = Signals(scanned: false); // no trader runs

        var first = sim.Single(idle);
        Assert.Equal((AgentRunKind.Analyst, AgentRunPurpose.Analyst), (first.Kind, first.Purpose));
        sim.Advance(TimeSpan.FromMinutes(8));
        sim.Finish(first, AgentRunOutcome.Done);
        Assert.Equal(T0 + TimeSpan.FromHours(6), sim.State.NextAnalyst); // from the slot, not from the end of the run

        sim.Advance(T0 + TimeSpan.FromHours(6) - TimeSpan.FromMinutes(1) - sim.Now);
        Assert.Empty(sim.Tick(idle));
        sim.Advance(TimeSpan.FromMinutes(1));
        var second = sim.Single(idle);
        sim.Finish(second, AgentRunOutcome.NotRun);
        Assert.Equal(sim.Now + TimeSpan.FromMinutes(30), sim.State.NextAnalyst);

        sim.Advance(TimeSpan.FromMinutes(30));
        sim.Finish(sim.Single(idle), AgentRunOutcome.Done);
        Assert.Equal(T0 + TimeSpan.FromHours(12), sim.State.NextAnalyst); // the missed slot isn't made up twice
        Assert.Equal(0, sim.State.AnalystRetries);

        sim.Advance(TimeSpan.FromHours(20)); // a long outage: one run, then a fresh cadence
        var late = sim.Single(idle);
        sim.Finish(late, AgentRunOutcome.Failed);
        Assert.Equal(sim.Now + TimeSpan.FromHours(6), sim.State.NextAnalyst);
    }

    [Fact]
    public void AnalystRetries_NeverAddUpToMoreThanOneInterval()
    {
        var sim = new Sim(Options(o => o.Analyst = true));
        var idle = Signals(scanned: false);

        for (var i = 0; i < 12; i++)
        {
            sim.Finish(sim.Single(idle), AgentRunOutcome.NotRun);
            sim.Advance(TimeSpan.FromMinutes(30));
        }

        Assert.Equal(12, sim.State.AnalystRetries);
        sim.Finish(sim.Single(idle), AgentRunOutcome.NotRun); // the 13th attempt, 6 hours in
        Assert.Equal(T0 + TimeSpan.FromMinutes(330) + TimeSpan.FromHours(6), sim.State.NextAnalyst); // back to the normal slot, from the last retry
        Assert.Equal(0, sim.State.AnalystRetries);
    }

    [Fact]
    public void RunsNeverClaimed_CountAsNotRun_AndRunsNeverReported_AsFailed()
    {
        var sim = new Sim(Options());

        var heartbeat = sim.Single(Signals());
        sim.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(sim.Tick(Signals()));
        Assert.Equal((heartbeat.Id, AgentRunOutcome.NotRun), (sim.Expired.Single().Run.Id, sim.Expired.Single().Outcome));
        Assert.Equal((0, 1), (sim.State.Runs, sim.State.Refunds));
        Assert.Null(sim.Claim(heartbeat)); // its message expired with it

        var request = sim.Single(Signals(requests: "manual:a"));
        Assert.NotNull(sim.Claim(request));
        sim.Advance(TimeSpan.FromMinutes(24));
        Assert.Empty(sim.Tick(Signals(requests: "manual:a")));
        sim.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(sim.Tick(Signals(requests: "manual:a")));
        Assert.Equal(AgentRunOutcome.Failed, sim.Expired.Last().Outcome);
        Assert.Equal(1, sim.State.RequestRuns); // it may have used Claude
        Assert.Null(sim.Complete(request, AgentRunOutcome.Done)); // too late to report
        sim.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(AgentRunKind.Requests, sim.Single(Signals(requests: "manual:a")).Kind); // retried
    }

    [Fact]
    public void ARun_IsClaimedOnce_ByItsOwnJob_AndReportedOnce()
    {
        var sim = new Sim(Options());
        var run = sim.Single(Signals());

        Assert.Null(sim.Claim(run, AgentRunKind.Analyst));
        Assert.Null(sim.Complete(run, AgentRunOutcome.Done)); // not claimed yet
        Assert.NotNull(sim.Claim(run));
        Assert.Null(sim.Claim(run)); // a repeated message
        Assert.Null(sim.Complete(run, AgentRunOutcome.Done, AgentRunKind.Requests));
        Assert.NotNull(sim.Complete(run, AgentRunOutcome.Done));
        Assert.Null(sim.Complete(run, AgentRunOutcome.NotRun)); // no second refund
        Assert.Equal((1, 0), (sim.State.Runs, sim.State.Refunds));

        sim.Advance(TimeSpan.FromHours(4));
        var stale = sim.Single(Signals());
        sim.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(sim.Claim(stale)); // queued too long ago
    }

    [Fact]
    public void AnUnsentRun_IsNotRun()
    {
        var sim = new Sim(Options());
        var run = sim.Single(Signals("buy:SOL-EUR"));
        var state = sim.Planner.Unsent(sim.State, run, sim.Now);

        Assert.Empty(state.InFlight);
        Assert.Equal((0, 1), (state.Runs, state.Refunds));
        Assert.Same(state, sim.Planner.Unsent(state, run, sim.Now)); // once only
    }

    [Theory]
    [InlineData("buy:SOL-EUR", "", true)]
    [InlineData("buy:SOL-EUR", "buy:SOL-EUR", false)]
    [InlineData("buy:SOL-EUR", "buy:SOL-EUR|sell:BTC-EUR", false)]
    [InlineData("buy:SOL-EUR|sell:BTC-EUR", "buy:SOL-EUR", true)]
    [InlineData("", "buy:SOL-EUR", false)]
    [InlineData("buy:SOL", "buy:SOL-EUR", true)]
    public void HasNew_ComparesWholeItems(string key, string previous, bool expected) =>
        Assert.Equal(expected, AgentRunPlanner.HasNew(key, previous));

    [Fact]
    public void RunIds_AreOrderedSafeLabels()
    {
        var at = new DateTimeOffset(2026, 10, 26, 14, 5, 0, TimeSpan.Zero);
        var trader = AgentRun.NewId(AgentRunKind.Trader, at);
        var analyst = AgentRun.NewId(AgentRunKind.Analyst, at);

        Assert.Matches("^run-20261026-140500-[0-9a-f]{6}$", trader);
        Assert.Matches("^analyst-20261026-140500-[0-9a-f]{6}$", analyst);
        Assert.True(ToolCallContext.IsSafeLabel(trader) && ToolCallContext.IsSafeLabel(analyst));
        Assert.NotEqual(trader, AgentRun.NewId(AgentRunKind.Trader, at));
    }

    [Fact]
    public void Options_AreValidated()
    {
        static AgentRunOptions Queue(Action<AgentRunOptions>? change = null) => Options(o =>
        {
            o.Mode = AgentRunMode.Queue;
            o.Queues.ServiceUri = "https://cixruns.queue.core.windows.net";
            change?.Invoke(o);
        });

        new AgentRunOptions().Validate(ToolServerAuth.Loopback, durableState: false); // Off: Compose as it is
        Queue().Validate(ToolServerAuth.Entra, durableState: true);
        Queue(o => o.Queues.ManagedIdentityClientId = Guid.NewGuid().ToString()).Validate(ToolServerAuth.Entra, true);

        Assert.Throws<InvalidOperationException>(() => Queue().Validate(ToolServerAuth.Loopback, true));
        Assert.Throws<InvalidOperationException>(() => Queue().Validate(ToolServerAuth.Entra, false));
        Assert.Throws<InvalidOperationException>(() => Queue(o => o.Queues.ServiceUri = null).Validate(ToolServerAuth.Entra, true));
        Assert.Throws<InvalidOperationException>(() => Queue(o => o.Queues.ServiceUri = "http://cixruns.queue.core.windows.net").Validate(ToolServerAuth.Entra, true));
        Assert.Throws<InvalidOperationException>(() => Queue(o => o.Queues.ServiceUri = "https://cixruns.queue.core.windows.net/runs-trader").Validate(ToolServerAuth.Entra, true));
        Assert.Throws<InvalidOperationException>(() => Queue(o => o.Queues.Trader = "Runs_Trader").Validate(ToolServerAuth.Entra, true));
        Assert.Throws<InvalidOperationException>(() => Queue(o => o.Queues.Analyst = "runs-trader").Validate(ToolServerAuth.Entra, true));
        Assert.Throws<InvalidOperationException>(() => Queue(o => o.Queues.ManagedIdentityClientId = "engine").Validate(ToolServerAuth.Entra, true));
        Assert.Throws<InvalidOperationException>(() => Options(o => o.AnalystIntervalHours = 13).Validate(ToolServerAuth.Loopback, false));
        Assert.Throws<InvalidOperationException>(() => Options(o => o.PollSeconds = 1).Validate(ToolServerAuth.Loopback, false));
        Assert.Throws<InvalidOperationException>(() => Options(o => o.MaxRunsPerDay = 0).Validate(ToolServerAuth.Loopback, false));
        Assert.Throws<InvalidOperationException>(() => Options(o => o.AnalystRetryMinutes = 400).Validate(ToolServerAuth.Loopback, false));
    }
}
