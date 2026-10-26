using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cix.Engine.Journal;
using Cix.Engine.Strategy;
using ModelContextProtocol;

namespace Cix.Engine.Tools;

/// <summary>Bound from the "ToolServer" section.</summary>
public sealed class ToolServerOptions
{
    public const string SectionName = "ToolServer";
    public const string AgentHeader = "X-Cix-Agent";
    public const string RunHeader = "X-Cix-Run";
    public const string OperatorKeyHeader = "X-Cix-Operator-Key";

    /// <summary>
    /// Where the MCP endpoint listens: loopback with <see cref="ToolServerAuth.Loopback"/>; any address (in Azure,
    /// <c>http://0.0.0.0:5077</c> behind the environment's internal ingress) with <see cref="ToolServerAuth.Entra"/>.
    /// </summary>
    public string Url { get; set; } = "http://127.0.0.1:5077";

    /// <summary>How callers are identified. Loopback unless configured (ADR-0013).</summary>
    public ToolServerAuth Auth { get; set; } = ToolServerAuth.Loopback;

    public EntraToolServerOptions Entra { get; set; } = new();

    /// <summary>
    /// Secret that unlocks operator tools, from user secrets (never in a file in the repo). Agents' MCP configuration
    /// doesn't carry it. When unset, operator tools are disabled.
    /// </summary>
    public string? OperatorKey { get; set; }

    public void Validate()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException("ToolServer:Url must be an absolute http URL.");
        if (Auth == ToolServerAuth.Entra)
        {
            Entra.Validate();
            if (!string.IsNullOrEmpty(OperatorKey))
                throw new InvalidOperationException("ToolServer:OperatorKey must be unset with Entra authentication: operator tools stay off the network.");
            return;
        }

        if (!uri.IsLoopback)
            throw new InvalidOperationException("ToolServer:Url must be a loopback address (127.0.0.1 or localhost) unless ToolServer:Auth is Entra.");
        if (OperatorKey is { Length: > 0 and < 24 })
            throw new InvalidOperationException("ToolServer:OperatorKey must be at least 24 characters.");
    }
}

