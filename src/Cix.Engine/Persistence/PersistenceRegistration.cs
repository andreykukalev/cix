using System.Text.Json;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.Paper;
using Cix.Engine.Strategy;

namespace Cix.Engine.Persistence;

/// <summary>
/// Saves the paper account every second when it changed, and once more at shutdown, so a restart (or a crash, losing at most
/// a second) resumes the same balances and orders. The engine state records each order before it is sent, so an order lost
/// in that second is noticed as gone at the next reconciliation and its plan removed; nothing is placed twice.
/// </summary>
public sealed partial class PaperAccountSaver(PaperExchange paper, IPaperAccountStore store, TimeProvider time, ILogger<PaperAccountSaver> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private string? _lastSaved;
    private bool _restored;
    private bool _warm;

    /// <summary>Saves if anything changed since the last save. Returns whether it wrote.</summary>
    public async Task<bool> SaveIfChangedAsync(CancellationToken cancellationToken)
    {
        var state = paper.ExportState();
        var json = JsonSerializer.Serialize(state, CosmosDb.Json);
        if (json == _lastSaved)
            return false;
        await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        _lastSaved = json;
        return true;
    }

    /// <summary>
    /// Restores the saved account, if there is one. Call before the engine starts; safe to call again after a failed attempt
    /// (startup retries while the database isn't ready), and the account is restored once only.
    /// </summary>
    public async Task<bool> RestoreAsync(CancellationToken cancellationToken)
    {
        if (_restored)
            return true;
        var saved = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            _restored = true;
            _warm = true; // nothing resting to fill
            return false;
        }

        paper.RestoreState(saved);
        _restored = true;
        _lastSaved = JsonSerializer.Serialize(paper.ExportState(), CosmosDb.Json);
        LogRestored(logger, saved.Orders.Count(o => o.Status is Domain.Exchange.OrderStatus.New or Domain.Exchange.OrderStatus.PartiallyFilled));
        await TryWarmUpAsync(cancellationToken).ConfigureAwait(false); // so restored resting orders can fill; retried in the background
        return true;
    }

    /// <summary>Loads the pair rules restored resting orders need to fill. Market data may not be reachable yet: retried every second.</summary>
    private async Task TryWarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await paper.WarmUpAsync(cancellationToken).ConfigureAwait(false);
            _warm = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogWarmUpFailed(logger, ex.GetType().Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (_restored && !_warm)
                    await TryWarmUpAsync(stoppingToken).ConfigureAwait(false);
                await TrySaveAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await TrySaveAsync(cancellationToken).ConfigureAwait(false); // the final state on a clean shutdown
    }

    private async Task TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveIfChangedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSaveFailed(logger, ex.GetType().Name, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Paper account restored ({OpenOrders} open orders)")]
    private static partial void LogRestored(ILogger logger, int openOrders);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Paper pair rules not loaded yet ({Error}); restored resting orders fill once they are")]
    private static partial void LogWarmUpFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Paper account could not be saved ({Error}): {Detail}")]
    private static partial void LogSaveFailed(ILogger logger, string error, string detail);
}

public static class PersistenceRegistration
{
    /// <summary>
    /// With Cosmos DB enabled, replaces the file and memory stores: the journal, the engine state and (in Paper mode) the
    /// paper account all live in the database. Register after the execution and tool server services, so these win.
    /// </summary>
    public static IServiceCollection AddCixCosmos(this IServiceCollection services, CosmosOptions options, TradingMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!options.Enabled)
            return services;
        if (mode == TradingMode.Live && options.Emulator)
            throw new InvalidOperationException("Live trading can't use the Cosmos DB emulator: it doesn't authenticate, so anything on the machine could change the engine state.");

        services.AddSingleton(options);
        services.AddSingleton(sp => new CosmosDb(options));
        services.AddSingleton<IJournal>(sp => new CosmosJournal(sp.GetRequiredService<CosmosDb>()));
        services.AddSingleton<IEngineStateStore>(sp => new CosmosEngineStateStore(sp.GetRequiredService<CosmosDb>(), mode, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<CosmosSpecArchive>();
        services.AddSingleton<Status.IEngineStatusStore>(sp => new CosmosEngineStatusStore(sp.GetRequiredService<CosmosDb>(), mode));
        services.AddSingleton<Manual.IManualRequestStore>(sp => new Manual.CosmosManualRequestStore(sp.GetRequiredService<CosmosDb>()));
        services.AddSingleton<Scanning.IScannerFilterStore>(sp => new CosmosScannerFilterStore(sp.GetRequiredService<CosmosDb>(), mode));
        if (mode == TradingMode.Paper)
        {
            services.AddSingleton<IPaperAccountStore, CosmosPaperAccountStore>();
            services.AddSingleton<PaperAccountSaver>();
            services.AddHostedService(sp => sp.GetRequiredService<PaperAccountSaver>());
        }

        return services;
    }

    /// <summary>
    /// Before the engine starts: creates the database and containers, loads the engine state, archives the active strategy
    /// spec, and restores the paper account. Does nothing when Cosmos DB is disabled. Every step can run again after a failure,
    /// so startup can retry the whole thing while the database is still starting (see <see cref="StartupRetry"/>).
    /// </summary>
    public static async Task InitializeCixPersistenceAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<CosmosDb>() is not { } db)
            return;

        await db.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _ = services.GetRequiredService<IEngineStateStore>(); // reads the saved engine state now, so a failure here is retried too
        if (services.GetService<IStrategySpecStore>()?.Active is { } spec)
            await services.GetRequiredService<CosmosSpecArchive>().ArchiveAsync(spec, cancellationToken).ConfigureAwait(false);
        if (services.GetService<PaperAccountSaver>() is { } saver)
            await saver.RestoreAsync(cancellationToken).ConfigureAwait(false);
    }
}
