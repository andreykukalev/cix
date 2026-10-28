using System.Text.Json;
using System.Text.Json.Nodes;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.Manual;
using Cix.Engine.Paper;
using Cix.Engine.Persistence;
using Cix.Engine.Scanning;
using Cix.Engine.Strategy;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

public sealed class PaperAccountPersistenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static PaperExchange Paper(MutableTime time, decimal eur = 200m)
    {
        var options = new PaperOptions();
        options.InitialBalances["EUR"] = eur;
        return new PaperExchange(new FakeMarketData(), options, time);
    }

    private static MarketQuote Quote(MutableTime time, decimal bid, decimal ask) => new("BTC-EUR", bid, 0.01m, ask, 0.01m, time.GetUtcNow());

    [Fact]
    public async Task RestoredAccount_HasTheSameBalancesAndOrders_AndRestingOrdersStillFill()
    {
        var time = new MutableTime(T0);
        var before = Paper(time);
        before.OnQuote(Quote(time, 60_000m, 60_010m));
        await before.PlaceLimitOrderAsync(new OrderIntent("BTC-EUR", OrderSide.Buy, 0.0005m, 60_010m, "taker").Approved(), false, CancellationToken.None); // fills
        var resting = await before.PlaceLimitOrderAsync(new OrderIntent("BTC-EUR", OrderSide.Buy, 0.0005m, 59_000m, "maker").Approved(), false, CancellationToken.None);
        var saved = before.ExportState();

        var after = Paper(time, eur: 999m); // configured balances are ignored once a saved account exists
        after.RestoreState(saved);
        await after.WarmUpAsync(CancellationToken.None);
        after.OnQuote(Quote(time, 58_900m, 58_950m)); // trades through the resting buy

        Assert.Equal(
            JsonSerializer.Serialize(saved.Wallets, CosmosDb.Json),
            JsonSerializer.Serialize(Paper(time).Also(p => p.RestoreState(saved)).ExportState().Wallets, CosmosDb.Json));
        var filled = (await after.GetOrderAsync(resting.VenueOrderId, CancellationToken.None))!;
        Assert.Equal(OrderStatus.Filled, filled.Status);
        Assert.Equal(0.001m, (await after.GetBalancesAsync(CancellationToken.None)).Single(b => b.Currency == "BTC").Total);
        var duplicate = await after.PlaceLimitOrderAsync(new OrderIntent("BTC-EUR", OrderSide.Buy, 0.0005m, 59_000m, "maker").Approved(), false, CancellationToken.None);
        Assert.Equal(resting.VenueOrderId, duplicate.VenueOrderId); // client order IDs survive too
    }

    [Fact]
    public void Restore_RefusesAnInconsistentAccount()
    {
        var time = new MutableTime(T0);
        var order = new PaperOrderState("v1", "c1", "BTC-EUR", "BTC", "EUR", OrderSide.Buy, 0.001m, 60_000m, 0,
            T0, T0, OrderStatus.PartiallyFilled, false, 0.002m, 120m, 0m, 0m); // filled more than its size

        Assert.Throws<InvalidOperationException>(() => Paper(time).RestoreState(
            new PaperAccountState(new Dictionary<string, PaperWalletState>(), [order])));
    }

    [Fact]
    public void LiveTrading_RefusesTheEmulator() =>
        Assert.Throws<InvalidOperationException>(() => new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddCixCosmos(
            new CosmosOptions { Enabled = true, Emulator = true }, TradingMode.Live));

    [Theory]
    [InlineData(TradingMode.Paper)]
    [InlineData(TradingMode.Live)]
    public void BothModes_SwitchStrategyAndPairs_OnlyPaperKeepsAPaperAccount(TradingMode mode)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddCixCosmos(new CosmosOptions
        {
            Enabled = true, Endpoint = "https://acct.documents.azure.com", Database = "cix", ManagedIdentityClientId = Guid.NewGuid().ToString(),
        }, mode);

        Assert.Contains(services, d => d.ServiceType == typeof(StrategySelectionService));
        Assert.Contains(services, d => d.ServiceType == typeof(IStrategyRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(Pairs.TradablePairsService));
        Assert.Contains(services, d => d.ServiceType == typeof(Pairs.ITradablePairsRepository));
        Assert.Equal(mode == TradingMode.Paper, services.Any(d => d.ServiceType == typeof(PaperAccountSaver)));
    }

    [Fact]
    public void Restore_IsRefusedOnceTheAccountIsInUse()
    {
        var time = new MutableTime(T0);
        var paper = Paper(time);
        paper.OnQuote(Quote(time, 60_000m, 60_010m));

        Assert.Throws<InvalidOperationException>(() => paper.RestoreState(paper.ExportState()));
    }

    [Fact]
    public async Task Saver_WritesOnlyWhenTheAccountChanged_AndRestores()
    {
        var time = new MutableTime(T0);
        var paper = Paper(time);
        var store = new InMemoryPaperAccountStore();
        var saver = new PaperAccountSaver(paper, store, time, NullLogger<PaperAccountSaver>.Instance);

        Assert.True(await saver.SaveIfChangedAsync(CancellationToken.None));
        Assert.False(await saver.SaveIfChangedAsync(CancellationToken.None));
        paper.OnQuote(Quote(time, 60_000m, 60_010m));
        Assert.False(await saver.SaveIfChangedAsync(CancellationToken.None)); // quotes alone aren't account state
        await paper.PlaceLimitOrderAsync(new OrderIntent("BTC-EUR", OrderSide.Buy, 0.0005m, 59_000m, "b").Approved(), false, CancellationToken.None);
        Assert.True(await saver.SaveIfChangedAsync(CancellationToken.None));

        var restarted = Paper(time);
        var restored = await new PaperAccountSaver(restarted, store, time, NullLogger<PaperAccountSaver>.Instance).RestoreAsync(CancellationToken.None);

        Assert.True(restored);
        Assert.Single(await restarted.GetOpenOrdersAsync(null, CancellationToken.None));
        Assert.Equal(2, store.Saves);
    }
}

