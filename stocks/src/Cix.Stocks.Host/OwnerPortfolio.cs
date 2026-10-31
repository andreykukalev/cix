namespace Cix.Stocks.Host;

/// <summary>One holding as the owner enters it on the Stocks page.</summary>
/// <param name="Stop">The stop resting at the broker, if one is.</param>
/// <param name="OpenedOn">When it was bought; the day of the submission when not given. The trailing stop counts from it.</param>
public sealed record OwnerHoldingInput(string Symbol, string? Name, decimal Quantity, decimal AverageCost, decimal? Stop, DateOnly? OpenedOn);

/// <summary>
/// The owner's portfolio as sent from the Stocks page: the whole of it, replacing the one before (ADR-0026). The dashboard's
/// writer can only create these, one per <see cref="Sequence"/> (<c>portfolio-1</c>, <c>portfolio-2</c>, …, in the
/// <c>requests</c> container); the next night's run applies the newest.
/// </summary>
public sealed record PortfolioSubmission(int Sequence, DateTimeOffset SubmittedAt, decimal Capital, IReadOnlyList<OwnerHoldingInput> Holdings);

/// <summary>One of the owner's real holdings, as the run keeps it.</summary>
public sealed record OwnerHolding(string Symbol, string? Name, decimal Quantity, decimal AverageCost, decimal? Stop, DateOnly OpenedOn)
{
    public Holding ToHolding() => new(Symbol, Quantity, AverageCost, Stop, OpenedOn);
}

/// <summary>
/// The owner's real portfolio between runs: the capital they give the advisor, what they hold, the submission it came from,
/// and the terms of each holding's bars (to recognise a split). The advisor assumes a stop it advised was placed.
/// </summary>
public sealed record OwnerState(
    decimal Capital, IReadOnlyList<OwnerHolding> Holdings, int AppliedSequence, DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, SymbolTerms>? Terms = null)
{
    /// <summary>What the holdings cost: the capital minus this is what new positions may use.</summary>
    public decimal Invested => Holdings.Sum(h => h.Quantity * h.AverageCost);
}

/// <summary>The advice for the owner's real portfolio, on the Stocks page beside the paper record.</summary>
/// <param name="Cash">What new positions may use: the capital minus what the holdings cost, never under zero.</param>
/// <param name="Invested">What the holdings cost, in the base currency.</param>
/// <param name="Value">The holdings at the last close; null when one has no bars.</param>
/// <param name="AppliedSequence">The submission the portfolio comes from (<c>portfolio-N</c>), so the page knows a newer one waits.</param>
/// <param name="UpdatedAt">When that submission was sent.</param>
public sealed record OwnerSection(
    decimal Capital, decimal Cash, decimal Invested, decimal? Value, int AppliedSequence, DateTimeOffset UpdatedAt,
    IReadOnlyList<ActionLine> Actions, IReadOnlyList<HoldingLine> Holdings, IReadOnlyList<NoteLine> Notes);

/// <summary>The newest submission, refused: the portfolio stays as it was. <see cref="Reason"/> is one of <see cref="OwnerPortfolio.Apply"/>'s codes, or <c>unreadable</c>.</summary>
public sealed record PortfolioRefusal(int Sequence, string Reason);

/// <summary>Turning submissions into the owner's state.</summary>
public static class OwnerPortfolio
{
    public const int MaxHoldings = 50;

    /// <summary>The most submissions one run looks through after the applied one: the newest of them is applied.</summary>
    public const int MaxPending = 50;
    public const decimal MaxCapital = 1_000_000m;
    private const decimal MaxPrice = 1_000_000m;
    private const decimal MaxQuantity = 1_000_000m;

    /// <summary>
    /// The state a submission makes, or null with the reason it was refused (the page checks too; this is the last word):
    /// <c>capital</c>, <c>holdings</c> (too many), <c>symbol</c> (not a US share, or twice), <c>figures</c> (a quantity, cost
    /// or stop not positive or out of bounds), <c>opened</c> (bought after the submission). The owner's broker account is in
    /// USD, so US shares only. The submission's figures are the broker's as they are now, so the terms of the bars (to
    /// recognise a split) start again from this run's bars.
    /// </summary>
    public static (OwnerState? State, string? Error) Apply(PortfolioSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Sequence < 1)
            return (null, "sequence");
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
            if (h.OpenedOn is { } opened && opened > day)
                return (null, "opened");
            holdings.Add(new(h.Symbol, Name(h.Name), h.Quantity, h.AverageCost, h.Stop, h.OpenedOn ?? day));
        }

        return (new OwnerState(submission.Capital, holdings, submission.Sequence, submission.SubmittedAt), null);
    }

    /// <summary>A name to show, or null: at most 80 characters, none that could disguise the ticker beside it.</summary>
    private static string? Name(string? name)
    {
        var trimmed = name?.Trim();
        if (trimmed is not { Length: > 0 and <= 80 })
            return null;
        foreach (var c in trimmed)
        {
            if (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
                return null;
        }

        return trimmed;
    }
}
