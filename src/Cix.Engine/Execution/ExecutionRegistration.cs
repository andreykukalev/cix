using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.MarketData;

namespace Cix.Engine.Execution;

/// <summary>Bound from the "Execution" section.</summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    /// <summary>
    /// Live mode only, required: absolute path where the kill switch and loss baselines persist. It must be on storage that
    /// survives restarts and redeploys (on App Service Linux, under <c>/home</c>). Paper mode keeps this state in memory,
    /// like its balances.
    /// </summary>
    public string? StateFile { get; set; }
}

public static class ExecutionRegistration
{
    /// <summary>
    /// Registers the order path: <see cref="ExecutionService"/> (the only placer), the kill switch, the portfolio and the
    /// pair catalog. Needs the exchange, market data, <see cref="Domain.Risk.RiskGate"/> and <see cref="Domain.Risk.RiskLimits"/>.
    /// </summary>
    public static IServiceCollection AddCixExecution(
        this IServiceCollection services,
        TradingMode mode,
        ExecutionOptions options,
        SafetyOptions? safety = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        safety ??= new SafetyOptions();
        safety.Validate();

        services.AddSingleton(options);
        if (mode == TradingMode.Live && string.IsNullOrWhiteSpace(options.StateFile))
            throw new InvalidOperationException("Live trading needs Execution:StateFile, an absolute path on persistent storage.");

        services.AddSingleton<IEngineStateStore>(sp => mode == TradingMode.Live
            ? new FileEngineStateStore(options.StateFile!, sp.GetRequiredService<TimeProvider>())
            : new InMemoryEngineStateStore());
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
            journal: sp.GetService<Journal.IJournal>()));
        services.AddSingleton<SafetyMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<SafetyMonitor>());
        return services;
    }
}
