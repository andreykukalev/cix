using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Scanning;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Scanning;
using Cix.Engine.Strategy;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>Binance's candles answering what <see cref="BinanceFeed"/> asks, and what it asked.</summary>
internal sealed class FakeKlines(Func<string, int, IReadOnlyList<Candle>?> answer) : IBinanceKlines
{
    public List<(string Symbol, int Interval, int Limit)> Calls { get; } = [];

    public Task<IReadOnlyList<Candle>?> GetAsync(string symbol, int intervalMinutes, int limit, CancellationToken cancellationToken)
    {
        Calls.Add((symbol, intervalMinutes, limit));
        return Task.FromResult(answer(symbol, intervalMinutes));
    }
}

/// <summary>The plays of ADR-0031 in the engine: Binance's candles, the scan's play signals, their buys, time exits and loss stop.</summary>
public sealed class PlaysTests
{
    private const string Btc = Rig.Btc;
    private static readonly DateTimeOffset T0 = Rig.T0; // 2026-10-22 12:00 UTC

    /// <summary>
    /// 200 Binance hourly BTCUSDT candles to the hour that closed at <see cref="T0"/>: flat at 60,000 (highs 60,100, volume
    /// 10), then a close at 60,300 on 60, above the 96-hour high on six times the volume.
    /// </summary>
    internal static List<Candle> HourlyBurst(decimal lastVolume = 60m)
    {
        var newest = T0.AddHours(-1);
        return Enumerable.Range(0, 200).Select(i => i < 199
            ? new Candle(newest.AddHours(i - 199), 60_000m, 60_100m, 59_900m, 60_000m, 10m)
            : new Candle(newest, 60_000m, 60_400m, 59_950m, 60_300m, lastVolume)).ToList();
    }

