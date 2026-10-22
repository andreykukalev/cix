using Cix.Domain.Exchange;
using Cix.Engine.Paper;
using Cix.Exchange.RevolutX;

namespace Cix.Engine;

public static class ExchangeRegistration
{
    /// <summary>
    /// Registers the exchange for the trading mode. In Paper mode no order-capable client exists at all: market data
    /// comes from Revolut X public endpoints (no credentials, both modes) and orders go to <see cref="PaperExchange"/>,
    /// which also receives every polled quote.
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

        // Market data never needs credentials, in either mode: public endpoints only.
        services.AddSingleton(sp => new RevolutXPublicMarketData(new HttpClient(), revolutX, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMarketDataSource>(sp => sp.GetRequiredService<RevolutXPublicMarketData>());
        services.AddSingleton<IQuoteSource>(sp => sp.GetRequiredService<RevolutXPublicMarketData>());

        if (mode == TradingMode.Live)
        {
            services.AddSingleton(sp => new RevolutXClient(new HttpClient(), revolutX, time: sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<RevolutXClient>());
            return services;
        }

        services.AddSingleton(paper);
        services.AddSingleton(sp => new PaperExchange(
            sp.GetRequiredService<IMarketDataSource>(),
            sp.GetRequiredService<PaperOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<PaperExchange>());
        services.AddSingleton<IQuoteListener>(sp => sp.GetRequiredService<PaperExchange>());
        return services;
    }
}
