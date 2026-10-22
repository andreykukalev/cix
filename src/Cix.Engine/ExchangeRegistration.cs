using Cix.Domain.Exchange;
using Cix.Engine.Paper;
using Cix.Exchange.RevolutX;

namespace Cix.Engine;

public static class ExchangeRegistration
{
    /// <summary>
    /// Registers the exchange for the trading mode. In Paper mode no order-capable client exists at all: market data
    /// comes from Revolut X public endpoints (no credentials) and orders go to <see cref="PaperExchange"/>.
    /// Only Live mode registers the signed <see cref="RevolutXClient"/>.
    /// </summary>
    public static IServiceCollection AddCixExchange(
        this IServiceCollection services,
        TradingMode mode,
        RevolutXOptions revolutX,
        PaperOptions paper)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(revolutX);
        ArgumentNullException.ThrowIfNull(paper);

        services.AddSingleton(revolutX);

        if (mode == TradingMode.Live)
        {
            services.AddSingleton(sp => new RevolutXClient(new HttpClient(), revolutX, time: sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<RevolutXClient>());
            services.AddSingleton<IMarketDataSource>(sp => sp.GetRequiredService<RevolutXClient>());
            return services;
        }

        services.AddSingleton<IMarketDataSource>(sp => new RevolutXPublicMarketData(new HttpClient(), revolutX, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(paper);
        services.AddSingleton(sp => new PaperExchange(
            sp.GetRequiredService<IMarketDataSource>(),
            sp.GetRequiredService<PaperOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<PaperExchange>());
        return services;
    }
}
