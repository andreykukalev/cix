using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cix.Stocks.Host;

/// <summary>One holding as the analyst sees it, with what the rules advise for it tonight (if anything).</summary>
/// <param name="RuleAction">The rule advisor's action on it tonight (<c>sell</c>, <c>set-stop</c>, <c>raise-stop</c>, <c>buy</c>), or null.</param>
public sealed record BriefHolding(
    string Symbol, string? Name, decimal Quantity, decimal AverageCost, decimal? LastClose, decimal? Stop, DateOnly OpenedOn,
    string? RuleAction, string? RuleReason);

/// <summary>A buy the rules advise tonight, sized by them.</summary>
public sealed record BriefBuy(string Symbol, string? Name, decimal Quantity, decimal Price, decimal? Stop, string Reason);

/// <summary>A stock the market-wide search found tonight, sized by the rules.</summary>
public sealed record BriefCandidate(string Symbol, string? Name, string Status, decimal Close, decimal BreakoutHigh, decimal Stop, decimal Shares, decimal Momentum);

/// <summary>
/// What the stocks analyst reads (ADR-0027): the night's advice for the owner's real portfolio (the paper one before the
/// owner sent theirs), the search's candidates, and recent news on each of those stocks. Built by the host, so Claude needs
/// no key and no database; written to a file the analyst job hands to Claude.
/// </summary>
/// <param name="RealPortfolio">The holdings and cash are the owner's real ones; otherwise the paper portfolio's.</param>
public sealed record AnalystBrief(
    DateOnly AsOf, DateTimeOffset GeneratedAt, string Regime, string Benchmark, bool RealPortfolio, decimal Capital, decimal Cash,
    IReadOnlyList<BriefHolding> Holdings, IReadOnlyList<BriefBuy> Buys, IReadOnlyList<BriefCandidate> Candidates,
    IReadOnlyDictionary<string, IReadOnlyList<NewsItem>> News, IReadOnlyList<string> NewsMissing);

/// <summary>The analyst's word on one holding.</summary>
/// <param name="Action"><c>hold</c>, <c>sell-half</c>, <c>sell-all</c> or <c>add</c> (<see cref="AnalystActions"/>).</param>
/// <param name="Earnings">Its next earnings report, when the analyst found one within <see cref="AnalystAnswer.EarningsDays"/> days.</param>
/// <param name="ReasonRu">The reason in Russian, for the page in Russian (the English one stays the reference).</param>
public sealed record HoldingView(string Symbol, string Action, string Reason, DateOnly? Earnings, string? ReasonRu = null);

/// <summary>A buy the analyst picks, ranked from 1, among the rules' buys and the search's candidates.</summary>
public sealed record PickView(string Symbol, int Rank, string Reason, DateOnly? Earnings, string? ReasonRu = null);

/// <summary>A buy or candidate the analyst would leave out, and why.</summary>
public sealed record SkipView(string Symbol, string Reason, DateOnly? Earnings, string? ReasonRu = null);

/// <summary>
/// The analyst's verdict on one close (<c>analysis-latest</c> in the advice container; stocks/README.md has the contract),
/// checked against the brief: only stocks the brief named, only the actions it allows, text cleaned and cut. The amounts and
/// stops stay the rule advisor's: the analyst can only advise less risk (skip, sell sooner), never a bigger position.
/// </summary>
/// <param name="Status"><c>done</c>, <c>refused</c> (the answer couldn't be read) or <c>not-run</c> (Claude didn't run).</param>
/// <param name="Reason">Why it wasn't done: <c>usage-limit</c>, <c>credentials</c>, <c>timeout</c>, <c>failed</c> or <c>unreadable</c>.</param>
/// <param name="Dropped">Lines of the answer left out as outside the brief or malformed.</param>
public sealed record AnalysisDocument(
    DateOnly AsOf, DateTimeOffset GeneratedAt, string Status, string? Reason, string? Market,
    IReadOnlyList<HoldingView> Holdings, IReadOnlyList<PickView> Picks, IReadOnlyList<SkipView> Skips, int Dropped, string? MarketRu = null)
{
    public const string Done = "done";
    public const string Refused = "refused";
    public const string NotRun = "not-run";

    /// <summary>Why a run didn't use Claude or failed, as the analyst job reports it.</summary>
    public static readonly IReadOnlySet<string> NotRunReasons = new HashSet<string>(StringComparer.Ordinal) { "usage-limit", "credentials", "timeout", "failed" };

    public static AnalysisDocument Missing(DateOnly asOf, DateTimeOffset now, string status, string reason) => new(asOf, now, status, reason, null, [], [], [], 0);
}

public static class AnalystActions
{
    public const string Hold = "hold";
    public const string SellHalf = "sell-half";
    public const string SellAll = "sell-all";

    /// <summary>Buy more: shown as the analyst's view only; the amount, if any, is the rules' buy.</summary>
    public const string Add = "add";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Hold, SellHalf, SellAll, Add };
}

