using System.Text;

namespace Cix.Stocks.Host;

/// <summary>One holding as the owner enters it on the Stocks page.</summary>
/// <param name="Stop">The stop resting at the broker, if one is.</param>
/// <param name="OpenedOn">When it was bought; the day of the submission when not given. The trailing stop counts from it.</param>
public sealed record OwnerHoldingInput(string Symbol, string? Name, decimal Quantity, decimal AverageCost, decimal? Stop, DateOnly? OpenedOn);

/// <summary>
/// A buy or a sell the owner made at the broker, as entered on the Stocks page: the shares, the price per share, the fees
/// the broker charged (none when not given), the day, and for a buy the stop placed with it.
/// </summary>
/// <param name="Kind"><c>buy</c> or <c>sell</c>.</param>
public sealed record OwnerTradeInput(string Kind, string Symbol, string? Name, decimal Quantity, decimal Price, decimal? Fees, DateOnly Day, decimal? Stop = null);

/// <summary>
/// A change of the owner's portfolio as sent from the Stocks page (ADR-0026): either the whole portfolio, replacing the one
/// before (<see cref="Capital"/> and <see cref="Holdings"/>), or one trade on it (<see cref="Trade"/>). The dashboard's
/// writer can only create these, one per <see cref="Sequence"/> (<c>portfolio-1</c>, <c>portfolio-2</c>, …, in the
/// <c>requests</c> container); the next night's run applies them in order.
/// </summary>
public sealed record PortfolioSubmission(
    int Sequence, DateTimeOffset SubmittedAt, decimal Capital, IReadOnlyList<OwnerHoldingInput>? Holdings, OwnerTradeInput? Trade = null);

/// <summary>One of the owner's real holdings, as the run keeps it.</summary>
public sealed record OwnerHolding(string Symbol, string? Name, decimal Quantity, decimal AverageCost, decimal? Stop, DateOnly OpenedOn)
{
    public Holding ToHolding() => new(Symbol, Quantity, AverageCost, Stop, OpenedOn);
}

/// <summary>
/// The owner's real portfolio between runs: the capital they give the advisor, what they hold, the submission it came from,
/// and the terms of each holding's bars (to recognise a split). The advisor assumes a stop it advised was placed.
/// </summary>
/// <param name="AppliedSequence">The submission the portfolio comes from; 0 when none was ever applied, and the record only
/// remembers the refused ones.</param>
/// <param name="SeenSequence">The newest submission the run looked at, applied or refused: the next run looks after it.</param>
/// <param name="Refusal">The newest submission, when it was refused: shown until a newer one is applied.</param>
/// <param name="Trades">The owner's latest trades, oldest first (at most <see cref="OwnerPortfolio.MaxTrades"/>); each is
/// also kept once in the trades container as <c>owner-N</c>.</param>
public sealed record OwnerState(
    decimal Capital, IReadOnlyList<OwnerHolding> Holdings, int AppliedSequence, DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, SymbolTerms>? Terms = null, int SeenSequence = 0, PortfolioRefusal? Refusal = null,
    IReadOnlyList<TradeLine>? Trades = null);

/// <summary>The advice for the owner's real portfolio, on the Stocks page beside the paper record.</summary>
/// <param name="Cash">What new positions may use: the capital minus what the holdings cost, never under zero.</param>
/// <param name="Invested">What the holdings cost, in the base currency.</param>
/// <param name="Value">The holdings at the last close; null when one has no bars.</param>
/// <param name="AppliedSequence">The submission the portfolio comes from (<c>portfolio-N</c>), so the page knows a newer one waits.</param>
/// <param name="UpdatedAt">When that submission was sent.</param>
/// <param name="Trades">The owner's latest trades, newest first.</param>
public sealed record OwnerSection(
    decimal Capital, decimal Cash, decimal Invested, decimal? Value, int AppliedSequence, DateTimeOffset UpdatedAt,
    IReadOnlyList<ActionLine> Actions, IReadOnlyList<HoldingLine> Holdings, IReadOnlyList<NoteLine> Notes,
    IReadOnlyList<TradeLine>? Trades = null);

/// <summary>The newest submission, refused: the portfolio stays as it was. <see cref="Reason"/> is one of <see cref="OwnerPortfolio.Apply"/>'s codes, or <c>unreadable</c>.</summary>
public sealed record PortfolioRefusal(int Sequence, string Reason);

