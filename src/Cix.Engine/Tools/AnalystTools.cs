using System.ComponentModel;
using Cix.Domain.Scanning;
using Cix.Engine.Analysis;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Scanning;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cix.Engine.Tools;

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
        + "sources: the https links you relied on. The view expires after validHours (1-12); an expired view means neutral.")]
    public async Task<string> RecordMarketView(
        [Description("risk-on, neutral or risk-off.")] string regime,
        [Description("Your reasoning in 20 to 1000 characters, for the owner's review.")] string summary,
        [Description("https links you relied on (1 to 10).")] string[] sources,
        [Description("Hours the view stays in force, 1 to 12.")] int validHours = 6,
        [Description("Symbols from the brief's `symbols` to keep out of new entries, for example after a hack or before a large unlock.")] string[]? avoidSymbols = null,
        CancellationToken cancellationToken = default)
    {
        await context.RequireAllowedAsync("record_market_view", cancellationToken).ConfigureAwait(false);

        if (!MarketViewStore.TryRegime(regime, out var parsed))
            throw new McpException("regime must be risk-on, neutral or risk-off.");
        if (validHours is < 1 or > MaxValidHours)
            throw new McpException($"validHours must be 1 to {MaxValidHours}.");
        // Control and format characters (bidi overrides, zero-width) removed: the text is from the web and the owner reads it.
        var cleanSummary = new string((summary ?? string.Empty)
            .Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        if (cleanSummary.Length is < 20 or > 1000)
            throw new McpException("summary must be 20 to 1000 characters.");
        var cleanSources = (sources ?? []).Select(s => s?.Trim() ?? string.Empty).ToList();
        if (cleanSources.Count is < 1 or > MaxSources
            || cleanSources.Any(s => s.Length > 300 || !Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
            throw new McpException($"sources must be 1 to {MaxSources} https links of at most 300 characters.");

        var avoid = new List<string>();
        foreach (var raw in avoidSymbols ?? [])
        {
            var symbol = MarketDataPoller.NormaliseSymbol(raw ?? string.Empty);
            if (!universe.IsTradable(symbol))
                throw new McpException($"{symbol} isn't a symbol from the brief; allowed: {string.Join(", ", universe.Tradable)}.");
            if (!avoid.Contains(symbol, StringComparer.Ordinal))
                avoid.Add(symbol);
        }

        var now = time.GetUtcNow();
        var until = now.AddHours(validHours);
        var view = new MarketView(parsed, now, until, avoid.Select(s => new SymbolFlag(s, until)).ToList());
        var spec = analystSpec.Active!;
        var data = MarketViewStore.ToData(view, cleanSources);
        data["analystSpec"] = spec.Label;
        var entry = new JournalEntry(
            Guid.NewGuid().ToString("N"), now, MarketViewStore.Kind, context.Agent, context.RunId, spec.Label, cleanSummary, data);
        await views.RecordAsync(view, entry, cancellationToken).ConfigureAwait(false);
        var riskOffFor = views.RiskOffDuration(now);

        return ToolJson.Of(new
        {
            recorded = true,
            regime = MarketViewStore.RegimeName(parsed),
            validUntil = until,
            avoid,
            riskOffHours = riskOffFor is { } d ? Math.Round(d.TotalHours, 1) : (double?)null,
            effect = parsed == MarketRegime.RiskOff
                ? "No new entries until validUntil; exits continue."
                : avoid.Count > 0 ? "No new entries in the avoided symbols until validUntil." : "No restriction.",
        });
    }
}