/// <summary>Builds the brief from the night's advice and candidates, with the news on each stock in them.</summary>
public sealed class AnalystBriefing(IStocksStore store, INewsSource news, TimeProvider time)
{
    /// <summary>News on at most this many stocks: 5 EODHD calls each (the holdings first, then the buys, then the candidates).</summary>
    public const int MaxNewsSymbols = 30;

    /// <summary>Articles per stock.</summary>
    public const int NewsPerSymbol = 5;

    /// <summary>Articles from this many days before the close.</summary>
    public const int NewsDays = 7;

    /// <summary>
    /// The brief for the latest advice, or null when there is no advice, or the analyst already answered for its close (a
    /// second scheduled run that night has nothing to do). A stock whose news can't be fetched is listed as missing.
    /// </summary>
    public async Task<AnalystBrief?> BuildAsync(CancellationToken cancellationToken)
    {
        var advice = await store.GetAdviceAsync("advice-latest", cancellationToken).ConfigureAwait(false);
        if (advice is null)
            return null;
        var done = await store.GetAnalysisAsync("analysis-latest", cancellationToken).ConfigureAwait(false);
        if (done is { Status: AnalysisDocument.Done } && done.AsOf >= advice.AsOf)
            return null;

        var candidates = await store.GetCandidatesAsync("candidates-latest", cancellationToken).ConfigureAwait(false);
        var owner = advice.Owner;
        var actions = owner?.Actions ?? advice.Actions;
        var holdings = (owner?.Holdings ?? advice.Holdings).Select(h =>
        {
            var rule = actions.FirstOrDefault(a => a.Symbol == h.Symbol && a.Kind != "buy");
            return new BriefHolding(h.Symbol, h.Name, h.Quantity, h.AverageCost, h.LastClose, h.Stop, h.OpenedOn, rule?.Kind, rule?.Reason);
        }).ToList();
        var buys = actions.Where(a => a.Kind == "buy")
            .Select(a => new BriefBuy(a.Symbol, a.Name, a.Quantity, a.ReferencePrice, a.Stop, a.Reason)).ToList();
        var found = candidates is { AsOf: { } day } && day == advice.AsOf
            ? candidates.Candidates.Select(c => new BriefCandidate(c.Symbol, c.Name, c.Status, c.Close, c.BreakoutHigh, c.Stop, c.Shares, c.Momentum)).ToList()
            : [];

        var symbols = holdings.Select(h => h.Symbol).Concat(buys.Select(b => b.Symbol)).Concat(found.Select(c => c.Symbol))
            .Distinct(StringComparer.Ordinal).ToList();
        var articles = new Dictionary<string, IReadOnlyList<NewsItem>>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var symbol in symbols)
        {
            if (articles.Count + missing.Count >= MaxNewsSymbols)
            {
                missing.Add(symbol);
                continue;
            }

            try
            {
                articles[symbol] = await news.GetNewsAsync(symbol, advice.AsOf.AddDays(-NewsDays), NewsPerSymbol, cancellationToken).ConfigureAwait(false);
            }
            catch (StockDataException)
            {
                missing.Add(symbol);
            }
        }

        return new AnalystBrief(
            advice.AsOf, time.GetUtcNow(), advice.Regime, advice.Benchmark, owner is not null, owner?.Capital ?? advice.Capital,
            owner?.Cash ?? advice.Cash, holdings, buys, found, articles, missing);
    }
}

