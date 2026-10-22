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
        var normalised = symbol.Trim().Replace('/', '-').ToUpperInvariant();
        var hourly = store.Recent(normalised, MarketDataPoller.HourInterval, HourlyCandlesNeeded);
        return FeatureCalculator.Compute(normalised, hourly, state.Quote(normalised), time.GetUtcNow(), options.MaxQuoteAge);
    }
}

public static class MarketDataRegistration
{
    /// <summary>Registers the poller for <paramref name="symbols"/> (the risk gate's allowlist) and the feature service.</summary>
    public static IServiceCollection AddCixMarketData(this IServiceCollection services, MarketDataOptions options, IEnumerable<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        var symbolList = symbols.ToList();
        options.Validate(symbolList.Count);

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
            symbolList,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<MarketDataPoller>>()));
        services.AddHostedService<MarketDataService>();
        return services;
    }
}
