using Cix.Engine.Journal;
using Cix.Engine.Strategy;

namespace Cix.Engine.Tools;

/// <summary>Bound from the "Journal" section.</summary>
public sealed class JournalOptions
{
    public const string SectionName = "Journal";

    /// <summary>Absolute folder for the daily journal files. Defaults to the local app data folder, separately per trading mode.</summary>
    public string? Directory { get; set; }

    public string Resolve(TradingMode mode)
    {
        if (!string.IsNullOrWhiteSpace(Directory))
            return Directory;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(appData))
            throw new InvalidOperationException("No local app data folder for this user; set Journal:Directory to an absolute path.");
        return Path.Combine(appData, "cix", "journal", mode.ToString().ToLowerInvariant());
    }
}

public static class ToolServerRegistration
{
    public const string Endpoint = "/mcp";

    /// <summary>
    /// The MCP tool server: the only path from agents to the engine (ADR-0001). Stateless Streamable HTTP, so each call
    /// stands alone and the engine keeps no per-agent session state.
    /// </summary>
    public static IServiceCollection AddCixToolServer(
        this IServiceCollection services,
        TradingMode mode,
        ToolServerOptions options,
        StrategyOptions strategy,
        JournalOptions journal)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(journal);
        return services.AddCixToolServer(options, new FileStrategySpecStore(strategy, mode), new FileJournal(journal.Resolve(mode)));
    }

    /// <summary>Registers the tool server over the given spec store and journal.</summary>
    public static IServiceCollection AddCixToolServer(
        this IServiceCollection services,
        ToolServerOptions options,
        IStrategySpecStore specs,
        IJournal journal)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddHttpContextAccessor();
        services.AddSingleton(options);
        services.AddSingleton(specs);
        services.AddSingleton(journal);
        services.AddSingleton<ToolCallContext>();
        services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<AgentTools>()
            .WithTools<OrderTools>()
            .WithTools<ScanTools>()
            .WithTools<AnalystTools>()
            .WithTools<OperatorTools>();
        return services;
    }

    /// <summary>Read-only summary of the latest scan, polled by the trader loop.</summary>
    public const string SignalsEndpoint = "/signals";

    /// <summary>Readiness for Docker and the agents: 200 once the engine has loaded its state and is listening.</summary>
    public const string HealthEndpoint = "/healthz";

    /// <summary>
    /// Maps the MCP endpoint behind a guard: requests with an <c>Origin</c> header (any browser page) get 403, and posts must
    /// be JSON, so a web page can't drive the tools through the user's browser even though the server is on localhost.
    /// </summary>
    public static WebApplication UseCixToolServer(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Endpoint, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.StartsWithSegments(SignalsEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.Headers.Origin.Count > 0)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                if (HttpMethods.IsPost(context.Request.Method)
                    && !(context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    return;
                }
            }

            await next(context).ConfigureAwait(false);
        });
        app.MapMcp(Endpoint);
        app.MapGet(HealthEndpoint, (HttpContext http) =>
        {
            // Docker's health check and the agents' readiness wait. The listener only starts after the database and the saved
            // state are loaded, so answering at all means ready. A halted engine is still healthy: it is up, just not trading.
            // "degraded" means a dependency is failing right now: orders that need it are refused, and it is retried.
            var services = http.RequestServices;
            var database = services.GetService<Status.DependencyHealth>()?.Database ?? Status.DatabaseHealth.Unknown;
            var execution = services.GetService<Execution.ExecutionService>();
            return Results.Json(new
            {
                status = database.State == "unreachable" ? "degraded" : "ready",
                mode = services.GetService<TradingContext>()?.Mode.ToString().ToLowerInvariant(),
                halted = services.GetService<Execution.KillSwitch>()?.IsEngaged,
                database = database.State,
                databaseOkAt = database.OkAt,
                lastReconciledAt = execution?.LastReconciledAt,
            });
        });
        app.MapGet(SignalsEndpoint, (HttpContext http) =>
        {
            // Read-only and cheap: the latest background scan, no exchange call. The trader loop polls it to decide whether an
            // agent run (an LLM call) is worth starting.
            if (http.RequestServices.GetService<Scanning.MarketScanService>() is not { } scanner)
                return Results.NotFound();
            var latest = scanner.Latest;
            return Results.Json(new
            {
                asOf = latest?.AsOf,
                signalDriven = scanner.SignalDriven,
                halted = scanner.Halted,
                entries = latest?.Entries.Count ?? 0,
                exits = latest?.Exits.Count ?? 0,
                staleOrders = scanner.StaleOrderSymbols.Count,
                key = scanner.SignalKey,
            });
        });
        return app;
    }

    /// <summary>
    /// Listens on the configured loopback port only. Endpoints from Kestrel configuration are refused, because they would
    /// override this binding.
    /// </summary>
    public static void ListenOnLoopback(this ConfigureWebHostBuilder webHost, IConfiguration configuration, ToolServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (configuration.GetSection("Kestrel:Endpoints").Exists())
            throw new InvalidOperationException("Kestrel:Endpoints is not allowed; set ToolServer:Url (loopback only) instead.");

        var port = new Uri(options.Url).Port;
        webHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, port));
    }
}
