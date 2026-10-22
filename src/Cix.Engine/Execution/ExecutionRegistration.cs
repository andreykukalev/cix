namespace Cix.Engine.Execution;

/// <summary>Bound from the "Execution" section.</summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    /// <summary>
    /// Live mode only: where the kill switch and loss baselines persist. Defaults to the user profile's local data folder
    /// (under <c>/home</c> on App Service, which survives restarts). Paper mode keeps this state in memory, like its balances.
    /// </summary>
    public string? StateFile { get; set; }

    public static string DefaultStateFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cix", "engine-state.json");
}

public static class ExecutionRegistration
{
    /// <summary>
    /// Registers the order path: <see cref="ExecutionService"/> (the only placer), the kill switch, the portfolio and the
    /// pair catalog. Needs the exchange, market data, <see cref="Domain.Risk.RiskGate"/> and <see cref="Domain.Risk.RiskLimits"/>.
    /// </summary>
    public static IServiceCollection AddCixExecution(this IServiceCollection services, TradingMode mode, ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<IEngineStateStore>(_ => mode == TradingMode.Live
            ? new FileEngineStateStore(string.IsNullOrWhiteSpace(options.StateFile) ? ExecutionOptions.DefaultStateFile : options.StateFile)
            : new InMemoryEngineStateStore());
        services.AddSingleton<PairCatalog>();
        services.AddSingleton<KillSwitch>();
        services.AddSingleton<PortfolioService>();
        services.AddSingleton<ExecutionService>();
        return services;
    }
}
