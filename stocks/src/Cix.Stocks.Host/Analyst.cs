using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cix.Stocks.Host;

/// <summary>
/// One holding as the analyst sees it, with what the rules advise for it tonight (if anything). Sizes are fractions of the
/// capital, never amounts: the brief sits beside untrusted news in a session that can search the web, so the owner's money
/// stays out of it.
/// </summary>
/// <param name="Change">The gain since it was bought, as a fraction (0.12 is +12%).</param>
/// <param name="Weight">Its value at the last close (the cost when there is none) as a fraction of the capital.</param>
/// <param name="RuleAction">The rule advisor's action on it tonight (<c>sell</c>, <c>set-stop</c>, <c>raise-stop</c>), or null.</param>
public sealed record BriefHolding(
    string Symbol, string? Name, decimal? LastClose, decimal? Change, decimal Weight, decimal? Stop, DateOnly OpenedOn,
    string? RuleAction, string? RuleReason);

/// <summary>A buy the rules advise tonight, sized by them (<paramref name="Weight"/>: its amount as a fraction of the capital).</summary>
public sealed record BriefBuy(string Symbol, string? Name, decimal Price, decimal? Stop, decimal Weight, string Reason);

/// <summary>A stock the market-wide search found tonight, sized by the rules (<paramref name="Weight"/> as for a buy).</summary>
public sealed record BriefCandidate(string Symbol, string? Name, string Status, decimal Close, decimal BreakoutHigh, decimal Stop, decimal Weight, decimal Momentum);

/// <summary>
/// What the stocks analyst reads (ADR-0027): the night's advice for the owner's real portfolio (the paper one before the
/// owner sent theirs), the search's candidates, and recent news on each of those stocks. Built by the host, so Claude needs
/// no key and no database; written to a file the analyst job hands to Claude.
/// </summary>
/// <param name="RealPortfolio">The holdings are the owner's real ones; otherwise the paper portfolio's.</param>
/// <param name="CashWeight">Free cash as a fraction of the capital.</param>
/// <param name="NewsMissing">Stocks whose news couldn't be fetched.</param>
/// <param name="NewsSkipped">Stocks past <see cref="AnalystBriefing.MaxNewsSymbols"/>, whose news wasn't asked for.</param>
/// <param name="Pass">Which run the brief is handed to (<see cref="AnalystPass"/>); set in the analyst job's file, null as stored.</param>
/// <param name="Night">For the check before the open: the night's done analysis of this close, if there is one.</param>
public sealed record AnalystBrief(
    DateOnly AsOf, DateTimeOffset GeneratedAt, string Regime, string Benchmark, bool RealPortfolio, decimal CashWeight,
    IReadOnlyList<BriefHolding> Holdings, IReadOnlyList<BriefBuy> Buys, IReadOnlyList<BriefCandidate> Candidates,
    IReadOnlyDictionary<string, IReadOnlyList<NewsItem>> News, IReadOnlyList<string> NewsMissing, IReadOnlyList<string> NewsSkipped,
    string? Pass = null, AnalysisDocument? Night = null);

/// <summary>
/// The analyst's two runs on a close: the night's, after the nightly advice, and the check before the US open, which looks
/// for news since the night (reports before the bell, guidance, pre-market moves, the 8:30 data) and can only take risk
/// away from it.
/// </summary>
public static class AnalystPass
{
    public const string Night = "night";
    public const string PreOpen = "pre-open";

    /// <summary>The check before the open runs from 08:35 New York time (after the 8:30 data) to the 09:30 open.</summary>
    public static readonly TimeOnly PreOpenFrom = new(8, 35);
    public static readonly TimeOnly PreOpenUntil = new(9, 30);

    /// <summary>
    /// The pass a run at <paramref name="now"/> makes: the check before the open on a weekday from <see cref="PreOpenFrom"/>
    /// to <see cref="PreOpenUntil"/> New York time, the night's otherwise (which skips a close already analysed). The
    /// schedule has a slot an hour apart in UTC (12:40 and 13:40), so one falls in the window in summer and in winter.
    /// </summary>
    public static string For(DateTimeOffset now)
    {
        var local = NewYork(now);
        var time = TimeOnly.FromDateTime(local);
        return local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && time >= PreOpenFrom && time < PreOpenUntil ? PreOpen : Night;
    }

