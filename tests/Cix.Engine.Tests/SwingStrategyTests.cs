using System.Text.Json;
using System.Text.Json.Nodes;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.MarketData;
using Cix.Domain.Risk;
using Cix.Domain.Scanning;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Scanning;
using Cix.Engine.Strategy;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>The daily swing strategy in the engine (ADR-0028): its spec, daily candles, the scan on them and the trailing stop.</summary>
public sealed class SwingStrategyTests
{
    private const string Btc = Cix.Engine.Tests.Rig.Btc;

    /// <summary>A spec with swing.v1's parameters, from the file, as the scanner reads them.</summary>
    private static StrategySpec Swing()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "strategies", "swing.v1.json")))!.AsObject();
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(node["parameters"]!.ToJsonString())!;
        return ToolRig.Observer with { Name = "swing", Parameters = parameters, AllowedTools = ["scan_market"] };
    }

    /// <summary>
    /// 120 daily BTC candles to the one that started 2026-10-21 (closed at midnight before the rig's 12:00): rising 150 a day
    /// from 40,000 with highs and lows 600 either side, then a close at 60,000, above the 58,300 high of the 20 days before.
    /// </summary>
    private static List<Candle> DailyBreakout()
    {
        var newest = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, 120).Select(i =>
        {
            var (close, high, low) = i < 119 ? (40_000m + (150m * i), 40_600m + (150m * i), 39_400m + (150m * i)) : (60_000m, 60_300m, 57_500m);
            return new Candle(newest.AddDays(i - 119), close, high, low, close, 1m);
        }).ToList();
    }

    private static ToolRig Swinging(decimal btc = 0m)
    {
        var tools = new ToolRig(Swing(), btc: btc);
        ScannerServiceTests.SeedHourly(tools, 20m); // fresh features
        tools.Candles.Upsert(Btc, MarketDataPoller.DayInterval, DailyBreakout());
        return tools;
    }

    [Fact]
    public void TheSwingSpec_ReadsAsADailyScan_WithItsTrailingStopAndNoTarget()
    {
        var p = MarketScanService.FromSpec(Swing(), new ScannerParameters());

        Assert.Equal((ScannerParameters.Daily, 10, 50, 100, 20), (p.Timeframe, p.FastEmaPeriod, p.SlowEmaPeriod, p.RegimeEmaPeriod, p.BreakoutCandles));
        Assert.Equal((3m, 3m, 0m, 4m, 50m, 0.003m), (p.StopAtr, p.TrailAtr, p.RewardRisk, p.RiskPerTradeQuote, p.OrderQuote, p.MaxSpreadFraction));
        Assert.Equal(ScannerParameters.Hourly, MarketScanService.FromSpec(ToolRig.Observer, new ScannerParameters()).Timeframe);
    }

    [Theory]
    [InlineData("""{ "timeframe": "1d", "scanner": { "timeframe": "1d" } }""", "set timeframe once")]
    [InlineData("""{ "timeframe": 1 }""", "timeframe must be text")]
    [InlineData("""{ "timeframe": "4h" }""", "timeframe must be")]
    [InlineData("""{ "scanner": { "trailAtr": 3 } }""", "daily candles only")]
    public void FromSpec_RefusesATimeframeItCantRun(string parameters, string message)
    {
        var spec = ToolRig.Observer with { Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters)! };

        var ex = Assert.Throws<InvalidOperationException>(() => MarketScanService.FromSpec(spec, new ScannerParameters()));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_AcceptsTheDailyTimeframe_AndExplainsTheNewParameters()
    {
        var spec = Swing() with { Status = SpecStatus.Paper, Instructions = "Trade the signals.", AllowedTools = ["scan_market", "place_order"] };
        Assert.Empty(StrategySchema.Validate(spec, Cix.Engine.Tests.Rig.Limits with { MaxOrderQuote = 50m, MaxDailyLossQuote = 20m, MaxPositionQuotePerSymbol = 50m }, TradingMode.Paper));

        var fourHours = StrategySchemaTests.With(spec, "timeframe", "4h");
        Assert.Contains("parameters.timeframe must be \"1h\" (hourly candles) or \"1d\" (daily candles).",
            StrategySchema.Validate(fourHours, Cix.Engine.Tests.Rig.Limits with { MaxOrderQuote = 50m, MaxDailyLossQuote = 20m, MaxPositionQuotePerSymbol = 50m }, TradingMode.Paper));

        var keys = StrategySchema.Parameters(Cix.Engine.Tests.Rig.Limits).Select(p => p.Key).ToList();
        Assert.Contains("scanner.breakoutCandles", keys);
        Assert.Contains("scanner.regimeEmaPeriod", keys);
        Assert.Contains("scanner.entryMaxAboveBreakoutAtr", keys);
        Assert.Equal("open-positions-too", StrategySchema.Parameters(Cix.Engine.Tests.Rig.Limits).Single(p => p.Key == "scanner.trailAtr").Applies);
        Assert.Equal(0m, StrategySchema.Parameters(Cix.Engine.Tests.Rig.Limits).Single(p => p.Key == "scanner.rewardRisk").Min);
    }

    [Fact]
    public async Task DailyBreakout_IsABuySignal_WithAStopAndNoTarget()
    {
        var tools = Swinging();

        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);

        var entry = Assert.Single(scan.Entries);
        Assert.Equal((Btc, 60_010m), (entry.Symbol, entry.LimitPrice));
        Assert.Null(entry.TakeProfit);
        var atr = Indicators.Atr(DailyBreakout(), 14)!.Value;
        Assert.Equal(Math.Floor((60_010m - (3m * atr)) / 0.01m) * 0.01m, entry.StopLoss);
        Assert.True(entry.QuoteSize <= 50m, $"{entry.QuoteSize}");
        var signal = Assert.Single(tools.Journal.Entries, e => e.Kind == MarketScanService.SignalKind);
        Assert.Contains("no target", signal.Summary, StringComparison.Ordinal);
        Assert.False(signal.Data!.ContainsKey("takeProfit"));
    }

    [Fact]
    public async Task HeldPosition_HasItsStopRaisedToTheTrail_OnlyUpward_AndJournaled()
    {
        var tools = Swinging(btc: 0.001m);
        var filled = Cix.Engine.Tests.Rig.T0.AddDays(-10);
        tools.Rig.Store.Update(s => s with
        {
            Positions = new Dictionary<string, PositionPlanState>(StringComparer.Ordinal)
            {
                [Btc] = new(Btc, PositionPlanState.Open, "c-entry", 50_000m, null, filled, EntryPrice: 58_000m, FilledAt: filled),
            },
        });

        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);
        var atr = Indicators.Atr(DailyBreakout(), 14)!.Value;
        var level = Math.Floor((60_000m - (3m * atr)) / 0.01m) * 0.01m; // the highest daily close since the fill, less 3 ATR
        var raise = Assert.Single(scan.StopRaises!);
        Assert.Equal((Btc, 50_000m, level), (raise.Symbol, raise.From, raise.To));
        Assert.Empty(scan.Entries); // already held

        Assert.Equal(1, await tools.Scanner.RaiseStopsAsync(scan, CancellationToken.None));
        Assert.Equal(level, tools.Rig.Execution.PositionPlanStates[Btc].StopLoss);
        var journaled = Assert.Single(tools.Journal.Entries, e => e.Kind == MarketScanService.StopRaisedKind);
        Assert.Equal(("engine", "50000", level.ToString(System.Globalization.CultureInfo.InvariantCulture)), (journaled.Actor, journaled.Data!["from"], journaled.Data["to"]));

        // Applied once: the same raise again, or a lower one, changes nothing and journals nothing.
        Assert.Equal(0, await tools.Scanner.RaiseStopsAsync(scan, CancellationToken.None));
        var lower = scan with { StopRaises = [new StopRaise(Btc, level, level - 100m, "lower")] };
        Assert.Equal(0, await tools.Scanner.RaiseStopsAsync(lower, CancellationToken.None));
        Assert.Equal(level, tools.Rig.Execution.PositionPlanStates[Btc].StopLoss);
        Assert.Single(tools.Journal.Entries, e => e.Kind == MarketScanService.StopRaisedKind);
        Assert.Empty((await tools.Scanner.ScanAsync(CancellationToken.None)).StopRaises!); // the next scan sees the raised stop
    }

    [Fact]
    public async Task ARaisedStop_IsArmedByTheProtection_AndSellsWhenTheBidReachesIt()
    {
        var tools = Swinging(btc: 0.001m);
        var filled = Cix.Engine.Tests.Rig.T0.AddDays(-10);
        tools.Rig.Store.Update(s => s with
        {
            Positions = new Dictionary<string, PositionPlanState>(StringComparer.Ordinal)
            {
                [Btc] = new(Btc, PositionPlanState.Open, "c-entry", 50_000m, null, filled, EntryPrice: 58_000m, FilledAt: filled),
            },
        });
        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(1, await tools.Scanner.RaiseStopsAsync(scan, CancellationToken.None));
        var level = Assert.Single(scan.StopRaises!).To;

        var protection = new ProtectionRig(tools.Rig);
        Assert.Equal(0, await protection.Service.RefreshAsync(CancellationToken.None)); // armed at the new level; the bid is above it
        Assert.Equal(level, protection.Trigger.StopFor(Btc));

        protection.Feed(new MarketQuote(Btc, level - 5m, 0.01m, level + 5m, 0.01m, tools.Rig.Time.GetUtcNow()));
        Assert.Equal(1, await protection.Service.RefreshAsync(CancellationToken.None));
        await protection.Service.WhenIdleAsync();

        Assert.Equal(0m, await protection.Held());
        Assert.Equal("placed", Assert.Single(protection.Orders()).Data!["status"]); // through the risk gate, as any stop
    }

    [Fact]
    public async Task APositionPlannedOnlyInTheJournal_IsNotTrailed()
    {
        var tools = Swinging(btc: 0.001m);
        var filled = Cix.Engine.Tests.Rig.T0.AddDays(-10);
        await tools.Journal.AppendAsync(
            new JournalEntry("fill-1", filled, "fill", "engine", null, null, "buy 0.001 of 0.001 BTC-EUR (filled)",
                new Dictionary<string, string> { ["symbol"] = Btc, ["side"] = "buy", ["averagePrice"] = "58000", ["stopLoss"] = "50000", ["clientOrderId"] = "c-old" }),
            CancellationToken.None);

        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(scan.StopRaises!); // no plan in the engine state to raise: no raise is suggested every scan
    }

    [Fact]
    public async Task ACoinSoldToday_IsNotBoughtBackOnTheSameBreakout()
    {
        var tools = Swinging();
        var at = tools.Rig.Time.GetUtcNow().AddHours(-2);
        await tools.Journal.AppendAsync(
            new JournalEntry("fill-2", at, "fill", "engine", null, null, "sell 0.001 of 0.001 BTC-EUR (filled)",
                new Dictionary<string, string> { ["symbol"] = Btc, ["side"] = "sell", ["averagePrice"] = "55000", ["clientOrderId"] = "c-stop" }),
            CancellationToken.None);

        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(scan.Entries);
        Assert.Equal("sold since the breakout", Assert.Single(scan.Ranked, r => r.Symbol == Btc).NoEntryReason);
    }

    [Fact]
    public async Task WhileTradingIsHalted_NoStopIsRaised()
    {
        var tools = Swinging(btc: 0.001m);
        var filled = Cix.Engine.Tests.Rig.T0.AddDays(-10);
        tools.Rig.Store.Update(s => s with
        {
            Positions = new Dictionary<string, PositionPlanState>(StringComparer.Ordinal)
            {
                [Btc] = new(Btc, PositionPlanState.Open, "c-entry", 50_000m, null, filled, EntryPrice: 58_000m, FilledAt: filled),
            },
        });
        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);
        Assert.Single(scan.StopRaises!);

        tools.Rig.KillSwitch.Engage("owner pause", "owner");
        Assert.Equal(0, await tools.Scanner.RaiseStopsAsync(scan, CancellationToken.None));
        Assert.Equal(50_000m, tools.Rig.Execution.PositionPlanStates[Btc].StopLoss); // the stop set at the buy stays armed
        Assert.DoesNotContain(tools.Journal.Entries, e => e.Kind == MarketScanService.StopRaisedKind);
    }

    [Fact]
    public void StopRaise_OnlyMovesAnOpenPlansStopUp()
    {
        var at = Cix.Engine.Tests.Rig.T0;
        var state = EngineState.Initial with
        {
            Positions = new Dictionary<string, PositionPlanState>(StringComparer.Ordinal)
            {
                [Btc] = new(Btc, PositionPlanState.Open, "a", 100m, 130m, at),
                ["SOL-EUR"] = new("SOL-EUR", PositionPlanState.EntryPending, "b", 10m, null, at),
                ["ETH-EUR"] = new("ETH-EUR", PositionPlanState.Open, "c", null, 3_000m, at),
            },
        };

        var raised = state.WithStopRaised(Btc, 110m);
        Assert.Equal((110m, (decimal?)130m), (raised.Plans[Btc].StopLoss!.Value, raised.Plans[Btc].TakeProfit));
        Assert.Same(raised, raised.WithStopRaised(Btc, 105m));     // never down
        Assert.Same(raised, raised.WithStopRaised(Btc, 110m));     // nor to the same level
        Assert.Same(state, state.WithStopRaised("SOL-EUR", 12m));  // not before the buy fills
        Assert.Same(state, state.WithStopRaised("ETH-EUR", 2_500m)); // no stop to raise
        Assert.Same(state, state.WithStopRaised("ADA-EUR", 1m));   // no plan
    }

    [Fact]
    public void AFurtherBuy_NeverLowersAPositionsStop()
    {
        var at = Cix.Engine.Tests.Rig.T0;
        var plan = new PositionPlanState(Btc, PositionPlanState.Open, "a", 170m, null, at, EntryPrice: 150m, FilledAt: at); // a trailed stop
        var state = EngineState.Initial with { Positions = new Dictionary<string, PositionPlanState>(StringComparer.Ordinal) { [Btc] = plan } };

        var lower = state.WithSent(new SentOrder("b", Btc, "buy", 0.1m, 190m, at, "place_order", StopLoss: 2m)).WithEntryFilled(Btc, "b", 190m, at);
        Assert.Equal(170m, lower.Plans[Btc].StopLoss);
        var higher = state.WithSent(new SentOrder("c", Btc, "buy", 0.1m, 190m, at, "place_order", StopLoss: 180m)).WithEntryFilled(Btc, "c", 190m, at);
        Assert.Equal(180m, higher.Plans[Btc].StopLoss);
    }

    /// <summary>
    /// Daily candles that start at London midnight, as Revolut X's do (00:00 UTC in winter, 23:00 UTC in summer), including
    /// the one still open, and hourly candles on the UTC hour. The clocks go forward on 30 March 2025 and 29 March 2026 (those
    /// days last 23 hours), so the first day to start at 23:00 UTC is 31 March 2025 and 30 March 2026; the last is the 25-hour
    /// day of 26 October 2025 and 25 October 2026.
    /// </summary>
    private sealed class LondonCandles(TimeProvider time) : IMarketDataSource, IQuoteSource
    {
        public static DateTimeOffset DayStart(DateOnly day)
        {
            var summer = (day >= new DateOnly(2025, 3, 31) && day <= new DateOnly(2025, 10, 26)) || (day >= new DateOnly(2026, 3, 30) && day <= new DateOnly(2026, 10, 25));
            var midnight = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return summer ? midnight.AddHours(-1) : midnight;
        }

        /// <summary>When the daily candle that started at <paramref name="start"/> closes.</summary>
        public static DateTimeOffset DayEnd(DateTimeOffset start) => DayStart(DateOnly.FromDateTime(start.AddHours(2).UtcDateTime).AddDays(1));

        public Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken)
        {
            var now = time.GetUtcNow();
            var candles = new List<Candle>();
            if (intervalMinutes == MarketDataPoller.DayInterval)
            {
                for (var day = DateOnly.FromDateTime(since.UtcDateTime).AddDays(-1); DayStart(day) <= now && candles.Count < 1000; day = day.AddDays(1))
                {
                    if (DayStart(day) >= since)
                        candles.Add(new Candle(DayStart(day), 100m, 101m, 99m, 100m, 1m)); // the last one is still open
                }
            }
            else
            {
                var interval = TimeSpan.FromMinutes(intervalMinutes);
                var first = new DateTimeOffset(since.UtcTicks + ((interval.Ticks - (since.UtcTicks % interval.Ticks)) % interval.Ticks), TimeSpan.Zero);
                for (var t = first; t <= now && candles.Count < 1000; t += interval)
                    candles.Add(new Candle(t, 100m, 101m, 99m, 100m, 1m));
            }

            return Task.FromResult<IReadOnlyList<Candle>>(candles);
        }

        public Task<MarketQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken) =>
            Task.FromResult(new MarketQuote(symbol, 99.95m, 1m, 100.05m, 1m, time.GetUtcNow()));
    }

    [Theory]
    [InlineData("2026-03-26T12:00:30Z")] // spring: the day of 29 March lasts 23 hours
    [InlineData("2026-10-22T12:00:30Z")] // autumn: the day of 25 October lasts 25 hours
    public async Task DailyCandles_AcrossAClockChange_AreStoredOnceClosed_WithoutGaps(string start)
    {
        var time = new MutableTime(DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture));
        var source = new LondonCandles(time);
        var store = new InMemoryCandleStore();
        var poller = new MarketDataPoller(source, source, store, new MarketState(time), [], new MarketDataOptions(), [Btc], time, NullLogger<MarketDataPoller>.Instance);

        var end = time.GetUtcNow().AddDays(6);
        while (time.GetUtcNow() < end)
        {
            await poller.PollOnceAsync(CancellationToken.None);
            var at = time.GetUtcNow();
            var latest = store.Latest(Btc, MarketDataPoller.DayInterval);
            Assert.True(latest is null || LondonCandles.DayEnd(latest.Start) <= at, $"{latest?.Start:u} stored before it closed, at {at:u}");
            var closedDay = LondonCandles.DayStart(DateOnly.FromDateTime(at.AddHours(1).UtcDateTime).AddDays(-1));
            if (LondonCandles.DayEnd(closedDay) > at)
                closedDay = LondonCandles.DayStart(DateOnly.FromDateTime(closedDay.AddHours(2).UtcDateTime).AddDays(-1));
            Assert.True(latest is null || latest.Start == closedDay || at - LondonCandles.DayEnd(closedDay) < TimeSpan.FromMinutes(22),
                $"the day from {closedDay:u} closed at {LondonCandles.DayEnd(closedDay):u} and still wasn't stored at {at:u}");
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var stored = store.Recent(Btc, MarketDataPoller.DayInterval, 1000).Select(c => c.Start).ToList();
        var now = time.GetUtcNow();
        var expected = new List<DateTimeOffset>();
        for (var day = DateOnly.FromDateTime(stored[0].AddHours(2).UtcDateTime); LondonCandles.DayEnd(LondonCandles.DayStart(day)) <= now; day = day.AddDays(1))
            expected.Add(LondonCandles.DayStart(day));
        Assert.Equal(expected, stored); // every closed day once, the short and the long one included
        Assert.InRange(stored.Count, 330, 337); // the 330-day first load and the six days after
    }
}
