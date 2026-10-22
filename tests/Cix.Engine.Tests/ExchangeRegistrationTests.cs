using Cix.Domain.Exchange;
using Cix.Engine.Paper;
using Cix.Exchange.RevolutX;
using Microsoft.Extensions.DependencyInjection;

namespace Cix.Engine.Tests;

public sealed class ExchangeRegistrationTests
{
    private static ServiceCollection Services(TradingMode mode)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddCixExchange(mode, new RevolutXOptions(), new PaperOptions());
        return services;
    }

    [Fact]
    public void Paper_HasNoOrderCapableClient_AndNeedsNoCredentials()
    {
        var services = Services(TradingMode.Paper);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(RevolutXClient));
        Assert.Null(provider.GetService<RevolutXClient>());
        Assert.IsType<PaperExchange>(provider.GetRequiredService<IExchangeClient>());

        // Resolving market data must work with empty credentials: it uses public endpoints only.
        Assert.IsType<RevolutXPublicMarketData>(provider.GetRequiredService<IMarketDataSource>());
    }

    [Fact]
    public void Live_RegistersTheSignedClient_AndNoPaperExchange()
    {
        var services = Services(TradingMode.Live);

        Assert.Contains(services, d => d.ServiceType == typeof(RevolutXClient));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(PaperExchange));
    }
}
