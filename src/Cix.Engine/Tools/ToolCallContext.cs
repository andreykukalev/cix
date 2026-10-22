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

    /// <summary>Where the MCP endpoint listens. Loopback only until the Azure deployment adds authentication.</summary>
    public string Url { get; set; } = "http://127.0.0.1:5077";

    /// <summary>
    /// Secret that unlocks operator tools, from user secrets (never in a file in the repo). Agents' MCP configuration
    /// doesn't carry it. When unset, operator tools are disabled.
    /// </summary>
    public string? OperatorKey { get; set; }

    public void Validate()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException("ToolServer:Url must be an absolute http URL.");
        if (!uri.IsLoopback)
            throw new InvalidOperationException("ToolServer:Url must be a loopback address (127.0.0.1 or localhost) until authentication exists.");
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
    TimeProvider time)
{
    public string Agent => Header(ToolServerOptions.AgentHeader) ?? "agent";

    public string? RunId => Header(ToolServerOptions.RunHeader);

    public StrategySpec? Spec => specs.Active;

    public bool IsOperator
    {
        get
        {
            var supplied = http.HttpContext?.Request.Headers[ToolServerOptions.OperatorKeyHeader].ToString();
            if (string.IsNullOrEmpty(options.OperatorKey) || string.IsNullOrEmpty(supplied))
                return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(options.OperatorKey));
        }
    }

    /// <summary>Throws unless the active spec lets agents call <paramref name="tool"/>. The operator may call any agent tool.</summary>
    public void RequireAllowed(string tool)
    {
        if (IsOperator)
            return;
        if (specs.Active is not { } spec)
            throw new McpException("No strategy spec is active, so agent tools are disabled.");
        if (!spec.Allows(tool))
            throw new McpException($"The active strategy spec ({spec.Label}) doesn't allow {tool}.");
    }

    public void RequireOperator()
    {
        if (!IsOperator)
            throw new McpException("This is an operator tool.");
    }

    public Task JournalAsync(string tool, string summary, IReadOnlyDictionary<string, string>? data, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>(data ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["tool"] = tool };
        return journal.AppendAsync(
            new JournalEntry(
                Guid.NewGuid().ToString("N"),
                time.GetUtcNow(),
                "tool-call",
                IsOperator ? "owner" : Agent,
                RunId,
                specs.Active?.Label,
                summary,
                fields),
            cancellationToken);
    }

    /// <summary>Identity headers are free text from the caller: keep them short and plain.</summary>
    private string? Header(string name)
    {
        var value = http.HttpContext?.Request.Headers[name].ToString();
        return string.IsNullOrEmpty(value) ? null : SafeLabel().IsMatch(value) ? value : "invalid";
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex SafeLabel();
}