/// <summary>Saves an analysis without ever losing a better one.</summary>
public static class AnalysisRecord
{
    /// <summary>
    /// Saves <paramref name="analysis"/> as <c>analysis-latest</c> (and, when done, as the dated <c>analysis-YYYY-MM-DD</c>,
    /// written once) unless the latest is for a later close, or is a done analysis of the same close and this one isn't (a
    /// second run that night that failed keeps the first one's answer). True when it was saved.
    /// </summary>
    public static async Task<bool> SaveAsync(IStocksStore store, AnalysisDocument analysis, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(analysis);
        var latest = await store.GetAnalysisAsync("analysis-latest", cancellationToken).ConfigureAwait(false);
        if (latest is not null && (latest.AsOf > analysis.AsOf
                                   || (latest.AsOf == analysis.AsOf && latest.Status == AnalysisDocument.Done && analysis.Status != AnalysisDocument.Done)))
            return false;

        if (analysis.Status == AnalysisDocument.Done)
            await store.AddAnalysisAsync($"analysis-{analysis.AsOf:yyyy-MM-dd}", analysis, cancellationToken).ConfigureAwait(false);
        await store.SaveLatestAnalysisAsync(analysis, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Reads and checks the analyst's answer against its brief.</summary>
public static class AnalystAnswer
{
    /// <summary>An answer is a few kilobytes; anything far larger isn't one.</summary>
    public const int MaxBytes = 64 * 1024;

    public const int MaxPicks = 5;
    public const int MaxSkips = 30;

    /// <summary>An earnings date further out than this isn't news for tonight's advice.</summary>
    public const int EarningsDays = 60;

    private const int MaxReason = 400;
    private const int MaxMarket = 600;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        MaxDepth = 8,
    };

    /// <summary>
    /// The analysis an answer makes: the JSON object in Claude's final text (the first <c>{</c> to the last <c>}</c>, as it
    /// may wrap it in words or a code fence), each line kept only when it names a stock of the brief, an allowed action and a
    /// reason. An answer that isn't such an object is <c>refused</c> (<c>unreadable</c>), never an exception.
    /// </summary>
    public static AnalysisDocument Check(AnalystBrief brief, string answer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(brief);
        Raw? raw = null;
        var start = answer?.IndexOf('{', StringComparison.Ordinal) ?? -1;
        var end = answer?.LastIndexOf('}') ?? -1;
        if (answer is not null && start >= 0 && end > start && Encoding.UTF8.GetByteCount(answer) <= MaxBytes)
        {
            try
            {
                raw = JsonSerializer.Deserialize<Raw>(answer[start..(end + 1)], Json);
            }
            catch (JsonException)
            {
            }
        }

        if (raw is null)
            return AnalysisDocument.Missing(brief.AsOf, now, AnalysisDocument.Refused, "unreadable");

        var dropped = 0;
        var held = brief.Holdings.Select(h => h.Symbol).ToHashSet(StringComparer.Ordinal);
        var buyable = brief.Buys.Select(b => b.Symbol).Concat(brief.Candidates.Select(c => c.Symbol)).ToHashSet(StringComparer.Ordinal);

        var holdings = new List<HoldingView>();
        foreach (var h in raw.Holdings ?? [])
        {
            if (h?.Symbol is not { } symbol || !held.Contains(symbol) || holdings.Any(x => x.Symbol == symbol)
                || h.Action is not { } action || !AnalystActions.All.Contains(action) || Text(h.Reason, MaxReason) is not { } reason)
            {
                dropped++;
                continue;
            }

            holdings.Add(new(symbol, action, reason, Earnings(h.Earnings, brief.AsOf), Text(h.ReasonRu, MaxReason)));
        }

        var picks = new List<PickView>();
        foreach (var p in (raw.Picks ?? []).Where(p => p is not null).OrderBy(p => p!.Rank ?? int.MaxValue))
        {
            if (p!.Symbol is not { } symbol || !buyable.Contains(symbol) || picks.Any(x => x.Symbol == symbol) || picks.Count == MaxPicks
                || Text(p.Reason, MaxReason) is not { } reason)
            {
                dropped++;
                continue;
            }

            picks.Add(new(symbol, picks.Count + 1, reason, Earnings(p.Earnings, brief.AsOf), Text(p.ReasonRu, MaxReason))); // ranks 1, 2, … in the analyst's order
        }

        var skips = new List<SkipView>();
        foreach (var s in raw.Skips ?? [])
        {
            if (s?.Symbol is not { } symbol || !buyable.Contains(symbol) || picks.Any(x => x.Symbol == symbol) || skips.Any(x => x.Symbol == symbol)
                || skips.Count == MaxSkips || Text(s.Reason, MaxReason) is not { } reason)
            {
                dropped++;
                continue;
            }

            skips.Add(new(symbol, reason, Earnings(s.Earnings, brief.AsOf), Text(s.ReasonRu, MaxReason)));
        }

        return new(brief.AsOf, now, AnalysisDocument.Done, null, Text(raw.Market, MaxMarket), holdings, picks, skips, dropped, Text(raw.MarketRu, MaxMarket));
    }

    private static DateOnly? Earnings(string? value, DateOnly asOf) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
        && day >= asOf && day <= asOf.AddDays(EarningsDays) ? day : null;

    /// <summary>
    /// The analyst's text as the page shows it: no control, format (bidi overrides, zero-width) or separator characters,
    /// whitespace collapsed, at most <paramref name="max"/> characters (code points). Null when nothing is left.
    /// </summary>
    internal static string? Text(string? value, int max)
    {
        if (value is null)
            return null;
        var kept = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
                kept.Append(' ');
            else if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                     or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse))
                kept.Append(rune.ToString());
        }

        var collapsed = string.Join(' ', kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
            return null;
        return string.Concat(collapsed.EnumerateRunes().Take(max).Select(r => r.ToString())).TrimEnd();
    }

    private sealed record Raw(string? Market, string? MarketRu, IReadOnlyList<RawHolding?>? Holdings, IReadOnlyList<RawPick?>? Picks, IReadOnlyList<RawSkip?>? Skips);

    private sealed record RawHolding(string? Symbol, string? Action, string? Reason, string? Earnings, string? ReasonRu);

    private sealed record RawPick(string? Symbol, int? Rank, string? Reason, string? Earnings, string? ReasonRu);

    private sealed record RawSkip(string? Symbol, string? Reason, string? Earnings, string? ReasonRu);
}
