using System.Globalization;
using System.Text.Json.Nodes;
using Cix.Engine.Journal;
using Cix.Engine.Manual;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>The owner's Pause and Resume buttons (ADR-0018): requests the engine turns into the kill switch, in either mode.</summary>
public sealed class TradingControlTests
{
    private static readonly string LiveMode = TradingMode.Live.ToString().ToLowerInvariant();

    private sealed class Setup
    {
        public Setup(TradingMode mode = TradingMode.Paper, FixedLease? lease = null)
        {
            Rig = new Rig();
            Service = new TradingControlService(
                Requests, Rig.Execution, Rig.KillSwitch, new TradingContext(mode), Rig.Time, NullLogger<TradingControlService>.Instance, lease);
        }

        public Rig Rig { get; }

        public InMemoryManualRequestStore Requests { get; } = new("ok");

        public TradingControlService Service { get; }

        /// <summary>A Pause or Resume request as the dashboard creates it, <paramref name="minute"/> minutes after T0.</summary>
        public void Request(string id, string action, int minute, string mode = "paper", string? note = null)
        {
            var at = Rig.T0.AddMinutes(minute);
            Requests.Add(new JsonObject
            {
                ["id"] = id, ["day"] = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["createdAt"] = at.ToString("O", CultureInfo.InvariantCulture),
                ["type"] = TradingControlService.RequestType, ["action"] = action, ["note"] = note,
                ["requestedBy"] = "owner", ["tradingMode"] = mode, ["status"] = "new",
            });
        }

        public string? Status(string id) => Requests.Find(id)?.Document["status"]?.GetValue<string>();

        public string? Detail(string id) => Requests.Find(id)?.Document["events"]?.AsArray().Last()?["detail"]?.GetValue<string>();

        public Task<int> Poll() => Service.PollOnceAsync(CancellationToken.None);

        public void At(int minute) => Rig.Time.Advance(Rig.T0.AddMinutes(minute) - Rig.Time.GetUtcNow());

        public IReadOnlyList<JournalEntry> KillSwitchEntries => Rig.Journal.Entries.Where(e => e.Kind == "kill-switch").ToList();
    }

    [Fact]
    public async Task Pause_EngagesTheKillSwitchAsTheOwner_WithTheNote()
    {
        var setup = new Setup();
        setup.Request("p1", TradingControlService.Pause, 0, note: "news looks bad");
        setup.At(1);

        Assert.Equal(1, await setup.Poll());

        Assert.True(setup.Rig.KillSwitch.IsEngaged);
        Assert.Equal("owner", setup.Rig.KillSwitch.State.By);
        Assert.Equal("Paused by the owner: news looks bad", setup.Rig.KillSwitch.State.Reason);
        Assert.Equal("accepted", setup.Status("p1"));
        var entry = Assert.Single(setup.KillSwitchEntries);
        Assert.Equal("owner", entry.Actor);
    }

    [Fact]
    public async Task Resume_ReleasesAHaltThatWasThereWhenTheOwnerPressedIt()
    {
        var setup = new Setup();
        await setup.Rig.Execution.EngageKillSwitchAsync("No engine state; the owner must confirm.", "engine", CancellationToken.None); // at T0
        setup.Request("r1", TradingControlService.Resume, 2);
        setup.At(3);

        await setup.Poll();

        Assert.False(setup.Rig.KillSwitch.IsEngaged);
        Assert.Equal("accepted", setup.Status("r1"));
        Assert.Contains(setup.KillSwitchEntries, e => e.Actor == "owner" && e.Data?["action"] == "released");
    }

