using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cix.Engine.MarketData;

namespace Cix.Engine.Manual;

/// <summary>Bound from the "ManualRequests" section (ADR-0011).</summary>
public sealed class ManualRequestOptions
{
    public const string SectionName = "ManualRequests";

    /// <summary>When false, requests are left untouched in the database and nothing is executed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether a Live engine acts on requests. Off by default and owner-only, like the live switch: until requests are
    /// authenticated beyond write access to the database (ADR-0011), only paper trading takes them.
    /// </summary>
    public bool AllowLive { get; set; }

    /// <summary>How often the engine looks for new requests.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A request older than this when the engine first sees it (the engine was down) expires instead of trading on a stale intent.</summary>
    public TimeSpan MaxAgeAtPickup { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>An agent-assisted request the trader hasn't answered within this expires.</summary>
    public TimeSpan AgentAnswerTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Requests the engine accepts per UTC day (any outcome after validation); more are refused. Bounds a misbehaving writer.</summary>
    public int MaxPerDay { get; set; } = 20;

    public void Validate()
    {
        if (PollInterval < TimeSpan.FromSeconds(1) || PollInterval > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("ManualRequests:PollInterval must be between 1 second and 1 minute.");
        if (MaxAgeAtPickup < TimeSpan.FromMinutes(1) || MaxAgeAtPickup > TimeSpan.FromHours(1))
            throw new InvalidOperationException("ManualRequests:MaxAgeAtPickup must be between 1 minute and 1 hour.");
        if (AgentAnswerTimeout < TimeSpan.FromMinutes(5) || AgentAnswerTimeout > TimeSpan.FromHours(2))
            throw new InvalidOperationException("ManualRequests:AgentAnswerTimeout must be between 5 minutes and 2 hours.");
        if (MaxPerDay is < 1 or > 100)
            throw new InvalidOperationException("ManualRequests:MaxPerDay must be between 1 and 100.");
    }
}

/// <summary>Where a request stands. The requester writes <see cref="New"/>; the engine writes everything after.</summary>
public static class ManualRequestStatus
{
    public const string New = "new";

    /// <summary>Agent-assisted: waiting for the trader's answer.</summary>
    public const string AwaitingAgent = "awaiting-agent";

    /// <summary>Claimed for execution, with its client order ID, just before the order is sent.</summary>
    public const string Executing = "executing";

    /// <summary>The exchange accepted the order; it may still be working.</summary>
    public const string Placed = "placed";

    /// <summary>Sent, but the outcome isn't known yet; the engine reconciles it.</summary>
    public const string Unconfirmed = "unconfirmed";

    public const string Filled = "filled";

    /// <summary>The order ended without a fill (cancelled or expired), or was never sent.</summary>
    public const string Closed = "closed";

    /// <summary>The trader decided not to act on it.</summary>
    public const string Declined = "declined";

    /// <summary>The engine or the risk gate refused it (see the outcome's rule).</summary>
    public const string Rejected = "rejected";

    public const string Failed = "failed";
    public const string Invalid = "invalid";
    public const string Duplicate = "duplicate";
    public const string Expired = "expired";

    /// <summary>Statuses the engine still acts on: the rest are final.</summary>
    public static readonly IReadOnlySet<string> Active = new HashSet<string>(StringComparer.Ordinal)
    {
        New, AwaitingAgent, Executing, Placed, Unconfirmed,
    };

    /// <summary>Requests that block another request for the same symbol and side.</summary>
    public static readonly IReadOnlySet<string> InFlight = new HashSet<string>(StringComparer.Ordinal)
    {
        AwaitingAgent, Executing, Placed, Unconfirmed,
    };
}

/// <summary>
/// An owner's request, as read from its document. Sells: <see cref="Portion"/> of the sellable position (0 to 1) when the order
/// is placed, at the bid, direct or agent-assisted. Buys: <see cref="QuoteAmount"/> to spend, at the ask, agent-assisted only.
/// </summary>
public sealed record ManualRequest(
    string Id,
    string Day,
    string Side,
    string Mode,
    string Symbol,
    decimal? Portion,
    decimal? QuoteAmount,
    string? Note,
    string RequestedBy,
    DateTimeOffset CreatedAt)
{
    public const string Buy = "buy";
    public const string Sell = "sell";
    public const string Direct = "direct";
    public const string Agent = "agent";

    public const int MaxNote = 280;
    public const decimal MaxAmount = 1_000_000m;

    public bool IsSell => Side == Sell;

    public string Describe()
    {
        var amount = IsSell
            ? string.Create(CultureInfo.InvariantCulture, $"{Portion:0.####} of the position")
            : string.Create(CultureInfo.InvariantCulture, $"{QuoteAmount} quote");
        return $"{(Mode == Direct ? "direct" : "agent-assisted")} {Side} {Symbol}, {amount}";
    }
}

/// <summary>
/// Reads and writes request documents. The document is untrusted input (written through the dashboard): every field is
/// checked, numbers are accepted as JSON numbers or strings, and text is cleaned before it reaches the journal or an agent.
/// The engine's own fields are written as strings, like every number in the database (CLAUDE.md rule 7).
/// </summary>
public static partial class ManualRequestDocument
{
    public const string Type = "manual-request";

    public static string? Id(JsonObject document) => Text(document, "id");

    public static string? Day(JsonObject document) => Text(document, "day");

    /// <summary>The document's status; a missing one counts as <see cref="ManualRequestStatus.New"/>.</summary>
    public static string Status(JsonObject document) => Text(document, "status") ?? ManualRequestStatus.New;

    public static string? Field(JsonObject document, string name) => Text(document, name);

    /// <summary>The request, or why it can't be accepted. <paramref name="allowed"/> is the risk gate's symbol allowlist.</summary>
    public static (ManualRequest? Request, string? Problem) Parse(JsonObject document, IReadOnlySet<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(allowed);

        var id = Text(document, "id");
        var day = Text(document, "day");
        if (id is null || !SafeId().IsMatch(id))
            return (null, "id must be 8 to 64 letters, digits or dashes.");
        if (day is null || !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayValue))
            return (null, "day must be a UTC date like 2026-10-26.");

        var side = Text(document, "side")?.ToLowerInvariant();
        var mode = Text(document, "mode")?.ToLowerInvariant();
        if (side is not (ManualRequest.Buy or ManualRequest.Sell))
            return (null, "side must be buy or sell.");
        if (mode is not (ManualRequest.Direct or ManualRequest.Agent))
            return (null, "mode must be direct or agent.");
        if (side == ManualRequest.Buy && mode == ManualRequest.Direct)
            return (null, "Buys are agent-assisted only: use mode agent.");

        var rawSymbol = Text(document, "symbol");
        if (rawSymbol is null || rawSymbol.Length > 20)
            return (null, "symbol is required, for example BTC-EUR.");
        var symbol = MarketDataPoller.NormaliseSymbol(rawSymbol);
        if (!allowed.Contains(symbol))
            return (null, $"{symbol} is not in the risk allowlist.");

        if (document.ContainsKey("limitPrice"))
            return (null, "limitPrice isn't supported: sells go at the bid and buys at the ask, so a resting order can't hold a position past its stop.");
        if (!Number(document, "portion", out var portion) || !Number(document, "quoteAmount", out var quoteAmount))
            return (null, "portion and quoteAmount must be numbers.");
        if (side == ManualRequest.Sell)
        {
            if (portion is not { } p || p <= 0m || p > 1m)
                return (null, "A sell needs portion: the share of the position to sell, more than 0 and at most 1.");
            if (quoteAmount is not null)
                return (null, "quoteAmount is for buys; a sell takes portion.");
        }
        else
        {
            if (quoteAmount is not { } q || q <= 0m || q > ManualRequest.MaxAmount)
                return (null, "A buy needs quoteAmount: the amount of quote currency to spend, more than 0.");
            if (portion is not null)
                return (null, "A buy takes quoteAmount only.");
        }

        var createdText = Text(document, "createdAt");
        if (createdText is null || !DateTimeOffset.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAt))
            return (null, "createdAt must be a timestamp like 2026-10-26T10:00:00Z.");
        if (DateOnly.FromDateTime(createdAt.UtcDateTime) != dayValue)
            return (null, "day must be the UTC date of createdAt.");

        var requestedBy = Text(document, "requestedBy") ?? "owner"; // a role label, never an email address
        if (!SafeLabel().IsMatch(requestedBy))
            requestedBy = "invalid";

        return (new ManualRequest(id, day, side, mode, symbol, portion, quoteAmount, Clean(Text(document, "note"), ManualRequest.MaxNote),
            requestedBy, createdAt.ToUniversalTime()), null);
    }

