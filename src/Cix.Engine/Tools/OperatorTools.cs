using System.ComponentModel;
using Cix.Engine.Journal;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cix.Engine.Tools;

/// <summary>Tools for the owner to review what agents did. They need the operator key (<see cref="ToolServerOptions.OperatorKey"/>).</summary>
[McpServerToolType]
public sealed class OperatorTools(ToolCallContext context, IJournal journal)
{
    public const int MaxEntries = 200;

    [McpServerTool(Name = "query_journal", ReadOnly = true)]
    [Description("Operator only. Journal entries, newest first, filtered by UTC dates (yyyy-MM-dd), kind (tool-call, order, kill-switch), run ID or client order ID.")]
    public async Task<string> QueryJournal(
        [Description("First UTC day, yyyy-MM-dd.")] string? from = null,
        [Description("Last UTC day, yyyy-MM-dd.")] string? to = null,
        [Description("Entry kind, for example order.")] string? kind = null,
        [Description("Agent run ID.")] string? runId = null,
        [Description("Client order ID.")] string? clientOrderId = null,
        [Description("At most this many entries (1-200).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        context.RequireOperator();
        if (limit is < 1 or > MaxEntries)
            throw new McpException($"limit must be between 1 and {MaxEntries}.");
        var entries = await journal.QueryAsync(
            new JournalQuery(ToolJson.Date(from, "from"), ToolJson.Date(to, "to"), kind, runId, null, clientOrderId, limit),
            cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(entries);
    }

    [McpServerTool(Name = "get_daily_summary", ReadOnly = true)]
    [Description("Operator only. Counts of journal entries for one UTC day by kind, actor and tool, plus the strategy versions in use.")]
    public async Task<string> GetDailySummary(
        [Description("UTC day, yyyy-MM-dd.")] string date,
        CancellationToken cancellationToken = default)
    {
        context.RequireOperator();
        var day = ToolJson.Date(date, "date") ?? throw new McpException("date is required.");
        var entries = await journal.QueryAsync(new JournalQuery(day, day, Limit: FileJournal.MaxQueryLimit), cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(new
        {
            date = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            entries = entries.Count,
            truncated = entries.Count >= FileJournal.MaxQueryLimit,
            byKind = entries.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()),
            byActor = entries.GroupBy(e => e.Actor).ToDictionary(g => g.Key, g => g.Count()),
            byTool = entries.Where(e => e.Data?.ContainsKey("tool") == true).GroupBy(e => e.Data!["tool"]).ToDictionary(g => g.Key, g => g.Count()),
            specVersions = entries.Select(e => e.SpecVersion).OfType<string>().Distinct().Order(StringComparer.Ordinal),
        });
    }
}
