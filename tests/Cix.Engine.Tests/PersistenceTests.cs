using System.Text.Json;
using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.Paper;
using Cix.Engine.Persistence;
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

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}

/// <summary>
/// Against a real Cosmos DB (the emulator in CI, <c>CIX_COSMOS_EMULATOR</c>). Without that variable each test returns at
/// once, so a machine without the emulator still runs the rest of the suite.
/// </summary>
public sealed class CosmosPersistenceTests : IAsyncLifetime
{
    private static readonly string? Endpoint = Environment.GetEnvironmentVariable("CIX_COSMOS_EMULATOR");
    private CosmosDb? _db;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(Endpoint))
            return;
        _db = new CosmosDb(new CosmosOptions { Enabled = true, Emulator = true, Endpoint = Endpoint, Database = "cix-test-" + Guid.NewGuid().ToString("N")[..8] });
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
        var journal = new CosmosJournal(_db);
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
    public async Task Journal_RefusesToOverwriteAnEntry()
    {
        if (_db is null)
            return;
        var journal = new CosmosJournal(_db);
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
        Assert.Equal("original", (await archive.GetAsync(spec.Label, CancellationToken.None))!.Instructions);
    }
}