    /// <summary>The New York trading day of <paramref name="at"/>: a check before the open is done once per such day.</summary>
    public static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(NewYork(at));

    /// <summary>
    /// New York's local time, by the US rule (daylight time from 02:00 on the second Sunday of March to 02:00 on the first
    /// Sunday of November), so the image needs no time zone data.
    /// </summary>
    public static DateTime NewYork(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        var start = Sunday(utc.Year, 3, 2).AddHours(7); // 02:00 EST
        var end = Sunday(utc.Year, 11, 1).AddHours(6); // 02:00 EDT
        return DateTime.SpecifyKind(utc.AddHours(utc >= start && utc < end ? -4 : -5), DateTimeKind.Unspecified);
    }

    private static DateTime Sunday(int year, int month, int nth)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        return first.AddDays(((7 - (int)first.DayOfWeek) % 7) + (7 * (nth - 1)));
    }
}

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
/// The analyst's verdict on one close (<c>analysis-latest</c> in the analysis container; stocks/README.md has the contract),
/// checked against the brief: only stocks the brief named, only the actions it allows, text cleaned and cut. The amounts and
/// stops stay the rule advisor's: the analyst can only advise less risk (skip, sell sooner), never a bigger position.
/// </summary>
/// <param name="Status"><c>done</c>, <c>refused</c> (the answer couldn't be read) or <c>not-run</c> (Claude didn't run).</param>
/// <param name="Reason">Why it wasn't done: <c>usage-limit</c>, <c>credentials</c>, <c>timeout</c>, <c>failed</c> or <c>unreadable</c>.</param>
/// <param name="Dropped">Lines of the answer left out as outside the brief or malformed.</param>
/// <param name="Pass">The run that wrote it (<see cref="AnalystPass"/>); null, from before the check before the open, is the night's.</param>
public sealed record AnalysisDocument(
    DateOnly AsOf, DateTimeOffset GeneratedAt, string Status, string? Reason, string? Market,
    IReadOnlyList<HoldingView> Holdings, IReadOnlyList<PickView> Picks, IReadOnlyList<SkipView> Skips, int Dropped, string? MarketRu = null,
    string? Pass = null)
{
    public const string Done = "done";
    public const string Refused = "refused";
    public const string NotRun = "not-run";

    /// <summary>Why a run didn't use Claude or failed, as the analyst job reports it.</summary>
    public static readonly IReadOnlySet<string> NotRunReasons = new HashSet<string>(StringComparer.Ordinal) { "usage-limit", "credentials", "timeout", "failed" };

    public static AnalysisDocument Missing(DateOnly asOf, DateTimeOffset now, string status, string reason, string? pass = null) =>
        new(asOf, now, status, reason, null, [], [], [], 0, Pass: pass);

    /// <summary>The check before the open, as opposed to the night's run.</summary>
    public bool IsPreOpen => Pass == AnalystPass.PreOpen;
}

public static class AnalystActions
{
    public const string Hold = "hold";
    public const string SellHalf = "sell-half";
    public const string SellAll = "sell-all";

