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
    [property: Description("The https link this comes from; it must also be in the view's sources.")] string Source,
    [property: Description("Affected symbols from the brief's `symbols`, if any.")] string[]? Symbols = null,
    [property: Description("Why it matters, in one sentence of at most 300 characters.")] string? Detail = null,
    [property: Description("The title in Russian for the owner, 10 to 200 characters; keep names, tickers, abbreviations and trading terms in English.")] string? TitleRu = null,
    [property: Description("The detail in Russian for the owner, at most 400 characters; keep names, tickers, abbreviations and trading terms in English.")] string? DetailRu = null);

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
    public const int MaxSummaryRu = 1500;

    private static readonly System.Text.Json.JsonSerializerOptions HighlightJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Removes control, format (bidi overrides, zero-width, invisible tag characters), private-use and unpaired surrogate
    /// characters, by code point: the text comes from the web, and the owner and operator sessions read it.
    /// </summary>
    public static string Clean(string? text) =>
        string.Concat((text ?? string.Empty).EnumerateRunes()
            .Where(r => !System.Text.Rune.IsControl(r) && System.Text.Rune.GetUnicodeCategory(r) is not (
                System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Surrogate))
            .Select(r => r.ToString())).Trim();

    /// <summary>An absolute https link of at most 300 characters with no user info, returned in its normalised form.</summary>
    public static bool TryHttpsLink(string? s, out string normalised)
    {
        normalised = string.Empty;
        if (s is null || s.Length > 300 || !Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u)
            || u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0 || u.AbsoluteUri.Length > 300)
            return false;
        normalised = u.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// Checks the highlights and returns the valid ones, cleaned, as stored in the journal (camelCase JSON), plus a fixed
    /// reason code for each one dropped (input is never echoed). A bad highlight is dropped rather than refusing the
    /// view, so dashboard decoration can never block a protective view. Each needs a source that is also among the
    /// view's <paramref name="sources"/>, and its symbols must be tradable. A Russian title or detail that doesn't fit is
    /// left out on its own (the dashboard then shows the English) and listed in TranslationsDropped.
    /// </summary>
    public static (IReadOnlyList<MarketHighlight> Kept, IReadOnlyList<string> Dropped, IReadOnlyList<string> TranslationsDropped) CleanHighlights(
        IReadOnlyList<MarketHighlight>? highlights, IReadOnlyCollection<string> sources, MarketUniverse universe)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(universe);
        var kept = new List<MarketHighlight>();
        var dropped = new List<string>();
        var translationsDropped = new List<string>();
        foreach (var (h, i) in (highlights ?? []).Select((h, i) => (h, i)))
        {
            var reason = Check(h, sources, universe, out var clean);
            if (reason is null && kept.Count >= MaxHighlights)
                reason = "too-many";
            if (reason is null)
                kept.Add(WithRussian(clean!, h!, i, translationsDropped));
            else
                dropped.Add($"{i}:{reason}");
        }

        return (kept, dropped, translationsDropped);
    }

    /// <summary>The highlight with its Russian title and detail, cleaned, each left out when it doesn't fit.</summary>
    private static MarketHighlight WithRussian(MarketHighlight clean, MarketHighlight raw, int index, List<string> dropped)
    {
        var titleRu = Clean(raw.TitleRu);
        var detailRu = Clean(raw.DetailRu);
        if (titleRu.Length > 0 && titleRu.Length is < 10 or > 200)
        {
            dropped.Add($"{index}:title-ru-length");
            titleRu = string.Empty;
        }

        if (clean.Detail is null)
        {
            detailRu = string.Empty; // a translation of nothing: the Russian adds nothing the English doesn't say
        }
        else if (detailRu.Length > 400)
        {
            dropped.Add($"{index}:detail-ru-length");
            detailRu = string.Empty;
        }

        return clean with { TitleRu = titleRu.Length == 0 ? null : titleRu, DetailRu = detailRu.Length == 0 ? null : detailRu };
    }

    private static string? Check(MarketHighlight? h, IReadOnlyCollection<string> sources, MarketUniverse universe, out MarketHighlight? clean)
    {
        clean = null;
        if (h is null)
            return "empty";
        var title = Clean(h.Title);
        var detail = Clean(h.Detail);
        var impact = (h.Impact ?? string.Empty).Trim().ToLowerInvariant();
        var direction = (h.Direction ?? string.Empty).Trim().ToLowerInvariant();
        if (title.Length is < 10 or > 160)
            return "title-length";
        if (detail.Length > 300)
            return "detail-length";
        if (impact is not ("high" or "medium"))
            return "impact";
        if (direction is not ("risk" or "opportunity" or "watch"))
            return "direction";
        if (!TryHttpsLink(h.Source, out var source))
            return "source-not-https";
        if (!sources.Contains(source, StringComparer.Ordinal))
            return "source-not-in-sources";
        var symbols = new List<string>();
        foreach (var raw in h.Symbols ?? [])
        {
            var symbol = MarketDataPoller.NormaliseSymbol(raw ?? string.Empty);
            if (!universe.IsTradable(symbol))
                return "symbol-not-tradable";
            if (!symbols.Contains(symbol, StringComparer.Ordinal))
                symbols.Add(symbol);
        }

        clean = new MarketHighlight(title, impact, direction, source, symbols.Count == 0 ? null : symbols.ToArray(), detail.Length == 0 ? null : detail);
        return null;
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
        + "highlights: up to 5 of the most important news items or market events for the owner's dashboard, each with a source that "
        + "is also in sources (they don't change what the trader may do; invalid ones are dropped and listed in highlightsDropped). "
        + "summaryRu, and titleRu and detailRu in each highlight: the same in Russian for the owner, with names, tickers, abbreviations "
        + "and trading terms kept in English (dashboard only; one that doesn't fit is left out and listed in translationsDropped).")]
    public async Task<string> RecordMarketView(
        [Description("risk-on, neutral or risk-off.")] string regime,
        [Description("Your reasoning in 20 to 1000 characters, for the owner's review.")] string summary,
        [Description("https links you relied on (up to 10; at least one for risk-off or avoidSymbols).")] string[] sources,
        [Description("Hours the view stays in force, 1 to 12.")] int validHours = 6,
        [Description("Symbols from the brief's `symbols` to keep out of new entries, for example after a hack or before a large unlock.")] string[]? avoidSymbols = null,
        [Description("Up to 5 of the most important items for the owner, most important first; leave out routine news.")] MarketHighlight[]? highlights = null,
        [Description("The summary in Russian for the owner, 20 to 1500 characters; names, tickers, abbreviations and trading terms stay in English.")] string? summaryRu = null,
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
        var cleanSources = new List<string>();
        var badSource = false;
        foreach (var raw in sources ?? [])
        {
            if (TryHttpsLink(raw, out var link))
                cleanSources.Add(link);
            else
                badSource = true;
        }

        // A cautionary view (risk-off or an avoided symbol) needs evidence; neutral or risk-on may have none (research failed).
        var minSources = parsed == MarketRegime.RiskOff || (avoidSymbols?.Length ?? 0) > 0 ? 1 : 0;
        if (badSource || cleanSources.Count < minSources || cleanSources.Count > MaxSources)
            throw new McpException($"sources must be up to {MaxSources} https links of at most 300 characters, and at least one for risk-off or avoidSymbols.");

        var (cleanHighlights, droppedHighlights, droppedTranslations) = CleanHighlights(highlights, cleanSources, universe);
        var translationsDropped = droppedTranslations.ToList();
        var cleanSummaryRu = Clean(summaryRu);
        if (cleanSummaryRu.Length > 0 && cleanSummaryRu.Length is < 20 or > MaxSummaryRu)
        {
            translationsDropped.Insert(0, "summary-ru-length"); // a translation never blocks the view
            cleanSummaryRu = string.Empty;
        }

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
        if (cleanSummaryRu.Length > 0)
            data["summaryRu"] = cleanSummaryRu; // for the owner's dashboard only
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
            highlightsDropped = droppedHighlights, // "index:reason"; fix and record again only if they matter
            translationsDropped, // "summary-ru-length" or "index:title-ru-length"; the dashboard shows the English instead
            riskOffHours = riskOffFor is { } d ? Math.Round(d.TotalHours, 1) : (double?)null,
            effect = parsed == MarketRegime.RiskOff
                ? "No new entries until validUntil; exits continue."
                : avoid.Count > 0 ? "No new entries in the avoided symbols until validUntil." : "No restriction.",
        });
    }
}
