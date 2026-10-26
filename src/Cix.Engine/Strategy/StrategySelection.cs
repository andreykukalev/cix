using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cix.Domain.Risk;
using Cix.Engine.AgentRuns;
using Cix.Engine.Journal;
using Cix.Engine.Manual;
using Cix.Engine.Persistence;
using Cix.Engine.Tools;

namespace Cix.Engine.Strategy;

/// <summary>
/// Which strategy version a trading mode should run, as the owner (the dashboard, a script or Data Explorer) last set it in
/// <c>strategy-selection-&lt;mode&gt;</c>. The engine acts on each selection once, identified by name, version and
/// <paramref name="RequestedAt"/>: to retry a refused one, save it again.
/// </summary>
public sealed record StrategySelection(string Name, int Version, string? RequestedBy = null, DateTimeOffset? RequestedAt = null, string? Note = null)
{
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Name}@v{Version}");
}

/// <summary>
/// Where strategy versions and selections live (the <c>state</c> container in Cosmos DB). Versions are created once and
/// never replaced by the engine; the selection and the schema are one document each.
/// </summary>
public interface IStrategyRepository
{
    Task<StrategySelection?> ReadSelectionAsync(TradingMode mode, CancellationToken cancellationToken);

    /// <summary>The version, with its hash set from its content; null when there is no such version.</summary>
    Task<StrategySpec?> ReadVersionAsync(string name, int version, CancellationToken cancellationToken);

    /// <summary>Adds a version unless one with that name and number exists. Returns whether it was added.</summary>
    Task<bool> TryAddVersionAsync(StrategySpec spec, CancellationToken cancellationToken);

    /// <summary>Keeps an immutable copy of the exact spec that runs, under its label, for the journal (see <see cref="CosmosSpecArchive"/>).</summary>
    Task ArchiveAsync(StrategySpec spec, CancellationToken cancellationToken);

    Task SaveSchemaAsync(StrategySchemaDocument schema, CancellationToken cancellationToken);

    /// <summary>Writes the selection for <paramref name="mode"/>: the owner's choice, taken from a dashboard request.</summary>
    Task SaveSelectionAsync(TradingMode mode, StrategySelection selection, CancellationToken cancellationToken);
}

/// <summary>For tests, and to show the shape the Cosmos DB documents hold.</summary>
public sealed class InMemoryStrategyRepository : IStrategyRepository
{
    private readonly Dictionary<string, StrategySpec> _versions = new(StringComparer.Ordinal);

    public Dictionary<TradingMode, StrategySelection> Selections { get; } = [];

    public Dictionary<string, StrategySpec> Archived { get; } = new(StringComparer.Ordinal);

    public StrategySchemaDocument? Schema { get; private set; }

    /// <summary>When set, every read throws it: a database outage.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, reading a version throws it: a document that doesn't deserialize, for example.</summary>
    public Exception? VersionFailure { get; set; }

    public IReadOnlyCollection<StrategySpec> Versions => _versions.Values;

    /// <summary>Stores a version as a document would hold it, replacing any (the engine itself never replaces one).</summary>
    public void Put(StrategySpec spec) => _versions[Key(spec.Name, spec.Version)] = spec with { Hash = string.Empty };

    public Task<StrategySelection?> ReadSelectionAsync(TradingMode mode, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult(Selections.GetValueOrDefault(mode));
    }

    public Task<StrategySpec?> ReadVersionAsync(string name, int version, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        if (VersionFailure is { } malformed)
            throw malformed;
        return Task.FromResult(_versions.TryGetValue(Key(name, version), out var spec) ? StrategyHash.WithHash(spec) : null);
    }

    public Task<bool> TryAddVersionAsync(StrategySpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ThrowIfFailing();
        return Task.FromResult(_versions.TryAdd(Key(spec.Name, spec.Version), spec with { Hash = string.Empty }));
    }

    public Task ArchiveAsync(StrategySpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Archived.TryAdd(spec.Label, spec);
        return Task.CompletedTask;
    }

    public Task SaveSchemaAsync(StrategySchemaDocument schema, CancellationToken cancellationToken)
    {
        Schema = schema;
        return Task.CompletedTask;
    }

    public Task SaveSelectionAsync(TradingMode mode, StrategySelection selection, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Selections[mode] = selection;
        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (Failure is { } failure)
            throw failure;
    }