    /// <summary>Fields only the engine writes. A document that arrives with any of them wasn't written by the engine: refused.</summary>
    public static readonly IReadOnlyList<string> EngineFields = ["outcome", "events", "updatedAt"];

    public const int MaxDocumentChars = 16_384;
    public const int MaxDocumentDepth = 8;

    /// <summary>Why a document is refused before it is read field by field, or null: too big, too deeply nested, or carrying engine fields.</summary>
    public static string? ShapeProblem(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Depth(document) > MaxDocumentDepth || document.ToJsonString().Length > MaxDocumentChars)
            return "The request document is too large or too deeply nested.";
        return EngineFields.Any(document.ContainsKey) || (Status(document) != ManualRequestStatus.New)
            ? "Only the engine writes status, outcome, events and updatedAt; a new request has status new and none of the others."
            : null;
    }

    /// <summary>The trading mode a request is for: its <c>tradingMode</c>, <c>paper</c> when absent.</summary>
    public static string ModeOf(JsonObject document) =>
        Text(document ?? throw new ArgumentNullException(nameof(document)), "tradingMode")?.ToLowerInvariant() ?? "paper";

    /// <summary>The id if it is safe to write to the journal and logs, otherwise <c>invalid-id</c>.</summary>
    public static string SafeIdOrInvalid(string? id) => id is not null && SafeId().IsMatch(id) ? id : "invalid-id";

    private static int Depth(JsonNode? node) => node switch
    {
        JsonObject o => 1 + (o.Count == 0 ? 0 : o.Max(p => Depth(p.Value))),
        JsonArray a => 1 + (a.Count == 0 ? 0 : a.Max(Depth)),
        _ => 0,
    };

    /// <summary>A copy of <paramref name="document"/> with a new status, outcome fields and one more event in its history.</summary>
    public static JsonObject WithStatus(
        JsonObject document, string status, DateTimeOffset at, string by, string? detail, IReadOnlyDictionary<string, string?>? outcome = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var next = (JsonObject)document.DeepClone();
        var stamp = at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        next["status"] = status;
        next["updatedAt"] = stamp;
        if (next["type"] is null)
            next["type"] = Type;

        if (outcome is not null)
        {
            if (next["outcome"] is not JsonObject target)
            {
                target = new JsonObject();
                next["outcome"] = target;
            }

            foreach (var (key, value) in outcome)
                target[key] = value;
        }

        if (next["events"] is not JsonArray events)
        {
            events = new JsonArray();
            next["events"] = events;
        }

        var item = new JsonObject { ["at"] = stamp, ["status"] = status, ["by"] = by };
        if (!string.IsNullOrEmpty(detail))
            item["detail"] = Clean(detail, 1_000);
        events.Add(item);
        return next;
    }

    /// <summary>A value from the engine's <c>outcome</c> object.</summary>
    public static string? Outcome(JsonObject document, string name) =>
        document["outcome"] is JsonObject outcome ? Text(outcome, name) : null;

    /// <summary>When the request was last set to <paramref name="status"/>, from its history.</summary>
    public static DateTimeOffset? LastAt(JsonObject document, string status)
    {
        if (document["events"] is not JsonArray events)
            return null;
        DateTimeOffset? last = null;
        foreach (var e in events.OfType<JsonObject>())
        {
            if (Text(e, "status") == status && DateTimeOffset.TryParse(Text(e, "at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                last = at;
        }

        return last;
    }

    /// <summary>One line, no control or format characters (they could disguise what the owner or an agent reads), bounded.</summary>
    public static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var clean = new string(single.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());
        if (clean.Length > max)
            clean = clean[..(char.IsHighSurrogate(clean[max - 1]) ? max - 1 : max)];
        return clean.Length == 0 ? null : clean;
    }

    private static string? Text(JsonObject document, string name)
    {
        if (!document.TryGetPropertyValue(name, out var node) || node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            return null;
        var text = value.GetValue<string>().Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>False if the field is there but isn't a number (or a string holding one); a missing or null field is a null number.</summary>
    private static bool Number(JsonObject document, string name, out decimal? number)
    {
        number = null;
        if (!document.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        if (node is not JsonValue value)
            return false;
        switch (value.GetValueKind())
        {
            case JsonValueKind.Number when value.TryGetValue<decimal>(out var d):
                number = d;
                return true;
            case JsonValueKind.Number:
                // Values parsed from JSON text hold a JsonElement.
                if (value.TryGetValue<JsonElement>(out var element) && element.TryGetDecimal(out var parsed))
                {
                    number = parsed;
                    return true;
                }

                return false;
            case JsonValueKind.String:
                if (decimal.TryParse(value.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var fromText))
                {
                    number = fromText;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeId();

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex SafeLabel();
}
