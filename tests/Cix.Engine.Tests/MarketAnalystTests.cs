using System.Text.Json;
using Cix.Domain.Scanning;
using Cix.Engine.Analysis;
using Cix.Engine.Journal;
using Cix.Engine.Strategy;
using Cix.Engine.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Cix.Engine.Tests;

public sealed class MarketAnalystTests
{
    private static readonly string[] Sources = ["https://example.com/news/1"];

    private static ToolRig Analyst()
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["scan_market"] });
        ScannerServiceTests.SeedHourly(tools, 20m); // an uptrend: the scanner offers a BTC entry
        tools.Header(ToolServerOptions.AgentHeader, "analyst");
        return tools;
    }

    [Fact]
    public async Task RiskOffView_BlocksTheScannersEntries_AndIsJournaledWithItsSources()
    {
        var tools = Analyst();
        Assert.Single((await tools.Scanner.ScanAsync(CancellationToken.None)).Entries);

        using var reply = JsonDocument.Parse(await tools.Analyst.RecordMarketView(
            "risk-off", "CPI release in two hours; markets jittery.", Sources, validHours: 3, cancellationToken: CancellationToken.None));
        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.True(reply.RootElement.GetProperty("recorded").GetBoolean());
        Assert.Empty(scan.Entries);
        Assert.Equal("risk-off (market view)", Assert.Single(scan.Ranked).NoEntryReason);
        var entry = Assert.Single(tools.Journal.Entries, e => e.Kind == MarketViewStore.Kind);
        Assert.Equal(("analyst", "risk-off", "https://example.com/news/1"), (entry.Actor, entry.Data!["regime"], entry.Data["sources"]));
        Assert.Equal("CPI release in two hours; markets jittery.", entry.Summary);
        Assert.DoesNotContain("CPI", JsonSerializer.Serialize(scan), StringComparison.Ordinal); // the analyst's words never reach the trader
    }

    [Fact]
    public async Task ViewExpires_AndThenNoLongerBlocks()
    {
        var tools = Analyst();
        await tools.Analyst.RecordMarketView("risk-off", "Exchange outage reported widely.", Sources, validHours: 1, cancellationToken: CancellationToken.None);

        tools.Rig.Time.Advance(TimeSpan.FromMinutes(61));
        tools.Rig.Quote(60_000m, 60_010m);
        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.False(tools.Views.Current!.IsActive(tools.Rig.Time.GetUtcNow()));
        Assert.NotEqual("risk-off (market view)", scan.Ranked.Single().NoEntryReason);
    }

    [Fact]
    public async Task AvoidedSymbol_IsExcludedAsNews()
    {
        var tools = Analyst();

        await tools.Analyst.RecordMarketView(
            "neutral", "Exploit reported on a major bridge used by this token.", Sources, avoidSymbols: ["btc/eur"], cancellationToken: CancellationToken.None);
        var scan = await tools.Scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(scan.Entries);
        Assert.Equal("news", scan.Excluded.Single(e => e.Symbol == Btc).Reason);
    }

    private const string Btc = "BTC-EUR";

    public static TheoryData<string, string, string, int, string?> BadViews => new()
    {
        { "bullish", "A reasonable summary of the market.", "https://example.com/a", 6, null },     // unknown regime
        { "neutral", "too short", "https://example.com/a", 6, null },
        { "neutral", "A reasonable summary of the market.", "http://example.com/a", 6, null },     // not https
        { "neutral", "A reasonable summary of the market.", "https://example.com/a", 13, null },   // longer than 12 hours
        { "neutral", "A reasonable summary of the market.", "https://example.com/a", 0, null },
        { "neutral", "A reasonable summary of the market.", "https://example.com/a", 6, "DOGE-EUR" }, // not tradable here
    };

    [Theory]
    [MemberData(nameof(BadViews))]
    public async Task InvalidViews_AreRefused_AndChangeNothing(string regime, string summary, string source, int hours, string? avoid)
    {
        var tools = Analyst();

        await Assert.ThrowsAsync<McpException>(() => tools.Analyst.RecordMarketView(
            regime, summary, [source], hours, avoid is null ? null : [avoid], CancellationToken.None));

        Assert.Null(tools.Views.Current);
        Assert.DoesNotContain(tools.Journal.Entries, e => e.Kind == MarketViewStore.Kind);
    }

    [Fact]
    public async Task AnalystTools_AreGovernedByTheAnalystSpec_NotTheTradingSpec()
    {
        // A trading spec that lists record_market_view still doesn't grant it: only the analyst spec does.
        var noAnalyst = new ToolRig(ToolRig.Observer with { AllowedTools = ["record_market_view", "get_analyst_brief"] }, noAnalyst: true);
        var briefOnly = new ToolRig(analyst: ToolRig.AnalystSpec with { AllowedTools = ["get_analyst_brief"] });
        noAnalyst.Header(ToolServerOptions.AgentHeader, "analyst");
        briefOnly.Header(ToolServerOptions.AgentHeader, "analyst");

        await Assert.ThrowsAsync<McpException>(() => noAnalyst.Analyst.RecordMarketView("risk-off", "A reasonable summary of the market.", Sources, cancellationToken: CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => noAnalyst.Analyst.GetAnalystBrief(CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => briefOnly.Analyst.RecordMarketView("risk-off", "A reasonable summary of the market.", Sources, cancellationToken: CancellationToken.None));
        using var brief = JsonDocument.Parse(await briefOnly.Analyst.GetAnalystBrief(CancellationToken.None));
        Assert.Equal("analyst@v1#fedcba654321", brief.RootElement.GetProperty("version").GetString());

        // And the analyst spec doesn't grant trading tools.
        var analystOnly = new ToolRig(noSpec: true);
        await Assert.ThrowsAsync<McpException>(() => analystOnly.Orders.PlaceOrder(
            "BTC-EUR", "buy", 0.0001m, 60_000m, "an analyst trying to trade", cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task EachAgent_KeepsToItsOwnTools_WhateverItsClientAllows()
    {
        var spec = ToolRig.Observer with { AllowedTools = ["place_order", "get_portfolio", "scan_market"] };

        var analyst = new ToolRig(spec);
        analyst.Header(ToolServerOptions.AgentHeader, "analyst");
        var ex = await Assert.ThrowsAsync<McpException>(() => analyst.Orders.PlaceOrder(
            "BTC-EUR", "buy", 0.0001m, 60_000m, "injected text asking for a buy", cancellationToken: CancellationToken.None));
        Assert.Contains("market analyst can't use place_order", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<McpException>(() => analyst.Agent.GetPortfolio(CancellationToken.None));

        foreach (var who in new[] { "trader", null })
        {
            var other = new ToolRig(spec);
            if (who is not null)
                other.Header(ToolServerOptions.AgentHeader, who);
            var refused = await Assert.ThrowsAsync<McpException>(() => other.Analyst.RecordMarketView(
                "risk-off", "A reasonable summary of the market.", Sources, cancellationToken: CancellationToken.None));
            Assert.Contains("for the market analyst only", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RiskOff_HoldsNewBuysAtTheOrderTool_ButSellsPass()
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["place_order"] }, btc: 0.001m);
        tools.Header(ToolServerOptions.AgentHeader, "analyst");
        await tools.Analyst.RecordMarketView("risk-off", "Major stablecoin depeg under way.", Sources, avoidSymbols: ["BTC-EUR"], cancellationToken: CancellationToken.None);
        tools.Header(ToolServerOptions.AgentHeader, "trader");

        var buy = await Assert.ThrowsAsync<McpException>(() => tools.Orders.PlaceOrder(
            "BTC-EUR", "buy", 0.0001m, 60_010m, "signal entry BTC-EUR", cancellationToken: CancellationToken.None));
        var sell = await tools.Orders.PlaceOrder("BTC-EUR", "sell", 0.001m, 60_000m, "signal exit BTC-EUR stop", cancellationToken: CancellationToken.None);

        Assert.Contains("paused by the market view (risk-off)", buy.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stablecoin", buy.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(sell));
    }

    [Fact]
    public async Task UnbrokenRiskOff_IsMeasuredAcrossRenewals()
    {
        var tools = Analyst();
        for (var hour = 0; hour <= 24; hour += 8)
        {
            await tools.Analyst.RecordMarketView("risk-off", "Renewed: exchange outage continues.", Sources, validHours: 12, cancellationToken: CancellationToken.None);
            tools.Rig.Time.Advance(TimeSpan.FromHours(8));
        }

        tools.Rig.Time.Advance(TimeSpan.FromHours(-8)); // back to the last recording
        Assert.Equal(TimeSpan.FromHours(24), tools.Views.RiskOffDuration(tools.Rig.Time.GetUtcNow()));

        await tools.Analyst.RecordMarketView("neutral", "Outage resolved, markets calm.", Sources, cancellationToken: CancellationToken.None);
        Assert.Null(tools.Views.RiskOffDuration(tools.Rig.Time.GetUtcNow()));
    }

    [Fact]
    public async Task ActiveView_IsRestoredFromTheJournalAfterARestart_ExpiredOneIsNot()
    {
        var tools = Analyst();
        await tools.Analyst.RecordMarketView("risk-off", "FOMC decision within the hour.", Sources, validHours: 2, avoidSymbols: ["BTC-EUR"], cancellationToken: CancellationToken.None);

        var restarted = new MarketViewStore(tools.Journal, tools.Rig.Time, NullLogger<MarketViewStore>.Instance);
        await restarted.RestoreAsync(CancellationToken.None);
        tools.Rig.Time.Advance(TimeSpan.FromHours(3));
        var later = new MarketViewStore(tools.Journal, tools.Rig.Time, NullLogger<MarketViewStore>.Instance);
        await later.RestoreAsync(CancellationToken.None);

        Assert.Equal(MarketRegime.RiskOff, restarted.Current!.Regime);
        Assert.Equal("BTC-EUR", Assert.Single(restarted.Current.Flags).Symbol);
        Assert.Null(later.Current);
    }

    [Fact]
    public void AnalystSpecFile_LoadsForPaper_AndAllowsOnlyItsTwoTools()
    {
        var spec = FileStrategySpecStore.Load(Path.Combine(AppContext.BaseDirectory, "strategies", "analyst.v1.json"), "analyst", 1, TradingMode.Paper);

        Assert.Equal(new[] { "get_analyst_brief", "record_market_view" }, spec.AllowedTools.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(6, spec.Parameters["validHours"].GetInt32());
    }
}