    /// <summary>Buy more: shown as the analyst's view only; the amount, if any, is the rules' buy.</summary>
    public const string Add = "add";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Hold, SellHalf, SellAll, Add };

    /// <summary>How cautious an action is, from <c>add</c> (0) to <c>sell-all</c> (3).</summary>
    public static int Caution(string action) => action switch
    {
        Add => 0,
        Hold => 1,
        SellHalf => 2,
        SellAll => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not an analyst action."),
    };
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
    /// The stored brief (<c>brief-latest</c>, written by the nightly run) for the pass a run at <paramref name="now"/> makes
    /// (<see cref="AnalystPass.For"/>), when it is for the latest advice and that pass has nothing done for it yet; null
    /// otherwise: an older brief says nothing about tonight's advice. The night's pass is done once that close has any done
    /// analysis; the check before the open, once it has run that New York day (a holiday's check doesn't use up the next
    /// open's), and it gets the close's latest done analysis to check.
    /// </summary>
    public static async Task<AnalystBrief?> ToAnalyseAsync(IStocksStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (await store.GetBriefAsync(cancellationToken).ConfigureAwait(false) is not { } brief
            || await store.GetAdviceAsync("advice-latest", cancellationToken).ConfigureAwait(false) is not { } advice || brief.AsOf != advice.AsOf)
            return null;
        var latest = await store.GetAnalysisAsync("analysis-latest", cancellationToken).ConfigureAwait(false);
        var done = latest is { Status: AnalysisDocument.Done } ? latest : null;
        if (done is not null && done.AsOf > brief.AsOf)
            return null;
        var pass = AnalystPass.For(now);
        if (pass == AnalystPass.Night)
            return done is not null && done.AsOf == brief.AsOf ? null : brief with { Pass = pass, Night = null };
        if (done is { IsPreOpen: true } && done.AsOf == brief.AsOf && AnalystPass.Day(done.GeneratedAt) == AnalystPass.Day(now))
            return null;
        return brief with { Pass = pass, Night = done is not null && done.AsOf == brief.AsOf ? done : null };
    }

    /// <summary>
    /// The brief for the latest advice, or null when there is no advice, or the analyst already answered for its close (a
    /// second scheduled run that night has nothing to do). A stock whose news can't be fetched is listed as missing; from
    /// <paramref name="newsUntil"/> on, no more news is fetched and the rest are listed as skipped.
    /// </summary>
    public async Task<AnalystBrief?> BuildAsync(CancellationToken cancellationToken, DateTimeOffset? newsUntil = null)
    {
        var advice = await store.GetAdviceAsync("advice-latest", cancellationToken).ConfigureAwait(false);
        if (advice is null)
            return null;
        var done = await store.GetAnalysisAsync("analysis-latest", cancellationToken).ConfigureAwait(false);
        if (done is { Status: AnalysisDocument.Done } && done.AsOf >= advice.AsOf)
            return null;

        var candidates = await store.GetCandidatesAsync("candidates-latest", cancellationToken).ConfigureAwait(false);
        var owner = advice.Owner;
        var capital = owner?.Capital ?? advice.Capital;
        if (capital <= 0)
            return null;
        decimal Part(decimal amount) => Math.Round(amount / capital, 3, MidpointRounding.ToEven);

        var actions = owner?.Actions ?? advice.Actions;
        var holdings = (owner?.Holdings ?? advice.Holdings).Select(h =>
        {
            var rule = actions.FirstOrDefault(a => a.Symbol == h.Symbol && a.Kind != "buy");
            var change = h.LastClose is { } close && h.AverageCost > 0 ? Math.Round(close / h.AverageCost - 1, 3, MidpointRounding.ToEven) : (decimal?)null;
            return new BriefHolding(h.Symbol, h.Name, h.LastClose, change, Part(h.Quantity * (h.LastClose ?? h.AverageCost)), h.Stop, h.OpenedOn, rule?.Kind, rule?.Reason);
        }).ToList();
        var buys = actions.Where(a => a.Kind == "buy")
            .Select(a => new BriefBuy(a.Symbol, a.Name, a.ReferencePrice, a.Stop, Part(a.Quantity * a.ReferencePrice), a.Reason)).ToList();
        var found = candidates is { AsOf: { } day } && day == advice.AsOf
            ? candidates.Candidates.Select(c => new BriefCandidate(c.Symbol, c.Name, c.Status, c.Close, c.BreakoutHigh, c.Stop, Part(c.Shares * c.Close), c.Momentum)).ToList()
            : [];

        var symbols = holdings.Select(h => h.Symbol).Concat(buys.Select(b => b.Symbol)).Concat(found.Select(c => c.Symbol))
            .Distinct(StringComparer.Ordinal).ToList();
        var articles = new Dictionary<string, IReadOnlyList<NewsItem>>(StringComparer.Ordinal);
        var missing = new List<string>();
        var asked = 0;
        foreach (var symbol in symbols.Take(MaxNewsSymbols))
        {
            if (newsUntil is { } until && time.GetUtcNow() >= until)
                break;
            asked++;
            try
            {
                articles[symbol] = await news.GetNewsAsync(symbol, advice.AsOf.AddDays(-NewsDays), NewsPerSymbol, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is StockDataException or ArgumentException)
            {
                missing.Add(symbol);
            }
        }

        return new AnalystBrief(
            advice.AsOf, time.GetUtcNow(), advice.Regime, advice.Benchmark, owner is not null, Part(owner?.Cash ?? advice.Cash),
            holdings, buys, found, articles, missing, symbols.Skip(asked).ToList());
    }

    /// <summary>
    /// The nightly run's last step when the analyst is deployed: builds the brief for the advice just written and saves it as
    /// <c>brief-latest</c>, fetching news until <paramref name="until"/> (inside the job's timeout; each request is bounded by
    /// the client's own timeout). Never fails the run: what happened comes back as a line for the log, and the advice stands.
    /// </summary>
    public async Task<string> WriteAsync(DateTimeOffset until, CancellationToken cancellationToken)
    {
        try
        {
            if (await BuildAsync(cancellationToken, until).ConfigureAwait(false) is not { } brief)
                return "Analyst brief: nothing new to analyse.";
            await store.SaveBriefAsync(brief, cancellationToken).ConfigureAwait(false);
            return $"Analyst brief for {brief.AsOf:yyyy-MM-dd}: {brief.Holdings.Count} holdings, {brief.Buys.Count} buys, " +
                   $"{brief.Candidates.Count} candidates, news on {brief.News.Count} ({brief.NewsMissing.Count} failed, {brief.NewsSkipped.Count} not fetched).";
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return $"Analyst brief failed (the advice stands): {e.GetType().Name}: {e.Message}";
        }
    }
}