/// <summary>The journal's mode filter without a database (#65); the emulator tests below run it for real.</summary>
public sealed class JournalModeTests
{
    private static readonly JournalEntry Entry = new("e1", new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero), "fill", "engine", null, null, "fill");

    [Fact]
    public void EachModeQueriesItsOwnEntries_AndPaperAlsoTheUntaggedOnes()
    {
        var query = new JournalQuery(Kind: "fill", ClientOrderId: "c-1");
        var paper = CosmosJournal.Sql(query, TradingMode.Paper);
        var live = CosmosJournal.Sql(query, TradingMode.Live);

        Assert.StartsWith("SELECT * FROM c WHERE (NOT IS_DEFINED(c.mode) OR c.mode = @mode) AND ", paper.QueryText, StringComparison.Ordinal);
        Assert.StartsWith("SELECT * FROM c WHERE c.mode = @mode AND ", live.QueryText, StringComparison.Ordinal);
        Assert.DoesNotContain("IS_DEFINED", live.QueryText, StringComparison.Ordinal);
        Assert.Equal("paper", paper.GetQueryParameters().Single(p => p.Name == "@mode").Value);
        Assert.Equal("live", live.GetQueryParameters().Single(p => p.Name == "@mode").Value);
    }

    [Fact]
    public void ReadDocumentsAreCheckedAgain_SoNoQueryMistakeShowsOneModeToTheOther()
    {
        var untagged = new CosmosJournal.JournalDocument("e1", "2026-10-27", Entry);
        var paper = untagged with { Mode = "paper" };
        var live = untagged with { Mode = "live" };
        var odd = untagged with { Mode = "Live" };

        Assert.True(CosmosJournal.IsVisible(untagged, TradingMode.Paper));
        Assert.False(CosmosJournal.IsVisible(untagged, TradingMode.Live));
        Assert.True(CosmosJournal.IsVisible(paper, TradingMode.Paper));
        Assert.False(CosmosJournal.IsVisible(paper, TradingMode.Live));
        Assert.True(CosmosJournal.IsVisible(live, TradingMode.Live));
        Assert.False(CosmosJournal.IsVisible(live, TradingMode.Paper));
        Assert.False(CosmosJournal.IsVisible(odd, TradingMode.Live));
        Assert.False(CosmosJournal.IsVisible(odd, TradingMode.Paper));
    }

    [Fact]
    public void FileJournals_OfTheTwoModesNeverShareAFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "cix-journal");
        var options = new Tools.JournalOptions { Directory = root };

        Assert.Equal(Path.Combine(root, "paper"), options.Resolve(TradingMode.Paper));
        Assert.Equal(Path.Combine(root, "live"), options.Resolve(TradingMode.Live));
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}