    /// <summary>120 Binance daily BTC candles to the one that closed at midnight before <see cref="T0"/>, rising: above its EMA100.</summary>
    internal static List<Candle> DailyUp()
    {
        var newest = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, 120).Select(i => new Candle(newest.AddDays(i - 119), 40_000m + (100m * i), 40_300m + (100m * i), 39_700m + (100m * i), 40_000m + (100m * i), 1_000m)).ToList();
    }

    private static StrategySpec Burst(decimal orderFraction = 0.15m) => ToolRig.Observer with
    {
        AllowedTools = ["scan_market", "place_order"],
        Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            string.Create(CultureInfo.InvariantCulture, $$"""{ "plays": { "burst": { "orderFraction": {{orderFraction}} } } }"""))!,
    };

    private static async Task<ToolRig> Bursting(StrategySpec? spec = null, decimal btc = 0m, decimal lastVolume = 60m)
    {
        var klines = new FakeKlines((symbol, interval) => interval == MarketDataPoller.DayInterval ? DailyUp() : HourlyBurst(lastVolume));
        var tools = new ToolRig(spec ?? Burst(), btc: btc, binance: time => new BinanceFeed(klines, () => [Btc], time, NullLogger<BinanceFeed>.Instance));
        ScannerServiceTests.SeedHourly(tools, 0m); // fresh Revolut X features, no trend: the strategy's own scan buys nothing
        Assert.Equal(2, await tools.Binance!.PollOnceAsync(CancellationToken.None));
        return tools;
    }

    [Fact]
    public async Task ABurstOnBinance_IsAPlaySignal_AndItsBuyIsTaggedWithThePlayAndItsTimeToSell()
    {
        var tools = await Bursting();

        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);

        var entry = Assert.Single(scan.Entries);
        Assert.Equal((Btc, MarketPlays.Burst, T0.AddHours(72), 60_010m), (entry.Symbol, entry.Play, entry.ExitBy, entry.LimitPrice));
        Assert.Equal(0.00049991m, entry.BaseSize); // 15% of the 200 EUR account at the ask
        Assert.Null(entry.TakeProfit);
        Assert.Equal("signal", Assert.Single(scan.Plays!).State);

        await tools.Orders.PlaceOrder(Btc, "buy", entry.BaseSize, entry.LimitPrice, "burst play: Binance volume burst on BTC", stopLoss: entry.StopLoss,
            cancellationToken: CancellationToken.None);

        var order = Assert.Single(tools.Journal.Entries, e => e.Kind == "order");
        Assert.Equal(("burst", T0.AddHours(72).ToString("O", CultureInfo.InvariantCulture)), (order.Data!["play"], order.Data["exitBy"]));
        var plan = tools.Rig.Execution.PositionPlanStates[Btc];
        Assert.Equal((MarketPlays.Burst, T0.AddHours(72), entry.StopLoss), (plan.Play, plan.ExitBy, plan.StopLoss));
    }

    [Fact]
    public async Task ABuyFromAnOldScan_OrOfAnotherCoin_IsNotAPlay()
    {
        var tools = await Bursting();
        var entry = Assert.Single((await tools.Scanner.ScanAsync(CancellationToken.None)).Entries);

        tools.Rig.Time.Advance(TimeSpan.FromMinutes(61));
        tools.Rig.Quote(60_000m, 60_010m);
        await tools.Orders.PlaceOrder(Btc, "buy", entry.BaseSize, entry.LimitPrice, "a buy an hour after the burst", stopLoss: entry.StopLoss,
            cancellationToken: CancellationToken.None);

        Assert.False(Assert.Single(tools.Journal.Entries, e => e.Kind == "order").Data!.ContainsKey("play"));
        Assert.Null(tools.Rig.Execution.PositionPlanStates[Btc].Play);
    }

    [Fact]
    public async Task APlayPosition_IsSoldByTheEngine_WhenItsTimeIsUp()
    {
        var tools = await Bursting(btc: 0.001m, lastVolume: 10m); // no burst now
        tools.Rig.Store.Update(s => s with
        {
            Positions = new Dictionary<string, PositionPlanState>(StringComparer.Ordinal)
            {
                [Btc] = new(Btc, PositionPlanState.Open, "c-entry", 50_000m, null, T0.AddHours(-73), EntryPrice: 59_000m, FilledAt: T0.AddHours(-73),
                    Play: MarketPlays.Burst, ExitBy: T0.AddHours(-1)),
            },
        });

        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);
        Assert.Equal("time", Assert.Single(scan.Exits).Reason);
        Assert.Equal(1, await tools.Scanner.ApplyProtectiveExitsAsync(scan, CancellationToken.None));

        var sell = Assert.Single(tools.Rig.Store.Current.SentOrders, o => o.Side == "sell");
        Assert.Equal((ExitReasons.Time, MarketPlays.Burst), (sell.ExitReason, sell.Play));
        await tools.Rig.Execution.ReconcileAsync(CancellationToken.None);
        var fill = Assert.Single(tools.Journal.Entries, e => e.Kind == "fill" && e.Data!["side"] == "sell");
        Assert.Equal((ExitReasons.Time, "burst"), (fill.Data!["exitReason"], fill.Data["play"]));
    }

    [Fact]
    public async Task ThePlaysLossStop_StopsNewPlays_FromThePlaysBoughtInTheWindow()
    {
        var tools = await Bursting();
        Task Fill(string id, string symbol, string side, decimal size, decimal quote, int daysAgo, string? play = "burst") =>
            tools.Journal.AppendAsync(new JournalEntry(id, T0.AddDays(-daysAgo), "fill", "execution", null, null, "fill",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["clientOrderId"] = id, ["symbol"] = symbol, ["side"] = side, ["filledSize"] = size.ToString(CultureInfo.InvariantCulture),
                    ["filledQuote"] = quote.ToString(CultureInfo.InvariantCulture), ["fee"] = "0", ["play"] = play ?? "",
                }), CancellationToken.None);

        // A play bought for 100 and sold for 64: 36 lost, the 18% stop of 200: the stop is reached.
        await Fill("p1", "SOL-EUR", "buy", 1m, 100m, 3);
        await Fill("p2", "SOL-EUR", "sell", 1m, 64m, 1);
        await Fill("s1", "ADA-EUR", "buy", 100m, 500m, 2, play: null); // the strategy's own buys don't count
        // A rally bought 70 days ago and sold 50 days ago: its buy is outside the 60 days, so its sale is no gain.
        await Fill("p3", "ETH-EUR", "buy", 1m, 50m, 70, play: "rally");
        await Fill("p4", "ETH-EUR", "sell", 1m, 80m, 50, play: "rally");

        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);
        Assert.Empty(scan.Entries);
        var status = Assert.Single(scan.Plays!);
        Assert.Equal(("paused", "plays' loss stop: 36 lost, the stop is 18% of the capital (36)"), (status.State, status.Detail));

        // A play that gained 1 in the window: 35 lost, the plays buy again. Half a sale is half its proceeds.
        await Fill("p5", "XRP-EUR", "buy", 2m, 10m, 3);
        await Fill("p6", "XRP-EUR", "sell", 1m, 5.5m, 2);
        await Fill("p7", "XRP-EUR", "sell", 1m, 5.5m, 1);
        Assert.Single((await tools.Scanner.ScanAsync(CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task WithoutBinance_OrWhileHalted_ThePlaysSayWhyTheyWait()
    {
        var noFeed = new ToolRig(Burst());
        ScannerServiceTests.SeedHourly(noFeed, 0m);
        Assert.Equal("no Binance data in this engine", Assert.Single((await noFeed.Scanner.ScanAsync(CancellationToken.None)).Plays!).Detail);

        var halted = await Bursting();
        halted.Rig.KillSwitch.Engage("owner pause", "owner");
        var scan = await halted.Scanner.ScanAsync(CancellationToken.None);
        Assert.Empty(scan.Entries);
        Assert.Equal(("paused", "trading halted"), (Assert.Single(scan.Plays!).State, scan.Plays![0].Detail));
    }

    [Fact]
    public async Task TheStrategysOwnExposureCap_LeavesThePlaysTheirRoom()
    {
        // 0.002 BTC (120 EUR) of the strategy's own, of a 320 EUR account: above 40% of the capital (128)? No: 37.5%.
        var below = await Bursting(btc: 0.002m);
        await below.Scanner.ScanAsync(CancellationToken.None); // values the account
        var plays = MarketScanService.PlaysOf(Burst())! with { MainExposureFraction = 0.4m };
        Assert.Null(below.Scanner.MainExposurePaused(plays));
        Assert.Equal("strategy's own exposure cap reached (the rest is the plays')", below.Scanner.MainExposurePaused(plays with { MainExposureFraction = 0.3m }));

        // The same coins held by a play don't count against the strategy's own cap.
        below.Rig.Store.Update(s => s with
        {
            Positions = new Dictionary<string, PositionPlanState>(StringComparer.Ordinal)
            {
                [Btc] = new(Btc, PositionPlanState.Open, "c-entry", 50_000m, null, T0.AddHours(-2), EntryPrice: 59_000m, FilledAt: T0.AddHours(-2),
                    Play: MarketPlays.Burst, ExitBy: T0.AddHours(70)),
            },
        });
        Assert.Null(below.Scanner.MainExposurePaused(plays with { MainExposureFraction = 0.3m }));
        Assert.Null(below.Scanner.MainExposurePaused(null));
    }

    [Fact]
    public async Task APlayBuy_KeepsToItsSignalsSizeAndStop()
    {
        var tools = await Bursting();
        var entry = Assert.Single((await tools.Scanner.ScanAsync(CancellationToken.None)).Entries);

        var noStop = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.Orders.PlaceOrder(Btc, "buy", entry.BaseSize, entry.LimitPrice, "burst play without its stop",
            cancellationToken: CancellationToken.None));
        Assert.Contains("burst play signal", noStop.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.Orders.PlaceOrder(Btc, "buy", entry.BaseSize, entry.LimitPrice, "burst play, a lower stop",
            stopLoss: entry.StopLoss - 100m, cancellationToken: CancellationToken.None));
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.Orders.PlaceOrder(Btc, "buy", entry.BaseSize * 2, entry.LimitPrice, "burst play, twice the size",
            stopLoss: entry.StopLoss, cancellationToken: CancellationToken.None));
        Assert.DoesNotContain(tools.Journal.Entries, e => e.Kind == "order");

        // A smaller size and a higher stop are within the signal.
        await tools.Orders.PlaceOrder(Btc, "buy", entry.BaseSize / 2, entry.LimitPrice, "burst play, half the size", stopLoss: entry.StopLoss + 10m,
            cancellationToken: CancellationToken.None);
        Assert.Equal(MarketPlays.Burst, tools.Rig.Execution.PositionPlanStates[Btc].Play);
    }

    [Fact]
    public async Task APlayBuyStillOnTheBook_CountsForThePlaysLimits()
    {
        var tools = await Bursting(spec: Burst() with
        {
            Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{ "plays": { "burst": { "orderFraction": 0.15, "maxPositions": 1 } } }""")!,
        });
        tools.Rig.Store.Update(s => s with
        {
            Positions = new Dictionary<string, PositionPlanState>(StringComparer.Ordinal)
            {
                ["SOL-EUR"] = new("SOL-EUR", PositionPlanState.EntryPending, "c-sol", 90m, null, T0.AddMinutes(-3), Play: MarketPlays.Burst, ExitBy: T0.AddHours(72)),
            },
        });

        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(scan.Entries);
        Assert.Equal("full", Assert.Single(scan.Plays!).State);
    }

    [Fact]
    public async Task APlaySignal_PassesTheStrategysCoinFilter_ButTheStrategysOwnBuysKeepToTheirShare()
    {
        var tools = await Bursting(btc: 0.002m);
        await tools.Scanner.ScanAsync(CancellationToken.None);
        Assert.Null(tools.Scanner.BuyRefusal(Btc)); // the burst's coin, though held by the strategy itself

        var capped = await Bursting(spec: Burst() with
        {
            Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{ "plays": { "mainExposureFraction": 0.3, "burst": { "orderFraction": 0.15 } } }""")!,
        }, btc: 0.002m, lastVolume: 10m); // no burst: a buy of BTC is the strategy's own
        await capped.Scanner.ScanAsync(CancellationToken.None);
        Assert.Equal("BTC-EUR: strategy's own exposure cap reached (the rest is the plays').", capped.Scanner.BuyRefusal(Btc));
    }

    [Fact]
    public void Klines_WithPricesNoMarketHas_AreRefused()
    {
        foreach (var row in new[] { """[[1761134400000, "0", "2", "0", "1", "10"]]""", """[[1761134400000, "1", "0.5", "0.4", "1", "10"]]""",
                     """[[1761134400000, "1", "2", "0.5", "1.5", "-1"]]""", """[[1761134400000, "1", "2e20", "0.5", "1.5", "10"]]""" })
        {
            using var doc = JsonDocument.Parse(row);
            Assert.Throws<JsonException>(() => BinanceKlinesClient.Parse(doc.RootElement));
        }
    }

    [Theory]
    [InlineData("""{ "plays": 1 }""", "parameters.plays must be an object")]
    [InlineData("""{ "plays": { "burst": { "orderFraction": 0.5 } } }""", "plays.burst is out of range")]
    [InlineData("""{ "plays": { "burst": { "volumeMultiplier": 4 } } }""", "parameters.plays is not valid")]
    [InlineData("""{ "plays": { "rally": { "btcMoveFraction": 0.13, "stopFraction": 0.12, "holdDays": 30 } } }""", "a basket of 1 to 10 coins")]
    [InlineData("""{ "plays": { "rally": { "btcMoveFraction": 0.13, "stopFraction": 0.12, "holdDays": 30, "basket": null } } }""", "a basket of 1 to 10 coins")]
    [InlineData("""{ "plays": { "rally": { "btcMoveFraction": 0.13, "stopFraction": 0.12, "holdDays": 45, "basket": ["BTC-EUR"] } } }""", "holdDays 1-30")]
    public void PlaysOf_RefusesAMalformedPlay(string parameters, string message)
    {
        var spec = ToolRig.Observer with { Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters)! };

        var ex = Assert.Throws<InvalidOperationException>(() => MarketScanService.PlaysOf(spec));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.False(MarketScanService.HasPlays(spec)); // the Binance feed stays off, it doesn't fail
    }

    [Fact]
    public void SwingV3_IsV2WithThePlays_AndFitsTheLiveLimits()
    {
        static StrategySpec Load(int version)
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "strategies", $"swing.v{version}.json")))!.AsObject();
            var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(node["parameters"]!.ToJsonString())!;
            return ToolRig.Observer with
            {
                Name = "swing", Version = version, Parameters = parameters, Instructions = "Trade the signals.", AllowedTools = ["scan_market", "place_order"],
            };
        }

        var v3 = Load(3);
        Assert.Equal(MarketScanService.FromSpec(Load(2), new ScannerParameters()), MarketScanService.FromSpec(v3, new ScannerParameters()));

        var plays = MarketScanService.PlaysOf(v3)!;
        Assert.Equal((0.18m, 0.4m), (plays.LossStopFraction, plays.MainExposureFraction));
        Assert.Equal((0.06m, 4, 96, 168, 4m, 4m, 72, 100), (plays.Burst!.OrderFraction, plays.Burst.MaxPositions, plays.Burst.BreakoutHours, plays.Burst.VolumeHours,
            plays.Burst.VolumeMultiple, plays.Burst.StopAtr, plays.Burst.HoldHours, plays.Burst.RegimeEmaPeriod));
        Assert.Equal((0.13m, 3, 0.12m, 30, 6), (plays.Rally!.BtcMoveFraction, plays.Rally.MoveDays, plays.Rally.StopFraction, plays.Rally.HoldDays, plays.Rally.Basket.Count));
        Assert.Equal((-0.15m, 3, 0.25m, 14), (plays.Crash!.BtcMoveFraction, plays.Crash.MoveDays, plays.Crash.StopFraction, plays.Crash.HoldDays));
        Assert.Equal(plays.Rally.Basket, plays.Crash.Basket);

        // Within the live limits (the file test marks it live): plays at 6% of the capital, under the 20% per order. More is refused.
        var live = RiskLimitsOptions.LoadFor(TradingMode.Live, AppContext.BaseDirectory);
        Assert.Empty(StrategySchema.Validate(v3, live, TradingMode.Paper));
        Assert.Contains(StrategySchema.Validate(v3 with { Parameters = WithBurstOrder(v3, 0.21) }, live, TradingMode.Paper),
            e => e.Contains("plays", StringComparison.Ordinal));
    }

    private static Dictionary<string, JsonElement> WithBurstOrder(StrategySpec spec, double fraction)
    {
        var parameters = new Dictionary<string, JsonElement>(spec.Parameters!, StringComparer.Ordinal);
        var plays = JsonNode.Parse(parameters[MarketScanService.PlaysKey].GetRawText())!.AsObject();
        plays["burst"]!["orderFraction"] = fraction;
        parameters[MarketScanService.PlaysKey] = JsonSerializer.Deserialize<JsonElement>(plays.ToJsonString());
        return parameters;
    }
}