/// <summary>Saves an analysis without ever losing a better one.</summary>
public static class AnalysisRecord
{
    /// <summary>
    /// Saves <paramref name="analysis"/> as <c>analysis-latest</c> (and, when done, as the dated <c>analysis-YYYY-MM-DD</c>,
    /// or <c>analysis-YYYY-MM-DD-pre-open-YYYY-MM-DD</c> for the check before the open on a New York day, each written once)
    /// unless the latest is for a later close, or is a done analysis of the same close and this one isn't (a second run that
    /// night that failed keeps the first one's answer), or is the check before the open and this is the night's (never
    /// replaced by an older look), or the same pass already saved its done analysis (an overlapping run keeps the first).
    /// True when it was saved.
    /// </summary>
    public static async Task<bool> SaveAsync(IStocksStore store, AnalysisDocument analysis, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(analysis);
        var done = analysis.Status == AnalysisDocument.Done;
        var latest = await store.GetAnalysisAsync("analysis-latest", cancellationToken).ConfigureAwait(false);
        if (latest is not null && (latest.AsOf > analysis.AsOf
                                   || (latest.AsOf == analysis.AsOf && latest.Status == AnalysisDocument.Done && (!done || (latest.IsPreOpen && !analysis.IsPreOpen)))))
            return false;
        // A run overlapping this one may have saved a done analysis since: its dated copy says so. A failed run never
        // replaces either pass's; the night's never replaces the check before the open; a pass's own keeps the first.
        var own = DatedId(analysis.AsOf, analysis.Pass, analysis.GeneratedAt);
        var check = DatedId(analysis.AsOf, AnalystPass.PreOpen, analysis.GeneratedAt);
        string[] guards = !done ? [DatedId(analysis.AsOf), check] : analysis.IsPreOpen ? [own] : [own, check];
        foreach (var id in guards)
        {
            if (await store.GetAnalysisAsync(id, cancellationToken).ConfigureAwait(false) is { Status: AnalysisDocument.Done })
                return false;
        }

        if (done)
            await store.AddAnalysisAsync(own, analysis, cancellationToken).ConfigureAwait(false);
        await store.SaveLatestAnalysisAsync(analysis, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The dated id of a done analysis: the close's, and for the check before the open, also its New York day.</summary>
    public static string DatedId(DateOnly asOf, string? pass = null, DateTimeOffset? at = null) => pass == AnalystPass.PreOpen
        ? string.Create(CultureInfo.InvariantCulture, $"analysis-{asOf:yyyy-MM-dd}-pre-open-{AnalystPass.Day(at ?? throw new ArgumentNullException(nameof(at))):yyyy-MM-dd}")
        : string.Create(CultureInfo.InvariantCulture, $"analysis-{asOf:yyyy-MM-dd}");
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

    /// <summary>
    /// The analysis an answer makes: the JSON object in Claude's final text (inside a <c>```json</c> fence when there is one,
    /// else the first <c>{</c> to the last <c>}</c>), each line kept only when it names a stock of the brief, an allowed
    /// action and a reason; a line of another shape is dropped and counted, not the whole answer. An answer that isn't such
    /// an object, or has no <c>holdings</c> or <c>picks</c> list, is <c>refused</c> (<c>unreadable</c>); one that keeps no
    /// line for the brief's holdings, or none at all for a brief with stocks in it, is refused as <c>empty</c>: an empty
    /// "done" would read as Claude finding nothing wrong. Never an exception.
    /// </summary>
    public static AnalysisDocument Check(AnalystBrief brief, string? answer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(brief);
        AnalysisDocument Refuse(string reason) => AnalysisDocument.Missing(brief.AsOf, now, AnalysisDocument.Refused, reason, brief.Pass);

        if (answer is null || Encoding.UTF8.GetByteCount(answer) > MaxBytes || ObjectText(answer) is not { } json)
            return Refuse("unreadable");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return Refuse("unreadable");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || (Lines(root, "holdings") is null && Lines(root, "picks") is null))
                return Refuse("unreadable");

            var dropped = 0;
            var held = brief.Holdings.Select(h => h.Symbol).ToHashSet(StringComparer.Ordinal);
            var buyable = brief.Buys.Select(b => b.Symbol).Concat(brief.Candidates.Select(c => c.Symbol)).ToHashSet(StringComparer.Ordinal);
            // Before the open, after a done night: the check can only take risk away from the night's view. Only the night's
            // picks can be kept, a holding's action can't be less cautious than the night's, and what the answer leaves out
            // of the night's holdings and skips is carried over.
            var night = brief is { Pass: AnalystPass.PreOpen, Night: { Status: AnalysisDocument.Done } n } ? n : null;
            var pickable = night is not null
                ? night.Picks.Select(p => p.Symbol).Where(buyable.Contains).ToHashSet(StringComparer.Ordinal)
                : buyable;
            var nightHoldings = (night?.Holdings ?? []).Where(h => held.Contains(h.Symbol) && AnalystActions.All.Contains(h.Action))
                .DistinctBy(h => h.Symbol, StringComparer.Ordinal).ToDictionary(h => h.Symbol, StringComparer.Ordinal);

            var holdings = new List<HoldingView>();
            foreach (var h in Lines(root, "holdings") ?? [])
            {
                if (Str(h, "symbol") is not { } symbol || !held.Contains(symbol) || holdings.Any(x => x.Symbol == symbol)
                    || Str(h, "action") is not { } action || !AnalystActions.All.Contains(action) || Text(Str(h, "reason"), MaxReason) is not { } reason)
                {
                    dropped++;
                    continue;
                }

                var earnings = Earnings(Str(h, "earnings"), brief.AsOf);
                if (nightHoldings.TryGetValue(symbol, out var before) && AnalystActions.Caution(action) < AnalystActions.Caution(before.Action))
                {
                    dropped++; // less cautious than the night: the night's line stands
                    holdings.Add(before with { Earnings = earnings ?? before.Earnings });
                    continue;
                }

                holdings.Add(new(symbol, action, reason, earnings ?? nightHoldings.GetValueOrDefault(symbol)?.Earnings, Text(Str(h, "reasonRu"), MaxReason)));
            }

            var answered = holdings.Count;
            holdings.AddRange(nightHoldings.Values.Where(h => holdings.All(x => x.Symbol != h.Symbol)));

            // Ranked as the analyst ranked them (unranked last, in its order); a rank that isn't a whole number drops the line.
            var ranked = (Lines(root, "picks") ?? []).Select((p, i) => (Line: p, Rank: Rank(p), Order: i)).ToList();
            dropped += ranked.Count(r => r.Rank is null);
            var picks = new List<PickView>();
            foreach (var (p, _, _) in ranked.Where(r => r.Rank is not null).OrderBy(r => r.Rank).ThenBy(r => r.Order))
            {
                if (Str(p, "symbol") is not { } symbol || !pickable.Contains(symbol) || picks.Any(x => x.Symbol == symbol) || picks.Count == MaxPicks
                    || Text(Str(p, "reason"), MaxReason) is not { } reason)
                {
                    dropped++;
                    continue;
                }

                picks.Add(new(symbol, picks.Count + 1, reason, Earnings(Str(p, "earnings"), brief.AsOf), Text(Str(p, "reasonRu"), MaxReason))); // ranks 1, 2, …
            }

            var skips = new List<SkipView>();
            foreach (var k in Lines(root, "skips") ?? [])
            {
                if (Str(k, "symbol") is not { } symbol || !buyable.Contains(symbol) || picks.Any(x => x.Symbol == symbol) || skips.Any(x => x.Symbol == symbol)
                    || skips.Count == MaxSkips || Text(Str(k, "reason"), MaxReason) is not { } reason)
                {
                    dropped++;
                    continue;
                }

                skips.Add(new(symbol, reason, Earnings(Str(k, "earnings"), brief.AsOf), Text(Str(k, "reasonRu"), MaxReason)));
            }

            // Judged on the answer's own lines: lines carried over from the night don't make an empty answer a check.
            if ((held.Count > 0 && answered == 0) || (buyable.Count > 0 && answered + picks.Count + skips.Count == 0))
                return Refuse("empty");
            foreach (var k in night?.Skips ?? [])
            {
                if (skips.Count < MaxSkips && buyable.Contains(k.Symbol) && picks.All(x => x.Symbol != k.Symbol) && skips.All(x => x.Symbol != k.Symbol))
                    skips.Add(k);
            }

            return new(brief.AsOf, now, AnalysisDocument.Done, null, Text(Str(root, "market"), MaxMarket), holdings, picks, skips, dropped,
                Text(Str(root, "marketRu"), MaxMarket), brief.Pass);
        }
    }

    /// <summary>The JSON object's text: a <c>```json</c> fence's contents when there is one, then its first <c>{</c> to its last <c>}</c>.</summary>
    private static string? ObjectText(string answer)
    {
        var text = answer;
        var fence = answer.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fence >= 0)
        {
            var body = fence + "```json".Length;
            var close = answer.IndexOf("```", body, StringComparison.Ordinal);
            text = close >= 0 ? answer[body..close] : answer[body..];
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static IEnumerable<JsonElement>? Lines(JsonElement root, string name) =>
        root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : null;

    /// <summary>A line's text field, or null when the line isn't an object or the field isn't a string.</summary>
    private static string? Str(JsonElement line, string name) =>
        line.ValueKind == JsonValueKind.Object && line.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A pick's rank: a whole number from 1, as a number or a string; <see cref="int.MaxValue"/> when absent; null when malformed.</summary>
    private static int? Rank(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty("rank", out var rank) || rank.ValueKind == JsonValueKind.Null)
            return int.MaxValue;
        if (rank.ValueKind == JsonValueKind.Number && rank.TryGetInt32(out var n) && n >= 1)
            return n;
        return rank.ValueKind == JsonValueKind.String && int.TryParse(rank.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m >= 1 ? m : null;
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
}