/// <summary>Turning submissions into the owner's state.</summary>
public static class OwnerPortfolio
{
    public const int MaxHoldings = 50;

    /// <summary>The trades the owner's record keeps for the page; older ones stay in the trades container.</summary>
    public const int MaxTrades = 100;

    /// <summary>The reason of an owner's trade in the trades container and on the page.</summary>
    public const string TradeReason = "owner";

    /// <summary>The newest submission a run looked at: the next looks after it.</summary>
    public static int Seen(OwnerState? state) => state is null ? 0 : Math.Max(state.SeenSequence, state.AppliedSequence);

    /// <summary>Whether the record holds a portfolio to advise, not only the refusals before the first.</summary>
    public static bool HasPortfolio(OwnerState? state) => state is { AppliedSequence: > 0 };

    /// <summary>
    /// The most submissions one run looks through after the last one seen: the newest of them is applied. More than the page
    /// lets wait (50 after the last one seen), so the two never disagree on which is the newest.
    /// </summary>
    public const int MaxPending = 1000;
    public const decimal MaxCapital = 1_000_000m;
    private const decimal MaxPrice = 1_000_000m;
    private const decimal MaxQuantity = 1_000_000m;

    /// <summary>
    /// The state a submission makes, or null with the reason it was refused (the page checks too; this is the last word):
    /// <c>capital</c>, <c>holdings</c> (too many), <c>symbol</c> (not a US share, or twice), <c>figures</c> (a quantity, cost
    /// or stop not positive or out of bounds), <c>opened</c> (bought after the submission's day, give or take a day for the
    /// owner's time zone: the page sends their local date). The owner's broker account is in
    /// USD, so US shares only. The submission's figures are the broker's as they are now, so the terms of the bars (to
    /// recognise a split) start again from this run's bars.
    /// </summary>
    public static (OwnerState? State, string? Error) Apply(PortfolioSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Sequence < 1)
            return (null, "sequence");
        if (submission.Trade is not null)
            return (null, "trade");
        if (submission.Capital is <= 0 or > MaxCapital)
            return (null, "capital");
        if (submission.Holdings is not { Count: <= MaxHoldings })
            return (null, "holdings");
        var day = DateOnly.FromDateTime(submission.SubmittedAt.UtcDateTime);
        var holdings = new List<OwnerHolding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in submission.Holdings)
        {
            if (h is null || !StockSymbol.IsValid(h.Symbol) || !h.Symbol.EndsWith(".US", StringComparison.Ordinal) || !seen.Add(h.Symbol))
                return (null, "symbol");
            if (h.Quantity is <= 0 or > MaxQuantity || h.AverageCost is <= 0 or > MaxPrice || h.Stop is <= 0 or > MaxPrice)
                return (null, "figures");
            if (h.OpenedOn is { } opened && opened > day.AddDays(1))
                return (null, "opened");
            holdings.Add(new(h.Symbol, Name(h.Name), h.Quantity, h.AverageCost, h.Stop, h.OpenedOn ?? day));
        }

        return (new OwnerState(submission.Capital, holdings, submission.Sequence, submission.SubmittedAt, SeenSequence: submission.Sequence), null);
    }

    /// <summary>
    /// The state one submission makes from <paramref name="current"/> (the portfolio so far, null before any): a whole
    /// portfolio replaces it (<see cref="Apply(PortfolioSubmission)"/>), keeping the record of trades; a trade changes it,
    /// with its line for the record. Null with the reason when refused.
    /// </summary>
    public static (OwnerState? State, string? Error, TradeLine? Trade) Apply(OwnerState? current, PortfolioSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Trade is null)
        {
            var (state, error) = Apply(submission);
            return (state is null ? null : state with { Trades = current?.Trades }, error, null);
        }

        return ApplyTrade(current, submission);
    }

    /// <summary>
    /// A trade on the owner's portfolio, in decimal as the broker counts it. A buy adds the shares at their price, the fees
    /// included in the cost (a new holding opens on the trade's day, with its stop if given; an existing one keeps its day,
    /// and its stop unless a new one is given); the capital stays, so the free cash falls by what was paid. A sell takes the
    /// shares off at the average cost and adds the profit or loss, fees off, to the capital. Refused: <c>no-portfolio</c>
    /// (none yet: send the whole portfolio first), <c>kind</c>, <c>symbol</c>, <c>figures</c>, <c>opened</c> (a day after
    /// the submission's, give or take a day for the owner's time zone), <c>not-held</c>, <c>too-many</c> (more shares sold
    /// than held), <c>holdings</c> (one too many), <c>capital</c> (a loss that leaves none).
    /// </summary>
    public static (OwnerState? State, string? Error, TradeLine? Trade) ApplyTrade(OwnerState? current, PortfolioSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Sequence < 1)
            return (null, "sequence", null);
        if (submission.Trade is not { } trade)
            return (null, "trade", null);
        if (!HasPortfolio(current))
            return (null, "no-portfolio", null);
        if (trade.Kind is not ("buy" or "sell"))
            return (null, "kind", null);
        if (!StockSymbol.IsValid(trade.Symbol) || !trade.Symbol.EndsWith(".US", StringComparison.Ordinal))
            return (null, "symbol", null);
        if (trade.Quantity is <= 0 or > MaxQuantity || trade.Price is <= 0 or > MaxPrice || trade.Fees is < 0 or > MaxPrice || trade.Stop is <= 0 or > MaxPrice)
            return (null, "figures", null);
        if (trade.Day > DateOnly.FromDateTime(submission.SubmittedAt.UtcDateTime).AddDays(1))
            return (null, "opened", null);

        var fees = trade.Fees ?? 0m;
        var holdings = current!.Holdings.ToList();
        var index = holdings.FindIndex(h => h.Symbol == trade.Symbol);
        var capital = current.Capital;
        decimal? profit = null;
        if (trade.Kind == "buy")
        {
            if (index < 0)
            {
                if (holdings.Count >= MaxHoldings)
                    return (null, "holdings", null);
                holdings.Add(new(trade.Symbol, Name(trade.Name), trade.Quantity, Cost(trade.Quantity * trade.Price + fees, trade.Quantity), trade.Stop, trade.Day));
            }
            else
            {
                var h = holdings[index];
                var quantity = h.Quantity + trade.Quantity;
                if (quantity > MaxQuantity)
                    return (null, "figures", null);
                holdings[index] = h with
                {
                    Quantity = quantity,
                    AverageCost = Cost((h.Quantity * h.AverageCost) + (trade.Quantity * trade.Price) + fees, quantity),
                    Stop = trade.Stop ?? h.Stop,
                };
            }
        }
        else
        {
            if (index < 0)
                return (null, "not-held", null);
            var h = holdings[index];
            if (trade.Quantity > h.Quantity)
                return (null, "too-many", null);
            profit = Math.Round((trade.Quantity * (trade.Price - h.AverageCost)) - fees, 2, MidpointRounding.ToEven);
            capital += profit.Value;
            if (capital is <= 0 or > MaxCapital)
                return (null, "capital", null);
            if (trade.Quantity == h.Quantity)
                holdings.RemoveAt(index);
            else
                holdings[index] = h with { Quantity = h.Quantity - trade.Quantity };
        }

        var line = new TradeLine(trade.Day, trade.Symbol, trade.Kind, trade.Quantity, trade.Price, fees, profit, TradeReason);
        var state = current with
        {
            Capital = capital,
            Holdings = holdings,
            AppliedSequence = submission.Sequence,
            UpdatedAt = submission.SubmittedAt,
            SeenSequence = submission.Sequence,
            Refusal = null,
            Trades = (current.Trades ?? []).Append(line).TakeLast(MaxTrades).ToList(),
        };
        return (state, null, line);
    }

    /// <summary>An average cost per share, to 8 decimals, as the page shows and sends prices.</summary>
    private static decimal Cost(decimal paid, decimal quantity) => Math.Round(paid / quantity, 8, MidpointRounding.ToEven);

    /// <summary>
    /// A name to show, or null: without the characters that could disguise the ticker beside it (control, format such as bidi
    /// overrides and zero-width, line and paragraph separators), at most 80 characters (code points, as the page counts).
    /// </summary>
    private static string? Name(string? name)
    {
        if (name is null)
            return null;
        var kept = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator))
                kept.Append(rune.ToString());
        }

        var trimmed = kept.ToString().Trim();
        if (trimmed.Length == 0)
            return null;
        var runes = trimmed.EnumerateRunes().Take(80).Select(r => r.ToString());
        return string.Concat(runes).TrimEnd();
    }
}
