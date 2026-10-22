using System.Reflection;
using System.Text.Json;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Strategy;
using Cix.Engine.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cix.Engine.Tests;

internal sealed class FixedSpecs(StrategySpec? spec) : IStrategySpecStore
{
    public StrategySpec? Active => spec;
}

/// <summary>Tool classes over a paper rig, with a fake HTTP request carrying the caller's headers.</summary>
internal sealed class ToolRig
{
    public static readonly string OperatorKey = new('k', 32); // built at runtime: not a secret, and scanners needn't think so

    public static readonly StrategySpec Observer = new(
        "observer", 1, SpecStatus.Paper, "Observe only.",
        new Dictionary<string, JsonElement>(),
        ["get_strategy_spec", "get_market_view", "get_portfolio"],
        "test") { Hash = "abc123def456" };

    public ToolRig(StrategySpec? spec = null, bool noSpec = false)
    {
        Rig = new Rig();
        Http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        Specs = new FixedSpecs(noSpec ? null : spec ?? Observer);
        Context = new ToolCallContext(Http, Specs, Journal, new ToolServerOptions { OperatorKey = OperatorKey }, Rig.Time);
        Features = new FeatureService(new InMemoryCandleStore(), Rig.State, new MarketDataOptions(), Rig.Time);
        Agent = new AgentTools(Context, Features, Rig.Execution, Rig.Exchange, Rig.KillSwitch, Rig.Limits);
        Operator = new OperatorTools(Context, Journal);
    }

    public Rig Rig { get; }
    public HttpContextAccessor Http { get; }
    public IStrategySpecStore Specs { get; }
    public InMemoryJournal Journal { get; } = new();
    public ToolCallContext Context { get; }
    public FeatureService Features { get; }
    public AgentTools Agent { get; }
    public OperatorTools Operator { get; }

    public void Header(string name, string value) => Http.HttpContext!.Request.Headers[name] = value;
}

public sealed class ToolTests
{
    [Fact]
    public async Task AllowedTool_ReturnsJson_AndIsJournaledWithTheCaller()
    {
        var tools = new ToolRig();
        tools.Header(ToolServerOptions.AgentHeader, "trader");
        tools.Header(ToolServerOptions.RunHeader, "run-42");

        var json = await tools.Agent.GetMarketView(CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("BTC-EUR", doc.RootElement[0].GetProperty("symbol").GetString());
        Assert.Equal(60_005m, doc.RootElement[0].GetProperty("price").GetDecimal());
        var entry = Assert.Single(tools.Journal.Entries);
        Assert.Equal(("tool-call", "trader", "run-42", "observer@v1#abc123def456"), (entry.Kind, entry.Actor, entry.RunId, entry.SpecVersion));
        Assert.Equal("get_market_view", entry.Data!["tool"]);
    }

    [Fact]
    public async Task ToolNotInTheSpec_IsRefused()
    {
        var tools = new ToolRig();

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.Agent.GetOpenOrders(CancellationToken.None));

        Assert.Contains("doesn't allow get_open_orders", ex.Message, StringComparison.Ordinal);
        Assert.Empty(tools.Journal.Entries);
    }

