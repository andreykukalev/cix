using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Execution;
using Cix.Engine.MarketData;
using Cix.Engine.Paper;
using Cix.Exchange.RevolutX;
using Microsoft.Extensions.DependencyInjection;

namespace Cix.Engine.Tests;

public sealed class ExchangeRegistrationTests
{
    private static ServiceCollection Services(TradingMode mode, RevolutXOptions? revolutX = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddCixExchange(mode, revolutX ?? (mode == TradingMode.Live ? WithKeys() : new RevolutXOptions()), new PaperOptions());
        return services;
    }

    /// <summary>Placeholders built at runtime: not keys, and never used to sign anything here.</summary>
    private static RevolutXOptions WithKeys(bool apiKey = true, bool privateKey = true) => new()
    {
        ApiKey = apiKey ? new string('a', 64) : string.Empty,
        PrivateKeyPem = privateKey ? TestKeys.FakePem() : string.Empty,
    };

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Paper_RefusesToStartWithRevolutXKeys_AndNamesNoValue(bool apiKey, bool privateKey)
    {
        var keys = WithKeys(apiKey, privateKey);

        var error = Assert.Throws<InvalidOperationException>(() => Services(TradingMode.Paper, keys));

        Assert.Contains("Paper mode holds no trading credentials", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 16), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('p', 16), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Live_RefusesToStartWithoutBothKeys(bool apiKey, bool privateKey)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Services(TradingMode.Live, WithKeys(apiKey, privateKey)));

        Assert.Contains("Live mode needs both", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_TrimsTheKeys_AndRefusesAnApiKeyWithWhitespaceInside()
    {
        var keys = WithKeys();
        var apiKey = keys.ApiKey;
        keys.ApiKey += "\r"; // a file saved with a Windows line ending: the secret files strip only the \n
        Services(TradingMode.Live, keys);
        Assert.Equal(apiKey, keys.ApiKey);

        var split = WithKeys();
        split.ApiKey = split.ApiKey[..32] + " " + split.ApiKey[32..];
        var error = Assert.Throws<InvalidOperationException>(() => Services(TradingMode.Live, split));
        Assert.DoesNotContain(new string('a', 16), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingTheStatusOrTheToolsKeep_CanReachTheKeys()
    {
        // The status document and tool output are built from these types: none may hold the settings the keys live in.
        var forbidden = new[] { typeof(RevolutXOptions), typeof(Microsoft.Extensions.Configuration.IConfiguration) };
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var types = typeof(ExchangeRegistration).Assembly.GetTypes()
            .Where(t => t.Namespace is "Cix.Engine.Status" or "Cix.Engine.Tools")
            .ToList();

        var holders = types.SelectMany(t =>
                t.GetFields(all).Select(f => (t, f.Name, f.FieldType))
                    .Concat(t.GetProperties(all).Select(p => (t, p.Name, p.PropertyType)))
                    .Concat(t.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => (t, p.Name ?? "", p.ParameterType))))
            .Where(m => forbidden.Any(f => f.IsAssignableFrom(m.Item3)))
            .Select(m => $"{m.t.Name}.{m.Item2}")
            .ToList();

        Assert.NotEmpty(types);
        Assert.Empty(holders);
    }

    [Fact]
    public void RevolutXOptions_NeverPrintTheKeys()
    {
        var text = WithKeys().ToString();

        Assert.DoesNotContain(new string('a', 8), text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.Contains("ApiKey = <set>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Paper_HasNoOrderCapableClient_AndNeedsNoCredentials()
    {
        var services = Services(TradingMode.Paper);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(RevolutXClient));
        Assert.Null(provider.GetService<RevolutXClient>());
        Assert.IsType<PaperExchange>(provider.GetRequiredService<IExchangeClient>());

        // Resolving market data must work with empty credentials: it uses public endpoints only, and has no tickers
        // (the paper exchange fills against order-book sizes, which tickers don't carry).
        Assert.IsType<RevolutXPublicMarketData>(provider.GetRequiredService<IMarketDataSource>());
        Assert.IsType<RevolutXPublicMarketData>(provider.GetRequiredService<IQuoteSource>());
        Assert.Null(provider.GetService<IBulkQuoteSource>());
    }

    [Fact]
    public void Live_RegistersTheSignedClient_AndNoPaperExchange()
    {
        var services = Services(TradingMode.Live);

        Assert.Contains(services, d => d.ServiceType == typeof(RevolutXClient));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(PaperExchange));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IQuoteListener));

        // Candles and pairs stay on the unsigned public client; live quotes (tickers, order books) go over the signed client,
        // which can't be resolved here (the placeholder key isn't a key), so its registrations are checked by type.
        using var provider = services.BuildServiceProvider();
        Assert.IsType<RevolutXPublicMarketData>(provider.GetRequiredService<IMarketDataSource>());
        Assert.Contains(services, d => d.ServiceType == typeof(IQuoteSource));
        Assert.Contains(services, d => d.ServiceType == typeof(IBulkQuoteSource));
    }

    [Fact]
    public void Paper_FullEngine_ResolvesTheExecutionService_WithInMemoryState()
    {
        var limits = new RiskLimits(200m, 40m, 100m, 15m, 0.15m, 4, 0.01m, new HashSet<string> { "BTC-EUR" });
        var services = Services(TradingMode.Paper);
        services.AddLogging();
        services.AddSingleton(limits);
        services.AddSingleton(new RiskGate(limits));
        services.AddCixMarketData(new MarketDataOptions(), limits.AllowedSymbols);
        services.AddCixExecution(TradingMode.Paper, new ExecutionOptions());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.NotNull(provider.GetRequiredService<ExecutionService>());
        Assert.IsType<InMemoryEngineStateStore>(provider.GetRequiredService<IEngineStateStore>());
    }

    [Fact]
    public void Live_NeedsDurableState_CosmosOrAStateFile()
    {
        // In Azure the live engine keeps its kill switch and baselines in its own database (AddCixCosmos replaces the store).
        // Never memory: if Cosmos DB didn't replace the store, resolving it fails.
        var withCosmos = Services(TradingMode.Live);
        withCosmos.AddCixExecution(TradingMode.Live, new ExecutionOptions(), durableState: true);
        using (var provider = withCosmos.BuildServiceProvider())
            Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IEngineStateStore>());

        // Without either, it refuses to start: a restart must never forget an engaged kill switch.
        var error = Assert.Throws<InvalidOperationException>(() => Services(TradingMode.Live).AddCixExecution(TradingMode.Live, new ExecutionOptions()));
        Assert.Contains("Execution:StateFile", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Paper_WithMarketData_ResolvesThePoller_AndFeedsThePaperExchange()
    {
        var services = Services(TradingMode.Paper);
        services.AddLogging();
        services.AddCixMarketData(new MarketDataOptions(), ["BTC-EUR"]);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var poller = provider.GetRequiredService<MarketDataPoller>();

        Assert.Equal(new[] { "BTC-EUR" }, poller.Symbols);
        Assert.IsType<PaperExchange>(Assert.Single(provider.GetServices<IQuoteListener>()));
    }
}

/// <summary>Key-shaped placeholders, assembled at runtime so the source holds nothing a secret scanner takes for a key.</summary>
internal static class TestKeys
{
    private const string Kind = "PRIVATE" + " KEY";

    public static string FakePem() => string.Join('\n', "-----BEGIN " + Kind + "-----", new string('p', 64), "-----END " + Kind + "-----");
}
