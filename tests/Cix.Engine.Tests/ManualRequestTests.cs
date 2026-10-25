using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cix.Domain;
using Cix.Engine.Execution;
using Cix.Engine.Manual;
using Cix.Engine.Strategy;
using Cix.Engine.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Cix.Engine.Tests;

/// <summary>The owner's manual requests from the dashboard (#63, ADR-0011): validated, executed once, through the risk gate.</summary>
public sealed class ManualRequestTests
{
    private const string Btc = "BTC-EUR";
    private const string Why = "Momentum is fading after the jump; taking half.";

    /// <summary>A trading spec: place_order allowed, so the manual tools are too.</summary>
    private static readonly StrategySpec Trading = ToolRig.Observer with { Name = "trader", AllowedTools = ["get_features", "place_order"] };

    private sealed class ManualRig
    {
        public ManualRig(decimal btc = 0.001m, StrategySpec? spec = null, ManualRequestOptions? options = null, TradingMode mode = TradingMode.Paper)
        {
            Tools = new ToolRig(spec ?? Trading, btc: btc);
            Tools.Header(ToolServerOptions.AgentHeader, "trader");
            Tools.Header(ToolServerOptions.RunHeader, "run-9");
            Service = new ManualRequestService(
                Store, Tools.Rig.Execution, Tools.Rig.State, Rig.Limits, Tools.Journal, Tools.Specs, options ?? new ManualRequestOptions(),
                new TradingContext(mode), Tools.Rig.Time, NullLogger<ManualRequestService>.Instance, Tools.Views);
            Manual = new ManualTools(Tools.Context, Service, Tools.Rig.Execution, Tools.Rig.State);
        }

        public ToolRig Tools { get; }
        public InMemoryManualRequestStore Store { get; } = new("ok");
        public ManualRequestService Service { get; }
        public ManualTools Manual { get; }
        public MutableTime Time => Tools.Rig.Time;

        public string Status(string id) => Store.Find(id)!.Status;

        public string? Outcome(string id, string name) => ManualRequestDocument.Outcome(Store.Find(id)!.Document, name);

        public IReadOnlyList<string> History(string id) =>
            ((JsonArray)Store.Find(id)!.Document["events"]!).Select(e => e!["status"]!.GetValue<string>()).ToList();

        public Task<int> Poll() => Service.PollOnceAsync(CancellationToken.None);