    [Fact]
    public async Task WithoutASpec_AgentToolsAreDisabled()
    {
        var tools = new ToolRig(noSpec: true);

        await Assert.ThrowsAsync<McpException>(() => tools.Agent.GetPortfolio(CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => tools.Agent.GetStrategySpec(CancellationToken.None));
    }

    [Theory]
    [InlineData("DOGE-EUR")]
    [InlineData("")]
    [InlineData("BTC-EUR-BTC-EUR-BTC-EUR")]
    public async Task UnknownOrMalformedSymbol_IsRefused(string symbol)
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["get_features"] });

        await Assert.ThrowsAsync<McpException>(() => tools.Agent.GetFeatures(symbol, CancellationToken.None));
    }

    [Fact]
    public async Task Symbol_IsNormalised()
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["get_features"] });

        var json = await tools.Agent.GetFeatures("btc/eur", CancellationToken.None);

        Assert.Equal("BTC-EUR", JsonDocument.Parse(json).RootElement.GetProperty("symbol").GetString());
    }

    [Fact]
    public async Task Portfolio_ShowsEquityLimitsAndHaltState()
    {
        var tools = new ToolRig();
        await tools.Rig.Execution.EngageKillSwitchAsync("drill", "owner", CancellationToken.None);

        using var doc = JsonDocument.Parse(await tools.Agent.GetPortfolio(CancellationToken.None));

        Assert.Equal(200m, doc.RootElement.GetProperty("equity").GetDecimal());
        Assert.True(doc.RootElement.GetProperty("halted").GetBoolean());
        Assert.Equal("drill", doc.RootElement.GetProperty("haltReason").GetString());
        Assert.Equal(40m, doc.RootElement.GetProperty("limits").GetProperty("maxOrderQuote").GetDecimal());
    }

    [Fact]
    public async Task OperatorTools_NeedTheKey()
    {
        var tools = new ToolRig();
        await Assert.ThrowsAsync<McpException>(() => tools.Operator.QueryJournal(cancellationToken: CancellationToken.None));

        tools.Header(ToolServerOptions.OperatorKeyHeader, "wrong-key-wrong-key-wrong-key");
        await Assert.ThrowsAsync<McpException>(() => tools.Operator.QueryJournal(cancellationToken: CancellationToken.None));

        tools.Header(ToolServerOptions.OperatorKeyHeader, ToolRig.OperatorKey);
        await tools.Agent.GetMarketView(CancellationToken.None);
        using var doc = JsonDocument.Parse(await tools.Operator.QueryJournal(cancellationToken: CancellationToken.None));
        Assert.Equal("owner", doc.RootElement[0].GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Operator_MayCallAgentToolsTheSpecDoesNotList()
    {
        var tools = new ToolRig();
        tools.Header(ToolServerOptions.OperatorKeyHeader, ToolRig.OperatorKey);

        var json = await tools.Agent.GetOpenOrders(CancellationToken.None);

        Assert.Equal("[]", json);
    }

    [Fact]
    public async Task DailySummary_CountsByKindActorAndTool()
    {
        var tools = new ToolRig();
        tools.Header(ToolServerOptions.AgentHeader, "trader");
        await tools.Agent.GetMarketView(CancellationToken.None);
        await tools.Agent.GetPortfolio(CancellationToken.None);
        tools.Header(ToolServerOptions.OperatorKeyHeader, ToolRig.OperatorKey);

        using var doc = JsonDocument.Parse(await tools.Operator.GetDailySummary("2026-10-22", CancellationToken.None));

        Assert.Equal(2, doc.RootElement.GetProperty("entries").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("byActor").GetProperty("trader").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("byTool").GetProperty("get_portfolio").GetInt32());
    }

    [Theory]
    [InlineData("trader one")]
    [InlineData("<script>")]
    public async Task OddIdentityHeaders_AreNotWrittenToTheJournal(string agent)
    {
        var tools = new ToolRig();
        tools.Header(ToolServerOptions.AgentHeader, agent);

        await tools.Agent.GetMarketView(CancellationToken.None);

        Assert.Equal("invalid", Assert.Single(tools.Journal.Entries).Actor);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5077")]
    [InlineData("http://10.0.0.5:5077")]
    [InlineData("https://localhost:5077")]
    public void ToolServer_ListensOnLoopbackHttpOnly(string url) =>
        Assert.Throws<InvalidOperationException>(() => new ToolServerOptions { Url = url }.Validate());

    [Fact]
    public void ShortOperatorKey_IsRefused() =>
        Assert.Throws<InvalidOperationException>(() => new ToolServerOptions { OperatorKey = "short" }.Validate());
}

/// <summary>The real thing: Kestrel on a free loopback port, the official MCP client over Streamable HTTP.</summary>
public sealed class McpEndToEndTests
{
    [Fact]
    public async Task Client_ListsTheTools_AndCallsOne()
    {
        var tools = new ToolRig();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(tools.Context);
        builder.Services.AddSingleton(tools.Features);
        builder.Services.AddSingleton(tools.Rig.Execution);
        builder.Services.AddSingleton<Domain.Exchange.IExchangeClient>(tools.Rig.Exchange);
        builder.Services.AddSingleton(tools.Rig.KillSwitch);
        builder.Services.AddSingleton(Rig.Limits);
        builder.Services.AddSingleton<IJournal>(tools.Journal);
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<AgentTools>().WithTools<OperatorTools>();
        await using var app = builder.Build();
        app.MapCixToolServer();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(address + ToolServerRegistration.Endpoint) }));
        var listed = await client.ListToolsAsync();
        var result = await client.CallToolAsync("get_strategy_spec", new Dictionary<string, object?>());
        var refused = await client.CallToolAsync("get_open_orders", new Dictionary<string, object?>());

        Assert.Equal(
            new[] { "get_daily_summary", "get_features", "get_market_view", "get_open_orders", "get_portfolio", "get_strategy_spec", "query_journal" },
            listed.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("observer@v1#abc123def456", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);
        Assert.True(refused.IsError);
        Assert.Contains("doesn't allow", Assert.IsType<TextContentBlock>(refused.Content[0]).Text, StringComparison.Ordinal);

        await app.StopAsync();
    }
}

