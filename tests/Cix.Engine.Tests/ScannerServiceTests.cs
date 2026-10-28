using System.Text.Json;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Scanning;
using Cix.Engine.Strategy;
using Cix.Engine.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Cix.Engine.Tests;

public sealed class ScannerServiceTests
{
    /// <summary>
    /// 60 hourly BTC candles up to 11:00 (the rig's clock is 12:00), closes moving by <paramref name="slope"/> an hour to 60,000,
    /// high/low ±300 (ATR 600, 1% of price), 1 BTC each (1.44M EUR a day). Slope +20 gives EMA20 59,810 over EMA50 59,510: an
    /// uptrend with the 60,005 mid 0.325 ATR above EMA20. Slope -20 gives a downtrend.
    /// </summary>
    internal static void SeedHourly(ToolRig tools, decimal slope)
    {
        var newest = Rig.T0 - TimeSpan.FromHours(1);
        var candles = Enumerable.Range(0, 60).Select(i =>
        {
            var close = 60_000m - (slope * (59 - i));
            return new Candle(newest - TimeSpan.FromHours(59 - i), close, close + 300m, close - 300m, close, 1m);
        }).ToList();
        tools.Candles.Upsert(Rig.Btc, 60, candles);
    }

    private static ToolRig Uptrend(decimal btc = 0m)
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["scan_market"] }, btc: btc);
        SeedHourly(tools, 20m);
        return tools;
    }

    [Fact]
    public void Universe_IsTheWatchedList_AndFollowsIt()
    {
        var limits = Rig.Limits;
        var watched = new TradableSymbols(limits, ["SOL-EUR", "BTC-EUR"]);
        var universe = new MarketUniverse(watched);

        Assert.Equal(new[] { "BTC-EUR", "SOL-EUR" }, universe.Candidates); // nothing from configuration (ADR-0021)
        Assert.Equal(universe.Candidates, universe.Tradable);
        Assert.Empty(watched.TrySet(["BTC-EUR", "DOGE-EUR"], Rig.T0));
        Assert.Equal(new[] { "BTC-EUR", "DOGE-EUR" }, universe.Candidates);
        Assert.False(universe.IsTradable("SOL-EUR"));
    }

    [Fact]
    public void Options_Validate_AndAConfiguredUniverseStopsStartup()
    {
        new ScannerOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new ScannerOptions { Interval = TimeSpan.FromSeconds(10) }.Validate());

        var withList = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Scanner:Universe:0"] = "BTC-EUR" }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => ScannerOptions.ThrowIfUniverseConfigured(withList));
        Assert.Contains("Scanner:Universe was removed", error.Message, StringComparison.Ordinal);
        ScannerOptions.ThrowIfUniverseConfigured(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Scanner:Interval"] = "00:05:00" }).Build());
    }

    [Fact]
    public async Task Scan_SuggestsASizedEntry_ScansOnlyTheWatchedCoins_AndJournalsTheSignalOnce()
    {
        var tools = Uptrend();

        var result = await tools.Scanner.ScanAsync(CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal((Rig.Btc, 60_010m, 59_110m, 61_810m), (entry.Symbol, entry.LimitPrice, entry.StopLoss, entry.TakeProfit));
        Assert.Equal(0.00008331m, entry.BaseSize); // the 5 EUR order cap, floored to the pair's step
        Assert.DoesNotContain(result.Excluded, e => e.Reason == "not-allowed"); // only watched coins are scanned
        Assert.DoesNotContain(result.Ranked, r => r.Symbol != Rig.Btc);

        await tools.Scanner.ScanAsync(CancellationToken.None); // same signal again: not journaled twice
        var signal = Assert.Single(tools.Journal.Entries, e => e.Kind == MarketScanService.SignalKind);
        Assert.Equal(("scanner", "buy", Rig.Btc, "0.00008331"), (signal.Actor, signal.Data!["side"], signal.Data["symbol"], signal.Data["baseSize"]));
    }

    [Fact]
    public async Task Scan_WhileHalted_GivesNoEntries()
    {
        var tools = Uptrend();
        await tools.Rig.Execution.EngageKillSwitchAsync("drill", "owner", CancellationToken.None);

        var result = await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(result.Entries);
        Assert.Equal("trading halted", Assert.Single(result.Ranked).NoEntryReason);
    }

    [Fact]
    public async Task Scan_FlagsAHeldPositionInADowntrend_ForExit()
    {
        var tools = new ToolRig(btc: 0.001m);
        SeedHourly(tools, -20m);

        var result = await tools.Scanner.ScanAsync(CancellationToken.None);

        var exit = Assert.Single(result.Exits);
        Assert.Equal((Rig.Btc, 0.001m, 60_000m, "trend-down"), (exit.Symbol, exit.BaseSize, exit.LimitPrice, exit.Reason));
        Assert.Empty(result.Entries);
        Assert.Contains(tools.Journal.Entries, e => e.Kind == MarketScanService.SignalKind && e.Data!["side"] == "sell");
    }

    [Fact]
    public async Task Scan_WithoutCandles_ExcludesTheSymbol()
    {
        var tools = new ToolRig();

        var result = await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(result.Ranked);
        Assert.Equal("stale", result.Excluded.Single(e => e.Symbol == Rig.Btc).Reason); // no candles: features are stale
    }

    [Fact]
    public async Task BuyRefusal_FollowsTheLatestScan_AndRefusesACoinNotScannedYet()
    {
        var stale = new ToolRig();
        Assert.StartsWith("BTC-EUR hasn't been scanned yet", stale.Scanner.BuyRefusal(Rig.Btc), StringComparison.Ordinal);
        await stale.Scanner.ScanAsync(CancellationToken.None);
        Assert.Equal("BTC-EUR is filtered out by the strategy (stale): Market data is stale.", stale.Scanner.BuyRefusal("btc-eur"));

        var passing = Uptrend();
        await passing.Scanner.ScanAsync(CancellationToken.None);
        Assert.Null(passing.Scanner.BuyRefusal(Rig.Btc)); // ranked: it passes every filter of the strategy
        Assert.StartsWith("SOL-EUR hasn't been scanned yet", passing.Scanner.BuyRefusal("SOL-EUR"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanMarketTool_ReturnsCompactJson_AndIsJournaled()
    {
        var tools = Uptrend();
        tools.Header(ToolServerOptions.AgentHeader, "trader");

        using var doc = JsonDocument.Parse(await tools.Scan.ScanMarket(CancellationToken.None));

        var root = doc.RootElement;
        Assert.False(root.GetProperty("halted").GetBoolean());
        Assert.Equal(Rig.Btc, root.GetProperty("entries")[0].GetProperty("symbol").GetString());
        Assert.Equal("up", root.GetProperty("ranked")[0].GetProperty("trend").GetString());
        Assert.Equal(6, root.GetProperty("excluded").GetArrayLength());
        var call = Assert.Single(tools.Journal.Entries, e => e.Kind == "tool-call");
        Assert.Equal(("scan_market", "trader"), (call.Data!["tool"], call.Actor));
    }

    [Fact]
    public async Task ScanMarketTool_NeedsTheSpecToAllowIt()
    {
        var tools = new ToolRig(); // the observer spec doesn't list scan_market

        await Assert.ThrowsAsync<McpException>(() => tools.Scan.ScanMarket(CancellationToken.None));
    }

    [Fact]
    public async Task ScanMarketTool_WhenThePortfolioCantBeRead_GivesAGenericError()
    {
        var tools = Uptrend();
        tools.Rig.Exchange.FailReads = true;

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.Scan.ScanMarket(CancellationToken.None));

        Assert.Equal("The market can't be scanned right now; try again shortly.", ex.Message);
        var background = new MarketScanBackgroundService(tools.Scanner, new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanBackgroundService>.Instance);
        Assert.False(await background.ScanOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AStrategySwitch_StartsAScanAtOnce_WithoutWaitingForTheTick()
    {
        var tools = Uptrend();
        var specs = new RuntimeStrategySpecs(ToolRig.Observer);
        var service = new MarketScanBackgroundService(tools.Scanner, new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanBackgroundService>.Instance, specs);
        var tick = new TaskCompletionSource<bool>().Task; // the 5-minute tick, still far off

        var next = service.NextAsync(tick, CancellationToken.None);
        Assert.False(next.IsCompleted);
        specs.Activate(ToolRig.Observer with { Version = 2 }, tools.Rig.Time.GetUtcNow());

        Assert.Equal(MarketScanBackgroundService.ScanTrigger.Switch, await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(service.NextAsync(tick, CancellationToken.None).IsCompleted); // one scan per switch
    }

    [Fact]
    public async Task ASwitchMadeDuringAScan_IsScannedForNext_AndTheTickStillScans()
    {
        var tools = Uptrend();
        var specs = new RuntimeStrategySpecs(ToolRig.Observer);
        var service = new MarketScanBackgroundService(tools.Scanner, new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanBackgroundService>.Instance, specs);
        var never = new TaskCompletionSource<bool>().Task;

        specs.Activate(ToolRig.Observer with { Version = 2 }, tools.Rig.Time.GetUtcNow()); // while no one was waiting

        Assert.Equal(MarketScanBackgroundService.ScanTrigger.Switch, await service.NextAsync(never, CancellationToken.None));
        Assert.Equal(MarketScanBackgroundService.ScanTrigger.Tick, await service.NextAsync(Task.FromResult(true), CancellationToken.None));
        Assert.Equal(MarketScanBackgroundService.ScanTrigger.Stopped, await service.NextAsync(Task.FromResult(false), CancellationToken.None));
    }

    [Fact]
    public async Task APairChange_StartsAScanWithoutWaitingForTheTick_AndAStrategySwitchComesFirst()
    {
        var tools = Uptrend();
        var tradable = new Cix.Domain.Risk.TradableSymbols(Rig.Limits with { AllowedQuoteCurrency = "EUR", MaxTradableSymbols = 3 });
        var specs = new RuntimeStrategySpecs(ToolRig.Observer);
        var service = new MarketScanBackgroundService(
            tools.Scanner, new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanBackgroundService>.Instance, specs, tradable);
        var tick = new TaskCompletionSource<bool>().Task;

        var next = service.NextAsync(tick, CancellationToken.None);
        Assert.False(next.IsCompleted);
        Assert.Empty(tradable.TrySet([Rig.Btc, "SOL-EUR"], tools.Rig.Time.GetUtcNow()));

        Assert.Equal(MarketScanBackgroundService.ScanTrigger.Pairs, await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(service.NextAsync(tick, CancellationToken.None).IsCompleted); // one scan per change

        // Both while the scanner was busy: one scan, after the switch, covers the pairs too.
        var fresh = new MarketScanBackgroundService(
            tools.Scanner, new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanBackgroundService>.Instance, specs, tradable);
        Assert.Empty(tradable.TrySet([Rig.Btc], tools.Rig.Time.GetUtcNow()));
        specs.Activate(ToolRig.Observer with { Version = 2 }, tools.Rig.Time.GetUtcNow());
        Assert.Equal(MarketScanBackgroundService.ScanTrigger.Switch, await fresh.NextAsync(tick, CancellationToken.None));
        Assert.False(fresh.NextAsync(tick, CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task WithoutRunTimeSwitching_OnlyTheTickScans()
    {
        var tools = Uptrend();
        var service = new MarketScanBackgroundService(tools.Scanner, new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanBackgroundService>.Instance);

        Assert.Equal(MarketScanBackgroundService.ScanTrigger.Tick, await service.NextAsync(Task.FromResult(true), CancellationToken.None));
    }

    [Fact]
    public async Task BackgroundScan_ReportsSuccess()
    {
        var tools = Uptrend();
        var service = new MarketScanBackgroundService(tools.Scanner, new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanBackgroundService>.Instance);

        Assert.True(await service.ScanOnceAsync(CancellationToken.None));
        Assert.NotNull(tools.Scanner.Latest);
    }
}