    [Fact]
    public async Task Resume_LeavesAHaltThatEngagedAfterTheOwnerPressedIt()
    {
        var setup = new Setup();
        setup.Request("r1", TradingControlService.Resume, 0);
        setup.At(1);
        await setup.Rig.Execution.EngageKillSwitchAsync("Daily loss limit reached.", "safety-monitor", CancellationToken.None); // after the request

        await setup.Poll();

        Assert.True(setup.Rig.KillSwitch.IsEngaged);
        Assert.Equal("safety-monitor", setup.Rig.KillSwitch.State.By);
        Assert.Equal(ManualRequestStatus.Rejected, setup.Status("r1"));
        Assert.Contains("halted again after you pressed Resume", setup.Detail("r1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNewestRequestActs_OlderOnesAreSuperseded()
    {
        var setup = new Setup();
        await setup.Rig.Execution.EngageKillSwitchAsync("Paused.", "owner", CancellationToken.None); // at T0
        setup.Request("p1", TradingControlService.Pause, 1);
        setup.Request("r1", TradingControlService.Resume, 2);
        setup.At(3);

        Assert.Equal(2, await setup.Poll());

        Assert.False(setup.Rig.KillSwitch.IsEngaged);
        Assert.Equal("accepted", setup.Status("r1"));
        Assert.Equal("superseded", setup.Status("p1"));
    }

    [Fact]
    public async Task StaleMalformedOrOtherModeRequests_ChangeNothing()
    {
        var setup = new Setup();
        setup.Request("old", TradingControlService.Pause, 0);
        setup.Request("odd", "sell-everything", 9);
        setup.Request("live", TradingControlService.Pause, 9, mode: LiveMode);
        setup.Requests.Add(new JsonObject
        {
            ["id"] = "forged", ["day"] = "2026-10-22", ["createdAt"] = Rig.T0.AddMinutes(9).ToString("O", CultureInfo.InvariantCulture),
            ["type"] = TradingControlService.RequestType, ["action"] = TradingControlService.Pause, ["tradingMode"] = "paper", ["status"] = "accepted",
        });
        setup.At(10);

        await setup.Poll();

        Assert.False(setup.Rig.KillSwitch.IsEngaged);
        Assert.Equal(ManualRequestStatus.Expired, setup.Status("old"));
        Assert.Equal(ManualRequestStatus.Invalid, setup.Status("odd"));
        Assert.Equal("new", setup.Status("live")); // the live engine's to take, not paper's
        Assert.Equal("accepted", setup.Status("forged")); // created with a status: never read as new, never changed
        Assert.Empty(setup.KillSwitchEntries);
    }

    [Fact]
    public async Task ALiveEngineTakesLiveRequestsOnly()
    {
        var setup = new Setup(TradingMode.Live);
        setup.Request("paper", TradingControlService.Pause, 0);
        setup.Request("live", TradingControlService.Pause, 0, mode: LiveMode);
        setup.At(1);

        await setup.Poll();

        Assert.True(setup.Rig.KillSwitch.IsEngaged);
        Assert.Equal("accepted", setup.Status("live"));
        Assert.Equal("new", setup.Status("paper"));
    }

    [Fact]
    public async Task WithoutTheLease_NothingIsTaken()
    {
        var setup = new Setup(lease: new FixedLease(false));
        setup.Request("p1", TradingControlService.Pause, 0);

        Assert.Equal(0, await setup.Poll());

        Assert.False(setup.Rig.KillSwitch.IsEngaged);
        Assert.Equal("new", setup.Status("p1"));
    }

    [Fact]
    public async Task ARequestActsOnce_EvenWhenItsDocumentCouldNotBeUpdated()
    {
        var setup = new Setup();
        setup.Request("p1", TradingControlService.Pause, 0);
        setup.At(1);
        setup.Requests.FailWrites = true;

        await setup.Poll();
        Assert.True(setup.Rig.KillSwitch.IsEngaged);
        Assert.Equal("new", setup.Status("p1"));

        // The owner resumes another way; the old Pause must not halt trading again when its document is finally updated.
        await setup.Rig.Execution.ReleaseKillSwitchAsync("owner");
        setup.Requests.FailWrites = false;
        await setup.Poll();

        Assert.False(setup.Rig.KillSwitch.IsEngaged);
        Assert.Equal("accepted", setup.Status("p1"));
        Assert.Single(setup.KillSwitchEntries, e => e.Data?["action"] == "engaged");
    }
}
