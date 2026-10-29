using System.Globalization;
using System.Text.Json.Nodes;
using Cix.Engine.AgentRuns;
using Cix.Engine.Manual;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>The owner's Run now button: requests the engine turns into an analyst run outside the schedule.</summary>
public sealed class AnalystRunRequestTests
{
    private sealed class Setup
    {
        public Setup(TradingMode mode = TradingMode.Paper, bool scheduler = true, bool schedulerLease = true)
        {
            Runs = new AgentRunSchedulerTests.Setup(analyst: true, lease: schedulerLease);
            Service = new AnalystRunRequestService(
                Requests, new TradingContext(mode), Runs.Time, NullLogger<AnalystRunRequestService>.Instance, scheduler ? Runs.Scheduler : null);
        }

        public AgentRunSchedulerTests.Setup Runs { get; }

        public InMemoryManualRequestStore Requests { get; } = new("ok");

        public AnalystRunRequestService Service { get; }

        /// <summary>A Run now request as the dashboard creates it, <paramref name="minutesAgo"/> minutes before the clock.</summary>
        public void Request(string id, int minutesAgo = 0, string mode = "paper", string? day = null)
        {
            var at = Runs.Time.GetUtcNow().AddMinutes(-minutesAgo);
            Requests.Add(new JsonObject
            {
                ["id"] = id, ["day"] = day ?? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["createdAt"] = at.ToString("O", CultureInfo.InvariantCulture),
                ["type"] = AnalystRunRequestService.RequestType, ["requestedBy"] = "owner", ["tradingMode"] = mode, ["status"] = "new",
            });
        }

        public string? Status(string id) => Requests.Find(id)?.Document["status"]?.GetValue<string>();

        public string? Detail(string id) => Requests.Find(id)?.Document["events"]?.AsArray().Last()?["detail"]?.GetValue<string>();

        public Task<int> Poll() => Service.PollOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARequest_QueuesAnAnalystRunAtOnce_AndASecondOneIsRefusedWhileItIsInFlight()
    {
        var setup = new Setup();
        setup.Request("run-now-0001");
        setup.Request("run-now-0002");

        Assert.Equal(2, await setup.Poll());

        var sent = Assert.Single(setup.Runs.Queue.Sent);
        Assert.Equal((AgentRunKind.Analyst, AgentRunPurpose.Owner), (sent.Run.Kind, sent.Run.Purpose));
        Assert.True(sent.SavedFirst);
        Assert.Equal("accepted", setup.Status("run-now-0001"));
        Assert.Equal($"Analyst run {sent.Run.Id} queued; its market view usually arrives within 10 minutes. The next scheduled run is 6 hours after this one.",
            setup.Detail("run-now-0001"));
        Assert.Equal(ManualRequestStatus.Rejected, setup.Status("run-now-0002"));
        Assert.Equal("The analyst is already queued to run; its view arrives a few minutes after it starts.", setup.Detail("run-now-0002"));
        var queued = Assert.Single(setup.Runs.Steps("queued"));
        Assert.Equal(AgentRunPurpose.Owner, queued.Data!["purpose"]);
        Assert.Equal("analyst", queued.Data["job"]);

        Assert.Equal(0, await setup.Poll()); // answered: nothing new
    }

    [Fact]
    public async Task TheScheduledRun_CountsTowardTheCooldown()
    {
        var setup = new Setup();
        await setup.Runs.Scheduler.PollOnceAsync(CancellationToken.None); // the analyst's run at start (and the trader's)
        var run = setup.Runs.Queue.Sent.Single(s => s.Run.Kind == AgentRunKind.Analyst).Run;
        Assert.Equal(AgentRunAnswer.Yes, (await setup.Runs.Scheduler.ClaimAsync(run.Id, "analyst", null, CancellationToken.None)).Answer);
        setup.Runs.Time.Advance(TimeSpan.FromMinutes(8));
        Assert.Equal(AgentRunAnswer.Yes, await setup.Runs.Scheduler.CompleteAsync(run.Id, "analyst", AgentRunOutcome.Done, CancellationToken.None));

        setup.Request("run-now-0003");
        await setup.Poll();

        Assert.Equal(ManualRequestStatus.Rejected, setup.Status("run-now-0003"));
        Assert.Equal("The analyst was queued 8 min ago; ask again in 22 min (30 min between runs, to spare the Claude limit).", setup.Detail("run-now-0003"));
        Assert.Single(setup.Runs.Queue.Sent, s => s.Run.Kind == AgentRunKind.Analyst);
    }

    [Fact]
    public async Task WithoutTheScheduler_ARequestIsRefused_AndSaysWhy()
    {
        var setup = new Setup(scheduler: false);
        setup.Request("run-now-0004");

        Assert.Equal(1, await setup.Poll());

        Assert.Equal(ManualRequestStatus.Rejected, setup.Status("run-now-0004"));
        Assert.Contains("AgentRuns:Mode Off", setup.Detail("run-now-0004"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileTheSchedulerCannotDecide_TheRequestStaysNew()
    {
        var setup = new Setup(schedulerLease: false);
        setup.Request("run-now-0005");

        Assert.Equal(0, await setup.Poll());
        Assert.Equal("new", setup.Status("run-now-0005"));

        setup.Runs.Lease.IsHeld = true;
        Assert.Equal(1, await setup.Poll());
        Assert.Equal("accepted", setup.Status("run-now-0005"));
    }

    [Fact]
    public async Task OldMalformedAndOtherModeRequests_QueueNothing()
    {
        var setup = new Setup();
        setup.Request("run-now-0006", minutesAgo: 6);
        setup.Request("run-now-0007", day: "2026-10-25"); // not the day of its createdAt
        setup.Request("run-now-0008", mode: "live"); // the live engine's
        setup.Request("bad id!");

        Assert.Equal(3, await setup.Poll());

        Assert.Equal(ManualRequestStatus.Expired, setup.Status("run-now-0006"));
        Assert.Equal(ManualRequestStatus.Invalid, setup.Status("run-now-0007"));
        Assert.Equal("new", setup.Status("run-now-0008"));
        Assert.Equal(ManualRequestStatus.Invalid, setup.Status("bad id!"));
        Assert.Empty(setup.Runs.Queue.Sent);

        var live = new Setup(TradingMode.Live);
        live.Request("run-now-0009", mode: "live");
        await live.Poll();
        Assert.Equal("accepted", live.Status("run-now-0009"));
    }

    [Fact]
    public async Task AnAnswerWhoseDocumentCannotBeUpdated_IsKept_SoTheSchedulerIsNotAskedAgain()
    {
        var setup = new Setup();
        setup.Request("run-now-0010");
        setup.Requests.FailWrites = true;

        Assert.Equal(1, await setup.Poll());
        Assert.Equal("new", setup.Status("run-now-0010"));

        setup.Requests.FailWrites = false;
        Assert.Equal(1, await setup.Poll());
        Assert.Equal("accepted", setup.Status("run-now-0010")); // its own answer, not "already queued"
        Assert.Single(setup.Runs.Queue.Sent);
    }

    [Fact]
    public void WithAgentRunsOff_TheServiceIsRegistered_WithoutAScheduler()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(new TradingContext(TradingMode.Paper))
            .AddSingleton<IManualRequestStore>(new InMemoryManualRequestStore("ok"))
            .AddCixAgentRuns(new AgentRunOptions(), Tools.ToolServerAuth.Loopback, durableState: false);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<AnalystRunRequestService>());
        Assert.Null(provider.GetService<AgentRunScheduler>());
    }
}