public sealed class EngineStateContentTests
{
    [Fact]
    public void SameJson_ComparesWhatIsStored_NotListReferences()
    {
        var a = EngineState.Initial with { PeakEquity = 1.10m, KnownClientOrderIds = ["a", "b"] };
        var b = EngineState.Initial with { PeakEquity = 1.10m, KnownClientOrderIds = new List<string> { "a", "b" } };

        Assert.NotEqual(a, b); // record equality compares the lists by reference
        Assert.True(CosmosEngineStateStore.SameJson(a, b));
        Assert.False(CosmosEngineStateStore.SameJson(a, b with { PeakEquity = 1.11m }));
        Assert.False(CosmosEngineStateStore.SameJson(a, b with { KnownClientOrderIds = ["a"] }));
    }
}

/// <summary>
/// Against a real Cosmos DB (the emulator in CI, <c>CIX_COSMOS_EMULATOR</c>). Without that variable each test returns at
/// once, so a machine without the emulator still runs the rest of the suite.
/// </summary>
public sealed class CosmosPersistenceTests : IAsyncLifetime
{
    private static readonly string? Endpoint = Environment.GetEnvironmentVariable("CIX_COSMOS_EMULATOR");
    private readonly string _database = "cix-test-" + Guid.NewGuid().ToString("N")[..8];
    private CosmosDb? _db;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(Endpoint))
            return;
        _db = new CosmosDb(new CosmosOptions { Enabled = true, Emulator = true, Endpoint = Endpoint, Database = _database });
        await _db.InitializeAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_db is null)
            return;
        await _db.DeleteDatabaseAsync(CancellationToken.None);
        _db.Dispose();
    }

    private static JournalEntry Entry(DateTimeOffset at, string kind, string runId, string? clientOrderId = null) =>
        new(Guid.NewGuid().ToString("N"), at, kind, "trader", runId, "trend@v1#abc", kind,
            clientOrderId is null ? null : new Dictionary<string, string> { ["clientOrderId"] = clientOrderId });

    [Fact]
    public async Task Journal_AppendsAcrossDays_AndQueriesNewestFirstWithFilters()
    {
        if (_db is null)
            return;
        var journal = new CosmosJournal(_db, TradingMode.Paper);
        var day1 = new DateTimeOffset(2026, 10, 22, 23, 59, 0, TimeSpan.Zero);
        var day2 = day1.AddMinutes(2);
        await journal.AppendAsync(Entry(day1, "tool-call", "r1"), CancellationToken.None);
        await journal.AppendAsync(Entry(day2, "order", "r2", "c-1"), CancellationToken.None);
        await journal.AppendAsync(Entry(day2.AddMinutes(1), "tool-call", "r2"), CancellationToken.None);

        var all = await journal.QueryAsync(new JournalQuery(), CancellationToken.None);
        var run2 = await journal.QueryAsync(new JournalQuery(RunId: "r2"), CancellationToken.None);
        var byOrder = await journal.QueryAsync(new JournalQuery(ClientOrderId: "c-1"), CancellationToken.None);
        var firstDay = await journal.QueryAsync(new JournalQuery(To: new DateOnly(2026, 10, 22)), CancellationToken.None);

        Assert.Equal(new[] { day2.AddMinutes(1), day2, day1 }, all.Select(e => e.At));
        Assert.Equal(2, run2.Count);
        Assert.Equal("order", Assert.Single(byOrder).Kind);
        Assert.Equal(day1, Assert.Single(firstDay).At);
        Assert.DoesNotContain(typeof(CosmosJournal).GetMethods(), m => m.Name is "Update" or "Delete" or "Replace" or "Upsert" or "Patch");
    }

    [Fact]
    public async Task Journal_EachModeSeesOnlyItsOwnEntries_AndUntaggedEntriesCountAsPaper()
    {
        if (_db is null)
            return;
        var paper = new CosmosJournal(_db, TradingMode.Paper);
        var live = new CosmosJournal(_db, TradingMode.Live);
        var at = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var day = "2026-10-27";

        // An entry written before the tag (#65): no mode on the document.
        var legacy = Entry(at.AddMinutes(-5), "fill", "r0", "c-old");
        await _db.Journal.CreateItemAsync(new { id = legacy.Id, day, entry = legacy }, new PartitionKey(day));

        // The same client order ID in both modes, as recovery and reconciliation look it up.
        await paper.AppendAsync(Entry(at, "order", "rp", "c-1"), CancellationToken.None);
        await paper.AppendAsync(Entry(at.AddMinutes(1), "fill", "rp", "c-1"), CancellationToken.None);
        await live.AppendAsync(Entry(at.AddMinutes(2), "order", "rl", "c-1"), CancellationToken.None);
        await live.AppendAsync(Entry(at.AddMinutes(3), "market-view", "rl"), CancellationToken.None);

        var paperAll = await paper.QueryAsync(new JournalQuery(), CancellationToken.None);
        var liveAll = await live.QueryAsync(new JournalQuery(), CancellationToken.None);

        Assert.Equal(new[] { "rp", "rp", "r0" }, paperAll.Select(e => e.RunId));
        Assert.Equal(new[] { "rl", "rl" }, liveAll.Select(e => e.RunId));

        // The reads the engine recovers from: fills and orders (position plans), a client order ID (reconciliation) and the
        // market view (restored at startup).
        Assert.Empty(await live.QueryAsync(new JournalQuery(Kind: "fill"), CancellationToken.None));
        Assert.Equal("rl", Assert.Single(await live.QueryAsync(new JournalQuery(Kind: "order"), CancellationToken.None)).RunId);
        Assert.Equal("rl", Assert.Single(await live.QueryAsync(new JournalQuery(ClientOrderId: "c-1"), CancellationToken.None)).RunId);
        Assert.Equal(2, (await paper.QueryAsync(new JournalQuery(Kind: "fill"), CancellationToken.None)).Count);
        Assert.Equal(new[] { "fill", "order" }, (await paper.QueryAsync(new JournalQuery(ClientOrderId: "c-1"), CancellationToken.None)).Select(e => e.Kind));
        Assert.Empty(await paper.QueryAsync(new JournalQuery(Kind: "market-view"), CancellationToken.None));
        Assert.Empty(await live.QueryAsync(new JournalQuery(ClientOrderId: "c-old"), CancellationToken.None));

        var stored = await _db.Journal.ReadItemAsync<JsonObject>(liveAll[0].Id, new PartitionKey(day));
        Assert.Equal("live", (string?)stored.Resource["mode"]);
    }

    [Fact]
    public async Task Journal_RefusesToOverwriteAnEntry()
    {
        if (_db is null)
            return;
        var journal = new CosmosJournal(_db, TradingMode.Paper);
        var entry = Entry(DateTimeOffset.UtcNow, "order", "r1");
        await journal.AppendAsync(entry, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => journal.AppendAsync(entry with { Summary = "rewritten" }, CancellationToken.None));
        Assert.Equal("order", Assert.Single(await journal.QueryAsync(new JournalQuery(), CancellationToken.None)).Summary);
    }

    [Fact]
    public async Task EngineState_SurvivesARestart_AndLiveStartsHalted()
    {
        if (_db is null)
            return;
        var time = new MutableTime(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var first = new CosmosEngineStateStore(_db, TradingMode.Paper, time);
        Assert.False(first.Current.KillSwitch.Engaged);
        first.Update(s => s with
        {
            KillSwitch = new KillSwitchState(true, "drill", "owner", time.GetUtcNow()),
            PeakEquity = 250.12345678m,
            Day = new DateOnly(2026, 10, 23),
            DayStartEquity = 240m,
            KnownClientOrderIds = ["a", "b"],
        });

        var restarted = new CosmosEngineStateStore(_db, TradingMode.Paper, time);
        var live = new CosmosEngineStateStore(_db, TradingMode.Live, time);

        Assert.True(restarted.Current.KillSwitch.Engaged);
        Assert.Equal(250.12345678m, restarted.Current.PeakEquity);
        Assert.Equal(new[] { "a", "b" }, restarted.Current.Known);
        Assert.True(live.Current.KillSwitch.Engaged); // a new live state starts halted, separate from paper
        Assert.Equal(FileEngineStateStore.NewFileReason, live.Current.KillSwitch.Reason);
    }

    [Fact]
    public async Task AgentRunState_SurvivesARestart_PerMode()
    {
        if (_db is null)
            return;
        var at = new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
        var state = new Cix.Engine.AgentRuns.AgentRunState
        {
            Day = new DateOnly(2026, 10, 26),
            Runs = 3,
            LastKey = "buy:SOL-EUR",
            NextHeartbeat = at.AddHours(4),
            InFlight = [new Cix.Engine.AgentRuns.AgentRun("run-20261026-080000-abcdef", Cix.Engine.AgentRuns.AgentRunKind.Trader, "signals", "buy:SOL-EUR", at, at.AddMinutes(1))],
        };
        var store = new CosmosAgentRunStateStore(_db, TradingMode.Paper);
        var first = await store.SaveAsync(state, null, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await store.SaveAsync(state, null, CancellationToken.None)); // it exists now

        var loaded = await new CosmosAgentRunStateStore(_db, TradingMode.Paper).LoadAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal((first, state.Day, 3, "buy:SOL-EUR", state.NextHeartbeat), (loaded.ETag, loaded.State.Day, loaded.State.Runs, loaded.State.LastKey, loaded.State.NextHeartbeat));
        Assert.Equal(state.InFlight.Single(), loaded.State.InFlight.Single());
        Assert.NotNull(await store.SaveAsync(state with { Runs = 4 }, first, CancellationToken.None));
        Assert.Null(await store.SaveAsync(state with { Runs = 5 }, first, CancellationToken.None)); // a stale engine's write
        Assert.Equal(4, (await store.LoadAsync(CancellationToken.None))!.State.Runs);
        Assert.Null(await new CosmosAgentRunStateStore(_db, TradingMode.Live).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EngineLease_IsHeldByOneEngineAtATime_PerMode()
    {
        if (_db is null)
            return;
        var time = new MutableTime(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
        EngineLease Lease(TradingMode mode, string holder) =>
            new(new CosmosLeaseStore(_db, mode), time, NullLogger<EngineLease>.Instance, holder, (_, _) => Task.CompletedTask);
        var first = Lease(TradingMode.Paper, "a");
        var second = Lease(TradingMode.Paper, "b");
        var live = Lease(TradingMode.Live, "c");

        Assert.True(await first.TryAcquireAsync(CancellationToken.None));
        Assert.False(await second.TryAcquireAsync(CancellationToken.None));
        Assert.True(await live.TryAcquireAsync(CancellationToken.None)); // paper and live never block each other
        Assert.True(await first.RenewAsync(CancellationToken.None));

        var store = new CosmosLeaseStore(_db, TradingMode.Paper);
        var stored = (await store.ReadAsync(CancellationToken.None))!;
        Assert.Equal("a", stored.Record.Holder);
        Assert.Null(await store.TryReplaceAsync(stored.Record with { Holder = "x" }, "\"stale\"", CancellationToken.None));
        Assert.Null(await store.TryCreateAsync(stored.Record, CancellationToken.None));

        await first.ReleaseAsync(CancellationToken.None);
        Assert.True(await second.TryAcquireAsync(CancellationToken.None));
        Assert.Equal("b", (await store.ReadAsync(CancellationToken.None))!.Record.Holder);
    }

    /// <summary>Lets the next replace through to the database, then loses its answer, as a timeout would.</summary>
    private sealed class LoseNextReplaceAnswer : RequestHandler
    {
        public bool Armed { get; set; }

        public override async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (Armed && request.Method == HttpMethod.Put && response.IsSuccessStatusCode)
            {
                Armed = false;
                throw new TimeoutException("The answer was lost after the write was applied.");
            }

            return response;
        }
    }

    [Fact]
    public void EngineState_AWriteWhoseAnswerWasLost_DoesNotBlockLaterWrites()
    {
        if (_db is null)
            return;
        var lose = new LoseNextReplaceAnswer();
        using var db = new CosmosDb(new CosmosOptions { Enabled = true, Emulator = true, Endpoint = Endpoint!, Database = _database }, handlers: [lose]);
        var time = new MutableTime(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        var store = new CosmosEngineStateStore(db, TradingMode.Paper, time);
        store.Update(s => s with { PeakEquity = 100m });

        lose.Armed = true;
        Assert.Throws<IOException>(() => store.Update(s => s with { PeakEquity = 110m })); // stored, but the store can't know
        var next = store.Update(s => s with { PeakEquity = 120m, Day = new DateOnly(2026, 10, 28) });

        Assert.Equal(120m, next.PeakEquity);
        Assert.Equal(120m, new CosmosEngineStateStore(db, TradingMode.Paper, time).Current.PeakEquity);
    }

    [Fact]
    public void EngineState_AChangeBySomethingElse_StillStopsWrites_AndIsKept()
    {
        if (_db is null)
            return;
        var time = new MutableTime(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        var store = new CosmosEngineStateStore(_db, TradingMode.Paper, time);
        store.Update(s => s with { PeakEquity = 100m });
        var other = new CosmosEngineStateStore(_db, TradingMode.Paper, time);
        other.Update(s => s with { KillSwitch = new KillSwitchState(true, "drill", "owner", time.GetUtcNow()) });

        Assert.Throws<IOException>(() => store.Update(s => s with { PeakEquity = 130m }));

        var reloaded = new CosmosEngineStateStore(_db, TradingMode.Paper, time).Current;
        Assert.Equal((true, 100m), (reloaded.KillSwitch.Engaged, reloaded.PeakEquity)); // the other writer's halt survives
    }

    [Fact]
    public async Task EngineState_TheSameContentRewritten_IsNotAConflict()
    {
        if (_db is null)
            return;
        var time = new MutableTime(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        var store = new CosmosEngineStateStore(_db, TradingMode.Paper, time);
        store.Update(s => s with { PeakEquity = 100m, KnownClientOrderIds = ["a"] });
        var current = await _db.State.ReadItemAsync<JsonObject>("engine-state-paper", new PartitionKey("engine-state-paper"));
        await _db.State.ReplaceItemAsync(current.Resource, "engine-state-paper", new PartitionKey("engine-state-paper")); // a new ETag, the same state

        var next = store.Update(s => s with { PeakEquity = 140m });

        Assert.Equal(140m, new CosmosEngineStateStore(_db, TradingMode.Paper, time).Current.PeakEquity);
        Assert.Equal(new[] { "a" }, next.Known);
    }

    [Fact]
    public async Task OrdersAndPlans_RoundTripThroughCosmos_WithReadableMetadata()
    {
        if (_db is null)
            return;
        var time = new MutableTime(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var store = new CosmosEngineStateStore(_db, TradingMode.Paper, time);
        store.Update(s => s.WithSent(new SentOrder("c-1", "XRP-EUR", "buy", 4.32488m, 1.1561m, time.GetUtcNow(), "place_order",
            StopLoss: 1.1481m, TakeProfit: 1.1641m, RunId: "run-1", Strategy: "risky@v1#abc")));

        var restarted = new CosmosEngineStateStore(_db, TradingMode.Paper, time);
        using var raw = await _db.State.ReadItemStreamAsync("engine-state-paper", new PartitionKey("engine-state-paper"));
        using var doc = await JsonDocument.ParseAsync(raw.Content);

        Assert.Equal((4.32488m, 1.1481m), (Assert.Single(restarted.Current.SentOrders).Size, restarted.Current.Plans["XRP-EUR"].StopLoss));
        Assert.Equal(PositionPlanState.EntryPending, restarted.Current.Plans["XRP-EUR"].Status);
        Assert.Equal("engine-state", doc.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("about").GetString()));
    }

    [Fact]
    public async Task PaperAccount_RoundTripsExactDecimals_StoredAsStrings()
    {
        if (_db is null)
            return;
        var store = new CosmosPaperAccountStore(_db);
        var state = new PaperAccountState(
            new Dictionary<string, PaperWalletState> { ["EUR"] = new(123456789.123456789m, 0.00000001m) },
            [new PaperOrderState("v1", "c1", "BTC-EUR", "BTC", "EUR", OrderSide.Buy, 0.00012345m, 60_000.01m, 0,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, OrderStatus.New, false, 0m, 0m, 0m, 7.2m)]);

        await store.SaveAsync(state, CancellationToken.None);
        var loaded = (await store.LoadAsync(CancellationToken.None))!;
        using var raw = await _db.State.ReadItemStreamAsync(CosmosPaperAccountStore.DocumentId, new PartitionKey(CosmosPaperAccountStore.DocumentId));
        using var doc = await JsonDocument.ParseAsync(raw.Content);

        Assert.Equal(123456789.123456789m, loaded.Wallets["EUR"].Available);
        Assert.Equal(0.00000001m, loaded.Wallets["EUR"].Reserved);
        Assert.Equal(60_000.01m, Assert.Single(loaded.Orders).LimitPrice);
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("value").GetProperty("wallets").GetProperty("EUR").GetProperty("available").ValueKind);
    }

    [Fact]
    public async Task SpecArchive_KeepsTheFirstCopyOfAVersion()
    {
        if (_db is null)
            return;
        var archive = new CosmosSpecArchive(_db);
        var spec = new StrategySpec("trend", 1, SpecStatus.Paper, "original", new Dictionary<string, JsonElement>(), ["get_portfolio"], "owner") { Hash = "abc123def456" };

        Assert.True(await archive.ArchiveAsync(spec, CancellationToken.None));
        Assert.False(await archive.ArchiveAsync(spec with { Instructions = "changed" }, CancellationToken.None));
        var archived = (await archive.GetAsync(spec.Label, CancellationToken.None))!;
        Assert.Equal(("original", "trend@v1#abc123def456"), (archived.Instructions, archived.Label));
    }

    [Fact]
    public async Task StrategyVersions_AreCreatedOnce_AndASelectionWrittenByHandIsRead()
    {
        if (_db is null)
            return;
        var repository = new Strategy.CosmosStrategyRepository(_db, new CosmosSpecArchive(_db));
        var balanced = StrategySchemaTests.Load("balanced", 1);

        Assert.True(await repository.TryAddVersionAsync(balanced, CancellationToken.None));
        Assert.False(await repository.TryAddVersionAsync(balanced with { Instructions = "changed" }, CancellationToken.None));
        var read = (await repository.ReadVersionAsync("balanced", 1, CancellationToken.None))!;
        Assert.Equal((balanced.Instructions, StrategyHash.Of(read)), (read.Instructions, read.Hash));
        Assert.Equal(10m, read.Parameters["orderQuote"].GetDecimal()); // parameters keep their JSON numbers
        Assert.Empty(StrategySchema.Validate(read, Rig.Limits, TradingMode.Paper));
        Assert.Null(await repository.ReadVersionAsync("balanced", 9, CancellationToken.None));

        // As the dashboard or Data Explorer would write it: plain JSON, version as a number.
        var id = Strategy.CosmosStrategyRepository.SelectionId(TradingMode.Paper);
        var selection = new JsonObject
        {
            ["id"] = id,
            ["type"] = "strategy-selection",
            ["value"] = new JsonObject { ["name"] = "balanced", ["version"] = 1, ["requestedBy"] = "owner", ["requestedAt"] = "2026-10-27T10:00:00Z" },
        };
        await _db.State.CreateItemAsync(selection, new PartitionKey(id));
        Assert.Equal(new StrategySelection("balanced", 1, "owner", DateTimeOffset.Parse("2026-10-27T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            await repository.ReadSelectionAsync(TradingMode.Paper, CancellationToken.None));
        Assert.Null(await repository.ReadSelectionAsync(TradingMode.Live, CancellationToken.None));

        await repository.SaveSchemaAsync(StrategySchema.Document(Rig.Limits, TradingMode.Paper, Rig.T0), CancellationToken.None);
        using var raw = await _db.State.ReadItemStreamAsync(Strategy.CosmosStrategyRepository.SchemaId(TradingMode.Paper), new PartitionKey("strategy-schema-paper"));
        using var schema = await JsonDocument.ParseAsync(raw.Content);
        Assert.Equal("strategy-schema", schema.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ManualRequests_RoundTripThroughCosmos_AndAreExecutedOnce()
    {
        if (_db is null)
            return;
        Assert.True(_db.RequestsAvailable);
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["place_order"] }, btc: 0.001m);
        var now = tools.Rig.Time.GetUtcNow();
        var day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var request = new JsonObject
        {
            ["id"] = "req-cosmos-1", ["day"] = day, ["side"] = "sell", ["mode"] = "direct", ["symbol"] = "BTC-EUR", ["portion"] = "0.5",
            ["createdAt"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ["status"] = "new", ["requestedBy"] = "owner",
        };
        await _db.Requests.CreateItemAsync(request, new PartitionKey(day)); // as the dashboard writes it
        var forLive = (JsonObject)request.DeepClone();
        forLive["id"] = "req-cosmos-live";
        forLive["tradingMode"] = "live";
        await _db.Requests.CreateItemAsync(forLive, new PartitionKey(day));
        var listed = await new CosmosManualRequestStore(_db).ListNewAsync(DateOnly.FromDateTime(now.UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime), "paper", CancellationToken.None);
        Assert.Equal("req-cosmos-1", Assert.Single(listed).Id); // the mode is filtered in the query
        var store = new CosmosManualRequestStore(_db);
        var service = new ManualRequestService(
            store, new CosmosManualLedgerStore(_db, TradingMode.Paper), tools.Rig.Execution, tools.Rig.State, tools.Journal, tools.Specs, new ManualRequestOptions(),
            new TradingContext(TradingMode.Paper), tools.Rig.Time, NullLogger<ManualRequestService>.Instance, tradable: tools.Rig.Tradable);

        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);

        var stored = (await store.GetAsync("req-cosmos-1", day, CancellationToken.None))!;
        Assert.Equal(ManualRequestStatus.Placed, stored.Status);
        Assert.Single((JsonArray)stored.Document["events"]!); // placed: nothing is written between the claim and the order
        Assert.Equal("owner", ManualRequestDocument.Field(stored.Document, "requestedBy")); // the requester's fields are kept
        Assert.Single(tools.Journal.Entries, e => e.Kind == "order");
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        Assert.Empty(await store.ListNewAsync(today.AddDays(-1), today.AddDays(1), "paper", CancellationToken.None)); // no longer new
        var ledger = (await new CosmosManualLedgerStore(_db, TradingMode.Paper).LoadAsync(CancellationToken.None))!;
        Assert.Equal((ManualRequestStatus.Placed, 0.5m, 1), (ledger.Active["req-cosmos-1"].Status, ledger.Active["req-cosmos-1"].Request.Portion, ledger.Accepted));
        Assert.Null(await store.TryReplaceAsync(stored with { ETag = "\"stale\"" }, stored.Document, CancellationToken.None));
    }

    [Fact]
    public async Task ScannerFilters_RoundTripThroughCosmos()
    {
        if (_db is null)
            return;
        var store = new CosmosScannerFilterStore(_db, TradingMode.Paper);
        var at = new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
        var state = new ScannerFilterState("risky@v2#b", new Domain.Scanning.ScannerParameters { MinQuoteVolume24h = 20_000m, MaxSpreadFraction = 0.004m },
            at, ["BTC-EUR", "DOGE-EUR"], new Dictionary<string, string> { ["SOL-EUR"] = "wide-spread" }, at.AddMinutes(5));

        Assert.Null(await store.LoadAsync(CancellationToken.None));
        await store.SaveAsync(state, CancellationToken.None);
        var loaded = (await store.LoadAsync(CancellationToken.None))!;

        Assert.Equal((state.Spec, state.Filters, state.Since, state.EligibilityAt), (loaded.Spec, loaded.Filters, loaded.Since, loaded.EligibilityAt));
        Assert.Equal(state.Passing, loaded.Passing);
        Assert.Equal("wide-spread", loaded.Excluded["SOL-EUR"]);
    }
}
