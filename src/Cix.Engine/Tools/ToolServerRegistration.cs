using Cix.Engine.Journal;
using Cix.Engine.Strategy;

namespace Cix.Engine.Tools;

/// <summary>Bound from the "Journal" section.</summary>
public sealed class JournalOptions
{
    public const string SectionName = "Journal";

    /// <summary>Absolute folder for the daily journal files. Defaults to the local app data folder, separately per trading mode.</summary>
    public string? Directory { get; set; }

    public string Resolve(TradingMode mode) =>
        string.IsNullOrWhiteSpace(Directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cix", "journal", mode.ToString().ToLowerInvariant())
            : Directory;
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
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddHttpContextAccessor();
        services.AddSingleton(options);
        services.AddSingleton<IStrategySpecStore>(new FileStrategySpecStore(strategy, mode));
        services.AddSingleton<IJournal>(new FileJournal(journal.Resolve(mode)));
        services.AddSingleton<ToolCallContext>();
        services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<AgentTools>()
            .WithTools<OperatorTools>();
        return services;
    }

    public static IEndpointRouteBuilder MapCixToolServer(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMcp(Endpoint);
        return endpoints;
    }
}
