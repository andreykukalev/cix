using Cix.Domain.MarketData;
using Cix.Domain.Scanning;
using Cix.Engine.Scanning;
using Cix.Engine.Status;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>The coin filters in force and which coins pass them are recorded in the database, incrementally (#63).</summary>
public sealed class ScannerFilterTests
{
    private static readonly DateTimeOffset T0 = Rig.T0;
    private static readonly ScannerParameters Strict = new();
    private static readonly ScannerParameters Loose = new() { MinQuoteVolume24h = 20_000m, MaxSpreadFraction = 0.004m };

    private static ScanRow Row(string symbol, int rank) =>
        new(symbol, rank, 1m, 100m, Trend.Up, 0.01m, 0.001m, 0.02m, 500_000m, 1m, 0.001m, 0.0038m, null);

    /// <summary>A scan at <paramref name="minutes"/> past T0 where <paramref name="passing"/> rank and the rest are excluded.</summary>
    private static ScanResult Scan(int minutes, string[] passing, params (string Symbol, string Reason)[] excluded) =>
        new(T0.AddMinutes(minutes), passing.Select((s, i) => Row(s, i + 1)).ToList(),
            excluded.Select(x => new ScanExclusion(x.Symbol, x.Reason, "detail")).ToList(), [], []);

    private static ScannerFilterRecorder Recorder(IScannerFilterStore store, Rig rig) =>
        new(store, rig.Journal, rig.Time, NullLogger<ScannerFilterRecorder>.Instance);

    [Fact]
    public async Task TheFirstScan_RecordsTheFilters_AndLaterScansWithoutChanges_WriteNothing()
    {
        var rig = new Rig();
        var store = new InMemoryScannerFilterStore();
        var recorder = Recorder(store, rig);

        await recorder.RecordAsync(Scan(0, ["BTC-EUR", "ETH-EUR"], ("DOGE-EUR", "low-volume")), Strict, "risky@v1#a", CancellationToken.None);
        await recorder.RecordAsync(Scan(5, ["BTC-EUR", "ETH-EUR"], ("DOGE-EUR", "low-volume")), Strict, "risky@v1#a", CancellationToken.None);
        // A restart: a new recorder reads the record back and finds nothing new.
        await Recorder(store, rig).RecordAsync(Scan(10, ["BTC-EUR", "ETH-EUR"], ("DOGE-EUR", "low-volume")), Strict, "risky@v1#a", CancellationToken.None);

        var entry = Assert.Single(rig.Journal.Entries);
        Assert.Equal((ScannerFilterRecorder.FiltersKind, "scanner", "risky@v1#a"), (entry.Kind, entry.Actor, entry.SpecVersion));
        Assert.Equal(("BTC-EUR,ETH-EUR", "DOGE-EUR:low-volume"), (entry.Data!["passing"], entry.Data["excluded"]));
        Assert.Contains("\"minQuoteVolume24h\":100000", entry.Data["filters"], StringComparison.Ordinal);
        Assert.Equal(1, store.Saves);
        Assert.Equal((Strict, T0), (store.Saved!.Filters, store.Saved.Since));
    }

    [Fact]
    public async Task ChangedFilters_AreJournaledWithEachChange_AndTheCoinsThatPassNow()
    {
        var rig = new Rig();
        var store = new InMemoryScannerFilterStore();
        await Recorder(store, rig).RecordAsync(Scan(0, ["BTC-EUR"], ("DOGE-EUR", "low-volume")), Strict, "risky@v1#a", CancellationToken.None);

        // The owner activates a new spec version with looser filters and restarts the engine.
        await Recorder(store, rig).RecordAsync(Scan(60, ["BTC-EUR", "DOGE-EUR"]), Loose, "risky@v2#b", CancellationToken.None);

        var changed = rig.Journal.Entries.Last();
        Assert.Equal(ScannerFilterRecorder.FiltersKind, changed.Kind);
        Assert.Equal(("risky@v1#a", "BTC-EUR,DOGE-EUR"), (changed.Data!["previousSpec"], changed.Data["passing"]));
        Assert.Contains("minQuoteVolume24h 100000 → 20000", changed.Data["changes"], StringComparison.Ordinal);
        Assert.Contains("maxSpreadFraction 0.002 → 0.004", changed.Data["changes"], StringComparison.Ordinal);
        Assert.DoesNotContain("stopAtr", changed.Data["changes"], StringComparison.Ordinal); // unchanged values aren't listed
        Assert.Equal((Loose, T0.AddMinutes(60), "risky@v2#b"), (store.Saved!.Filters, store.Saved.Since, store.Saved.Spec));
        Assert.Equal(2, rig.Journal.Entries.Count);
    }

    [Fact]
    public async Task AStrategySwitch_SaysWhichCoinsEnterAndLeave_AndWhichOfThoseAreHeld()
    {
        var rig = new Rig();
        var store = new InMemoryScannerFilterStore();
        await Recorder(store, rig).RecordAsync(Scan(0, ["BTC-EUR", "ETH-EUR"], ("DOGE-EUR", "low-volume")), Loose, "risky@v2#b", CancellationToken.None);
        Assert.Null(store.Saved!.LastChange); // the first record isn't a switch

        await Recorder(store, rig).RecordAsync(
            Scan(60, ["BTC-EUR", "DOGE-EUR"], ("ETH-EUR", "wide-spread")), Strict, "safe@v2#c", CancellationToken.None, held: ["ETH-EUR", "BTC-EUR", "UNI-EUR"]);

        var changed = rig.Journal.Entries.Last();
        Assert.Equal(("DOGE-EUR", "ETH-EUR", "ETH-EUR"), (changed.Data!["added"], changed.Data["removed"], changed.Data["heldOutside"]));
        var last = store.Saved!.LastChange!;
        Assert.Equal((T0.AddMinutes(60), "risky@v2#b", "safe@v2#c"), (last.At, last.FromSpec, last.ToSpec));
        Assert.Equal(new[] { "ETH-EUR" }, last.HeldOutside); // held and moved out: it keeps its stop, the owner decides; UNI never passed

        // Coins passing later under the same filters keep the switch on record, for the Coins tab's banner.
        var recorder = Recorder(store, rig);
        await recorder.RecordAsync(Scan(65, ["BTC-EUR"], ("DOGE-EUR", "low-volume"), ("ETH-EUR", "wide-spread")), Strict, "safe@v2#c", CancellationToken.None);
        await recorder.RecordAsync(Scan(70, ["BTC-EUR"], ("DOGE-EUR", "low-volume"), ("ETH-EUR", "wide-spread")), Strict, "safe@v2#c", CancellationToken.None);
        Assert.Equal(new[] { "BTC-EUR" }, store.Saved!.Passing);
        Assert.Equal(last, store.Saved.LastChange);
    }

    [Fact]
    public async Task ACoinThatStartsOrStopsPassing_IsJournaledOnceConfirmed_AndMissingDataIsNotAChange()
    {
        var rig = new Rig();
        var store = new InMemoryScannerFilterStore();
        var recorder = Recorder(store, rig);
        await recorder.RecordAsync(Scan(0, ["BTC-EUR", "ETH-EUR"], ("DOGE-EUR", "low-volume")), Strict, "s@v1#a", CancellationToken.None);

        // Candles still loading after a restart: ETH has no judgement yet, which isn't a change.
        await recorder.RecordAsync(Scan(5, ["BTC-EUR"], ("DOGE-EUR", "low-volume"), ("ETH-EUR", "insufficient-history")), Strict, "s@v1#a", CancellationToken.None);
        await recorder.RecordAsync(Scan(10, ["BTC-EUR"], ("DOGE-EUR", "low-volume"), ("ETH-EUR", "insufficient-history")), Strict, "s@v1#a", CancellationToken.None);
        Assert.Single(rig.Journal.Entries);

        // DOGE passes once, then not: a blip, not recorded.
        await recorder.RecordAsync(Scan(15, ["BTC-EUR", "DOGE-EUR", "ETH-EUR"]), Strict, "s@v1#a", CancellationToken.None);
        await recorder.RecordAsync(Scan(20, ["BTC-EUR", "ETH-EUR"], ("DOGE-EUR", "low-volume")), Strict, "s@v1#a", CancellationToken.None);
        Assert.Single(rig.Journal.Entries);

        // ETH fails on spread in two scans in a row: recorded once.
        await recorder.RecordAsync(Scan(25, ["BTC-EUR"], ("DOGE-EUR", "low-volume"), ("ETH-EUR", "wide-spread")), Strict, "s@v1#a", CancellationToken.None);
        await recorder.RecordAsync(Scan(30, ["BTC-EUR"], ("DOGE-EUR", "low-volume"), ("ETH-EUR", "wide-spread")), Strict, "s@v1#a", CancellationToken.None);
        await recorder.RecordAsync(Scan(35, ["BTC-EUR"], ("DOGE-EUR", "low-volume"), ("ETH-EUR", "wide-spread")), Strict, "s@v1#a", CancellationToken.None);

        var change = rig.Journal.Entries.Last();
        Assert.Equal(ScannerFilterRecorder.EligibilityKind, change.Kind);
        Assert.Equal(("", "ETH-EUR", "BTC-EUR"), (change.Data!["added"], change.Data["removed"], change.Data["passing"]));
        Assert.Contains("-ETH-EUR (wide-spread)", change.Summary, StringComparison.Ordinal);
        Assert.Equal(2, rig.Journal.Entries.Count);
        Assert.Equal((T0, T0.AddMinutes(30)), (store.Saved!.Since, store.Saved.EligibilityAt)); // filters unchanged since T0
    }

    [Fact]
    public async Task TheScannerRecordsFiltersAsItScans_AndTheStatusShowsThem()
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["scan_market"] });
        ScannerServiceTests.SeedHourly(tools, 20m);
        var store = new InMemoryScannerFilterStore();
        var recorder = Recorder(store, tools.Rig);
        var scanner = new MarketScanService(
            tools.Universe, tools.Features, tools.Candles, tools.Rig.Pairs, tools.Rig.Execution, tools.Rig.KillSwitch, tools.Journal, tools.Specs,
            new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanService>.Instance, tools.Views, recorder);

        await scanner.ScanAsync(CancellationToken.None);
        var status = new EngineStatusService(
            tools.Rig.Execution, tools.Rig.KillSwitch, tools.Rig.State, Rig.Limits, new TradingContext(TradingMode.Paper), new InMemoryEngineStatusStore(),
            new DependencyHealth(), tools.Rig.Time, NullLogger<EngineStatusService>.Instance, scanner, tools.Views, tools.Specs, tradable: tools.Rig.Tradable).Build();

        Assert.Single(tools.Journal.Entries, e => e.Kind == ScannerFilterRecorder.FiltersKind);
        Assert.Contains("BTC-EUR", store.Saved!.Passing);
        var filters = status.Filters!;
        Assert.Equal((tools.Specs.Active!.Label, (DateTimeOffset?)Rig.T0, 100_000m, 0.002m), (filters.Spec, filters.Since, filters.MinQuoteVolume24h, filters.MaxSpreadFraction));
        Assert.Null(status.ManualRequests); // not wired in this test
    }

    [Fact]
    public async Task TheStatusIsWritten_AsSoonAsAScanFinishes_NotOnlyEachMinute()
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["scan_market"] });
        ScannerServiceTests.SeedHourly(tools, 20m);
        var status = new EngineStatusService(
            tools.Rig.Execution, tools.Rig.KillSwitch, tools.Rig.State, Rig.Limits, new TradingContext(TradingMode.Paper), new InMemoryEngineStatusStore(),
            new DependencyHealth(), tools.Rig.Time, NullLogger<EngineStatusService>.Instance, tools.Scanner, tools.Views, tools.Specs, tradable: tools.Rig.Tradable);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1)); // the minute tick, far off

        var next = status.NextAsync(timer, CancellationToken.None);
        Assert.False(next.IsCompleted);
        await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(status.NextAsync(timer, CancellationToken.None).IsCompleted); // once per scan
    }

    [Fact]
    public void Changes_ListOnlyWhatDiffers()
    {
        Assert.Empty(ScannerFilterRecorder.Changes(Strict, Strict with { }));
        Assert.Equal(new[] { "requireUptrendPullback true → false" }, ScannerFilterRecorder.Changes(Strict, Strict with { RequireUptrendPullback = false }));
    }
}
