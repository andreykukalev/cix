using Cix.Domain;
using Cix.Engine.Scanning;
using Cix.Engine.Strategy;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>After a switch to a strategy with smaller caps, the engine sells what is held above them.</summary>
public sealed class StrategyTrimServiceTests
{
    /// <summary>Holding <paramref name="btc"/> (60 EUR a thousandth at the 60,000 bid) under the safe strategy: 10 EUR a coin, 50 in all.</summary>
    private static (ToolRig Tools, MarketScanService Scanner) Safe(decimal btc)
    {
        var tools = new ToolRig(StrategySchemaTests.Load("safe", 1), btc: btc);
        ScannerServiceTests.SeedHourly(tools, 20m); // fresh features, so the bid is known
        var scanner = new MarketScanService(
            tools.Universe, tools.Features, tools.Candles, tools.Rig.Pairs, tools.Rig.Execution, tools.Rig.KillSwitch, tools.Journal, tools.Specs,
            new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanService>.Instance, tools.Views, riskLimits: Rig.Limits);
        return (tools, scanner);
    }

    [Fact]
    public async Task AHoldingAboveTheNewPerCoinCap_IsSoldDownToIt_ThroughTheRiskGate()
    {
        var (tools, scanner) = Safe(btc: 0.001m);

        var placed = await scanner.TrimToStrategyAsync(await scanner.ScanAsync(CancellationToken.None), CancellationToken.None);

        Assert.Equal<int?>(1, placed);
        var order = Assert.Single(tools.Journal.Entries, e => e.Kind == "order" && e.Data!["tool"] == MarketScanService.TrimSource);
        Assert.Equal(("sell", "placed"), (order.Data!["side"], order.Data["status"]));
        var left = (await tools.Rig.Snapshot()).PositionQuoteBySymbol.GetValueOrDefault(Rig.Btc);
        Assert.InRange(left, 9.99m, 10m); // 50 of the 60 sold at the bid, rounded up to the pair's step
    }

    [Fact]
    public async Task HoldingsWithinTheCaps_ACoinWithAnOpenOrder_OrAHalt_AreLeftAlone()
    {
        var (within, withinScanner) = Safe(btc: 0.0001m); // 6 EUR
        Assert.Equal<int?>(0, await withinScanner.TrimToStrategyAsync(await withinScanner.ScanAsync(CancellationToken.None), CancellationToken.None));

        var (busy, busyScanner) = Safe(btc: 0.001m);
        await busy.Rig.Execution.PlaceAsync(new OrderIntent(Rig.Btc, OrderSide.Sell, 0.0001m, 60_500m, "c-resting"), postOnly: false, CancellationToken.None);
        Assert.Equal<int?>(0, await busyScanner.TrimToStrategyAsync(await busyScanner.ScanAsync(CancellationToken.None), CancellationToken.None));

        var (halted, haltedScanner) = Safe(btc: 0.001m);
        await halted.Rig.Execution.EngageKillSwitchAsync("drill", "owner", CancellationToken.None);
        Assert.Null(await haltedScanner.TrimToStrategyAsync(await haltedScanner.ScanAsync(CancellationToken.None), CancellationToken.None));

        Assert.DoesNotContain(within.Journal.Entries.Concat(busy.Journal.Entries).Concat(halted.Journal.Entries),
            e => e.Data?.GetValueOrDefault("tool") == MarketScanService.TrimSource);
    }

    [Fact]
    public async Task TheScanAfterASwitch_Trims_AndATrimThatCantBeJudgedYet_IsTriedAtTheNextScan()
    {
        var (tools, scanner) = Safe(btc: 0.001m);
        var specs = new RuntimeStrategySpecs(ToolRig.Observer);
        var service = new MarketScanBackgroundService(scanner, new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanBackgroundService>.Instance, specs);
        Assert.True(await service.ScanOnceAsync(CancellationToken.None)); // no switch yet: nothing sold
        Assert.DoesNotContain(tools.Journal.Entries, e => e.Data?.GetValueOrDefault("tool") == MarketScanService.TrimSource);

        specs.Activate(ToolRig.Observer with { Version = 2 }, tools.Rig.Time.GetUtcNow());
        Assert.Equal(MarketScanBackgroundService.ScanTrigger.Switch, await service.NextAsync(new TaskCompletionSource<bool>().Task, CancellationToken.None));
        await tools.Rig.Execution.EngageKillSwitchAsync("drill", "owner", CancellationToken.None);
        Assert.True(await service.ScanOnceAsync(CancellationToken.None)); // halted: the trim waits
        Assert.DoesNotContain(tools.Journal.Entries, e => e.Data?.GetValueOrDefault("tool") == MarketScanService.TrimSource);

        await tools.Rig.Execution.ReleaseKillSwitchAsync("owner");
        Assert.True(await service.ScanOnceAsync(CancellationToken.None));
        Assert.True(await service.ScanOnceAsync(CancellationToken.None)); // once per switch
        Assert.Single(tools.Journal.Entries, e => e.Data?.GetValueOrDefault("tool") == MarketScanService.TrimSource);
    }
}