    private static string Key(string name, int version) => string.Create(CultureInfo.InvariantCulture, $"{name}@v{version}");
}

/// <summary>
/// What the engine made of the latest selection, for the status record and the dashboard.
/// </summary>
/// <param name="State">
/// <c>config</c> (no selection saved: the configured spec runs), <c>active</c> (the selection runs), <c>pending</c> (waiting for
/// the trader run in progress to end), <c>refused</c> (it failed validation; the previous spec keeps running) or
/// <c>unreadable</c> (the database couldn't be read; the previous spec keeps running).
/// </param>
/// <param name="Requested">The selection's <c>name@vN</c>, or null without one.</param>
/// <param name="Problems">Why it was refused or isn't applied yet; empty otherwise.</param>
public sealed record StrategySelectionStatus(
    string State,
    string? Requested,
    string? RequestedBy,
    DateTimeOffset? RequestedAt,
    IReadOnlyList<string> Problems,
    DateTimeOffset? CheckedAt,
    DateTimeOffset? ActivatedAt);

/// <summary>
/// Switches the paper engine's strategy at run time (#105): every <see cref="Interval"/> it reads the selection, and when it
/// names another version, validates it (<see cref="StrategySchema.Validate"/>), journals the switch, and activates it for the
/// next scan and the next agent run. Never during a trader run: it waits for the run to end, so a run works under one spec.
/// A refused selection is journaled once and leaves the running spec as it was. Changes apply to new trades: open positions
/// keep the stop and target they were bought with; the trend and exit rules apply to every held coin from the next scan.
/// </summary>
public sealed partial class StrategySelectionService(
    RuntimeStrategySpecs specs,
    IStrategyRepository repository,
    RiskLimits limits,
    TradingContext trading,
    IJournal journal,
    TimeProvider time,
    ILogger<StrategySelectionService> logger,
    IEngineLease? lease = null,
    AgentRunScheduler? runs = null,
    IManualRequestStore? requests = null) : BackgroundService
{
    /// <summary>The <c>type</c> of a dashboard request to switch strategy, in the <c>requests</c> container.</summary>
    public const string RequestType = "strategy-selection";

    /// <summary>A request older than this when the engine first sees it expires instead of switching on a stale intent.</summary>
    public static readonly TimeSpan RequestMaxAge = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public const string Kind = "strategy";
    public const string Actor = "engine";

    /// <summary>The selection last acted on (activated, found unchanged, or refused): <c>name@vN|requestedAt</c>.</summary>
    private string? _handled;

    private StrategySelectionStatus _status = new("config", null, null, null, [], null, null);

    /// <summary>
    /// Hashes of the instructions in the reviewed spec files (the configured spec and those <see cref="SeedAsync"/> read):
    /// a version from the database must use one of them, so text that never went through a pull request can't reach the
    /// trader's prompt. Parameters may differ; the schema checks them.
    /// </summary>
    private readonly HashSet<string> _reviewedInstructions = specs.Active is { } configured ? [InstructionsHash(configured)] : [];

    /// <summary>The canonical hash of each spec file by <c>name@vN</c>: a database version with the same name must match it.</summary>
    private readonly Dictionary<string, string> _fileHashes = new(StringComparer.Ordinal);

    public StrategySelectionStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// At startup, before the engine scans or serves tools: seeds the database with the trading specs in
    /// <paramref name="directory"/> (each version once, never replacing one), writes the schema for the dashboard, and applies
    /// the saved selection, so a restart runs the selected strategy from the first scan.
    /// </summary>
    public async Task InitializeAsync(string directory, CancellationToken cancellationToken)
    {
        await SeedAsync(directory, cancellationToken).ConfigureAwait(false);
        await repository.SaveSchemaAsync(StrategySchema.Document(limits, trading.Mode, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds every runnable trading spec file in <paramref name="directory"/> (<c>name.vN.json</c>, paper status, valid against
    /// the schema) that the database doesn't have yet. Analyst specs and invalid files are skipped and logged.
    /// </summary>
    public async Task<int> SeedAsync(string directory, CancellationToken cancellationToken)
    {
        var folder = Path.IsPathFullyQualified(directory) ? directory : Path.Combine(AppContext.BaseDirectory, directory);
        if (!Directory.Exists(folder))
            return 0;
        var added = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            var match = SpecFileName().Match(Path.GetFileName(path));
            if (!match.Success)
                continue;
            StrategySpec spec;
            try
            {
                spec = FileStrategySpecStore.Load(path, match.Groups[1].Value, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), TradingMode.Paper);
            }
            catch (InvalidOperationException ex)
            {
                LogSeedSkipped(logger, Path.GetFileName(path), ex.Message);
                continue;
            }

            if (spec.AllowedTools.Any(AnalystTools.Names.Contains))
                continue; // the market analyst's spec isn't a trading strategy
            if (StrategySchema.Validate(spec, limits, TradingMode.Paper) is { Count: > 0 } problems)
            {
                LogSeedSkipped(logger, Path.GetFileName(path), string.Join(" ", problems));
                continue;
            }

            var key = string.Create(CultureInfo.InvariantCulture, $"{spec.Name}@v{spec.Version}");
            lock (_fileHashes)
            {
                _reviewedInstructions.Add(InstructionsHash(spec));
                _fileHashes[key] = StrategyHash.Of(spec);
            }

            if (await repository.TryAddVersionAsync(spec with { Hash = string.Empty }, cancellationToken).ConfigureAwait(false))
                added++;
            else
                await WarnIfDifferentAsync(spec, key, cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    private async Task WarnIfDifferentAsync(StrategySpec file, string key, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await repository.ReadVersionAsync(file.Name, file.Version, cancellationToken).ConfigureAwait(false);
            if (stored is not null && stored.Hash != StrategyHash.Of(file))
                LogSeedDiffers(logger, key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogReadFailed(logger, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Reads the selection and applies it if it is new. Returns true when the active spec changed. A database failure is
    /// logged and shown in the status; the running spec stays. Tests call this instead of running the loop.
    /// </summary>
    public async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (lease is { IsHeld: false })
            return false; // only the engine that trades acts on a selection
        var now = time.GetUtcNow();
        await TakeRequestsAsync(now, cancellationToken).ConfigureAwait(false);
        StrategySelection? selection;
        try
        {
            selection = await repository.ReadSelectionAsync(trading.Mode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogReadFailed(logger, ex.GetType().Name);
            SetStatus(Status with { State = "unreadable", Problems = ["The strategy selection couldn't be read from the database; the running strategy is unchanged."], CheckedAt = now });
            return false;
        }

        if (selection is null)
        {
            SetStatus(new StrategySelectionStatus("config", null, null, null, [], now, specs.ActivatedAt));
            return false;
        }

        var requested = StrategySchema.Clip(selection.Key);
        var requestedBy = Clean(selection.RequestedBy, 80);
        var token = string.Create(CultureInfo.InvariantCulture, $"{selection.Key}|{selection.RequestedAt:O}");
        if (token == _handled)
        {
            SetStatus(Status with { CheckedAt = now });
            return false;
        }

        if (runs?.TraderRunInProgress == true)
        {
            SetStatus(new StrategySelectionStatus("pending", requested, requestedBy, selection.RequestedAt,
                ["Waiting for the trader run in progress to end; the switch happens before the next run."], now, specs.ActivatedAt));
            return false;
        }

        StrategySpec? spec;
        var malformed = false;
        try
        {
            spec = selection.Version is >= 1 and <= 9999 && !string.IsNullOrEmpty(selection.Name)
                ? await repository.ReadVersionAsync(selection.Name, selection.Version, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            spec = null;
            malformed = true; // a document edited by hand into something that isn't a spec: refused, not retried as an outage
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogReadFailed(logger, ex.GetType().Name);
            SetStatus(Status with { State = "unreadable", Problems = ["The selected strategy version couldn't be read from the database; the running strategy is unchanged."], CheckedAt = now });
            return false;
        }

        var current = specs.Active;
        IReadOnlyList<string> problems = malformed
            ? [$"The document for {requested} isn't a valid strategy version."]
            : spec is null
            ? [$"There is no strategy version {requested} in the database."]
            : spec.Name != selection.Name || spec.Version != selection.Version
            ? [$"The document for {requested} declares {StrategySchema.Clip(spec.Name)}@v{spec.Version}."]
            : Reviewed(spec) is { } unreviewed ? [unreviewed]
            : StrategySchema.Validate(spec, limits, trading.Mode);
        if (problems.Count > 0)
        {
            if (!await AppendAsync("refused", $"Strategy {requested} refused: {problems[0]}", current, null, selection, requestedBy, problems).ConfigureAwait(false))
                return false; // not journaled: try again at the next poll
            _handled = token;
            LogRefused(logger, requested, string.Join(" ", problems));
            SetStatus(new StrategySelectionStatus("refused", requested, requestedBy, selection.RequestedAt, problems, now, specs.ActivatedAt));
            return false;
        }

        var selected = spec!;
        if (current is not null && current.Name == selected.Name && current.Version == selected.Version && StrategyHash.Of(current) == selected.Hash)
        {
            _handled = token; // already running, from the configured file or an earlier activation
            SetStatus(new StrategySelectionStatus("active", requested, requestedBy, selection.RequestedAt, [], now, specs.ActivatedAt));
            return false;
        }

        // A run may have been claimed while the version was read: check again, just before the switch.
        if (runs?.TraderRunInProgress == true)
        {
            SetStatus(new StrategySelectionStatus("pending", requested, requestedBy, selection.RequestedAt,
                ["Waiting for the trader run in progress to end; the switch happens before the next run."], now, specs.ActivatedAt));
            return false;
        }

        // Journal first: a switch the journal doesn't record doesn't happen.
        if (!await AppendAsync("activated", $"Strategy {current?.Label ?? "none"} -> {selected.Label}", current, selected, selection, requestedBy, []).ConfigureAwait(false))
            return false;
        specs.Activate(selected, now);
        _handled = token;
        LogActivated(logger, current?.Label ?? "none", selected.Label);
        SetStatus(new StrategySelectionStatus("active", requested, requestedBy, selection.RequestedAt, [], now, now));
        try
        {
            await repository.ArchiveAsync(selected, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogArchiveFailed(logger, selected.Label, ex.GetType().Name); // the version document still holds it; archived again at the next switch
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Turns the owner's dashboard requests (<see cref="RequestType"/> documents in the <c>requests</c> container, which the
    /// dashboard can only create) into the selection: the newest valid one is saved as <c>strategy-selection-&lt;mode&gt;</c>
    /// and then handled like any selection. Earlier ones are superseded; malformed or stale ones are refused. Each request's
    /// document records what became of it. Saving the same request twice is harmless: a selection is acted on once.
    /// </summary>
    private async Task TakeRequestsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (requests is null || requests.State != "ok")
            return;
        var mode = trading.Mode.ToString().ToLowerInvariant();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        IReadOnlyList<StoredRequest> found;
        try
        {
            found = await requests.ListNewAsync(today.AddDays(-1), today.AddDays(1), mode, cancellationToken, RequestType).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRequestsFailed(logger, ex.GetType().Name);
            return;
        }

        if (found.Count == 0)
            return;
        var parsed = found.Select(r => (Stored: r, Parsed: ParseRequest(r.Document, now))).OrderBy(p => p.Parsed.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.Stored.Id, StringComparer.Ordinal).ToList();
        var newest = parsed.LastOrDefault(p => p.Parsed.Selection is not null).Stored;
        foreach (var (stored, request) in parsed)
        {
            string status, detail;
            if (request.Selection is null)
            {
                (status, detail) = (request.Expired ? ManualRequestStatus.Expired : ManualRequestStatus.Invalid, request.Problem!);
            }
            else if (!ReferenceEquals(stored, newest))
            {
                (status, detail) = ("superseded", "A newer strategy request replaced this one.");
            }
            else
            {
                try
                {
                    await repository.SaveSelectionAsync(trading.Mode, request.Selection, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    LogRequestsFailed(logger, ex.GetType().Name);
                    continue; // still new: taken at the next poll
                }

                (status, detail) = ("accepted", $"Selected {request.Selection.Key}. The engine validates it and switches before the next trader run; the journal (kind strategy) and the engine status say what happened.");
            }

            try
            {
                // A document refused for its shape isn't copied back: only its id, day and type, with the outcome.
                var basis = ManualRequestDocument.ShapeProblem(stored.Document) is null
                    ? stored.Document
                    : new System.Text.Json.Nodes.JsonObject { ["id"] = stored.Id, ["day"] = stored.Day, ["type"] = RequestType };
                await requests.TryReplaceAsync(stored, ManualRequestDocument.WithStatus(basis, status, now, Actor, detail), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogRequestsFailed(logger, ex.GetType().Name); // read again next time; a saved selection is acted on once
            }
        }
    }

    internal sealed record ParsedRequest(StrategySelection? Selection, DateTimeOffset? CreatedAt, string? Problem, bool Expired = false);

    /// <summary>A dashboard request as a selection, or why it can't be one. The engine trusts nothing in the document.</summary>
    internal static ParsedRequest ParseRequest(System.Text.Json.Nodes.JsonObject document, DateTimeOffset now)
    {
        if (ManualRequestDocument.ShapeProblem(document) is { } shape)
            return new ParsedRequest(null, null, shape);
        var createdAt = ManualRequestDocument.Field(document, "createdAt") is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
        if (createdAt is null)
            return new ParsedRequest(null, null, "The request has no valid createdAt.");
        if (now - createdAt > RequestMaxAge)
            return new ParsedRequest(null, createdAt, "The request was more than 5 minutes old when the engine saw it; send it again.", Expired: true);
        if (createdAt - now > TimeSpan.FromMinutes(1))
            return new ParsedRequest(null, createdAt, "The request's time is in the future.");
        var name = ManualRequestDocument.Field(document, "name");
        if (name is null || !StrategyName().IsMatch(name))
            return new ParsedRequest(null, createdAt, "name must be a strategy name (lowercase letters, digits and dashes).");
        if (document["version"] is not System.Text.Json.Nodes.JsonValue v || !v.TryGetValue<int>(out var version) || version is < 1 or > 9999)
            return new ParsedRequest(null, createdAt, "version must be a whole number from 1 to 9999.");
        return new ParsedRequest(new StrategySelection(name, version, "owner", createdAt, Clean(ManualRequestDocument.Field(document, "note"), 280)), createdAt, null);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial System.Text.RegularExpressions.Regex StrategyName();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Strategy requests could not be read or updated ({Error}); retried at the next poll")]
    private static partial void LogRequestsFailed(ILogger logger, string error);

    private async Task<bool> AppendAsync(
        string action, string summary, StrategySpec? from, StrategySpec? to, StrategySelection selection, string? requestedBy, IReadOnlyList<string> problems)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = action,
            ["mode"] = trading.Mode.ToString().ToLowerInvariant(),
            ["requested"] = StrategySchema.Clip(selection.Key),
            ["from"] = from?.Label ?? string.Empty,
            ["requestedBy"] = requestedBy ?? string.Empty,
            ["requestedAt"] = selection.RequestedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            ["note"] = Clean(selection.Note, 280) ?? string.Empty,
        };
        if (to is not null)
        {
            data["to"] = to.Label;
            data["applies"] = "New trades from the next scan and agent run. Open positions keep their stop and target; the trend and exit rules apply to them from the next scan.";
        }

        if (problems.Count > 0)
            data["problems"] = string.Join(" ", problems);
        var now = time.GetUtcNow();
        try
        {
            await journal.AppendAsync(
                new JournalEntry(JournalIds.New(now, Kind), now, Kind, Actor, null, (to ?? from)?.Label, summary, data), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogJournalFailed(logger, ex.Message);
            SetStatus(Status with { State = "pending", Problems = ["The journal couldn't be written; the switch is retried shortly."], CheckedAt = now });
            return false;
        }
    }

    /// <summary>Why a database version may not run although its values are valid; null when it may.</summary>
    private string? Reviewed(StrategySpec spec)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{spec.Name}@v{spec.Version}");
        lock (_fileHashes)
        {
            if (_fileHashes.TryGetValue(key, out var file) && file != spec.Hash)
                return $"The database copy of {StrategySchema.Clip(key)} differs from the reviewed file strategies/{StrategySchema.Clip(spec.Name)}.v{spec.Version}.json; give a changed strategy a new version.";
            if (!_reviewedInstructions.Contains(InstructionsHash(spec)))
                return $"The instructions of {StrategySchema.Clip(key)} match no reviewed spec file in strategies/; instructions change only through a pull request.";
        }

        return null;
    }

    private static string InstructionsHash(StrategySpec spec) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(spec.Instructions ?? string.Empty)));

    private void SetStatus(StrategySelectionStatus status) => Volatile.Write(ref _status, status);

    /// <summary>Free text from the selection document: one plain line, no control or format characters, bounded.</summary>
    internal static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        line = new string(line.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());
        return line.Length <= max ? line : line[..max];
    }

    [System.Text.RegularExpressions.GeneratedRegex("^([a-z0-9-]{1,40})\\.v([0-9]{1,4})\\.json$")]
    private static partial System.Text.RegularExpressions.Regex SpecFileName();

    [LoggerMessage(Level = LogLevel.Information, Message = "Strategy switched: {From} -> {To}; new trades follow it from the next scan and run")]
    private static partial void LogActivated(ILogger logger, string from, string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Strategy selection {Requested} refused: {Problems}")]
    private static partial void LogRefused(ILogger logger, string requested, string problems);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Strategy selection could not be read ({Error}); the running strategy is unchanged")]
    private static partial void LogReadFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Strategy spec {Label} could not be archived ({Error})")]
    private static partial void LogArchiveFailed(ILogger logger, string label, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed for a strategy switch: {Detail}; retried at the next poll")]
    private static partial void LogJournalFailed(ILogger logger, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Strategy {Version} in the database differs from its file; selecting it is refused")]
    private static partial void LogSeedDiffers(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Strategy file {File} not added to the database: {Problem}")]
    private static partial void LogSeedSkipped(ILogger logger, string file, string problem);
}

/// <summary>
/// The Cosmos DB documents behind <see cref="IStrategyRepository"/>, in the <c>state</c> container:
/// <c>strategy-&lt;name&gt;-v&lt;N&gt;</c> (a version, created once), <c>strategy-selection-&lt;mode&gt;</c> (the owner's choice) and
/// <c>strategy-schema-&lt;mode&gt;</c> (parameters and limits for the dashboard). Archived copies of what ran stay in
/// <see cref="CosmosSpecArchive"/>.
/// </summary>
public sealed class CosmosStrategyRepository(CosmosDb db, CosmosSpecArchive archive) : IStrategyRepository
{
    public const string VersionType = "strategy-version";
    public const string SelectionType = "strategy-selection";
    public const string SchemaType = "strategy-schema";

    public const string VersionAbout =
        "A strategy version: instructions, parameters and allowed tools. Created once; a change is a new version with a higher number. The engine activates the version strategy-selection-<mode> names.";
    public const string SelectionAbout =
        "Which strategy version this mode should run: the owner's choice, from the dashboard (through a request) or written by hand. The engine validates it and journals each switch.";
    public const string SchemaAbout =
        "What each strategy parameter means, its valid range, and the mandatory safety limits, written by the engine at startup for the dashboard.";

    public static string VersionId(string name, int version) => string.Create(CultureInfo.InvariantCulture, $"strategy-{name}-v{version}");

    public static string SelectionId(TradingMode mode) => $"strategy-selection-{mode.ToString().ToLowerInvariant()}";

    public static string SchemaId(TradingMode mode) => $"strategy-schema-{mode.ToString().ToLowerInvariant()}";

    public Task<StrategySelection?> ReadSelectionAsync(TradingMode mode, CancellationToken cancellationToken) =>
        CosmosDb.ReadStateAsync<StrategySelection>(db.State, SelectionId(mode), cancellationToken);

    public async Task<StrategySpec?> ReadVersionAsync(string name, int version, CancellationToken cancellationToken)
    {
        var spec = await CosmosDb.ReadStateAsync<StrategySpec>(db.State, VersionId(name, version), cancellationToken).ConfigureAwait(false);
        return spec is null ? null : StrategyHash.WithHash(spec);
    }

    public async Task<bool> TryAddVersionAsync(StrategySpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var id = VersionId(spec.Name, spec.Version);
        try
        {
            await db.State.CreateItemAsync(
                new CosmosDb.StateDocument<StrategySpec>(id, spec, VersionType, VersionAbout, DateTimeOffset.UtcNow),
                new Microsoft.Azure.Cosmos.PartitionKey(id), cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return false; // versions are never replaced
        }
    }

    public Task ArchiveAsync(StrategySpec spec, CancellationToken cancellationToken) => archive.ArchiveAsync(spec, cancellationToken);

    public Task SaveSchemaAsync(StrategySchemaDocument schema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var mode = Enum.Parse<TradingMode>(schema.Mode, ignoreCase: true);
        return CosmosDb.WriteStateAsync(db.State, SchemaId(mode), schema, SchemaType, SchemaAbout, cancellationToken);
    }

    public Task SaveSelectionAsync(TradingMode mode, StrategySelection selection, CancellationToken cancellationToken) =>
        CosmosDb.WriteStateAsync(db.State, SelectionId(mode), selection, SelectionType, SelectionAbout, cancellationToken);
}