        /// <summary>A request as the dashboard writes it: numbers as JSON numbers unless <paramref name="numbersAsText"/>.</summary>
        public JsonObject Request(
            string id, string side, string mode, decimal? portion = null, decimal? quoteAmount = null, TimeSpan? age = null, string symbol = Btc,
            bool numbersAsText = false, string? tradingMode = null)
        {
            var at = Time.GetUtcNow() - (age ?? TimeSpan.Zero);
            JsonNode? Number(decimal? value) => value is not { } v ? null : numbersAsText ? JsonValue.Create(v.ToString(CultureInfo.InvariantCulture)) : JsonValue.Create(v);
            var doc = new JsonObject
            {
                ["id"] = id,
                ["day"] = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["type"] = "manual-request",
                ["side"] = side,
                ["mode"] = mode,
                ["symbol"] = symbol,
                ["portion"] = Number(portion),
                ["quoteAmount"] = Number(quoteAmount),
                ["note"] = "Price jumped‮ 8% in ten minutes",
                ["requestedBy"] = "owner",
                ["createdAt"] = at.ToString("O", CultureInfo.InvariantCulture),
                ["status"] = "new",
            };
            if (tradingMode is not null)
                doc["tradingMode"] = tradingMode;
            Assert.True(Store.Add(doc));
            return doc;
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static decimal Dec(string? value) => decimal.Parse(value!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task DirectSell_SellsThePortionAtTheBid_ThroughTheGate_AndRecordsEveryStep()
    {
        var rig = new ManualRig(btc: 0.001m);
        rig.Request("req-direct-1", "sell", "direct", portion: 0.5m, numbersAsText: true);

        await rig.Poll();

        Assert.Equal(ManualRequestStatus.Placed, rig.Status("req-direct-1"));
        Assert.Equal(new[] { "executing", "placed" }, rig.History("req-direct-1"));
        Assert.Equal((0.0005m, 60_000m), (Dec(rig.Outcome("req-direct-1", "requestedSize")), Dec(rig.Outcome("req-direct-1", "requestedPrice"))));
        var clientOrderId = rig.Outcome("req-direct-1", "clientOrderId")!;

        var journal = rig.Tools.Journal.Entries;
        var received = Assert.Single(journal, e => e.Kind == ManualRequestService.Kind && e.Data!["status"] == "received");
        Assert.Equal(("owner", "direct", "sell"), (received.Actor, received.Data!["mode"], received.Data["side"]));
        Assert.DoesNotContain('‮', received.Data["note"]); // format characters can't disguise what the owner reads
        var order = Assert.Single(journal, e => e.Kind == "order");
        Assert.Equal(("engine", ManualRequestService.DirectSource, "req-direct-1", clientOrderId, "placed"),
            (order.Actor, order.Data!["tool"], order.Data["requestId"], order.Data["clientOrderId"], order.Data["status"]));
        Assert.Single(journal, e => e.Kind == ManualRequestService.Kind && e.Data!["status"] == "placed");

        // The reconciler journals the fill; the next settle moves the request to filled.
        await rig.Tools.Rig.Execution.ReconcileAsync(CancellationToken.None);
        rig.Time.Advance(ManualRequestService.SettleInterval);
        await rig.Poll();

        Assert.Equal(ManualRequestStatus.Filled, rig.Status("req-direct-1"));
        Assert.Equal(0.0005m, Dec(rig.Outcome("req-direct-1", "filledSize")));
        var fill = Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "fill");
        Assert.Equal(ManualRequestService.DirectSource, fill.Data!["source"]);
        rig.Tools.Rig.Quote(60_000m, 60_010m);
        Assert.Equal(0.0005m, (await rig.Tools.Rig.Snapshot()).SellableBaseBySymbol![Btc]); // half is left
    }

    [Fact]
    public async Task ARequest_IsExecutedOnce_EvenWhenPolledAgain()
    {
        var rig = new ManualRig(btc: 0.001m);
        rig.Request("req-once-1", "sell", "direct", portion: 1m);

        await rig.Poll();
        await rig.Poll();
        rig.Time.Advance(TimeSpan.FromSeconds(31));
        await rig.Poll();

        Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "order");
    }

    [Theory]
    [InlineData("buy", "direct", null, "5", "BTC-EUR", "agent-assisted only")]
    [InlineData("sell", "direct", "1.5", null, "BTC-EUR", "portion")]
    [InlineData("sell", "direct", "0", null, "BTC-EUR", "portion")]
    [InlineData("sell", "direct", "0.5", null, "ETH-EUR", "allowlist")]
    [InlineData("sell", "market", "0.5", null, "BTC-EUR", "mode")]
    [InlineData("buy", "agent", null, null, "BTC-EUR", "quoteAmount")]
    [InlineData("sell", "agent", "0.5", "5", "BTC-EUR", "quoteAmount is for buys")]
    public async Task AnInvalidRequest_IsRefusedWithItsReason_AndNothingIsPlaced(string side, string mode, string? portion, string? quote, string symbol, string reason)
    {
        var rig = new ManualRig();
        rig.Request("req-invalid", side, mode, portion is null ? null : Dec(portion), quote is null ? null : Dec(quote), symbol: symbol);

        await rig.Poll();

        Assert.Equal(ManualRequestStatus.Invalid, rig.Status("req-invalid"));
        Assert.Contains(reason, rig.Outcome("req-invalid", "reason"), StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order");
        Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == ManualRequestService.Kind && e.Data!["status"] == "invalid");
    }

