using Microsoft.AspNetCore.Authentication;
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
        if (options.Auth == ToolServerAuth.Entra)
            services.AddAuthentication().AddJwtBearer(EntraToolServerOptions.Scheme, options.Entra.Configure);
        services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<AgentTools>()
            .WithTools<OrderTools>()
            .WithTools<ScanTools>()
            .WithTools<AnalystTools>()
            .WithTools<ManualTools>()
            .WithTools<OperatorTools>();
        return services;
    }

    /// <summary>Read-only summary of the latest scan, polled by the trader loop.</summary>
    public const string SignalsEndpoint = "/signals";

    /// <summary>Where agent jobs claim their runs and report how they ended (ADR-0013): <c>/runs/{id}/claim</c>, <c>/runs/{id}/outcome</c>.</summary>
    public const string RunsEndpoint = "/runs";

    /// <summary>Readiness for Docker and the agents: 200 once the engine has loaded its state and is listening.</summary>
    public const string HealthEndpoint = "/healthz";

    /// <summary>
    /// Maps the MCP endpoint behind a guard: requests with an <c>Origin</c> header (any browser page) get 403, and posts must
    /// be JSON, so a web page can't drive the tools through the user's browser even though the server is on localhost.
    /// With Entra authentication, every request except <c>GET /healthz</c> (the platform's probes) also needs a valid token
    /// from a listed managed identity: 401 without one, 403 for an identity that isn't listed.
    /// </summary>
    public static WebApplication UseCixToolServer(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<ToolServerOptions>();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Endpoint, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.StartsWithSegments(SignalsEndpoint, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.StartsWithSegments(RunsEndpoint, StringComparison.OrdinalIgnoreCase))
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

            // With Entra, everything except the health probe needs a listed identity: a route added later is closed by default.
            if (options.Auth == ToolServerAuth.Entra
                && !(HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Equals(HealthEndpoint, StringComparison.OrdinalIgnoreCase)))
            {
                var result = await context.AuthenticateAsync(EntraToolServerOptions.Scheme).ConfigureAwait(false);
                var role = result.Succeeded ? options.Entra.RoleFor(result.Principal) : null;
                if (role is null)
                {
                    context.Response.StatusCode = result.Succeeded ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
                    return;
                }

                context.Items[ToolCallContext.AuthenticatedAgentItem] = role;
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
            var manual = http.RequestServices.GetService<Manual.ManualRequestService>();
            return Results.Json(new
            {
                asOf = latest?.AsOf,
                signalDriven = scanner.SignalDriven,
                halted = scanner.Halted,
                entries = latest?.Entries.Count ?? 0,
                exits = latest?.Exits.Count ?? 0,
                staleOrders = scanner.StaleOrderSymbols.Count,
                key = scanner.SignalKey,
                // Owner requests waiting for the trader (manual:<id>|...): a run is due for each new one, whatever the strategy.
                manualKey = manual?.AgentKey ?? string.Empty,
                manualPending = manual?.Pending.Count ?? 0,
            });
        });
        MapAgentRuns(app);
        return app;
    }

    /// <summary>
    /// The agent jobs' side of the run queues. A job claims its run before it starts Claude (<c>{"wanted": false}</c> means
    /// drop the message), then reports <c>{"outcome": "done" | "failed" | "not-run"}</c>. The job is known by its token's
    /// role, so it can claim and report only its own kind of run. 503 while this engine can't answer (it doesn't hold the
    /// engine lease, a look is in progress for more than 10 seconds, or another engine saved the state): the job should
    /// try again shortly. 404 without the scheduler (Compose). A change is saved whether or not the caller is still
    /// waiting, so a dropped connection can't leave a claim half made.
    /// </summary>
    private static void MapAgentRuns(WebApplication app)
    {
        app.MapPost(RunsEndpoint + "/{id}/claim", async (string id, RunClaimBody body, HttpContext http) =>
        {
            if (http.RequestServices.GetService<AgentRuns.AgentRunScheduler>() is not { } scheduler)
                return Results.NotFound();
            if (body.Claimant is { } claimant && !ToolCallContext.IsSafeLabel(claimant))
                return Results.BadRequest();
            var (answer, run) = await scheduler.ClaimAsync(id, http.Items[ToolCallContext.AuthenticatedAgentItem] as string, body.Claimant, CancellationToken.None).ConfigureAwait(false);
            return answer == AgentRuns.AgentRunAnswer.Unavailable
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Json(new { wanted = answer == AgentRuns.AgentRunAnswer.Yes, purpose = run?.Purpose });
        });
        app.MapPost(RunsEndpoint + "/{id}/outcome", async (string id, RunOutcomeBody body, HttpContext http) =>
        {
            if (http.RequestServices.GetService<AgentRuns.AgentRunScheduler>() is not { } scheduler)
                return Results.NotFound();
            if (AgentRuns.AgentRunKinds.ParseOutcome(body.Outcome) is not { } outcome)
                return Results.BadRequest();
            return await scheduler.CompleteAsync(id, http.Items[ToolCallContext.AuthenticatedAgentItem] as string, outcome, CancellationToken.None).ConfigureAwait(false) switch
            {
                AgentRuns.AgentRunAnswer.Yes => Results.NoContent(),
                AgentRuns.AgentRunAnswer.Unavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                _ => Results.NotFound(), // unknown, not claimed, another job's, or reported already
            };
        });
    }

    /// <summary>The body of <c>POST /runs/{id}/claim</c>: the job execution's name, so it can repeat a claim whose answer it lost.</summary>
    public sealed record RunClaimBody(string? Claimant);

    /// <summary>The body of <c>POST /runs/{id}/outcome</c>.</summary>
    public sealed record RunOutcomeBody(string? Outcome);

    /// <summary>
    /// Listens on the configured port: on loopback only, or on every interface with Entra authentication. Endpoints from
    /// Kestrel configuration are refused, because they would override this binding.
    /// </summary>
    public static void ListenForAgents(this ConfigureWebHostBuilder webHost, IConfiguration configuration, ToolServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (configuration.GetSection("Kestrel:Endpoints").Exists())
            throw new InvalidOperationException("Kestrel:Endpoints is not allowed; set ToolServer:Url instead.");

        var port = new Uri(options.Url).Port;
        var address = options.Auth == ToolServerAuth.Entra ? System.Net.IPAddress.Any : System.Net.IPAddress.Loopback;
        webHost.ConfigureKestrel(k => k.Listen(address, port));
    }
}
