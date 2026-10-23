using Cix.Engine.Journal;
using Cix.Engine.Scanning;

namespace Cix.Engine.Tests;

/// <summary>The engine sells at a position's stop or target by itself, so a missing agent run can't leave it unprotected (#33).</summary>
public sealed class ProtectiveExitTests
{
    private const string Btc = "BTC-EUR";

    /// <summary>A held BTC position whose journaled buy recorded <paramref name="stop"/> and <paramref name="target"/>.</summary>
    private static async Task<ToolRig> Holding(decimal btc, decimal stop, decimal target, bool protectiveExits = true)
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["scan_market"] }, btc: btc, protectiveExits: protectiveExits);
        ScannerServiceTests.SeedHourly(tools, 20m); // fresh features, so exits can be judged
        var at = tools.Rig.Time.GetUtcNow();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        await tools.Journal.AppendAsync(new JournalEntry("o1", at.AddHours(-2), "order", "trader", "run-1", null, "buy",
            new Dictionary<string, string>
            {
                ["tool"] = "place_order", ["clientOrderId"] = "c-entry", ["symbol"] = Btc, ["side"] = "buy",
                ["stopLoss"] = stop.ToString(inv), ["takeProfit"] = target.ToString(inv),
            }), CancellationToken.None);
        await tools.Journal.AppendAsync(new JournalEntry("f1", at.AddHours(-2), "fill", "execution", null, null, "fill",
            new Dictionary<string, string>
            {
                ["clientOrderId"] = "c-entry", ["symbol"] = Btc, ["side"] = "buy", ["averagePrice"] = "62000",
            }), CancellationToken.None);
        return tools;
    }

    [Fact]
    public async Task PositionAtItsStop_IsSoldByTheEngine_WithoutAnAgentRun()
    {
        var tools = await Holding(btc: 0.001m, stop: 61_000m, target: 70_000m); // bid 60,000 is below the stop

        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);
        var placed = await tools.Scanner.ApplyProtectiveExitsAsync(scan, CancellationToken.None);

        Assert.Equal(1, placed);
        var order = Assert.Single(tools.Journal.Entries, e => e.Kind == "order" && e.Data!["tool"] == "protective-exit");
        Assert.Equal(("engine", "sell", "stop", "placed"), (order.Actor, order.Data!["side"], order.Data["rationale"].Split(' ')[3].TrimEnd(':'), order.Data["status"]));
        var snapshot = await tools.Rig.Snapshot();
        Assert.Equal(0m, snapshot.PositionQuoteBySymbol.GetValueOrDefault(Btc)); // sold at the bid
    }

    [Fact]
    public async Task PositionAtItsTarget_IsSoldToo_ButATrendExitIsLeftToTheTrader()
    {
        var atTarget = await Holding(btc: 0.001m, stop: 50_000m, target: 59_000m);
        Assert.Equal(1, await atTarget.Scanner.ApplyProtectiveExitsAsync(await atTarget.Scanner.ScanAsync(CancellationToken.None), CancellationToken.None));

        var trendOnly = new ToolRig(ToolRig.Observer with { AllowedTools = ["scan_market"] }, btc: 0.001m);
        ScannerServiceTests.SeedHourly(trendOnly, -20m); // downtrend, no plan: a trend exit signal only
        var scan = await trendOnly.Scanner.ScanAsync(CancellationToken.None);
        Assert.Equal("trend-down", Assert.Single(scan.Exits).Reason);
        Assert.Equal(0, await trendOnly.Scanner.ApplyProtectiveExitsAsync(scan, CancellationToken.None));
    }

    [Fact]
    public async Task UnfilledProtectiveSell_IsCancelled_AndSoldAgainAtTheNewBid()
    {
        // 0.05 BTC against 0.01 at the bid: the sell fills partly and the rest rests on the book.
        var tools = await Holding(btc: 0.05m, stop: 61_000m, target: 70_000m);
        await tools.Scanner.ApplyProtectiveExitsAsync(await tools.Scanner.ScanAsync(CancellationToken.None), CancellationToken.None);
        tools.Rig.Time.Advance(TimeSpan.FromSeconds(11)); // past the open-orders cache
        Assert.Single((await tools.Rig.Execution.GetOpenOrdersAsync(CancellationToken.None)).Where(o => o.IsOpen));

        tools.Rig.Time.Advance(TimeSpan.FromMinutes(5));
        tools.Rig.Quote(59_800m, 59_810m);
        var cancelled = await tools.Scanner.CancelStaleProtectiveExitsAsync(CancellationToken.None);
        tools.Rig.Time.Advance(TimeSpan.FromSeconds(11)); // past the open-orders cache
        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(1, cancelled);
        var exit = Assert.Single(scan.Exits);
        Assert.Equal(("stop", 59_800m), (exit.Reason, exit.LimitPrice)); // re-priced at the new bid
    }

    [Fact]
    public async Task ProtectiveExits_CanBeTurnedOff_AndNeverRunWhileHalted()
    {
        var off = await Holding(btc: 0.001m, stop: 61_000m, target: 70_000m, protectiveExits: false);
        Assert.Equal(0, await off.Scanner.ApplyProtectiveExitsAsync(await off.Scanner.ScanAsync(CancellationToken.None), CancellationToken.None));

        var halted = await Holding(btc: 0.001m, stop: 61_000m, target: 70_000m);
        var scan = await halted.Scanner.ScanAsync(CancellationToken.None);
        await halted.Rig.Execution.EngageKillSwitchAsync("drill", "owner", CancellationToken.None);
        Assert.Equal(0, await halted.Scanner.ApplyProtectiveExitsAsync(scan, CancellationToken.None));
    }

    [Fact]
    public async Task BackgroundScan_AppliesProtectiveExits()
    {
        var tools = await Holding(btc: 0.001m, stop: 61_000m, target: 70_000m);
        var service = new MarketScanBackgroundService(
            tools.Scanner, new ScannerOptions(), tools.Rig.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<MarketScanBackgroundService>.Instance);

        Assert.True(await service.ScanOnceAsync(CancellationToken.None));

        Assert.Contains(tools.Journal.Entries, e => e.Kind == "order" && e.Data!["tool"] == "protective-exit");
    }
}