    [Fact]
    public async Task MissingOrMalformedFields_AreInvalid_NotAnError()
    {
        var rig = new ManualRig();
        var doc = rig.Request("req-bad-fields", "sell", "direct", portion: 0.5m);
        var broken = (JsonObject)doc.DeepClone();
        broken["id"] = "req-bad-fields-2";
        broken["portion"] = new JsonArray(1, 2);
        rig.Store.Add(broken);
        var noTime = (JsonObject)doc.DeepClone();
        noTime["id"] = "req-no-time-1";
        noTime.Remove("createdAt");
        rig.Store.Add(noTime);

        await rig.Poll();

        Assert.Equal(ManualRequestStatus.Invalid, rig.Status("req-bad-fields-2"));
        Assert.Equal(ManualRequestStatus.Invalid, rig.Status("req-no-time-1"));
        Assert.Contains("createdAt", rig.Outcome("req-no-time-1", "reason"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleRequest_Expires_ADuplicateIsRefused_AndTheDailyCapHolds()
    {
        var rig = new ManualRig(options: new ManualRequestOptions { MaxPerDay = 2 });
        rig.Request("req-stale-1", "sell", "direct", portion: 1m, age: TimeSpan.FromMinutes(10));
        rig.Request("req-first-1", "sell", "agent", portion: 1m, age: TimeSpan.FromSeconds(3));
        rig.Request("req-second", "sell", "agent", portion: 0.5m, age: TimeSpan.FromSeconds(2));
        rig.Request("req-buy-01", "buy", "agent", quoteAmount: 10m, age: TimeSpan.FromSeconds(1));
        rig.Request("req-buy-02", "buy", "agent", quoteAmount: 10m);

        await rig.Poll();

        Assert.Equal(ManualRequestStatus.Expired, rig.Status("req-stale-1"));
        Assert.Equal(ManualRequestStatus.AwaitingAgent, rig.Status("req-first-1"));
        Assert.Equal(ManualRequestStatus.Duplicate, rig.Status("req-second"));
        Assert.Equal(ManualRequestStatus.AwaitingAgent, rig.Status("req-buy-01"));
        Assert.Equal(ManualRequestStatus.Rejected, rig.Status("req-buy-02"));
        Assert.Equal("daily-cap", rig.Outcome("req-buy-02", "rule"));
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order");
    }

    [Fact]
    public async Task ARequestForAnotherTradingMode_IsLeftAlone()
    {
        var paper = new ManualRig();
        paper.Request("req-live-01", "sell", "direct", portion: 1m, tradingMode: "live");
        var live = new ManualRig(mode: TradingMode.Live);
        live.Request("req-unmarked", "sell", "direct", portion: 1m); // no tradingMode: paper only

        await paper.Poll();
        await live.Poll();

        Assert.Equal(ManualRequestStatus.New, paper.Status("req-live-01"));
        Assert.Equal(ManualRequestStatus.New, live.Status("req-unmarked"));
        Assert.Empty(paper.Tools.Journal.Entries);
    }

    [Fact]
    public async Task AgentAssistedSell_WaitsForTheTrader_WhoMaySellLessThanAsked()
    {
        var rig = new ManualRig(btc: 0.001m);
        rig.Request("req-agent-1", "sell", "agent", portion: 1m);

        await rig.Poll();

        Assert.Equal(ManualRequestStatus.AwaitingAgent, rig.Status("req-agent-1"));
        Assert.Equal("manual:req-agent-1", rig.Service.AgentKey);
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order"); // nothing until the trader answers

        var listed = Parse(await rig.Manual.GetManualRequests(CancellationToken.None)).GetProperty("requests")[0];
        Assert.Equal(("req-agent-1", "sell", 1m), (listed.GetProperty("requestId").GetString(), listed.GetProperty("side").GetString(), listed.GetProperty("portion").GetDecimal()));
        Assert.Equal(0.001m, listed.GetProperty("position").GetProperty("sellableBase").GetDecimal());
        Assert.Equal(20, listed.GetProperty("minutesLeft").GetInt32());

        var tooMuch = await Assert.ThrowsAsync<McpException>(() => rig.Manual.AnswerManualRequest("req-agent-1", "execute", Why, portion: 1.5m));
        Assert.Contains("portion", tooMuch.Message, StringComparison.Ordinal);
        Assert.Equal(ManualRequestStatus.AwaitingAgent, rig.Status("req-agent-1")); // a bad answer leaves the request waiting

        var answer = Parse(await rig.Manual.AnswerManualRequest("req-agent-1", "execute", Why, portion: 0.5m));

        Assert.Equal("placed", answer.GetProperty("status").GetString());
        Assert.Equal(ManualRequestStatus.Placed, rig.Status("req-agent-1"));
        Assert.Equal(("trader", "run-9", Why), (rig.Outcome("req-agent-1", "decidedBy"), rig.Outcome("req-agent-1", "runId"), rig.Outcome("req-agent-1", "rationale")));
        var order = Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "order");
        Assert.Equal(("trader", ManualRequestService.AgentSource, 0.0005m), (order.Actor, order.Data!["tool"], Dec(order.Data["requestedSize"])));
        Assert.Empty(rig.Service.Pending);
        await Assert.ThrowsAsync<McpException>(() => rig.Manual.AnswerManualRequest("req-agent-1", "execute", Why)); // answered once only
    }

    [Fact]
    public async Task ADecline_IsRecordedWithTheRationale_AndNothingIsPlaced()
    {
        var rig = new ManualRig(btc: 0.001m);
        rig.Request("req-decline", "sell", "agent", portion: 1m);
        await rig.Poll();

        var answer = Parse(await rig.Manual.AnswerManualRequest("req-decline", "decline", "Trend is still up; I'd hold and keep the stop."));

        Assert.Equal("declined", answer.GetProperty("status").GetString());
        Assert.Equal(ManualRequestStatus.Declined, rig.Status("req-decline"));
        Assert.Equal("Trend is still up; I'd hold and keep the stop.", rig.Outcome("req-decline", "rationale"));
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order");
        var decided = Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == ManualRequestService.Kind && e.Data!["status"] == "declined");
        Assert.Equal(("trader", "run-9"), (decided.Actor, decided.RunId));
    }

    [Fact]
    public async Task AgentAssistedBuy_NeedsAStop_ThenPlacesAtTheAskWithItsPlan()
    {
        var rig = new ManualRig(btc: 0m);
        rig.Request("req-buy-1", "buy", "agent", quoteAmount: 30m);
        await rig.Poll();

        var noStop = await Assert.ThrowsAsync<McpException>(() => rig.Manual.AnswerManualRequest("req-buy-1", "execute", Why));
        Assert.Contains("stopLoss", noStop.Message, StringComparison.Ordinal);

        var answer = Parse(await rig.Manual.AnswerManualRequest("req-buy-1", "execute", Why, stopLoss: 59_000m, takeProfit: 62_000m));

        Assert.Equal("placed", answer.GetProperty("status").GetString());
        var order = Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "order");
        Assert.Equal((60_010m, 0.00049991m), (Dec(order.Data!["requestedPrice"]), Dec(order.Data["requestedSize"]))); // 30 EUR at the ask
        var plan = rig.Tools.Rig.Execution.PositionPlanStates[Btc];
        Assert.Equal((59_000m, 62_000m), (plan.StopLoss, plan.TakeProfit));
    }

