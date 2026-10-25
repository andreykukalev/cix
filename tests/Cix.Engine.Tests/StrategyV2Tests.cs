using System.Net;
using System.Text.Json;
using Cix.Domain.Scanning;
using Cix.Engine.Scanning;
using Cix.Engine.Strategy;
using Cix.Engine.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Cix.Engine.Tests;

public sealed class StrategyV2Tests
{
    private static string SpecPath(string name) => Path.Combine(AppContext.BaseDirectory, "strategies", $"{name}.v1.json");

    private static StrategySpec Spec(string parametersJson)
    {
        using var doc = JsonDocument.Parse(parametersJson);
        var parameters = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return new StrategySpec("t", 1, SpecStatus.Paper, "x", parameters, ["scan_market"], "test") { Hash = "abc123def456" };
    }

    [Fact]
    public void Good_LoadsForPaperOnly_AndKeepsTheStrictEntryRules()
    {
        var spec = FileStrategySpecStore.Load(SpecPath("good"), "good", 1, TradingMode.Paper);

        Assert.Equal(SpecStatus.Paper, spec.Status);
        Assert.True(spec.Allows("scan_market"));
        Assert.False(spec.Allows("get_market_view")); // signals replace the raw market view
        var p = MarketScanService.FromSpec(spec, new ScannerParameters());
        Assert.Equal((5m, 1.5m, 2m, 1m, 1m), (p.OrderQuote, p.StopAtr, p.RewardRisk, p.RiskPerTradeQuote, p.MinAtrToCostRatio));
        Assert.True(p.RequireUptrendPullback);
        Assert.True(p.TrendExits);
        Assert.Throws<InvalidOperationException>(() => FileStrategySpecStore.Load(SpecPath("good"), "good", 1, TradingMode.Live));
    }

    [Fact]
    public void Risky_IsGoodWithLooseEntries_ForTestingOrders_AndPaperOnly()
    {
        var good = FileStrategySpecStore.Load(SpecPath("good"), "good", 1, TradingMode.Paper);
        var risky = FileStrategySpecStore.Load(SpecPath("risky"), "risky", 1, TradingMode.Paper);

        Assert.Equal(good.Instructions, risky.Instructions);
        Assert.Equal(good.AllowedTools, risky.AllowedTools);
        Assert.Equal(
            MarketScanService.FromSpec(good, new ScannerParameters()) with
            {
                MinAtrToCostRatio = 0.5m, StopAtr = 1m, RewardRisk = 1m, RequireUptrendPullback = false, TrendExits = false,
            },
            MarketScanService.FromSpec(risky, new ScannerParameters()));
        Assert.Throws<InvalidOperationException>(() => FileStrategySpecStore.Load(SpecPath("risky"), "risky", 1, TradingMode.Live));
    }

    [Fact]
    public void ASpecWithoutAScannerSection_UsesConfiguration()
    {
        var fallback = new ScannerParameters { MaxSpreadFraction = 0.003m };

        var parameters = MarketScanService.FromSpec(Spec("""{ "orderQuote": 5 }"""), fallback);

        Assert.Equal(fallback with { OrderQuote = 5m }, parameters);
        Assert.Same(fallback, MarketScanService.FromSpec(null, fallback));
    }

    [Fact]
    public void SpecScannerValues_OverrideDefaults_AndOrderQuoteComesFromTheTop()
    {
        var configured = new ScannerParameters { MaxSpreadFraction = 0.003m };

        var parameters = MarketScanService.FromSpec(Spec("""{ "orderQuote": 3, "scanner": { "stopAtr": 2, "maxEntrySignals": 1 } }"""), configured);

        Assert.Equal((3m, 2m, 1), (parameters.OrderQuote, parameters.StopAtr, parameters.MaxEntrySignals));
        Assert.Equal(0.002m, parameters.MaxSpreadFraction); // the spec replaces configuration: unlisted values are code defaults
    }

