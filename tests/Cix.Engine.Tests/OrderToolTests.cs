using System.Text.Json;
using Cix.Domain;
using Cix.Engine.Tools;
using ModelContextProtocol;

namespace Cix.Engine.Tests;

public sealed class OrderToolTests
{
    private const string Why = "Trend up, spread tight; small starter position.";

    private static ToolRig Trader() =>
        new(ToolRig.Observer with { Name = "trader", AllowedTools = ["get_open_orders", "get_position_plans", "place_order", "cancel_order"] });

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static decimal Dec(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task PlaceOrder_GoesThroughTheGate_AndJournalsTheRationale()
    {
        var tools = Trader();
        tools.Header(ToolServerOptions.AgentHeader, "trader");

        var result = Parse(await tools.Orders.PlaceOrder("btc-eur", "buy", 0.0005m, 59_950.004m, Why, cancellationToken: CancellationToken.None));

        Assert.Equal("placed", result.GetProperty("status").GetString());
        Assert.Equal(59_950.00m, result.GetProperty("approved").GetProperty("price").GetDecimal());
        var open = Assert.Single(await tools.Rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
        Assert.Equal(result.GetProperty("orderId").GetString(), open.VenueOrderId);
        Assert.Equal(result.GetProperty("clientOrderId").GetString(), open.ClientOrderId);
        var entry = Assert.Single(tools.Journal.Entries, e => e.Kind == "order");
        Assert.Equal(("trader", Why, "placed", 0.0005m), (entry.Actor, entry.Data!["rationale"], entry.Data["status"], Dec(entry.Data["approvedSize"])));
    }

    [Fact]
    public async Task AnAgentsSell_CarriesItsExitReason_ToTheFill_AndABuyNone()
    {
        var tools = new ToolRig(ToolRig.Observer with { Name = "trader", AllowedTools = ["place_order"] }, btc: 0.001m);
        tools.Header(ToolServerOptions.AgentHeader, "trader");

        var sell = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "sell", 0.0005m, 60_000m, Why, cancellationToken: CancellationToken.None));
        var buy = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None));
        var sent = tools.Rig.Store.Current.SentOrders;
        await tools.Rig.Execution.ReconcileAsync(CancellationToken.None); // the sell filled at the bid: its fill is journaled

        Assert.Equal(("placed", "placed"), (sell.GetProperty("status").GetString(), buy.GetProperty("status").GetString()));
        Assert.Equal(Execution.ExitReasons.Agent, Assert.Single(sent, o => o.Side == "sell").ExitReason);
        Assert.Null(Assert.Single(sent, o => o.Side == "buy").ExitReason);
        var fill = Assert.Single(tools.Journal.Entries, e => e.Kind == "fill" && e.Data!["side"] == "sell");
        Assert.Equal(("place_order", Execution.ExitReasons.Agent), (fill.Data!["source"], fill.Data["exitReason"]));
    }

    [Fact]
    public async Task OrderTheGateRejects_IsNeverSent_AndTheReasonIsReturnedAndJournaled()
    {
        var tools = Trader();

        var result = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 64_000m, Why, cancellationToken: CancellationToken.None));

        Assert.Equal(("rejected", "price-band"), (result.GetProperty("status").GetString(), result.GetProperty("rule").GetString()));
        Assert.Empty(await tools.Rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
        Assert.Equal("price-band", Assert.Single(tools.Journal.Entries, e => e.Kind == "order").Data!["rule"]);
    }

    [Fact]
    public async Task OversizedOrder_IsResized_AndTheAgentIsTold()
    {
        var tools = Trader();

        var result = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.01m, 59_950m, Why, cancellationToken: CancellationToken.None));

        Assert.Equal(("placed", "max-order"), (result.GetProperty("status").GetString(), result.GetProperty("rule").GetString()));
        Assert.Equal(0.00066722m, result.GetProperty("approved").GetProperty("size").GetDecimal());
    }

    [Fact]
    public async Task SpecWithoutOrderTools_RefusesThem()
    {
        var tools = new ToolRig(); // observer

        await Assert.ThrowsAsync<McpException>(() => tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None));

        Assert.Empty(await tools.Rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
        Assert.Equal("tool-denied", Assert.Single(tools.Journal.Entries).Kind);
    }

    [Theory]
    [InlineData("BTC-EUR", "hold", "0.0005", "59950", Why)]
    [InlineData("BTC-EUR", "buy", "0", "59950", Why)]
    [InlineData("BTC-EUR", "buy", "0.0005", "-1", Why)]
    [InlineData("BTC-EUR", "buy", "0.0005", "59950", "because")]
    [InlineData("DOGE-EUR", "buy", "10", "0.2", Why)]
    public async Task InvalidArguments_AreRefusedBeforeAnythingIsSent(string symbol, string side, string size, string price, string rationale)
    {
        var tools = Trader();

        await Assert.ThrowsAsync<McpException>(() => tools.Orders.PlaceOrder(
            symbol, side, decimal.Parse(size, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), rationale, cancellationToken: CancellationToken.None));

        Assert.Empty(await tools.Rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
    }

    [Theory]
    [InlineData("79228162514264337593543950335", "60000")] // decimal.MaxValue
    [InlineData("0.0005", "79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001", "60000")]
    public async Task ExtremeNumbers_AreRefused_WithoutCountingTowardTheRejectStreak(string size, string price)
    {
        var tools = Trader();

        await Assert.ThrowsAsync<McpException>(() => tools.Orders.PlaceOrder(
            "BTC-EUR", "buy", Dec(size), Dec(price), Why, cancellationToken: CancellationToken.None));

        Assert.False(tools.Rig.KillSwitch.IsEngaged);
    }

    [Fact]
    public async Task ExchangeRefusal_IsReportedWithAFixedCode()
    {
        // The gate allows 40 EUR per order, but the account only holds 30: the exchange refuses for lack of funds.
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["place_order"] }, eur: 30m);

        var result = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0006m, 59_950m, Why, cancellationToken: CancellationToken.None));

        Assert.Equal(("failed", "exchange-refused"), (result.GetProperty("status").GetString(), result.GetProperty("rule").GetString()));
    }

    [Fact]
    public async Task Rationale_IsFlattenedAndCapped()
    {
        var tools = Trader();

        await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, "line one\nline two\t\u200B\u202E" + new string('x', 2_000), cancellationToken: CancellationToken.None);

        var rationale = Assert.Single(tools.Journal.Entries, e => e.Kind == "order").Data!["rationale"];
        Assert.StartsWith("line one line two x", rationale, StringComparison.Ordinal);
        Assert.Equal(OrderTools.MaxRationale, rationale.Length);
    }

    [Fact]
    public async Task RationaleRu_IsJournaledForTheOwner_AndLeftOutWhenTooShort()
    {
        var tools = Trader();

        await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, rationaleRu: "Откат в восходящем тренде,\nстоп ниже EMA20.", cancellationToken: CancellationToken.None);
        await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0001m, 59_950m, Why, rationaleRu: "  да ", cancellationToken: CancellationToken.None);

        var orders = tools.Journal.Entries.Where(e => e.Kind == "order").ToList();
        Assert.Equal("Откат в восходящем тренде, стоп ниже EMA20.", orders[0].Data!["rationaleRu"]);
        Assert.False(orders[1].Data!.ContainsKey("rationaleRu")); // optional: a translation that says nothing is dropped, the order still goes

        await tools.Orders.CancelOrder(orders[0].Data!["orderId"], "Trend turned; stepping aside.", rationaleRu: "Тренд развернулся, выхожу.", cancellationToken: CancellationToken.None);
        var cancel = Assert.Single(tools.Journal.Entries, e => e.Kind == "order" && e.Data!["tool"] == "cancel_order");
        Assert.Equal("Тренд развернулся, выхожу.", cancel.Data!["rationaleRu"]);
    }

    [Fact]
    public async Task CancelOrder_CancelsOwnOrders_Only()
    {
        var tools = Trader();
        var placed = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None));
        var foreign = await tools.Rig.Paper.PlaceLimitOrderAsync(
            new OrderIntent(Rig.Btc, OrderSide.Buy, 0.0005m, 59_900m, "someone-else").Approved(), false, CancellationToken.None);

        var own = Parse(await tools.Orders.CancelOrder(placed.GetProperty("orderId").GetString()!, "Trend turned; stepping aside.", cancellationToken: CancellationToken.None));
        var notOwn = Parse(await tools.Orders.CancelOrder(foreign.VenueOrderId, "Trying to cancel this one.", cancellationToken: CancellationToken.None));
        var missing = Parse(await tools.Orders.CancelOrder("no-such-order", "Trying to cancel this one.", cancellationToken: CancellationToken.None));

        Assert.Equal(("cancelled", "not-own", "not-found"), (own.GetProperty("status").GetString(), notOwn.GetProperty("status").GetString(), missing.GetProperty("status").GetString()));
        Assert.Equal(foreign.VenueOrderId, Assert.Single(await tools.Rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None)).VenueOrderId);
    }

    [Fact]
    public async Task FillOfAnOrderPlacedBeforeARestart_IsStillJournaled()
    {
        var tools = Trader();
        var placed = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None));
        var restarted = tools.Rig.Restart(() => tools.Rig.Store); // same exchange and state, fresh memory

        await restarted.Execution.ReconcileAsync(CancellationToken.None); // sees its own order open: tracks it
        restarted.Quote(59_900.00m, 59_940.00m); // the market trades through the 59,950 limit: it fills
        await restarted.Execution.ReconcileAsync(CancellationToken.None);

        var fill = Assert.Single(restarted.Journal.Entries, e => e.Kind == "fill");
        Assert.Equal(placed.GetProperty("orderId").GetString(), fill.Data!["orderId"]);
    }

    [Fact]
    public async Task PostOnlyThatWouldCross_IsReportedAsRejected_NotPlaced()
    {
        var tools = Trader();

        var result = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 60_010m, Why, postOnly: true, cancellationToken: CancellationToken.None));

        Assert.Equal(("failed", "post-only-would-cross"), (result.GetProperty("status").GetString(), result.GetProperty("rule").GetString()));
        Assert.Empty(await tools.Rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task UncertainOrderThatTurnsUp_HasItsFillJournaled()
    {
        var tools = Trader();
        tools.Rig.Exchange.HideOpenOrders = true;
        tools.Rig.Exchange.UncertainAfterSending = true; // reached the exchange, but the engine didn't hear back
        var result = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None));
        tools.Rig.Exchange.HideOpenOrders = false;
        tools.Rig.Exchange.UncertainAfterSending = null;

        await tools.Rig.Execution.ReconcileAsync(CancellationToken.None); // resolves it
        tools.Rig.Quote(59_900.00m, 59_940.00m); // it fills
        await tools.Rig.Execution.ReconcileAsync(CancellationToken.None);

        Assert.Equal("uncertain", result.GetProperty("status").GetString());
        Assert.Single(tools.Journal.Entries, e => e.Kind == "fill");
    }

    [Fact]
    public async Task CancelledOrder_IsJournaledAsClosed_AndCancelCarriesTheClientOrderId()
    {
        var tools = Trader();
        var placed = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None));
        await tools.Orders.CancelOrder(placed.GetProperty("orderId").GetString()!, "Trend turned; stepping aside.", cancellationToken: CancellationToken.None);

        await tools.Rig.Execution.ReconcileAsync(CancellationToken.None);

        var clientOrderId = placed.GetProperty("clientOrderId").GetString();
        Assert.Equal(clientOrderId, Assert.Single(tools.Journal.Entries, e => e.Kind == "order" && e.Data!["tool"] == "cancel_order").Data!["clientOrderId"]);
        Assert.Equal(clientOrderId, Assert.Single(tools.Journal.Entries, e => e.Kind == "order-closed").Data!["clientOrderId"]);
    }

    [Fact]
    public async Task StopAndTarget_AreRecorded_AndReturnedForTheHeldPosition()
    {
        var tools = Trader();
        await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 60_010m, Why, stopLoss: 59_000m, takeProfit: 62_000m, cancellationToken: CancellationToken.None); // fills at once
        await tools.Rig.Execution.ReconcileAsync(CancellationToken.None); // journals the fill

        var plan = Parse(await tools.Agent.GetPositionPlans(CancellationToken.None))[0];

        Assert.Equal("BTC-EUR", plan.GetProperty("symbol").GetString());
        Assert.Equal(60_010m, plan.GetProperty("entryPrice").GetDecimal());
        Assert.Equal(0.0005m, plan.GetProperty("sellableBaseSize").GetDecimal());
        Assert.Equal((59_000m, 62_000m), (plan.GetProperty("stopLoss").GetDecimal(), plan.GetProperty("takeProfit").GetDecimal()));
    }

    [Fact]
    public async Task Dust_IsNotAPosition_AndAHoldingWithoutABuyGetsANote()
    {
        var dust = new ToolRig(ToolRig.Observer with { AllowedTools = ["get_position_plans"] }, btc: 0.000001m); // 0.06 EUR
        var held = new ToolRig(ToolRig.Observer with { AllowedTools = ["get_position_plans"] }, btc: 0.001m);

        var none = await dust.Agent.GetPositionPlans(CancellationToken.None);
        var plan = Parse(await held.Agent.GetPositionPlans(CancellationToken.None))[0];

        Assert.Equal("[]", none);
        Assert.Equal(0.001m, plan.GetProperty("sellableBaseSize").GetDecimal());
        Assert.False(plan.TryGetProperty("stopLoss", out _));
        Assert.Contains("no stop", plan.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoPositions_NoPlans()
    {
        var tools = Trader();

        Assert.Equal("[]", await tools.Agent.GetPositionPlans(CancellationToken.None));
    }

    [Theory]
    [InlineData("buy", "60000", null)]      // stop at the limit price
    [InlineData("buy", null, "59000")]      // target below the limit price
    [InlineData("sell", "59000", null)]     // stops are for buys
    public async Task InconsistentStopOrTarget_IsRefused(string side, string? stop, string? target)
    {
        var tools = Trader();

        await Assert.ThrowsAsync<McpException>(() => tools.Orders.PlaceOrder(
            "BTC-EUR", side, 0.0001m, 60_000m, Why,
            stopLoss: stop is null ? null : Dec(stop), takeProfit: target is null ? null : Dec(target), cancellationToken: CancellationToken.None));
    }

    [Fact]
    public void Good_LoadsForPaper_AndAllowsOnlyAgentTools()
    {
        var spec = Strategy.FileStrategySpecStore.Load(Path.Combine(AppContext.BaseDirectory, "strategies", "good.v1.json"), "good", 1, TradingMode.Paper);

        Assert.Equal(5, spec.Parameters["orderQuote"].GetDecimal());
        Assert.Contains("place_order", spec.AllowedTools);
        Assert.DoesNotContain(spec.AllowedTools, t => t is "pause_trading" or "resume_trading" or "query_journal");
        Assert.Throws<InvalidOperationException>(() => Strategy.FileStrategySpecStore.Load(
            Path.Combine(AppContext.BaseDirectory, "strategies", "good.v1.json"), "good", 1, TradingMode.Live));
    }

    [Fact]
    public async Task PauseAndResume_AreOperatorOnly_AndJournaled()
    {
        var tools = Trader();
        await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None);
        await Assert.ThrowsAsync<McpException>(() => tools.Operator.PauseTrading("agent tries", CancellationToken.None));

        tools.Header(ToolServerOptions.OperatorKeyHeader, ToolRig.OperatorKey);
        var paused = Parse(await tools.Operator.PauseTrading("Reviewing the strategy.", CancellationToken.None));
        var blocked = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None));
        await tools.Operator.ResumeTrading(CancellationToken.None);
        var after = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None));

        Assert.Equal(1, paused.GetProperty("cancelled").GetInt32());
        Assert.Equal("kill-switch", blocked.GetProperty("rule").GetString());
        Assert.Equal("placed", after.GetProperty("status").GetString());
        var switches = tools.Journal.Entries.Where(e => e.Kind == "kill-switch").Select(e => (e.Actor, e.Data!["action"])).ToList();
        Assert.Equal(new[] { ("owner", "engaged"), ("owner", "released") }, switches);
    }

    [Fact]
    public async Task Fill_IsJournaledByReconciliation()
    {
        var tools = Trader();
        await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 60_010m, Why, cancellationToken: CancellationToken.None); // crosses: fills at once

        await tools.Rig.Execution.ReconcileAsync(CancellationToken.None);
        await tools.Rig.Execution.ReconcileAsync(CancellationToken.None); // a second pass doesn't journal it twice

        var fill = Assert.Single(tools.Journal.Entries, e => e.Kind == "fill");
        Assert.Equal((0.0005m, 60_010m, "filled"), (Dec(fill.Data!["filledSize"]), Dec(fill.Data["averagePrice"]), fill.Data["status"]));
    }
}
