using System.Text.Json;
using System.Text.Json.Nodes;
using Cix.Domain;
using Cix.Domain.Risk;
using Cix.Engine.AgentRuns;
using Cix.Engine.Journal;
using Cix.Engine.Scanning;
using Cix.Engine.Strategy;
using Cix.Engine.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>The built-in strategies and the schema every strategy from the database is checked against (#105).</summary>
public sealed class StrategySchemaTests
{
    private static readonly RiskLimits Limits = Rig.Limits; // 200 allocation, 40 per order, 100 per coin, 15 daily loss

    internal static StrategySpec Load(string name, int version) =>
        FileStrategySpecStore.Load(Path.Combine(AppContext.BaseDirectory, "strategies", $"{name}.v{version}.json"), name, version, TradingMode.Paper);

    /// <summary>A copy of <paramref name="spec"/> with one parameter set (a dotted key reaches into <c>scanner</c>), or removed when null.</summary>
    internal static StrategySpec With(StrategySpec spec, string key, JsonNode? value)
    {
        var parameters = JsonNode.Parse(JsonSerializer.Serialize(spec.Parameters))!.AsObject();
        var parts = key.Split('.');
        var target = parts.Length == 2 ? parameters[parts[0]]!.AsObject() : parameters;
        if (value is null)
            target.Remove(parts[^1]);
        else
            target[parts[^1]] = value;
        return spec with { Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters.ToJsonString())! };
    }

    [Theory]
    [InlineData("safe", 1)]
    [InlineData("balanced", 1)]
    [InlineData("risky", 2)]
    [InlineData("good", 1)]
    [InlineData("risky", 1)]
    [InlineData("observer", 1)]
    public void BuiltInStrategies_PassTheSchema(string name, int version) =>
        Assert.Empty(StrategySchema.Validate(Load(name, version), Limits, TradingMode.Paper));

    [Fact]
    public void SafeBalancedAndRisky_StepUpInSizeExposureAndFrequency_WithinTheMandatoryLimits()
    {
        var limits = new[] { Load("safe", 1), Load("balanced", 1), Load("risky", 2) }.Select(s => StrategySchema.LimitsOf(s, Limits)).ToList();

        Assert.True(limits[0].OrderQuote < limits[1].OrderQuote && limits[1].OrderQuote < limits[2].OrderQuote);
        Assert.True(limits[0].MaxExposureQuote < limits[1].MaxExposureQuote && limits[1].MaxExposureQuote < limits[2].MaxExposureQuote);
        Assert.True(limits[0].EntryCooldown > limits[1].EntryCooldown && limits[1].EntryCooldown > limits[2].EntryCooldown);
        Assert.True(limits[0].DailyLossStopQuote < limits[1].DailyLossStopQuote && limits[1].DailyLossStopQuote < limits[2].DailyLossStopQuote);
        Assert.All(limits, l => Assert.True(l.OrderQuote <= Limits.MaxOrderQuote && l.MaxPositionQuotePerSymbol <= Limits.MaxPositionQuotePerSymbol
            && l.MaxExposureQuote <= Limits.AllocationQuote && l.DailyLossStopQuote <= Limits.MaxDailyLossQuote));
        var safe = MarketScanService.FromSpec(Load("safe", 1), new());
        var risky = MarketScanService.FromSpec(Load("risky", 2), new());
        Assert.True(safe.RequireUptrendPullback && safe.EntryMaxAboveEma20Atr < risky.EntryMaxAboveEma20Atr && safe.MaxAtrFraction < risky.MaxAtrFraction);
    }

    [Fact]
    public void LimitsOf_ReadsTheSpecsOwnLimits_AndExposureAsAShareOfTheAllocation()
    {
        var limits = StrategySchema.LimitsOf(Load("balanced", 1), Limits);

        Assert.Equal(new StrategyLimits(10m, 25m, 100m, 6m, 1, TimeSpan.FromMinutes(60)), limits);
        Assert.Equal(StrategyLimits.None, StrategySchema.LimitsOf(null, Limits));
        Assert.Equal(new StrategyLimits(OrderQuote: 5m, DailyLossStopQuote: 5m, MaxNewPositionsPerRun: 1), StrategySchema.LimitsOf(Load("good", 1), Limits));
    }

    [Theory]
    [InlineData("orderQuote", "41", "orderQuote is 41; it must be between 1 and 40 (the maximum is the mandatory maxOrderQuote)")]
    [InlineData("maxPositionQuotePerSymbol", "101", "the mandatory maxPositionQuotePerSymbol")]
    [InlineData("dailyLossStopQuote", "16", "the mandatory maxDailyLossQuote")]
    [InlineData("maxExposureFraction", "1.5", "maxExposureFraction is 1.5")]
    [InlineData("scanner.stopAtr", "0", "scanner.stopAtr is 0")]
    [InlineData("scanner.maxEntrySignals", "1.5", "scanner.maxEntrySignals must be a whole number")]
    [InlineData("scanner.trendExits", "1", "scanner.trendExits must be true or false")]
    [InlineData("entryCooldownMinutes", "\"60\"", "entryCooldownMinutes must be a whole number")]
    [InlineData("leverage", "3", "parameters.leverage isn't a known parameter")]
    [InlineData("scanner.leverage", "3", "parameters.scanner.leverage isn't a known parameter")]
    [InlineData("timeframe", "\"5m\"", "timeframe must be \"1h\"")]
    [InlineData("scanner.riskPerTradeQuote", "11", "riskPerTradeQuote can't be more than orderQuote")]
    [InlineData("scanner.fastEmaPeriod", "50", "EMA periods must satisfy")]
    public void OutOfRangeOrUnknownParameters_AreRefused_WithAReadableReason(string key, string json, string expected)
    {
        var spec = With(Load("balanced", 1), key, JsonNode.Parse(json));

        var problems = StrategySchema.Validate(spec, Limits, TradingMode.Paper);

        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AnOrderSize_IsRequiredWhenTheSpecCanBuy()
    {
        var spec = With(Load("balanced", 1), "orderQuote", null);

        Assert.Contains("parameters.orderQuote is required when the spec allows place_order.", StrategySchema.Validate(spec, Limits, TradingMode.Paper));
    }

    [Theory]
    [InlineData(SpecStatus.Live)]
    [InlineData(SpecStatus.Draft)]
    public void OnlyPaperSpecs_CanBeActivatedFromTheDatabase(SpecStatus status)
    {
        var problems = StrategySchema.Validate(Load("balanced", 1) with { Status = status }, Limits, TradingMode.Paper);

        Assert.Contains(problems, p => p.StartsWith("status must be paper", StringComparison.Ordinal));
    }

    [Fact]
    public void LiveTrading_SwitchesNothingFromTheDatabase_Yet() =>
        Assert.Contains(StrategySchema.Validate(Load("balanced", 1), Limits, TradingMode.Live), p => p.Contains("#71", StringComparison.Ordinal));

    [Theory]
    [InlineData("record_market_view")]
    [InlineData("pause_trading")]
    [InlineData("answer_manual_request")]
    public void AnalystOperatorAndRequestTools_CanNeverBeGranted(string tool)
    {
        var spec = Load("balanced", 1) with { AllowedTools = ["get_strategy_spec", tool] };

        Assert.Contains(StrategySchema.Validate(spec, Limits, TradingMode.Paper), p => p.StartsWith("allowedTools may only list trading tools", StringComparison.Ordinal));
    }

    [Fact]
    public void TextFromTheDocument_IsMadePlainInErrors()
    {
        var spec = With(Load("balanced", 1), "bad\nkey‮<script>", JsonNode.Parse("1"));

        var problem = Assert.Single(StrategySchema.Validate(spec, Limits, TradingMode.Paper));

        Assert.Equal("parameters.bad?key??script? isn't a known parameter.", problem);
    }

    [Fact]
    public void TheSchema_ExplainsEveryParameter_WithRangesCappedByTheMandatoryLimits()
    {
        var schema = StrategySchema.Document(Limits, TradingMode.Paper, Rig.T0);

        Assert.Equal("paper", schema.Mode);
        Assert.All(schema.Parameters, p => Assert.False(string.IsNullOrWhiteSpace(p.Explanation)));
        Assert.Equal((1m, 40m, "maxOrderQuote"), schema.Parameters.Single(p => p.Key == "orderQuote") is var q ? (q.Min!.Value, q.Max!.Value, q.BoundBy) : default);
        Assert.Equal("open-positions-too", schema.Parameters.Single(p => p.Key == "scanner.trendExits").Applies);
        Assert.Equal("new-trades", schema.Parameters.Single(p => p.Key == "scanner.stopAtr").Applies);
        foreach (var group in new[] { "trend", "entry", "size", "stops", "exposure", "frequency", "exits" })
            Assert.Contains(schema.Parameters, p => p.Group == group);
        Assert.Equal("40", schema.Mandatory.Single(m => m.Key == "maxOrderQuote").Value);
        Assert.DoesNotContain(schema.Parameters, p => p.Explanation.Contains("guarantee", StringComparison.OrdinalIgnoreCase));

        // Every key a built-in strategy uses is explained, so the dashboard can show all of them.
        var keys = schema.Parameters.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var used = Load("risky", 2).Parameters.SelectMany(p => p.Key == StrategySchema.Scanner
            ? p.Value.EnumerateObject().Select(s => $"scanner.{s.Name}")
            : [p.Key]).Where(k => k != "timeframe");
        Assert.All(used, k => Assert.Contains(k, keys));
    }
}

/// <summary>Switching the paper engine's strategy from the database at run time (#105).</summary>
public sealed class StrategySelectionTests
{
    private sealed class FailingJournal(InMemoryJournal inner) : IJournal
    {
        public bool Fail { get; set; }

        public Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken) =>
            Fail ? throw new IOException("journal down") : inner.AppendAsync(entry, cancellationToken);

        public Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken cancellationToken) => inner.QueryAsync(query, cancellationToken);
    }

    private sealed class Setup
    {
        public Setup(StrategySpec? initial = null, AgentRunScheduler? runs = null, FixedLease? lease = null)
        {
            Journal = new FailingJournal(Entries);
            Specs = new RuntimeStrategySpecs(initial ?? StrategySchemaTests.Load("good", 1));
            Service = new StrategySelectionService(
                Specs, Repository, Rig.Limits, new TradingContext(TradingMode.Paper), Journal, Time, NullLogger<StrategySelectionService>.Instance, lease, runs);
        }

        public MutableTime Time { get; } = new(Rig.T0);
        public InMemoryJournal Entries { get; } = new();
        public FailingJournal Journal { get; }
        public InMemoryStrategyRepository Repository { get; } = new();
        public RuntimeStrategySpecs Specs { get; }
        public StrategySelectionService Service { get; }

        public IReadOnlyList<JournalEntry> Switches => Entries.Entries.Where(e => e.Kind == StrategySelectionService.Kind).ToList();

        public void Select(string name, int version, int minute = 0, string? by = "owner") =>
            Repository.Selections[TradingMode.Paper] = new StrategySelection(name, version, by, Rig.T0.AddMinutes(minute), "testing");

        public Task<bool> Poll() => Service.PollOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Seeding_AddsTheRunnableTradingSpecs_Once_AndNeverTheAnalyst()
    {
        var setup = new Setup();

        var added = await setup.Service.SeedAsync("strategies", CancellationToken.None);
        var again = await setup.Service.SeedAsync("strategies", CancellationToken.None);

        var names = setup.Repository.Versions.Select(v => $"{v.Name}@v{v.Version}").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["balanced@v1", "good@v1", "observer@v1", "risky@v1", "risky@v2", "safe@v1"], names);
        Assert.Equal((6, 0), (added, again));
    }

    [Fact]
    public async Task Initialize_WritesTheSchema_AndAppliesTheSavedSelection()
    {
        var setup = new Setup();
        setup.Select("safe", 1);

        await setup.Service.InitializeAsync("strategies", CancellationToken.None);

        Assert.Equal("paper", setup.Repository.Schema!.Mode);
        Assert.Equal("safe", setup.Specs.Active!.Name);
        Assert.Equal("active", setup.Service.Status.State);
    }

    [Fact]
    public async Task WithoutASelection_TheConfiguredSpecRuns()
    {
        var setup = new Setup();

        Assert.False(await setup.Poll());

        Assert.Equal("good", setup.Specs.Active!.Name);
        Assert.Equal(("config", (string?)null), (setup.Service.Status.State, setup.Service.Status.Requested));
        Assert.Empty(setup.Switches);
    }

    [Fact]
    public async Task ASelection_IsValidated_Journaled_Archived_AndActivated()
    {
        var setup = new Setup();
        await setup.Service.SeedAsync("strategies", CancellationToken.None);
        var before = setup.Specs.Active!.Label;
        setup.Select("balanced", 1);

        Assert.True(await setup.Poll());

        var active = setup.Specs.Active!;
        Assert.Equal(("balanced", 1, StrategyHash.Of(active)), (active.Name, active.Version, active.Hash));
        var entry = Assert.Single(setup.Switches);
        Assert.Equal(("engine", active.Label, "activated"), (entry.Actor, entry.SpecVersion, entry.Data!["action"]));
        Assert.Equal((before, active.Label, "owner", "testing"), (entry.Data["from"], entry.Data["to"], entry.Data["requestedBy"], entry.Data["note"]));
        Assert.Contains("Open positions keep their stop and target", entry.Data["applies"], StringComparison.Ordinal);
        Assert.Contains(active.Label, setup.Repository.Archived.Keys);
        Assert.Equal(("active", "balanced@v1", Rig.T0), (setup.Service.Status.State, setup.Service.Status.Requested, setup.Service.Status.ActivatedAt));

        Assert.False(await setup.Poll()); // the same selection is handled once
        Assert.Single(setup.Switches);
    }

    [Fact]
    public async Task SelectingTheVersionAlreadyRunning_ChangesNothing()
    {
        var setup = new Setup();
        await setup.Service.SeedAsync("strategies", CancellationToken.None);
        setup.Select("good", 1);

        Assert.False(await setup.Poll());

        Assert.Empty(setup.Switches);
        Assert.Equal("active", setup.Service.Status.State);
    }

    [Fact]
    public async Task AnInvalidVersion_IsRefusedOnce_AndTheRunningSpecStays()
    {
        var setup = new Setup();
        setup.Repository.Put(StrategySchemaTests.With(StrategySchemaTests.Load("balanced", 1), "orderQuote", JsonValue.Create(500)) with { Version = 2 });
        setup.Select("balanced", 2);
        var before = setup.Specs.Active;

        Assert.False(await setup.Poll());
        Assert.False(await setup.Poll());

        Assert.Same(before, setup.Specs.Active);
        var refused = Assert.Single(setup.Switches);
        Assert.Equal("refused", refused.Data!["action"]);
        Assert.Contains("the mandatory maxOrderQuote", refused.Data["problems"], StringComparison.Ordinal);
        Assert.Equal("refused", setup.Service.Status.State);
        Assert.Contains(setup.Service.Status.Problems, p => p.Contains("orderQuote is 500", StringComparison.Ordinal));

        // Saved again after a fix, it is tried again.
        setup.Repository.Put(StrategySchemaTests.Load("balanced", 1) with { Version = 2 });
        setup.Select("balanced", 2, minute: 5);
        Assert.True(await setup.Poll());
        Assert.Equal("balanced@v2", $"{setup.Specs.Active!.Name}@v{setup.Specs.Active.Version}");
    }

    [Fact]
    public async Task AMissingVersion_OrOneThatNamesAnother_IsRefused()
    {
        var setup = new Setup();
        setup.Select("nonexistent", 3);
        Assert.False(await setup.Poll());
        Assert.Contains("There is no strategy version nonexistent@v3", setup.Service.Status.Problems[0], StringComparison.Ordinal);

        setup.Repository.Put(StrategySchemaTests.Load("safe", 1) with { Version = 4 });
        setup.Select("safe", 4, minute: 1);
        Assert.True(await setup.Poll()); // names match: fine

        var setup2 = new Setup();
        setup2.Repository.Put(StrategySchemaTests.Load("safe", 1));
        setup2.Repository.Selections[TradingMode.Paper] = new StrategySelection("safe", 1, null, Rig.T0, null);
        Assert.True(await setup2.Poll());
    }

    [Fact]
    public async Task TheSwitch_WaitsForATraderRunInProgress()
    {
        var runs = new AgentRunSchedulerTests.Setup();
        await runs.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = runs.Queue.Sent.Single().Run;
        Assert.Equal(AgentRunAnswer.Yes, (await runs.Scheduler.ClaimAsync(run.Id, "trader", null, CancellationToken.None)).Answer);
        var setup = new Setup(runs: runs.Scheduler);
        await setup.Service.SeedAsync("strategies", CancellationToken.None);
        setup.Select("risky", 2);

        Assert.False(await setup.Poll());
        Assert.Equal(("good", "pending"), (setup.Specs.Active!.Name, setup.Service.Status.State));

        Assert.Equal(AgentRunAnswer.Yes, await runs.Scheduler.CompleteAsync(run.Id, "trader", AgentRunOutcome.Done, CancellationToken.None));
        Assert.True(await setup.Poll());
        Assert.Equal("risky", setup.Specs.Active!.Name);
    }

    [Fact]
    public async Task WhenTheJournalFails_NothingSwitches_AndItIsRetried()
    {
        var setup = new Setup();
        await setup.Service.SeedAsync("strategies", CancellationToken.None);
        setup.Select("safe", 1);
        setup.Journal.Fail = true;

        Assert.False(await setup.Poll());
        Assert.Equal("good", setup.Specs.Active!.Name);

        setup.Journal.Fail = false;
        Assert.True(await setup.Poll());
        Assert.Equal("safe", setup.Specs.Active!.Name);
    }

    [Fact]
    public async Task ADatabaseOutage_LeavesTheRunningSpec_AndShowsInTheStatus()
    {
        var setup = new Setup();
        setup.Repository.Failure = new HttpRequestException("database down");

        Assert.False(await setup.Poll());

        Assert.Equal(("good", "unreadable"), (setup.Specs.Active!.Name, setup.Service.Status.State));
    }

    [Fact]
    public async Task AnEngineWithoutTheLease_ActsOnNothing()
    {
        var setup = new Setup(lease: new FixedLease(false));
        await setup.Service.SeedAsync("strategies", CancellationToken.None);
        setup.Select("safe", 1);

        Assert.False(await setup.Poll());
        Assert.Equal("good", setup.Specs.Active!.Name);
    }

    [Fact]
    public void FreeTextFromTheSelection_IsOnePlainBoundedLine() =>
        Assert.Equal("owner via dashboard", StrategySelectionService.Clean("owner\n via‮ dashboard\t", 80));
}