    [Fact]
    public async Task ManualOrders_StillMeetTheRiskGate()
    {
        var rig = new ManualRig(btc: 0m);
        rig.Request("req-big-01", "buy", "agent", quoteAmount: 1_000m); // above the 40 EUR order cap
        await rig.Poll();

        var answer = Parse(await rig.Manual.AnswerManualRequest("req-big-01", "execute", Why, stopLoss: 59_000m));

        Assert.NotEqual("rejected", answer.GetProperty("status").GetString()); // resized by the gate, not passed through
        var order = Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "order");
        Assert.True(Dec(order.Data!["approvedSize"]) * 60_010m <= Rig.Limits.MaxOrderQuote);
    }

    [Fact]
    public async Task AnUnansweredRequest_Expires()
    {
        var rig = new ManualRig();
        rig.Request("req-wait-1", "sell", "agent", portion: 1m);
        await rig.Poll();

        rig.Time.Advance(TimeSpan.FromMinutes(21));
        await rig.Poll();

        Assert.Equal(ManualRequestStatus.Expired, rig.Status("req-wait-1"));
        Assert.Equal("no-answer", rig.Outcome("req-wait-1", "rule"));
        Assert.Equal(string.Empty, rig.Service.AgentKey);
    }

    [Fact]
    public async Task AClaimInterruptedByARestart_IsResolved_NeverSentAgain()
    {
        var rig = new ManualRig(btc: 0.001m);
        // A buy resting below the ask: still working on the exchange.
        await rig.Tools.Rig.Execution.PlaceAsync(new OrderIntent(Btc, OrderSide.Buy, 0.0001m, 59_900m, "c-working"), postOnly: false, CancellationToken.None);
        foreach (var (id, clientOrderId) in new[] { ("req-sent-01", "c-working"), ("req-unsent", "c-never-sent") })
        {
            var doc = rig.Request(id, "sell", "direct", portion: 1m);
            var claimed = ManualRequestDocument.WithStatus(doc, ManualRequestStatus.Executing, rig.Time.GetUtcNow(), "engine", "Sending.",
                new Dictionary<string, string?> { ["clientOrderId"] = clientOrderId });
            Assert.NotNull(await rig.Store.TryReplaceAsync(rig.Store.Find(id)!, claimed, CancellationToken.None));
        }

        await rig.Poll();
        Assert.Equal(ManualRequestStatus.Unconfirmed, rig.Status("req-sent-01"));
        Assert.Equal(ManualRequestStatus.Executing, rig.Status("req-unsent")); // within the grace period: it may still be on its way

        rig.Time.Advance(ManualRequestService.ClaimGrace);
        rig.Tools.Rig.Quote(60_000m, 60_010m);
        await rig.Poll();

        Assert.Equal(ManualRequestStatus.Closed, rig.Status("req-unsent"));
        Assert.Equal("not-sent", rig.Outcome("req-unsent", "rule"));
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order" && e.Data!.GetValueOrDefault("tool") == ManualRequestService.DirectSource);
    }

    [Fact]
    public async Task ManualTools_NeedASpecThatAllowsPlaceOrder()
    {
        var rig = new ManualRig(spec: ToolRig.Observer);

        await Assert.ThrowsAsync<McpException>(() => rig.Manual.GetManualRequests(CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => rig.Manual.AnswerManualRequest("req-any-1", "decline", Why));

        Assert.Equal(2, rig.Tools.Journal.Entries.Count(e => e.Kind == "tool-denied"));
    }

    [Fact]
    public async Task WithoutTheRequestsContainer_NothingHappens()
    {
        var rig = new ManualRig();
        rig.Store.State = "unavailable";
        rig.Request("req-ignored", "sell", "direct", portion: 1m);

        Assert.Equal(0, await rig.Poll());

        Assert.Equal(ManualRequestStatus.New, rig.Status("req-ignored"));
        Assert.Equal("unavailable", rig.Service.State);
    }

    [Fact]
    public void Options_AreBounded()
    {
        new ManualRequestOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new ManualRequestOptions { MaxPerDay = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ManualRequestOptions { PollInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ManualRequestOptions { AgentAnswerTimeout = TimeSpan.FromMinutes(1) }.Validate());
    }
}
