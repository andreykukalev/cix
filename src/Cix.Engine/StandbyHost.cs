using Cix.Engine.Tools;

namespace Cix.Engine;

/// <summary>
/// What a Paper engine runs instead when the Revolut X key files are among its secret files: the live environment's engine as
/// its template deploys it, until the owner switches it to Live (docs/live-trading.md, ADR-0017). A Paper engine never holds
/// trading credentials, so it refuses to run: it never reads the files, and starts nothing (no market data, no database, no
/// agents, no tools). It only listens, to answer <c>/healthz</c> with <c>standby</c> and the reason, so the platform keeps
/// the revision instead of failing the deployment over a container that keeps exiting.
/// </summary>
public static partial class StandbyHost
{
    public const string Status = "standby";
    public const string Reason = "paper-holds-exchange-keys";

    /// <summary>The standby app on <paramref name="builder"/>, listening where the tool server would.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var toolServer = builder.Configuration.GetSection(ToolServerOptions.SectionName).Get<ToolServerOptions>() ?? new ToolServerOptions();
        builder.WebHost.ListenForAgents(builder.Configuration, toolServer);
        var app = builder.Build();
        app.MapGet(ToolServerRegistration.HealthEndpoint, () => Results.Json(new { status = Status, mode = "paper", reason = Reason }));
        return app;
    }

    /// <summary>Runs the standby app until the host stops. <paramref name="held"/> names the key files, never their contents.</summary>
    public static async Task RunAsync(WebApplicationBuilder builder, IReadOnlyList<string> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        await using var app = Build(builder);
        LogStandingBy(app.Logger, string.Join(" and ", held));
        await app.RunAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Standing by: this engine is in Paper mode but its secret files hold {Files}, and a Paper engine holds no trading credentials. Nothing runs until the owner switches it to Live (docs/live-trading.md) or the files are removed.")]
    private static partial void LogStandingBy(ILogger logger, string files);
}
