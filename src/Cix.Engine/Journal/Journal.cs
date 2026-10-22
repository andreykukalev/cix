using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cix.Engine.Journal;

/// <summary>
/// One record in the decision journal. Kinds used so far: <c>tool-call</c>, <c>order</c>, <c>kill-switch</c>.
/// <see cref="Data"/> holds kind-specific fields as strings, so the journal never depends on engine types.
/// </summary>
/// <param name="Actor">Who acted: an agent name, <c>owner</c>, or an engine component such as <c>safety-monitor</c>.</param>
/// <param name="RunId">The agent run this belongs to, when an agent acted.</param>
/// <param name="SpecVersion">The strategy spec in force, as <c>name@vN#hash</c>.</param>
public sealed record JournalEntry(
    string Id,
    DateTimeOffset At,
    string Kind,
    string Actor,
    string? RunId,
    string? SpecVersion,
    string Summary,
    IReadOnlyDictionary<string, string>? Data = null);

/// <summary>Filters for <see cref="IJournal.QueryAsync"/>; all optional. Dates are UTC days, inclusive.</summary>
public sealed record JournalQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    string? Kind = null,
    string? RunId = null,
    string? SpecVersion = null,
    string? ClientOrderId = null,
    int Limit = 100);

/// <summary>
/// The append-only decision journal (CLAUDE.md rule 4). There is deliberately no update or delete: corrections are new
/// entries. A Cosmos DB implementation replaces the file one for Azure (#6).
/// </summary>
public interface IJournal
{
    Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken);

    /// <summary>Newest first, at most <see cref="JournalQuery.Limit"/> entries.</summary>
    Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// One JSON line per entry in a file per UTC day (<c>2026-10-22.jsonl</c>). Files are opened for append only; a write is
/// flushed before <see cref="AppendAsync"/> returns. Lines that fail to parse (a torn last line after a crash) are skipped.
/// </summary>
public sealed class FileJournal : IJournal
{
    /// <summary>Most entries one query returns; tools ask for far fewer, summaries use it to count a whole day.</summary>
    public const int MaxQueryLimit = 20_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly string _directory;

    public FileJournal(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The journal directory needs an absolute path.", nameof(directory));
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public async Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        var path = PathFor(DateOnly.FromDateTime(entry.At.UtcDateTime));

        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length > 0)
            {
                // A crash can leave a torn last line; start on a fresh line so this entry stays readable.
                stream.Seek(-1, SeekOrigin.End);
                var last = stream.ReadByte();
                stream.Seek(0, SeekOrigin.End);
                if (last != '\n')
                    line = "\n" + line;
            }

            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, MaxQueryLimit);
        var days = Directory.EnumerateFiles(_directory, "*.jsonl")
            .Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", out var d) ? d : (DateOnly?)null)
            .OfType<DateOnly>()
            .Where(d => (query.From is null || d >= query.From) && (query.To is null || d <= query.To))
            .OrderDescending();

        var results = new List<JournalEntry>();
        foreach (var day in days)
        {
            var entries = new List<JournalEntry>();
            await using var stream = new FileStream(PathFor(day), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (TryParse(line) is { } entry && Matches(entry, query))
                    entries.Add(entry);
            }

            entries.Reverse(); // so entries with the same time come back newest-written first
            results.AddRange(entries.OrderByDescending(e => e.At));
            if (results.Count >= limit)
                break;
        }

        return results.Take(limit).ToList();
    }

    private string PathFor(DateOnly day) => Path.Combine(_directory, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");

    private static JournalEntry? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            return JsonSerializer.Deserialize<JournalEntry>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool Matches(JournalEntry e, JournalQuery q) =>
        (q.From is null || DateOnly.FromDateTime(e.At.UtcDateTime) >= q.From)
        && (q.To is null || DateOnly.FromDateTime(e.At.UtcDateTime) <= q.To)
        && MatchesFields(e, q);

    private static bool MatchesFields(JournalEntry e, JournalQuery q) =>
        (q.Kind is null || string.Equals(e.Kind, q.Kind, StringComparison.Ordinal))
        && (q.RunId is null || string.Equals(e.RunId, q.RunId, StringComparison.Ordinal))
        && (q.SpecVersion is null || (e.SpecVersion?.StartsWith(q.SpecVersion, StringComparison.Ordinal) ?? false))
        && (q.ClientOrderId is null || (e.Data?.TryGetValue("clientOrderId", out var id) == true && string.Equals(id, q.ClientOrderId, StringComparison.Ordinal)));
}

/// <summary>For tests.</summary>
public sealed class InMemoryJournal : IJournal
{
    private readonly Lock _gate = new();
    private readonly List<JournalEntry> _entries = [];

    public IReadOnlyList<JournalEntry> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToList();
        }
    }

    public Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
            _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            IReadOnlyList<JournalEntry> result = Enumerable.Reverse(_entries) // same-time entries newest-written first, like FileJournal
                .Where(e => FileJournal.Matches(e, query))
                .OrderByDescending(e => e.At)
                .Take(Math.Clamp(query.Limit, 1, FileJournal.MaxQueryLimit))
                .ToList();
            return Task.FromResult(result);
        }
    }
}
