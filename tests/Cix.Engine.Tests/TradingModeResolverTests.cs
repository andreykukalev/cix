using Microsoft.Extensions.Configuration;

namespace Cix.Engine.Tests;

public sealed class TradingModeResolverTests
{
    private const string Production = TradingModeResolver.ProductionEnvironment;

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(value is null
                ? Array.Empty<KeyValuePair<string, string?>>()
                : new[] { new KeyValuePair<string, string?>(TradingModeResolver.Key, value) })
            .Build();

    [Fact]
    public void Resolve_NotConfigured_IsPaper() =>
        Assert.Equal(TradingMode.Paper, TradingModeResolver.Resolve(Config(null), Production));

    [Theory]
    [InlineData("Paper")]
    [InlineData("paper")]
    [InlineData(" ")]
    public void Resolve_PaperOrBlank_IsPaper(string value) =>
        Assert.Equal(TradingMode.Paper, TradingModeResolver.Resolve(Config(value), "Development"));

    [Fact]
    public void Resolve_ExactLiveInProduction_IsLive() =>
        Assert.Equal(TradingMode.Live, TradingModeResolver.Resolve(Config(nameof(TradingMode.Live)), Production));

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("production")]
    public void Resolve_LiveOutsideProduction_StopsStartup(string environment) =>
        Assert.Throws<InvalidOperationException>(() => TradingModeResolver.Resolve(Config(nameof(TradingMode.Live)), environment));

    [Theory]
    [InlineData("live")]
    [InlineData("LIVE")]
    [InlineData("1")]
    [InlineData("Production")]
    public void Resolve_AnythingElse_StopsStartup(string value) =>
        Assert.Throws<InvalidOperationException>(() => TradingModeResolver.Resolve(Config(value), Production));
}