/// <summary>The strategy's own limits on agents' buys, enforced by the engine before the risk gate.</summary>
public sealed class StrategyLimitTests
{
    private const string Why = "Signal entry BTC-EUR rank 1: uptrend pullback.";

    private static ToolRig Trader(StrategySpec spec) => new(spec);

    private static StrategySpec Balanced => StrategySchemaTests.Load("balanced", 1);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ABuyAboveTheStrategysOrderSize_IsReduced_AndTheAgentIsTold()
    {
        var tools = Trader(Balanced); // orderQuote 10, so up to 11 passes as it is

        var result = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None)); // ~30 EUR

        Assert.Equal(("placed", "strategy-order-size"), (result.GetProperty("status").GetString(), result.GetProperty("rule").GetString()));
        Assert.Equal(0.00016680m, result.GetProperty("approved").GetProperty("size").GetDecimal()); // 10 / 59,950, down to the step
    }

    [Fact]
    public async Task TheRiskGate_StaysFinal_AfterTheStrategyAllowsABuy()
    {
        var tools = Trader(Balanced);

        var result = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0001m, 64_000m, Why, cancellationToken: CancellationToken.None));

        Assert.Equal(("rejected", "price-band"), (result.GetProperty("status").GetString(), result.GetProperty("rule").GetString()));
        Assert.Null(tools.Rig.Execution.LastBuyAt); // nothing sent, so no cooldown starts
    }

    [Fact]
    public async Task PositionsPerRun_AndTheCooldown_AreEnforced_AndNeverCountTowardTheRejectStreak()
    {
        var spec = StrategySchemaTests.With(Balanced, "maxNewPositionsPerRun", JsonValue.Create(1));
        var tools = Trader(spec);
        tools.Header(ToolServerOptions.RunHeader, "run-1");

        var first = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0001m, 59_950m, Why, cancellationToken: CancellationToken.None));
        var second = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0001m, 59_950m, Why, cancellationToken: CancellationToken.None));
        tools.Header(ToolServerOptions.RunHeader, "run-2");
        tools.Rig.Time.Advance(TimeSpan.FromMinutes(30));
        var cooling = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0001m, 59_950m, Why, cancellationToken: CancellationToken.None));

        Assert.Equal("placed", first.GetProperty("status").GetString());
        Assert.Equal(("rejected", "strategy-positions-per-run"), (second.GetProperty("status").GetString(), second.GetProperty("rule").GetString()));
        Assert.Equal(("rejected", "strategy-cooldown"), (cooling.GetProperty("status").GetString(), cooling.GetProperty("rule").GetString()));
        Assert.False(tools.Rig.KillSwitch.IsEngaged);

        tools.Rig.Time.Advance(TimeSpan.FromMinutes(30));
        tools.Rig.Quote(60_000.00m, 60_010.00m);
        var later = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0001m, 59_950m, Why, cancellationToken: CancellationToken.None));
        Assert.Equal("placed", later.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Sells_AndSpecsWithoutLimits_AreUnaffected()
    {
        var tools = new ToolRig(ToolRig.Observer with { Name = "trader", AllowedTools = ["place_order"] }, btc: 0.001m);

        var buy = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0005m, 59_950m, Why, cancellationToken: CancellationToken.None));
        var sell = Parse(await tools.Orders.PlaceOrder("BTC-EUR", "sell", 0.0005m, 60_000m, Why, cancellationToken: CancellationToken.None));

        Assert.Equal(("placed", "ok"), (buy.GetProperty("status").GetString(), buy.GetProperty("rule").GetString()));
        Assert.Equal("placed", sell.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TheScanner_OffersNoEntries_WhileTheCooldownRuns()
    {
        var spec = Balanced;
        var tools = new ToolRig(spec);
        var scanner = new MarketScanService(
            tools.Universe, tools.Features, tools.Candles, tools.Rig.Pairs, tools.Rig.Execution, tools.Rig.KillSwitch, tools.Journal, tools.Specs,
            new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanService>.Instance, tools.Views, riskLimits: Rig.Limits);
        await tools.Orders.PlaceOrder("BTC-EUR", "buy", 0.0001m, 59_950m, Why, cancellationToken: CancellationToken.None);

        var result = await scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(result.Entries);
        Assert.All(result.Ranked, r => Assert.StartsWith("cooldown until", r.NoEntryReason ?? "cooldown until", StringComparison.Ordinal));
        Assert.NotNull(tools.Rig.Execution.LastBuyAt);
    }
}