    [Theory]
    [InlineData("""{ "scanner": { "orderQuote": 50 } }""")]          // a second orderQuote the order-size hook wouldn't see
    [InlineData("""{ "scanner": { "ORDERQUOTE": 50 } }""")]
    [InlineData("""{ "scanner": { "stopAtrr": 2 } }""")]             // typo
    [InlineData("""{ "scanner": { "stopAtr": -1 } }""")]             // fails validation
    [InlineData("""{ "scanner": [] }""")]
    [InlineData("""{ "orderQuote": "five" }""")]
    public void BadScannerSettings_FailLoudly(string parametersJson) =>
        Assert.Throws<InvalidOperationException>(() => MarketScanService.FromSpec(Spec(parametersJson), new ScannerParameters()));

    [Fact]
    public void SignalKey_IsStableAndOrdered()
    {
        var at = DateTimeOffset.UnixEpoch;
        var scan = new ScanResult(at, [], [],
            [new EntrySignal("SOL-EUR", 1, 1m, 1m, 1m, 0.9m, 1.2m, 0.1m, "r"), new EntrySignal("ETH-EUR", 2, 1m, 1m, 1m, 0.9m, 1.2m, 0.1m, "r")],
            [new ExitSignal("BTC-EUR", 1m, 1m, "stop", "d")]);

        Assert.Equal("buy:ETH-EUR|buy:SOL-EUR|sell:BTC-EUR:stop", MarketScanService.KeyOf(scan));
        Assert.Equal(string.Empty, MarketScanService.KeyOf(scan with { Entries = [], Exits = [] }));
        Assert.Equal("stale-order:SOL-EUR", MarketScanService.KeyOf(scan with { Entries = [], Exits = [] }, ["SOL-EUR"]));
    }

    [Fact]
    public async Task AnOrderRestingOverFiftyMinutes_CountsAsASignal_SoTheTraderGetsARun()
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["scan_market"] });
        var placed = await tools.Rig.Buy(0.0001m, 59_500m); // below the ask: rests unfilled
        Assert.NotNull(placed);

        await tools.Scanner.ScanAsync(CancellationToken.None);
        Assert.Empty(tools.Scanner.StaleOrderSymbols); // fresh order: not yet a reason to run

        tools.Rig.Time.Advance(TimeSpan.FromMinutes(51));
        tools.Rig.Quote(60_000m, 60_010m);
        await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(new[] { Rig.Btc }, tools.Scanner.StaleOrderSymbols.ToArray());
        Assert.Equal("stale-order:BTC-EUR", tools.Scanner.SignalKey);
    }

    [Fact]
    public async Task Signals_AreServedOverLoopbackHttp_WithoutAnLlm_AndRefusedToBrowsers()
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["scan_market"] });
        ScannerServiceTests.SeedHourly(tools, 20m);
        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);
        var description = MarketScanBackgroundService.Describe(scan);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(tools.Scanner);
        builder.Services.AddCixToolServer(new ToolServerOptions { OperatorKey = ToolRig.OperatorKey }, tools.Specs, tools.Journal);
        await using var app = builder.Build();
        app.UseCixToolServer();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient();

        using var doc = JsonDocument.Parse(await http.GetStringAsync(address + ToolServerRegistration.SignalsEndpoint));
        using var browser = new HttpRequestMessage(HttpMethod.Get, address + ToolServerRegistration.SignalsEndpoint);
        browser.Headers.Add("Origin", "https://evil.example");
        using var refused = await http.SendAsync(browser);

        var root = doc.RootElement;
        Assert.True(root.GetProperty("signalDriven").GetBoolean());
        Assert.Equal(1, root.GetProperty("entries").GetInt32());
        Assert.Equal("buy:BTC-EUR", root.GetProperty("key").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("#1 BTC-EUR", description, StringComparison.Ordinal);
        Assert.Contains("SOL-EUR not-allowed", description, StringComparison.Ordinal);
        await app.StopAsync();
    }

    [Fact]
    public void SignalDriven_FollowsTheActiveSpec()
    {
        Assert.False(new ToolRig().Scanner.SignalDriven); // observer: no scan_market
        Assert.True(new ToolRig(ToolRig.Observer with { AllowedTools = ["scan_market"] }).Scanner.SignalDriven);
    }
}
