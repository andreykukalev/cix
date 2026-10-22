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
    /// <summary>Names that only the owner or the engine itself appear as in the journal; agents can't claim them.</summary>
    public static readonly IReadOnlySet<string> ReservedActors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "owner", "engine", "execution", "reconciler", "safety-monitor", "invalid",
    };

    public string Agent
    {
        get
        {
            var name = Header(ToolServerOptions.AgentHeader);
            return name is null ? "agent" : ReservedActors.Contains(name) ? "invalid" : name;
        }
    }

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

    /// <summary>
    /// Throws unless the active spec lets agents call <paramref name="tool"/>; a refusal is journaled first. The operator
    /// may call any agent tool.
    /// </summary>
    public async Task RequireAllowedAsync(string tool, CancellationToken cancellationToken)
    {
        if (IsOperator)
            return;
        var reason = specs.Active is not { } spec
            ? "No strategy spec is active, so agent tools are disabled."
            : spec.Allows(tool) ? null : $"The active strategy spec ({spec.Label}) doesn't allow {tool}.";
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

    private Task DenyAsync(string tool, string reason, CancellationToken cancellationToken) =>
        journal.AppendAsync(
            new JournalEntry(Guid.NewGuid().ToString("N"), time.GetUtcNow(), "tool-denied", Agent, RunId, specs.Active?.Label, reason,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["tool"] = tool }),
            cancellationToken);

    public Task JournalAsync(string tool, string summary, IReadOnlyDictionary<string, string>? data, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>(data ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["tool"] = tool };
        return RecordAsync("tool-call", summary, fields, cancellationToken);
    }

    /// <summary>Appends an entry of any kind as the current caller, with the active spec.</summary>
    public Task RecordAsync(string kind, string summary, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken) =>
        journal.AppendAsync(
            new JournalEntry(Guid.NewGuid().ToString("N"), time.GetUtcNow(), kind, IsOperator ? "owner" : Agent, RunId, specs.Active?.Label, summary, data),
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
