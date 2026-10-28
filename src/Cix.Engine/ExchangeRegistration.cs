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

        // Keys only where they are used, and there both of them: a missing one stops startup rather than the first order.
        // Trimmed first, so a file saved with a Windows line ending can't pass here and then fail every signed call.
        revolutX.ApiKey = revolutX.ApiKey?.Trim() ?? string.Empty;
        revolutX.PrivateKeyPem = revolutX.PrivateKeyPem?.Trim() ?? string.Empty;
        var hasKey = revolutX.ApiKey.Length > 0;
        var hasPrivateKey = revolutX.PrivateKeyPem.Length > 0;
        if (mode != TradingMode.Live && (hasKey || hasPrivateKey))
            throw new InvalidOperationException("Paper mode holds no trading credentials: remove RevolutX:ApiKey and RevolutX:PrivateKeyPem from its configuration.");
        if (mode == TradingMode.Live && !(hasKey && hasPrivateKey))
            throw new InvalidOperationException("Live mode needs both RevolutX:ApiKey and RevolutX:PrivateKeyPem (the secret files RevolutX__ApiKey and RevolutX__PrivateKeyPem).");
        if (mode == TradingMode.Live && revolutX.ApiKey.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("RevolutX:ApiKey contains whitespace; save the key again on one line.");

        services.AddSingleton(revolutX);

        // Candles and pairs never need credentials, in either mode: public endpoints only.
        services.AddSingleton(sp => new RevolutXPublicMarketData(new HttpClient(), revolutX, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMarketDataSource>(sp => sp.GetRequiredService<RevolutXPublicMarketData>());

        if (mode == TradingMode.Live)
        {
            services.AddSingleton(sp => new RevolutXClient(new HttpClient(), revolutX, time: sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<RevolutXClient>());
            // Live quotes go over the signed endpoints (100 a second, 1,000 a minute): every coin's ticker every few seconds, and
            // an order book on demand for the stop-loss protection, instead of queueing behind the public 1 request a second.
            services.AddSingleton<IQuoteSource>(sp => sp.GetRequiredService<RevolutXClient>());
            services.AddSingleton<IBulkQuoteSource>(sp => sp.GetRequiredService<RevolutXClient>());
            return services;
        }

        // Paper quotes stay public: the paper exchange fills against the order book's sizes, which tickers don't carry.
        services.AddSingleton<IQuoteSource>(sp => sp.GetRequiredService<RevolutXPublicMarketData>());

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
