using Cix.Domain.Exchange;
using Cix.Domain.MarketData;

namespace Cix.Engine.MarketData;

/// <summary>Runs the poller every second. The poller itself decides what is due.</summary>
public sealed class MarketDataService(MarketDataPoller poller, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        do
        {
            await poller.PollOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>Features for agents, computed on demand from stored hourly candles and the latest quote.</summary>
public sealed class FeatureService(ICandleStore store, MarketState state, MarketDataOptions options, TimeProvider time)
{
    /// <summary>Enough hourly candles for EMA 50 plus 24h-over-24h comparisons.</summary>
    public const int HourlyCandlesNeeded = 60;

    public FeatureSet GetFeatures(string symbol)
    {
        var normalised = MarketDataPoller.NormaliseSymbol(symbol);
        var hourly = store.Recent(normalised, MarketDataPoller.HourInterval, HourlyCandlesNeeded);
        return FeatureCalculator.Compute(normalised, hourly, state.Quote(normalised), time.GetUtcNow(), options.MaxQuoteAge);
    }
}

public static class MarketDataRegistration
{
    private static readonly IReadOnlySet<string> NoSymbols = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Registers the poller for a fixed list of <paramref name="symbols"/> (every one gets order books) and the feature service. For tests.</summary>
    public static IServiceCollection AddCixMarketData(this IServiceCollection services, MarketDataOptions options, IEnumerable<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var symbolList = MarketDataPoller.NormaliseSymbols(symbols);
        return services.AddCixMarketData(options, () => symbolList, symbolList.Count, signedQuotes: false, activeOnly: false);
    }

    /// <summary>
    /// Registers the poller for the pairs watched now (<paramref name="universe"/>, which follows the owner's run-time list),
    /// checked against the rate limit for <paramref name="maxSymbols"/> of them, and the feature service. With
    /// <paramref name="signedQuotes"/> (live) the quotes and tickers count against the signed budget. Order books go only to the
    /// active coins (<see cref="IActiveSymbolSource"/>, the execution service) and to coins the tickers leave out.
    /// </summary>
    public static IServiceCollection AddCixMarketData(this IServiceCollection services, MarketDataOptions options, Scanning.MarketUniverse universe, int maxSymbols, bool signedQuotes)
    {
        ArgumentNullException.ThrowIfNull(universe);
        return services.AddCixMarketData(options, () => universe.Tradable, maxSymbols, signedQuotes, activeOnly: true);
    }

    private static IServiceCollection AddCixMarketData(
        this IServiceCollection services, MarketDataOptions options, Func<IReadOnlyList<string>> symbols, int maxSymbols, bool signedQuotes, bool activeOnly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(Math.Max(symbols().Count, maxSymbols), signedQuotes);

        services.AddSingleton(options);
        services.AddSingleton<ICandleStore>(new InMemoryCandleStore());
        services.AddSingleton<MarketState>();
        services.AddSingleton<FeatureService>();
        services.AddSingleton(sp => new MarketDataPoller(
            sp.GetRequiredService<IMarketDataSource>(),
            sp.GetRequiredService<IQuoteSource>(),
            sp.GetRequiredService<ICandleStore>(),
            sp.GetRequiredService<MarketState>(),
            sp.GetServices<IQuoteListener>(),
            options,
            symbols,
            maxSymbols,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<MarketDataPoller>>(),
            tickers: sp.GetService<IBulkQuoteSource>(), // signed in live, public in paper
            signedQuotes: signedQuotes,
            active: activeOnly ? () => sp.GetService<IActiveSymbolSource>()?.ActiveSymbols ?? NoSymbols : null));
        services.AddHostedService<MarketDataService>();
        return services;
    }
}