public sealed class JournalAndSpecTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cix-journal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static JournalEntry Entry(DateTimeOffset at, string kind, string? clientOrderId = null) =>
        new(Guid.NewGuid().ToString("N"), at, kind, "trader", "run-1", "observer@v1#abc", kind,
            clientOrderId is null ? null : new Dictionary<string, string> { ["clientOrderId"] = clientOrderId });

    [Fact]
    public void Journal_HasNoUpdateOrDelete()
    {
        var methods = typeof(IJournal).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal);

        Assert.Equal(new[] { nameof(IJournal.AppendAsync), nameof(IJournal.QueryAsync) }, methods);
        Assert.DoesNotContain(
            typeof(FileJournal).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            m => m.Name.Contains("Update", StringComparison.Ordinal) || m.Name.Contains("Delete", StringComparison.Ordinal) || m.Name.Contains("Remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileJournal_AppendsPerDay_AndQueriesNewestFirst()
    {
        var journal = new FileJournal(_dir);
        var day1 = new DateTimeOffset(2026, 10, 21, 23, 59, 0, TimeSpan.Zero);
        var day2 = day1.AddMinutes(2);
        await journal.AppendAsync(Entry(day1, "tool-call"), CancellationToken.None);
        await journal.AppendAsync(Entry(day2, "order", "c-1"), CancellationToken.None);
        await journal.AppendAsync(Entry(day2.AddMinutes(1), "tool-call"), CancellationToken.None);
        File.AppendAllText(Path.Combine(_dir, "2026-10-22.jsonl"), "{\"id\":\"torn"); // crash mid-write

        var all = await journal.QueryAsync(new JournalQuery(), CancellationToken.None);
        var orders = await journal.QueryAsync(new JournalQuery(ClientOrderId: "c-1"), CancellationToken.None);
        var firstDay = await journal.QueryAsync(new JournalQuery(To: new DateOnly(2026, 10, 21)), CancellationToken.None);
        var limited = await journal.QueryAsync(new JournalQuery(Limit: 1), CancellationToken.None);

        Assert.Equal(2, Directory.GetFiles(_dir, "*.jsonl").Length);
        Assert.Equal(new[] { day2.AddMinutes(1), day2, day1 }, all.Select(e => e.At));
        Assert.Equal("order", Assert.Single(orders).Kind);
        Assert.Equal(day1, Assert.Single(firstDay).At);
        Assert.Equal(day2.AddMinutes(1), Assert.Single(limited).At);
    }

    [Fact]
    public void SpecStore_LoadsTheBundledObserver_WithAHash()
    {
        var spec = FileStrategySpecStore.Load(Path.Combine(AppContext.BaseDirectory, "strategies", "observer.v1.json"), "observer", 1, TradingMode.Paper);

        Assert.Equal(SpecStatus.Paper, spec.Status);
        Assert.Matches("^observer@v1#[0-9a-f]{12}$", spec.Label);
        Assert.DoesNotContain("place_order", spec.AllowedTools);
    }

    [Theory]
    [InlineData("draft", "Paper")]
    [InlineData("paper", "Live")]
    public void SpecStore_RefusesASpecTheModeCannotRun(string status, string mode)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "s.v1.json");
        File.WriteAllText(path, $$"""{"name":"s","version":1,"status":"{{status}}","createdBy":"owner","instructions":"x","parameters":{},"allowedTools":[]}""");

        Assert.Throws<InvalidOperationException>(() => FileStrategySpecStore.Load(path, "s", 1, Enum.Parse<TradingMode>(mode)));
    }

    [Fact]
    public void SpecStore_RefusesAFileThatClaimsAnotherVersion()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "s.v2.json");
        File.WriteAllText(path, """{"name":"s","version":1,"status":"paper","createdBy":"owner","instructions":"x","parameters":{},"allowedTools":[]}""");

        Assert.Throws<InvalidOperationException>(() => FileStrategySpecStore.Load(path, "s", 2, TradingMode.Paper));
    }
}