/// <summary>
/// Who is calling a tool and what they may do. Agent tools must be listed in the active strategy spec; operator tools
/// need the operator key. Every call is journaled.
/// </summary>
public sealed partial class ToolCallContext(
    IHttpContextAccessor http,
    IStrategySpecStore specs,
    IJournal journal,
    ToolServerOptions options,
    TimeProvider time,
    ILogger<ToolCallContext> logger,
    Analysis.IAnalystSpecStore? analystSpecs = null)
{
    /// <summary>Names that only the owner or the engine itself appear as in the journal; agents can't claim them.</summary>
    public static readonly IReadOnlySet<string> ReservedActors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "owner", "engine", "execution", "reconciler", "safety-monitor", "scanner", "invalid",
    };

    /// <summary>The <c>X-Cix-Agent</c> value the analyst's MCP configuration sends.</summary>
    public const string AnalystAgent = "analyst";

    /// <summary>
    /// The <c>X-Cix-Agent</c> value of trader runs for owner requests (tools/trader/mcp-requests.json). Only these runs answer
    /// requests, and they can't place or cancel orders of their own, whatever the owner's note says (ADR-0011).
    /// </summary>
    public const string RequestsAgent = "trader-requests";

    private static readonly IReadOnlySet<string> OwnOrderTools = new HashSet<string>(StringComparer.Ordinal) { "place_order", "cancel_order" };

    /// <summary>Where the tool server's guard puts the agent role it took from a validated Entra token.</summary>
    public const string AuthenticatedAgentItem = "cix-agent";

    public string Agent
    {
        get
        {
            // With Entra, the identity decides: a caller can't name itself into another agent's tools.
            if (options.Auth == ToolServerAuth.Entra)
                return http.HttpContext?.Items[AuthenticatedAgentItem] as string ?? "invalid";
            var name = Header(ToolServerOptions.AgentHeader);
            return name is null ? "agent" : ReservedActors.Contains(name) ? "invalid" : name;
        }
    }

    public string? RunId => Header(ToolServerOptions.RunHeader);

    public StrategySpec? Spec => specs.Active;

    public DateTimeOffset Now => time.GetUtcNow();

    public bool IsOperator
    {
        get
        {
            var supplied = http.HttpContext?.Request.Headers[ToolServerOptions.OperatorKeyHeader].ToString();
            if (options.Auth != ToolServerAuth.Loopback || string.IsNullOrEmpty(options.OperatorKey) || string.IsNullOrEmpty(supplied))
                return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(options.OperatorKey));
        }
    }

    /// <summary>
    /// Throws unless the governing spec lets agents call <paramref name="tool"/>; a refusal is journaled first. Analyst tools
    /// are governed by the analyst's spec, every other agent tool by the trading spec. The operator may call any agent tool.
    /// </summary>
    public async Task RequireAllowedAsync(string tool, CancellationToken cancellationToken)
    {
        if (IsOperator)
            return;
        var analyst = AnalystTools.Names.Contains(tool);
        var governing = analyst ? analystSpecs?.Active : specs.Active;
        var isAnalyst = string.Equals(Agent, AnalystAgent, StringComparison.Ordinal);
        // Each agent keeps to its own tools, whatever its client allows: the analyst (which reads the web) never reaches
        // trading tools, and no other agent records market views.
        var isRequests = string.Equals(Agent, RequestsAgent, StringComparison.Ordinal);
        var reason = analyst != isAnalyst
            ? analyst ? $"{tool} is for the market analyst only." : $"The market analyst can't use {tool}."
            : ManualTools.Names.Contains(tool) && !isRequests
            ? $"{tool} is for runs that answer owner requests only."
            : isRequests && OwnOrderTools.Contains(tool)
            ? "Runs that answer owner requests can't place or cancel orders; answer with answer_manual_request."
            : governing is not { } spec
            ? analyst ? "No analyst spec is configured, so analyst tools are disabled." : "No strategy spec is active, so agent tools are disabled."
            : spec.Allows(tool) || (ManualTools.Names.Contains(tool) && spec.Allows(ManualTools.ImpliedBy))
            ? null // owner requests: allowed with place_order, which can do more than they can (ADR-0011)
            : $"The active {(analyst ? "analyst" : "strategy")} spec ({spec.Label}) doesn't allow {tool}.";
        if (reason is null)
            return;
        await DenyAsync(tool, reason, cancellationToken).ConfigureAwait(false);
        throw new McpException(reason);
    }

    public async Task RequireOperatorAsync(string tool, CancellationToken cancellationToken)
    {
        if (IsOperator)
            return;
        const string reason = "This is an operator tool.";
        await DenyAsync(tool, reason, cancellationToken).ConfigureAwait(false); // no key material is recorded
        throw new McpException(reason);
    }

    /// <summary>The label of the spec that governs <paramref name="tool"/>: the analyst's for analyst tools, else the trading spec's.</summary>
    private string? LabelFor(string? tool) =>
        tool is not null && AnalystTools.Names.Contains(tool) ? analystSpecs?.Active?.Label : specs.Active?.Label;

    private Task DenyAsync(string tool, string reason, CancellationToken cancellationToken) =>
        journal.AppendAsync(
            new JournalEntry(JournalIds.New(time.GetUtcNow(), "tool-denied"), time.GetUtcNow(), "tool-denied", Agent, RunId, LabelFor(tool), reason,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["tool"] = tool }),
            cancellationToken);

    public Task JournalAsync(string tool, string summary, IReadOnlyDictionary<string, string>? data, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>(data ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["tool"] = tool };
        return RecordAsync("tool-call", summary, fields, cancellationToken);
    }

    /// <summary>
    /// Records something that already happened (an order sent): never cancelled with the request and never thrown, so the
    /// caller still gets the result and doesn't retry an order that exists. A failure is logged.
    /// </summary>
    public async Task RecordOutcomeAsync(string kind, string summary, IReadOnlyDictionary<string, string> data)
    {
        try
        {
            await RecordAsync(kind, summary, data, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogJournalFailed(logger, kind, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed for a {Kind} entry: {Detail}")]
    private static partial void LogJournalFailed(ILogger logger, string kind, string detail);

    /// <summary>Appends an entry of any kind as the current caller, with the active spec.</summary>
    public Task RecordAsync(string kind, string summary, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken) =>
        journal.AppendAsync(
            new JournalEntry(JournalIds.New(time.GetUtcNow(), kind), time.GetUtcNow(), kind, IsOperator ? "owner" : Agent, RunId, LabelFor(data.GetValueOrDefault("tool")), summary, data),
            cancellationToken);

    /// <summary>Identity headers are free text from the caller: keep them short and plain.</summary>
    private string? Header(string name)
    {
        var value = http.HttpContext?.Request.Headers[name].ToString();
        return string.IsNullOrEmpty(value) ? null : IsSafeLabel(value) ? value : "invalid";
    }

    /// <summary>Short plain text: letters, digits and <c>. _ : -</c>, up to 64 characters.</summary>
    public static bool IsSafeLabel(string? value) => value is not null && SafeLabel().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex SafeLabel();
}