/// <summary>Binance's public candles (ADR-0031): what is read, how often, and what a failure or an unknown coin does.</summary>
public sealed class BinanceFeedTests
{
    private static readonly DateTimeOffset T0 = Rig.T0;

    private static (BinanceFeed Feed, FakeKlines Klines, MutableTime Time) Feed(Func<string, int, IReadOnlyList<Candle>?>? answer = null, Func<bool>? active = null, params string[] symbols)
    {
        var klines = new FakeKlines(answer ?? ((_, interval) => interval == MarketDataPoller.DayInterval ? PlaysTests.DailyUp() : PlaysTests.HourlyBurst()));
        var time = new MutableTime(T0);
        return (new BinanceFeed(klines, () => symbols.Length > 0 ? symbols : [Rig.Btc], time, NullLogger<BinanceFeed>.Instance, active), klines, time);
    }

    [Theory]
    [InlineData("SOL-EUR", "SOLUSDT")]
    [InlineData("pepe-eur", "PEPEUSDT")]
    [InlineData("BTC-USD", "BTCUSDT")]
    public void ARevolutPair_IsJudgedOnItsUsdtPair(string symbol, string binance) => Assert.Equal(binance, BinanceFeed.BinanceSymbolOf(symbol));

    [Fact]
    public void Klines_AreParsedFromTheirTextPrices_OldestFirst()
    {
        using var doc = JsonDocument.Parse("""
            [[1761134400000, "61000.5", "61200", "60900", "61100.25", "12.5", 1761137999999, "0", 10, "0", "0", "0"],
             [1761130800000, "60900", "61050", "60800", "61000.5", "8", 1761134399999, "0", 9, "0", "0", "0"]]
            """);

        var candles = BinanceKlinesClient.Parse(doc.RootElement);

        Assert.Equal(2, candles.Count);
        Assert.Equal(new Candle(DateTimeOffset.FromUnixTimeMilliseconds(1761130800000), 60_900m, 61_050m, 60_800m, 61_000.5m, 8m), candles[0]);
        Assert.Equal(61_100.25m, candles[1].Close);

        using var bad = JsonDocument.Parse("""[[1761134400000, "1", "2"]]""");
        Assert.Throws<JsonException>(() => BinanceKlinesClient.Parse(bad.RootElement));
        using var notArray = JsonDocument.Parse("""{ "code": -1121 }""");
        Assert.Throws<JsonException>(() => BinanceKlinesClient.Parse(notArray.RootElement));
    }

