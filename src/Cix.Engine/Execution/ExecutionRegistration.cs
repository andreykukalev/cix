using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.MarketData;

namespace Cix.Engine.Execution;

/// <summary>Bound from the "Execution" section.</summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    /// <summary>
    /// Live mode without Cosmos DB only, required: absolute path where the kill switch and loss baselines persist. It must be
    /// on storage that survives restarts and redeploys. With Cosmos DB the engine state is in the database instead (in Azure,
    /// the live engine's own). Paper mode without it keeps this state in memory, like its balances.
    /// </summary>
    public string? StateFile { get; set; }
}

public static class ExecutionRegistration
{
    /// <summary>
    /// Registers the order path: <see cref="ExecutionService"/> (the only placer), the kill switch, the portfolio and the
    /// pair catalog. Needs the exchange, market data, <see cref="Domain.Risk.RiskGate"/> and <see cref="Domain.Risk.RiskLimits"/>.
    /// <paramref name="durableState"/>: Cosmos DB replaces the engine state store (AddCixCosmos), so Live needs no state file.
    /// </summary>
    public static IServiceCollection AddCixExecution(
        this IServiceCollection services,
        TradingMode mode,
        ExecutionOptions options,
        SafetyOptions? safety = null,
        bool durableState = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        safety ??= new SafetyOptions();
        safety.Validate();

        services.AddSingleton(options);
        if (mode == TradingMode.Live && !durableState && string.IsNullOrWhiteSpace(options.StateFile))
            throw new InvalidOperationException("Live trading needs Cosmos DB or Execution:StateFile, an absolute path on persistent storage.");

        // Live never keeps its state in memory: a restart must not forget an engaged kill switch. With durable state, Cosmos DB
        // replaces this store; if it ever didn't, resolving it fails rather than falling back to memory.
        services.AddSingleton<IEngineStateStore>(sp => mode != TradingMode.Live
            ? new InMemoryEngineStateStore()
            : !string.IsNullOrWhiteSpace(options.StateFile)
                ? new FileEngineStateStore(options.StateFile, sp.GetRequiredService<TimeProvider>())
                : throw new InvalidOperationException("Live trading keeps its state in Cosmos DB, which didn't replace the engine state store."));
        services.AddSingleton<PairCatalog>();
        services.AddSingleton<KillSwitch>();
        services.AddSingleton<PortfolioService>();
        services.AddSingleton(safety);
        services.AddSingleton(sp => new ExecutionService(
            sp.GetRequiredService<IExchangeClient>(),
            sp.GetRequiredService<RiskGate>(),
            sp.GetRequiredService<PortfolioService>(),
            sp.GetRequiredService<MarketState>(),
            sp.GetRequiredService<PairCatalog>(),
            sp.GetRequiredService<KillSwitch>(),
            sp.GetRequiredService<IEngineStateStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ExecutionService>>(),
            safety: safety,
            journal: sp.GetService<Journal.IJournal>(),
            lease: sp.GetService<Persistence.IEngineLease>()));
        services.AddSingleton<SafetyMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<SafetyMonitor>());
        return services;
    }

    /// <summary>
    /// Registers the engine's stop-loss exits (<see cref="ProtectionService"/>): a quote listener that spots a stop on every quote,
    /// and the service that confirms it on a fresh order book and sells. Needs market data, execution and the journal. With
    /// <see cref="ProtectionOptions.Enabled"/> false, stops are left to the scan as before.
    /// </summary>
    public static IServiceCollection AddCixProtection(this IServiceCollection services, ProtectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        if (!options.Enabled)
            return services;

        services.AddSingleton<ProtectionTrigger>();
        services.AddSingleton<IQuoteListener>(sp => sp.GetRequiredService<ProtectionTrigger>());
        services.AddSingleton(sp =>
        {
            var poller = sp.GetRequiredService<MarketDataPoller>();
            return new ProtectionService(
                sp.GetRequiredService<ExecutionService>(),
                sp.GetRequiredService<ProtectionTrigger>(),
                sp.GetRequiredService<MarketState>(),
                poller.RefreshQuoteAsync,
                sp.GetRequiredService<Journal.IJournal>(),
                sp.GetRequiredService<RiskLimits>(),
                options,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ProtectionService>>(),
                strategy: () => sp.GetService<Strategy.IStrategySpecStore>()?.Active?.Label);
        });
        services.AddHostedService(sp => sp.GetRequiredService<ProtectionService>());
        return services;
    }
}
