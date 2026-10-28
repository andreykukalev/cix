using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cix.Domain;
using Cix.Domain.Scanning;
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
        public ManualRig(
            decimal btc = 0.001m, StrategySpec? spec = null, ManualRequestOptions? options = null, TradingMode mode = TradingMode.Paper, ToolRig? tools = null,
            InMemoryManualRequestStore? store = null, InMemoryManualLedgerStore? ledger = null)
        {
            Tools = tools ?? new ToolRig(spec ?? Trading, btc: btc);
            Tools.Header(ToolServerOptions.AgentHeader, ToolCallContext.RequestsAgent);
            Tools.Header(ToolServerOptions.RunHeader, "run-9");
            Store = store ?? new InMemoryManualRequestStore("ok");
            Ledger = ledger ?? new InMemoryManualLedgerStore();
            Options = options ?? new ManualRequestOptions();
            Mode = mode;
            Service = NewService();
            Manual = new ManualTools(Tools.Context, Service, Tools.Rig.Execution, Tools.Rig.State);
        }

        public ToolRig Tools { get; }
        public InMemoryManualRequestStore Store { get; }
        public InMemoryManualLedgerStore Ledger { get; }
        public ManualRequestOptions Options { get; }
        public TradingMode Mode { get; }
        public ManualRequestService Service { get; }
        public ManualTools Manual { get; }
        public MutableTime Time => Tools.Rig.Time;

        /// <summary>Another engine process on the same database: the ledger and the documents survive, memory doesn't.</summary>
        public ManualRequestService NewService() => new(
            Store, Ledger, Tools.Rig.Execution, Tools.Rig.State, Tools.Journal, Tools.Specs, Options,
            new TradingContext(Mode), Tools.Rig.Time, NullLogger<ManualRequestService>.Instance, Tools.Views, Tools.Rig.Tradable);

        public string Status(string id) => Store.Find(id)!.Status;

        public string? Outcome(string id, string name) => ManualRequestDocument.Outcome(Store.Find(id)!.Document, name);

        public IReadOnlyList<string> History(string id) =>
            ((JsonArray)Store.Find(id)!.Document["events"]!).Select(e => e!["status"]!.GetValue<string>()).ToList();

        public LedgerEntry? InLedger(string id) => Ledger.Saved?.Active.GetValueOrDefault(id);

        public Task<int> Poll() => Service.PollOnceAsync(CancellationToken.None);

        /// <summary>A request as the dashboard writes it: numbers as JSON numbers unless <paramref name="numbersAsText"/>.</summary>
        public JsonObject Request(
            string id, string side, string mode, decimal? portion = null, decimal? quoteAmount = null, TimeSpan? age = null, string symbol = Btc,
            bool numbersAsText = false, string? tradingMode = null, string? status = "new")
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
                ["status"] = status,
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
        Assert.Equal(new[] { "placed" }, rig.History("req-direct-1")); // nothing is written between the claim and the order
        Assert.False(((JsonObject)rig.Store.Find("req-direct-1")!.Document["outcome"]!).ContainsKey("runId")); // no run: left out, not null
        Assert.Equal((0.0005m, 60_000m), (Dec(rig.Outcome("req-direct-1", "requestedSize")), Dec(rig.Outcome("req-direct-1", "requestedPrice"))));
        var clientOrderId = rig.Outcome("req-direct-1", "clientOrderId")!;
        Assert.Equal((ManualRequestStatus.Placed, clientOrderId), (rig.InLedger("req-direct-1")!.Status, rig.InLedger("req-direct-1")!.ClientOrderId));

        var journal = rig.Tools.Journal.Entries;
        var received = Assert.Single(journal, e => e.Kind == ManualRequestService.Kind && e.Data!["status"] == "received");
        Assert.Equal(("owner", "direct", "sell"), (received.Actor, received.Data!["mode"], received.Data["side"]));
        Assert.DoesNotContain('‮', received.Data["note"]); // format characters can't disguise what the owner reads
        var order = Assert.Single(journal, e => e.Kind == "order");
        Assert.Equal(("engine", ManualRequestService.DirectSource, "req-direct-1", clientOrderId, "placed"),
            (order.Actor, order.Data!["tool"], order.Data["requestId"], order.Data["clientOrderId"], order.Data["status"]));
        Assert.Single(journal, e => e.Kind == ManualRequestService.Kind && e.Data!["status"] == "placed");

        // The reconciler journals the fill; the next settle moves the request to filled and out of the ledger.
        await rig.Tools.Rig.Execution.ReconcileAsync(CancellationToken.None);
        rig.Time.Advance(ManualRequestService.SettleInterval);
        await rig.Poll();

        Assert.Equal(ManualRequestStatus.Filled, rig.Status("req-direct-1"));
        Assert.Equal(0.0005m, Dec(rig.Outcome("req-direct-1", "filledSize")));
        Assert.Null(rig.InLedger("req-direct-1"));
        var fill = Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "fill");
        Assert.Equal((ManualRequestService.DirectSource, ExitReasons.Manual), (fill.Data!["source"], fill.Data["exitReason"]));
        rig.Tools.Rig.Quote(60_000m, 60_010m);
        Assert.Equal(0.0005m, (await rig.Tools.Rig.Snapshot()).SellableBaseBySymbol![Btc]); // half is left
    }

    [Fact]
    public async Task ARequest_IsExecutedOnce_EvenWhenItsDocumentCantBeUpdated_OrTheEngineRestarts()
    {
        var rig = new ManualRig(btc: 0.001m);
        rig.Store.FailWrites = true; // the document keeps saying "new"
        rig.Request("req-once-1", "sell", "direct", portion: 1m);

        await rig.Poll();
        await rig.Poll();
        await rig.NewService().PollOnceAsync(CancellationToken.None); // a restart reads the ledger, not the document

        Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "order");
        Assert.Equal(ManualRequestStatus.New, rig.Status("req-once-1"));

        rig.Store.FailWrites = false;
        await rig.Poll();
        Assert.Equal(ManualRequestStatus.Placed, rig.Status("req-once-1")); // the copy catches up
        Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "order");
    }

    [Fact]
    public async Task WhenTheLedgerCantBeSaved_NothingIsSent_AndTheRequestIsTakenLater()
    {
        var rig = new ManualRig(btc: 0.001m);
        rig.Ledger.Fail = true;
        rig.Request("req-ledger1", "sell", "direct", portion: 1m);

        await Assert.ThrowsAnyAsync<IOException>(() => rig.Poll()); // the day's ledger can't even start
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order");

        rig.Ledger.Fail = false;
        await rig.Poll();

        Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "order");
        Assert.Equal(ManualRequestStatus.Placed, rig.Status("req-ledger1"));
    }

    [Theory]
    [InlineData("buy", "direct", null, "5", "BTC-EUR", "agent-assisted only")]
    [InlineData("sell", "direct", "1.5", null, "BTC-EUR", "portion")]
    [InlineData("sell", "direct", "0", null, "BTC-EUR", "portion")]
    [InlineData("sell", "direct", "0.5", null, "ETH-EUR", "not among the watched pairs")]
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
    public async Task MalformedOrForgedDocuments_AreRefused_NotAnError()
    {
        var rig = new ManualRig();
        var doc = rig.Request("req-good-01", "sell", "direct", portion: 0.5m);
        JsonObject Variant(string id, Action<JsonObject> change)
        {
            var copy = (JsonObject)doc.DeepClone();
            copy["id"] = id;
            change(copy);
            Assert.True(rig.Store.Add(copy));
            return copy;
        }

        Variant("req-array-1", d => d["portion"] = new JsonArray(1, 2));
        Variant("req-no-time", d => d.Remove("createdAt"));
        Variant("req-limit-1", d => d["limitPrice"] = 61_000);
        Variant("req-future1", d => d["createdAt"] = rig.Time.GetUtcNow().AddMinutes(10).ToString("O", CultureInfo.InvariantCulture));
        Variant("req-forged1", d => d["events"] = new JsonArray(new JsonObject { ["status"] = "awaiting-agent" }));
        Variant("req-bad-id/", _ => { });
        Variant("req-huge-01", d => d["note"] = new string('x', 20_000));

        await rig.Poll();

        foreach (var id in new[] { "req-array-1", "req-no-time", "req-limit-1", "req-future1", "req-forged1", "req-bad-id/", "req-huge-01" })
            Assert.Equal(ManualRequestStatus.Invalid, rig.Status(id));
        Assert.Contains("createdAt", rig.Outcome("req-no-time", "reason"), StringComparison.Ordinal);
        Assert.Contains("limitPrice", rig.Outcome("req-limit-1", "reason"), StringComparison.Ordinal);
        Assert.Contains("future", rig.Outcome("req-future1", "reason"), StringComparison.Ordinal);
        Assert.Contains("Only the engine", rig.Outcome("req-forged1", "reason"), StringComparison.Ordinal);
        Assert.Null(rig.Store.Find("req-huge-01")!.Document["note"]); // refused as too large, and not written back
        Assert.Contains(rig.Tools.Journal.Entries, e => e.Data?.GetValueOrDefault("requestId") == "invalid-id"); // an unsafe id never reaches the journal
        Assert.Equal(ManualRequestStatus.Placed, rig.Status("req-good-01")); // the good one still ran
    }

    [Fact]
    public async Task ADocumentWithAForgedStatus_IsNeverActedOn_AndBlocksNothing()
    {
        var rig = new ManualRig(btc: 0.001m);
        rig.Request("req-fake-wait", "buy", "agent", quoteAmount: 30m, status: "awaiting-agent");
        rig.Request("req-fake-sell", "sell", "direct", portion: 1m, status: "placed");
        rig.Request("req-real-sell", "sell", "direct", portion: 1m);

        await rig.Poll();

        Assert.Empty(rig.Service.Pending);
        Assert.Equal(string.Empty, rig.Service.AgentKey);
        Assert.Equal(ManualRequestStatus.Placed, rig.Status("req-real-sell")); // not refused as a duplicate of the forged one
        Assert.Equal("awaiting-agent", rig.Status("req-fake-wait"));
        Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "order");
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
        Assert.Equal((2, 3), (rig.Ledger.Saved!.Accepted, rig.Ledger.Saved.Refused));
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order");
    }

    [Fact]
    public async Task OtherTradingModes_AndLiveWithoutTheOwnersSwitch_AreLeftAlone()
    {
        var paper = new ManualRig();
        paper.Request("req-live-01", "sell", "direct", portion: 1m, tradingMode: "live");
        var live = new ManualRig(mode: TradingMode.Live);
        live.Request("req-live-02", "sell", "direct", portion: 1m, tradingMode: "live");
        var allowed = new ManualRig(mode: TradingMode.Live, options: new ManualRequestOptions { AllowLive = true });
        allowed.Request("req-unmarked", "sell", "direct", portion: 1m); // no tradingMode: paper only

        await paper.Poll();
        await live.Poll();
        await allowed.Poll();

        Assert.Equal(ManualRequestStatus.New, paper.Status("req-live-01"));
        Assert.Equal(("off", ManualRequestStatus.New), (live.Service.State, live.Status("req-live-02")));
        Assert.Equal(("ok", ManualRequestStatus.New), (allowed.Service.State, allowed.Status("req-unmarked")));
        Assert.Empty(paper.Tools.Journal.Entries);
    }

    [Fact]
    public async Task ADirectSellBeforePricesLoad_WaitsInsteadOfFailing()
    {
        var tools = new ToolRig(Trading, btc: 0.001m);
        var rig = new ManualRig(tools: tools);
        rig.Request("req-early-1", "sell", "direct", portion: 1m, symbol: "btc-eur");
        var empty = new Cix.Engine.MarketData.MarketState(rig.Time);
        var service = new ManualRequestService(rig.Store, rig.Ledger, tools.Rig.Execution, empty, tools.Journal, tools.Specs,
            new ManualRequestOptions(), new TradingContext(TradingMode.Paper), rig.Time, NullLogger<ManualRequestService>.Instance, tradable: tools.Rig.Tradable);

        await service.PollOnceAsync(CancellationToken.None);
        Assert.Equal(ManualRequestStatus.New, rig.Status("req-early-1"));

        await rig.Poll(); // prices are there now
        Assert.Equal(ManualRequestStatus.Placed, rig.Status("req-early-1"));
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
        Assert.False(listed.GetProperty("ownerNote").GetString()!.Contains('‮', StringComparison.Ordinal));

        var tooMuch = await Assert.ThrowsAsync<McpException>(() => rig.Manual.AnswerManualRequest("req-agent-1", "execute", Why, portion: 1.5m));
        Assert.Contains("portion", tooMuch.Message, StringComparison.Ordinal);
        Assert.Equal(ManualRequestStatus.AwaitingAgent, rig.Status("req-agent-1")); // a bad answer leaves the request waiting

        var answer = Parse(await rig.Manual.AnswerManualRequest("req-agent-1", "execute", Why, portion: 0.5m));

        Assert.Equal("placed", answer.GetProperty("status").GetString());
        Assert.Equal(ManualRequestStatus.Placed, rig.Status("req-agent-1"));
        Assert.Equal(("trader-requests", "run-9", Why), (rig.Outcome("req-agent-1", "decidedBy"), rig.Outcome("req-agent-1", "runId"), rig.Outcome("req-agent-1", "rationale")));
        var order = Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "order");
        Assert.Equal(("trader-requests", ManualRequestService.AgentSource, 0.0005m), (order.Actor, order.Data!["tool"], Dec(order.Data["requestedSize"])));
        Assert.Empty(rig.Service.Pending);
        await Assert.ThrowsAsync<McpException>(() => rig.Manual.AnswerManualRequest("req-agent-1", "execute", Why)); // answered once only
    }

    [Fact]
    public async Task AWaitingRequest_SurvivesARestart()
    {
        var rig = new ManualRig(btc: 0.001m);
        rig.Request("req-restart", "sell", "agent", portion: 1m);
        await rig.Poll();

        var restarted = rig.NewService();
        await restarted.PollOnceAsync(CancellationToken.None);

        Assert.Equal("manual:req-restart", restarted.AgentKey);
        Assert.Equal(rig.Time.GetUtcNow(), Assert.Single(restarted.Pending).AwaitingSince); // the clock didn't restart with it
    }

    [Fact]
    public async Task ADecline_IsRecordedWithTheRationale_AndNothingIsPlaced()
    {
        var rig = new ManualRig(btc: 0.001m);
        rig.Request("req-decline", "sell", "agent", portion: 1m);
        await rig.Poll();

        var answer = Parse(await rig.Manual.AnswerManualRequest(
            "req-decline", "decline", "Trend is still up; I'd hold and keep the stop.", rationaleRu: "Тренд всё ещё вверх;\u200b я бы держал позицию и оставил stop-loss."));

        Assert.Equal("declined", answer.GetProperty("status").GetString());
        Assert.Equal(ManualRequestStatus.Declined, rig.Status("req-decline"));
        Assert.Equal("Trend is still up; I'd hold and keep the stop.", rig.Outcome("req-decline", "rationale"));
        Assert.Equal("Тренд всё ещё вверх; я бы держал позицию и оставил stop-loss.", rig.Outcome("req-decline", "rationaleRu")); // cleaned
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order");
        var decided = Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == ManualRequestService.Kind && e.Data!["status"] == "declined");
        Assert.Equal(("trader-requests", "run-9"), (decided.Actor, decided.RunId));
        Assert.Equal("Тренд всё ещё вверх; я бы держал позицию и оставил stop-loss.", decided.Data!["rationaleRu"]);
        Assert.Null(rig.InLedger("req-decline"));
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
    public async Task AgentAssistedBuy_InACoinTheStrategyFiltersOut_IsRefused()
    {
        var rig = new ManualRig(btc: 0m, tools: new ToolRig(Trading, strategyFilter: symbol => $"{symbol} hasn't been scanned yet; the strategy's filters decide once it has."));
        rig.Request("req-filt-1", "buy", "agent", quoteAmount: 30m);
        await rig.Poll();

        var answer = Parse(await rig.Manual.AnswerManualRequest("req-filt-1", "execute", Why, stopLoss: 59_000m));

        Assert.Equal(ManualRequestStatus.Rejected, answer.GetProperty("status").GetString());
        var order = Assert.Single(rig.Tools.Journal.Entries, e => e.Kind == "order");
        Assert.Equal("strategy-filter", order.Data!["rule"]); // the owner's rule: a filtered coin doesn't enter trading
        Assert.Empty(await rig.Tools.Rig.Exchange.GetOpenOrdersAsync(null, CancellationToken.None));
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
    public async Task ARiskOffMarketView_HoldsManualBuysToo()
    {
        var rig = new ManualRig(btc: 0m);
        var now = rig.Time.GetUtcNow();
        await rig.Tools.Views.RecordAsync(new MarketView(MarketRegime.RiskOff, now, now.AddHours(6), []),
            new Journal.JournalEntry("v1", now, "market-view", "analyst", null, null, "risk-off", null), CancellationToken.None);
        rig.Request("req-held-01", "buy", "agent", quoteAmount: 10m);
        await rig.Poll();

        var held = Parse(await rig.Manual.AnswerManualRequest("req-held-01", "execute", Why, stopLoss: 59_000m));

        Assert.Equal(("rejected", "market-view"), (held.GetProperty("status").GetString(), held.GetProperty("rule").GetString()));
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order");
        Assert.Equal(ManualRequestStatus.Rejected, rig.Status("req-held-01"));
    }

    [Fact]
    public async Task AnUnansweredRequest_Expires_AndCantBeAnsweredLate()
    {
        var rig = new ManualRig();
        rig.Request("req-wait-1", "sell", "agent", portion: 1m);
        await rig.Poll();

        rig.Time.Advance(TimeSpan.FromMinutes(21));
        rig.Tools.Rig.Quote(60_000m, 60_010m);
        await Assert.ThrowsAsync<McpException>(() => rig.Manual.AnswerManualRequest("req-wait-1", "execute", Why)); // late, even before the poll
        await rig.Poll();

        Assert.Equal(ManualRequestStatus.Expired, rig.Status("req-wait-1"));
        Assert.Equal("no-answer", rig.Outcome("req-wait-1", "rule"));
        Assert.Equal(string.Empty, rig.Service.AgentKey);
    }

    [Fact]
    public async Task AClaimInterruptedByARestart_IsResolved_NeverSentAgain_AndNeverCalledNotSent()
    {
        var rig = new ManualRig(btc: 0.001m);
        // A buy resting below the ask: still working on the exchange.
        await rig.Tools.Rig.Execution.PlaceAsync(new OrderIntent(Btc, OrderSide.Buy, 0.0001m, 59_900m, "c-working"), postOnly: false, CancellationToken.None);
        var now = rig.Time.GetUtcNow();
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ManualRequest Sell(string id) => new(id, day, "sell", "direct", Btc, 1m, null, null, "owner", now);
        rig.Request("req-sent-01", "sell", "direct", portion: 1m);
        rig.Request("req-unsent", "sell", "direct", portion: 1m);
        rig.Ledger.Saved = ManualLedger.Empty
            .With(new LedgerEntry(Sell("req-sent-01"), ManualRequestStatus.Executing, now, "c-working", now, "engine"))
            .With(new LedgerEntry(Sell("req-unsent"), ManualRequestStatus.Executing, now, "c-never-sent", now, "engine"))
            .WithSeen(day, "req-sent-01").WithSeen(day, "req-unsent");

        await rig.Poll();
        Assert.Equal(ManualRequestStatus.Unconfirmed, rig.Status("req-sent-01"));
        Assert.Equal(ManualRequestStatus.Executing, rig.InLedger("req-unsent")!.Status); // within the grace period: it may still be on its way

        rig.Time.Advance(ManualRequestService.ClaimGrace);
        rig.Tools.Rig.Quote(60_000m, 60_010m);
        await rig.Poll();

        Assert.Equal(ManualRequestStatus.Closed, rig.Status("req-unsent"));
        Assert.Equal("outcome-unknown", rig.Outcome("req-unsent", "rule"));
        Assert.Contains("Check the exchange", rig.Outcome("req-unsent", "reason"), StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order"); // nothing was sent again
    }

    [Fact]
    public async Task APlacedRequest_TheEngineLostTrackOf_IsClosed_NotLeftBlockingTheCoin()
    {
        var rig = new ManualRig(btc: 0.001m);
        var now = rig.Time.GetUtcNow();
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        rig.Request("req-lost-01", "sell", "direct", portion: 1m);
        rig.Ledger.Saved = ManualLedger.Empty
            .With(new LedgerEntry(new ManualRequest("req-lost-01", day, "sell", "direct", Btc, 1m, null, null, "owner", now), ManualRequestStatus.Placed, now, "c-gone", now))
            .WithSeen(day, "req-lost-01");

        await rig.Poll();
        Assert.Equal(ManualRequestStatus.Placed, rig.InLedger("req-lost-01")!.Status); // not yet

        rig.Time.Advance(ManualRequestService.UntrackedAfter);
        rig.Tools.Rig.Quote(60_000m, 60_010m);
        await rig.Poll();
        Assert.Null(rig.InLedger("req-lost-01"));
        Assert.Equal(("closed", "outcome-unknown"), (rig.Status("req-lost-01"), rig.Outcome("req-lost-01", "rule")));

        rig.Request("req-next-01", "sell", "direct", portion: 1m);
        await rig.Poll();
        Assert.Equal(ManualRequestStatus.Placed, rig.Status("req-next-01")); // the coin isn't blocked any more
    }

    [Fact]
    public async Task ManualTools_NeedASpecThatAllowsPlaceOrder_AndAgentRequestsAreRefusedWithoutOne()
    {
        var rig = new ManualRig(spec: ToolRig.Observer);
        rig.Request("req-no-trad", "sell", "agent", portion: 1m);

        await rig.Poll();
        await Assert.ThrowsAsync<McpException>(() => rig.Manual.GetManualRequests(CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => rig.Manual.AnswerManualRequest("req-no-trad", "decline", Why));

        Assert.Equal(2, rig.Tools.Journal.Entries.Count(e => e.Kind == "tool-denied"));
        Assert.Equal(("rejected", "no-trader"), (rig.Status("req-no-trad"), rig.Outcome("req-no-trad", "rule")));
        Assert.Equal(string.Empty, rig.Service.AgentKey);
    }

    [Fact]
    public async Task OnlyRequestRuns_AnswerRequests_AndTheyCantPlaceOrCancelOrders()
    {
        var rig = new ManualRig(spec: Trading with { AllowedTools = ["place_order", "cancel_order"] });
        rig.Request("req-split-1", "sell", "agent", portion: 1m);
        await rig.Poll();

        // A request run: no orders of its own, whatever a note might say.
        await Assert.ThrowsAsync<McpException>(() => rig.Tools.Orders.PlaceOrder(Btc, "sell", 0.001m, 60_000m, Why));
        await Assert.ThrowsAsync<McpException>(() => rig.Tools.Orders.CancelOrder("venue-1", Why));
        // A strategy run: no request tools.
        rig.Tools.Header(ToolServerOptions.AgentHeader, "trader");
        await Assert.ThrowsAsync<McpException>(() => rig.Manual.GetManualRequests(CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => rig.Manual.AnswerManualRequest("req-split-1", "decline", Why));

        Assert.Equal(4, rig.Tools.Journal.Entries.Count(e => e.Kind == "tool-denied"));
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == "order");
        Assert.Equal(ManualRequestStatus.AwaitingAgent, rig.Status("req-split-1"));
    }

    [Fact]
    public async Task ADeclineThatCantBeSaved_IsNotReported_AndTheRequestStillWaits()
    {
        var rig = new ManualRig(btc: 0.001m);
        rig.Request("req-save-01", "sell", "agent", portion: 1m);
        await rig.Poll();
        rig.Ledger.Fail = true;

        await Assert.ThrowsAnyAsync<IOException>(() => rig.Manual.AnswerManualRequest("req-save-01", "decline", "Holding: the trend is still up."));

        Assert.Equal(ManualRequestStatus.AwaitingAgent, rig.InLedger("req-save-01")!.Status);
        Assert.DoesNotContain(rig.Tools.Journal.Entries, e => e.Kind == ManualRequestService.Kind && e.Data!["status"] == "declined");
    }

    [Fact]
    public async Task AManualOrderThatRests_IsCancelled_SoItCantHoldThePosition()
    {
        var rig = new ManualRig(btc: 0.001m);
        await rig.Tools.Rig.Execution.PlaceAsync(new OrderIntent(Btc, OrderSide.Buy, 0.0001m, 59_900m, "c-resting"), postOnly: false, CancellationToken.None,
            context: new OrderContext(ManualRequestService.AgentSource));
        var now = rig.Time.GetUtcNow();
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        rig.Request("req-rest-01", "buy", "agent", quoteAmount: 6m);
        rig.Ledger.Saved = ManualLedger.Empty
            .With(new LedgerEntry(new ManualRequest("req-rest-01", day, "buy", "agent", Btc, null, 6m, null, "owner", now), ManualRequestStatus.Placed, now, "c-resting", now))
            .WithSeen(day, "req-rest-01");

        await rig.Poll();
        Assert.Single((await rig.Tools.Rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None)).Where(o => o.IsOpen)); // not yet

        rig.Time.Advance(ManualRequestService.RestingCancelAfter);
        rig.Tools.Rig.Quote(60_000m, 60_010m);
        await rig.Poll();
        Assert.Empty((await rig.Tools.Rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None)).Where(o => o.IsOpen));
        Assert.Single(rig.Tools.Journal.Entries, e => e.Data?.GetValueOrDefault("rule") == "cancel-unfilled");

        await rig.Tools.Rig.Execution.ReconcileAsync(CancellationToken.None);
        rig.Time.Advance(ManualRequestService.SettleInterval);
        await rig.Poll();
        Assert.Equal(("closed", "order-closed"), (rig.Status("req-rest-01"), rig.Outcome("req-rest-01", "rule")));
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
        Assert.False(new ManualRequestOptions().AllowLive);
        Assert.Throws<InvalidOperationException>(() => new ManualRequestOptions { MaxPerDay = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ManualRequestOptions { PollInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ManualRequestOptions { AgentAnswerTimeout = TimeSpan.FromMinutes(1) }.Validate());
    }
}