    [Fact]
    public async Task TheClient_AsksOnlyForCandles_AndAnUnknownSymbolIsNull()
    {
        var handler = new Answering(request => request.RequestUri!.Query.Contains("symbol=NOPEUSDT", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"code":-1121,"msg":"Invalid symbol."}""") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[[1761134400000,"1","2","0.5","1.5","10"]]""") });
        using var http = new HttpClient(handler);
        var client = new BinanceKlinesClient(http);

        var candles = await client.GetAsync("SOLUSDT", MarketDataPoller.HourInterval, 300, CancellationToken.None);

        Assert.Equal(1.5m, Assert.Single(candles!).Close);
        var asked = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Get, "https://data-api.binance.vision/api/v3/klines?symbol=SOLUSDT&interval=1h&limit=300"), (asked.Method, asked.RequestUri!.ToString()));
        Assert.Null(await client.GetAsync("NOPEUSDT", MarketDataPoller.DayInterval, 5000, CancellationToken.None));
        Assert.EndsWith("interval=1d&limit=1000", handler.Requests[1].RequestUri!.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync("SOL-EUR&x=1", MarketDataPoller.HourInterval, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAsync("SOLUSDT", 5, 1, CancellationToken.None));
    }

    [Fact]
    public async Task ASeries_IsReadOnceANewCandleHasClosed_AskingOnlyForTheNewOnes()
    {
        var (feed, klines, time) = Feed();

        Assert.Equal(2, await feed.PollOnceAsync(CancellationToken.None));
        Assert.Equal(new[] { ("BTCUSDT", 1440, 200), ("BTCUSDT", 60, 300) }, klines.Calls);
        Assert.Equal(200, feed.Hourly(Rig.Btc).Count);
        Assert.Equal(120, feed.BtcDaily().Count);

        Assert.Equal(0, await feed.PollOnceAsync(CancellationToken.None)); // nothing new has closed
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(20));
        Assert.Equal(1, await feed.PollOnceAsync(CancellationToken.None));
        Assert.Equal(("BTCUSDT", 60, 4), klines.Calls[^1]); // the hour to 13:00 closed: from the newest stored to the one forming
    }

    [Fact]
    public async Task TheCandleStillForming_IsNeverKept()
    {
        var forming = PlaysTests.HourlyBurst().Append(new Candle(T0, 60_300m, 60_500m, 60_200m, 60_450m, 5m)).ToList();
        var (feed, _, _) = Feed((_, interval) => interval == MarketDataPoller.DayInterval ? PlaysTests.DailyUp() : forming);

        await feed.PollOnceAsync(CancellationToken.None);

        Assert.Equal(T0.AddHours(-1), feed.Hourly(Rig.Btc)[^1].Start);
    }

    [Fact]
    public async Task ACoinBinanceDoesntList_IsAskedAgainADayLater_AFailureTwoMinutesLater()
    {
        var failing = true;
        var (feed, klines, time) = Feed((symbol, interval) => symbol switch
        {
            "SOLUSDT" => null,
            "ADAUSDT" when failing => throw new HttpRequestException("503"),
            _ => interval == MarketDataPoller.DayInterval ? PlaysTests.DailyUp() : PlaysTests.HourlyBurst(),
        }, null, "SOL-EUR", "ADA-EUR");

        Assert.Equal(1, await feed.PollOnceAsync(CancellationToken.None)); // BTC daily read; SOL unknown; ADA failed
        Assert.Empty(feed.Hourly("SOL-EUR"));
        failing = false;

        time.Advance(TimeSpan.FromMinutes(1));
        await feed.PollOnceAsync(CancellationToken.None);
        Assert.Equal(3, klines.Calls.Count); // neither asked again yet

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await feed.PollOnceAsync(CancellationToken.None));
        Assert.Equal(("ADAUSDT", 60, 300), klines.Calls[^1]);
        Assert.Equal(200, feed.Hourly("ADA-EUR").Count);

        time.Advance(TimeSpan.FromHours(24));
        await feed.PollOnceAsync(CancellationToken.None);
        Assert.Contains(("SOLUSDT", 60, 300), klines.Calls.Skip(4));
    }

    [Fact]
    public async Task WithoutPlaysRunning_NothingIsRead()
    {
        var on = false;
        var (feed, klines, _) = Feed(active: () => on);

        Assert.Equal(0, await feed.PollOnceAsync(CancellationToken.None));
        Assert.Empty(klines.Calls);
        on = true;
        Assert.Equal(2, await feed.PollOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AtMostFiveRequests_AreMadeInOnePoll()
    {
        var (feed, klines, _) = Feed(null, null, "SOL-EUR", "ADA-EUR", "XRP-EUR", "DOGE-EUR", "ETH-EUR", "BTC-EUR");

        Assert.Equal(BinanceFeed.MaxRequestsPerPoll, await feed.PollOnceAsync(CancellationToken.None));
        Assert.Equal(("BTCUSDT", 1440, 200), klines.Calls[0]); // BTC's daily trend first
        Assert.Equal(2, await feed.PollOnceAsync(CancellationToken.None)); // the rest next time
    }

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }
}
