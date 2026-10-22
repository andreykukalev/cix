using System.Text.Json;
using Cix.Domain;
using Cix.Engine.Tools;
using ModelContextProtocol;

namespace Cix.Engine.Tests;

public sealed class OrderToolTests
{
    private const string Why = "Trend up, spread tight; small starter position.";

    private static ToolRig Trader() =>
        new(ToolRig.Observer with { Name = "trader", AllowedTools = ["get_open_orders", "place_order", "cancel_order"] });

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

    [Fact]
    public async Task Rationale_IsFlattenedAndCapped()
    {
        var tools = Trader();

        await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, "line one\nline two\t" + new string('x', 2_000), cancellationToken: CancellationToken.None);

        var rationale = Assert.Single(tools.Journal.Entries, e => e.Kind == "order").Data!["rationale"];
        Assert.StartsWith("line one line two x", rationale, StringComparison.Ordinal);
        Assert.Equal(OrderTools.MaxRationale, rationale.Length);
    }

    [Fact]
    public async Task CancelOrder_CancelsOwnOrders_Only()
    {
        var tools = Trader();
        var placed = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None));
        var foreign = await tools.Rig.Paper.PlaceLimitOrderAsync(
            new OrderIntent(Rig.Btc, OrderSide.Buy, 0.0005m, 59_900m, "someone-else").Approved(), false, CancellationToken.None);

        var own = Parse(await tools.Orders.CancelOrder(placed.GetProperty("orderId").GetString()!, "Trend turned; stepping aside.", CancellationToken.None));
        var notOwn = Parse(await tools.Orders.CancelOrder(foreign.VenueOrderId, "Trying to cancel this one.", CancellationToken.None));
        var missing = Parse(await tools.Orders.CancelOrder("no-such-order", "Trying to cancel this one.", CancellationToken.None));

        Assert.Equal(("cancelled", "not-own", "not-found"), (own.GetProperty("status").GetString(), notOwn.GetProperty("status").GetString(), missing.GetProperty("status").GetString()));
        Assert.Equal(foreign.VenueOrderId, Assert.Single(await tools.Rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None)).VenueOrderId);
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
