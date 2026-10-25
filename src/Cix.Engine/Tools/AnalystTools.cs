using System.ComponentModel;
using Cix.Domain.Scanning;
using Cix.Engine.Analysis;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Scanning;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cix.Engine.Tools;

/// <summary>One important news item or market event, recorded with the market view for the owner's dashboard. Never shown to the trader.</summary>
public sealed record MarketHighlight(
    [property: Description("A short headline in your own words, 10 to 160 characters.")] string Title,
    [property: Description("high (could move the whole market or a coin sharply within hours) or medium.")] string Impact,
    [property: Description("risk (could hurt positions), opportunity (could favour entries) or watch (worth following).")] string Direction,
    [property: Description("The https link this comes from.")] string Source,
    [property: Description("Affected symbols from the brief's `symbols`, if any.")] string[]? Symbols = null,
    [property: Description("Why it matters, in one sentence of at most 300 characters.")] string? Detail = null);

/// <summary>
/// Tools for the market analyst (#25). They are gated by the analyst's spec, not the trading spec. The analyst can read the
/// market and record a view, which can only make the scanner more cautious; it has no order tools.
/// </summary>
[McpServerToolType]
public sealed class AnalystTools(
    ToolCallContext context,
    IAnalystSpecStore analystSpec,
    MarketUniverse universe,
    FeatureService features,
    MarketScanService scanner,
    MarketViewStore views,
    TimeProvider time)
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal) { "get_analyst_brief", "record_market_view" };

    public const int MaxValidHours = 12;
    public const int MaxSources = 10;
    public const int MaxHighlights = 5;

    private static readonly System.Text.Json.JsonSerializerOptions HighlightJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Removes control and format characters (bidi overrides, zero-width): the text comes from the web and the owner reads it.</summary>
    public static string Clean(string? text) =>
        new string((text ?? string.Empty)
            .Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();

    private static bool IsHttpsLink(string s) =>
        s.Length <= 300 && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Checks the highlights and returns them cleaned, as stored in the journal (camelCase JSON). Each needs a source, and
    /// symbols must be tradable; invalid input is refused without echoing it.
    /// </summary>
    public static IReadOnlyList<MarketHighlight> CleanHighlights(IReadOnlyList<MarketHighlight>? highlights, MarketUniverse universe)
    {
        ArgumentNullException.ThrowIfNull(universe);
        if (highlights is null)
            return [];
        if (highlights.Count > MaxHighlights)
            throw new McpException($"highlights: at most {MaxHighlights}, the most important only.");
        var cleaned = new List<MarketHighlight>();
        foreach (var h in highlights)
        {
            if (h is null)
                throw new McpException("highlights: an item is empty.");
            var title = Clean(h.Title);
            var detail = Clean(h.Detail);
            var source = (h.Source ?? string.Empty).Trim();
            var impact = (h.Impact ?? string.Empty).Trim().ToLowerInvariant();
            var direction = (h.Direction ?? string.Empty).Trim().ToLowerInvariant();
            if (title.Length is < 10 or > 160 || detail.Length > 300)
                throw new McpException("highlights: title must be 10 to 160 characters and detail at most 300.");
            if (impact is not ("high" or "medium") || direction is not ("risk" or "opportunity" or "watch"))
                throw new McpException("highlights: impact must be high or medium, and direction risk, opportunity or watch.");
            if (!IsHttpsLink(source))
                throw new McpException("highlights: each needs an https source link of at most 300 characters.");
            var symbols = new List<string>();
            foreach (var raw in h.Symbols ?? [])
            {
                var symbol = MarketDataPoller.NormaliseSymbol(raw ?? string.Empty);
                if (!universe.IsTradable(symbol))
                    throw new McpException($"highlights: symbols may hold only symbols from the brief: {string.Join(", ", universe.Tradable)}.");
                if (!symbols.Contains(symbol, StringComparer.Ordinal))
                    symbols.Add(symbol);
            }

            cleaned.Add(new MarketHighlight(title, impact, direction, source, symbols.Count == 0 ? null : symbols.ToArray(), detail.Length == 0 ? null : detail));
        }

        return cleaned;
    }

    [McpServerTool(Name = "get_analyst_brief", ReadOnly = true)]
    [Description("Your instructions and parameters, the current time, the current market view, compact features for every universe symbol, and the latest scan's ranking and exclusions. Read it first.")]
    public async Task<string> GetAnalystBrief(CancellationToken cancellationToken)
    {
        await context.RequireAllowedAsync("get_analyst_brief", cancellationToken).ConfigureAwait(false);
        var spec = analystSpec.Active!;
        var now = time.GetUtcNow();
        var view = views.Current;
        var scan = scanner.Latest;
        var result = new
        {
            version = spec.Label,
            spec.Instructions,
            spec.Parameters,
            now,
            currentView = view is null ? null : new
            {
                regime = MarketViewStore.RegimeName(view.Regime),
                view.RecordedAt,
                view.ValidUntil,
                active = view.IsActive(now),
                avoid = view.Flags.Select(f => f.Symbol),
            },
            symbols = universe.Tradable,
            market = universe.Tradable.Select(features.GetFeatures),
            scan = scan is null ? null : new
            {
                scan.AsOf,
                ranked = scan.Ranked.Select(r => new { r.Symbol, r.Rank, r.Score, r.Trend, r.Return24h, r.AtrFraction }), // no holdings
                excluded = scan.Excluded.Select(e => new { e.Symbol, e.Reason }),
                entries = scan.Entries.Count,
                exits = scan.Exits.Count,
            },
        };
        await context.JournalAsync("get_analyst_brief", spec.Label, null, cancellationToken).ConfigureAwait(false);
        return ToolJson.Of(result);
    }

    [McpServerTool(Name = "record_market_view", Destructive = false, Idempotent = false)]
    [Description(
        "Records your market view. regime: risk-on, neutral or risk-off. Risk-off stops new entries for validHours (exits continue); "
        + "avoidSymbols get no new entries for the same time. summary: your reasoning for the owner (never shown to the trader). "
        + "sources: the https links you relied on. The view expires after validHours (1-12); an expired view means neutral. "
        + "highlights: up to 5 of the most important news items or market events, each with a source, for the owner's dashboard "
        + "(they don't change what the trader may do).")]
    public async Task<string> RecordMarketView(
        [Description("risk-on, neutral or risk-off.")] string regime,
        [Description("Your reasoning in 20 to 1000 characters, for the owner's review.")] string summary,
        [Description("https links you relied on (up to 10; at least one for risk-off or avoidSymbols).")] string[] sources,
        [Description("Hours the view stays in force, 1 to 12.")] int validHours = 6,
        [Description("Symbols from the brief's `symbols` to keep out of new entries, for example after a hack or before a large unlock.")] string[]? avoidSymbols = null,
        [Description("Up to 5 of the most important items for the owner, most important first; leave out routine news.")] MarketHighlight[]? highlights = null,
        CancellationToken cancellationToken = default)
    {
        await context.RequireAllowedAsync("record_market_view", cancellationToken).ConfigureAwait(false);

        if (!MarketViewStore.TryRegime(regime, out var parsed))
            throw new McpException("regime must be risk-on, neutral or risk-off.");
        if (validHours is < 1 or > MaxValidHours)
            throw new McpException($"validHours must be 1 to {MaxValidHours}.");
        var cleanSummary = Clean(summary);
        if (cleanSummary.Length is < 20 or > 1000)
            throw new McpException("summary must be 20 to 1000 characters.");
        var cleanSources = (sources ?? []).Select(s => s?.Trim() ?? string.Empty).ToList();
        // A cautionary view (risk-off or an avoided symbol) needs evidence; neutral or risk-on may have none (research failed).
        var minSources = parsed == MarketRegime.RiskOff || (avoidSymbols?.Length ?? 0) > 0 ? 1 : 0;
        if (cleanSources.Count < minSources || cleanSources.Count > MaxSources
            || cleanSources.Any(s => !IsHttpsLink(s)))
            throw new McpException($"sources must be up to {MaxSources} https links of at most 300 characters, and at least one for risk-off or avoidSymbols.");

        var cleanHighlights = CleanHighlights(highlights, universe);
        var avoid = new List<string>();
        foreach (var raw in avoidSymbols ?? [])
        {
            var symbol = MarketDataPoller.NormaliseSymbol(raw ?? string.Empty);
            if (!universe.IsTradable(symbol))
                throw new McpException($"avoidSymbols may hold only symbols from the brief: {string.Join(", ", universe.Tradable)}."); // input not echoed
            if (!avoid.Contains(symbol, StringComparer.Ordinal))
                avoid.Add(symbol);
        }

        var now = time.GetUtcNow();
        var until = now.AddHours(validHours);
        var view = new MarketView(parsed, now, until, avoid.Select(s => new SymbolFlag(s, until)).ToList());
        var spec = analystSpec.Active!;
        var data = MarketViewStore.ToData(view, cleanSources);
        data["analystSpec"] = spec.Label;
        if (cleanHighlights.Count > 0)
            data["highlights"] = System.Text.Json.JsonSerializer.Serialize(cleanHighlights, HighlightJson); // for the owner's dashboard only
        var entry = new JournalEntry(
            JournalIds.New(now, MarketViewStore.Kind), now, MarketViewStore.Kind, context.Agent, context.RunId, spec.Label, cleanSummary, data);
        await views.RecordAsync(view, entry, cancellationToken).ConfigureAwait(false);
        var riskOffFor = views.RiskOffDuration(now);

        return ToolJson.Of(new
        {
            recorded = true,
            regime = MarketViewStore.RegimeName(parsed),
            validUntil = until,
            avoid,
            highlights = cleanHighlights.Count,
            riskOffHours = riskOffFor is { } d ? Math.Round(d.TotalHours, 1) : (double?)null,
            effect = parsed == MarketRegime.RiskOff
                ? "No new entries until validUntil; exits continue."
                : avoid.Count > 0 ? "No new entries in the avoided symbols until validUntil." : "No restriction.",
        });
    }
}
