using System.Text.Json;

namespace Cix.Stocks.Host.Tests;

public class StocksConfigTests
{
    private static string Shipped => Path.Combine(AppContext.BaseDirectory, "config", "stocks.json");

    [Fact]
    public void TheShippedConfigurationFitsTheFreeKey()
    {
        var config = StocksConfig.Load(Shipped);

        // One call per instrument and one for the benchmark, under the free key's 20 a day.
        Assert.True(config.Watchlist.Count + 1 <= config.MaxCallsPerRun);
        Assert.True(config.MaxCallsPerDay < 20);
        Assert.Equal(("USD", 200m, "SPY.US"), (config.BaseCurrency, config.AdvisorSettings.MaxSharePrice, config.AdvisorSettings.BenchmarkSymbol));
        Assert.All(config.Watchlist, w => Assert.NotNull(w.Theme));
    }

    [Fact]
    public void AMisspeltSettingFails()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, File.ReadAllText(Shipped).Replace("\"maxSharePrice\"", "\"maxSharePrise\"", StringComparison.Ordinal));
            Assert.Throws<JsonException>(() => StocksConfig.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BadSettingsAreRefused()
    {
        var good = StocksConfig.Load(Shipped);

        Assert.Throws<ArgumentException>(() => (good with { Watchlist = [.. good.Watchlist, good.Watchlist[0]] }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { Watchlist = [new("SPY.US", "Benchmark again", "USD")] }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { Watchlist = [new("XYZ.US", "XYZ", "USD", "Big Tech")] }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { Benchmark = "spy" }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { Watchlist = [new("SAP.XETRA", "SAP", "EUR")] }).Validate());
        (good with { Watchlist = [new("SAP.XETRA", "SAP", "EUR")], Fx = new Dictionary<string, decimal> { ["EUR"] = 1.08m } }).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (good with { Fx = new Dictionary<string, decimal> { ["EUR"] = 0m } }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (good with { Capital = 0m }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (good with { HistoryDays = 200 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (good with { DataReadyUtc = new TimeOnly(15, 0) }).Validate());
    }

    [Theory]
    // Thursday 02:00: Wednesday's session was ready at Thursday 01:00.
    [InlineData("2026-10-29T02:00:00Z", "2026-10-29T01:00:00Z")]
    // Thursday 00:30: not yet, so Tuesday's (ready Wednesday 01:00) is the latest.
    [InlineData("2026-10-29T00:30:00Z", "2026-10-28T01:00:00Z")]
    // Sunday and Monday: Friday's session, ready Saturday 01:00, until Tuesday 01:00.
    [InlineData("2026-11-01T12:00:00Z", "2026-10-31T01:00:00Z")]
    [InlineData("2026-11-02T23:00:00Z", "2026-10-31T01:00:00Z")]
    [InlineData("2026-11-03T01:00:00Z", "2026-11-03T01:00:00Z")]
    public void DataCutoff_IsTheLatestSessionsReadyTime(string now, string expected)
    {
        var config = StocksConfig.Load(Shipped);

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), config.DataCutoff(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
    }
}

public class StocksJsonTests
{
    [Fact]
    public void WordsAreTheDashboardsKebabCase()
    {
        Assert.Equal(["sell", "set-stop", "raise-stop", "buy"], Enum.GetValues<ActionKind>().Select(StocksJson.Word));
        Assert.Equal(["unknown", "risk-on", "risk-off"], Enum.GetValues<MarketRegime>().Select(StocksJson.Word));
        Assert.Equal("stop-out", StocksJson.Word(Simulation.TradeKind.StopOut));
    }

    [Fact]
    public void ThePortfolioKeepsItsDecimalsExactThroughJson()
    {
        var state = new PaperState(
            "USD", 10_000m, 8043.170125m,
            [new(new Holding("XYZ.US", 14m, 142.4423m, 139.2m, new DateOnly(2026, 10, 29)), 1999.0911m)],
            new DateOnly(2026, 10, 29), [new(ActionKind.RaiseStop, "XYZ.US", 14m, 150.1m, 147.1m, Reasons.Trail)],
            new DateOnly(2026, 10, 30), 15);

        var json = System.Text.Json.JsonSerializer.Serialize(state, StocksJson.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<PaperState>(json, StocksJson.Options)!;

        Assert.Contains("\"cash\":\"8043.170125\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"raise-stop\"", json, StringComparison.Ordinal);
        Assert.Equal(state.Cash, back.Cash);
        Assert.Equal(state.Positions, back.Positions);
        Assert.Equal(state.Pending, back.Pending);
        Assert.Equal((state.LastAdvisedDay, state.CallsDay, state.Calls), (back.LastAdvisedDay, back.CallsDay, back.Calls));
    }
}
