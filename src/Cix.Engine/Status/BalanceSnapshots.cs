using System.Globalization;
using Cix.Engine.Journal;

namespace Cix.Engine.Status;

/// <summary>
/// Balance and holding history (#70). <c>engine-status-&lt;mode&gt;</c> keeps only the latest portfolio, so once each UTC hour this
/// appends it to the journal as a <c>balance-snapshot</c>: equity, cash, peak equity, today's result, exposure and each
/// holding's value. Every day starts with one, since a new day is a new hour. The journal is append-only and tagged with the
/// engine's mode (#65), so paper's and live's histories never mix. Built from the status the engine already holds: no exchange
/// call, and a failed write is tried again at the next status write, without affecting trading.
/// </summary>
public sealed partial class BalanceSnapshots(IJournal journal, TradingContext trading, TimeProvider time, ILogger logger)
{
    public const string Kind = "balance-snapshot";

    /// <summary>A valuation older than this is left out: it would describe an earlier hour, or prices that are gone.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _lastHour;
    private bool _checkedJournal;

    /// <summary>Where the figures come from: the exchange's balances for live, the simulated account for paper.</summary>
    public string Source => trading.Mode == TradingMode.Live ? "exchange" : "simulated";

    /// <summary>
    /// Appends a snapshot of <paramref name="portfolio"/> unless this hour has one already (after a restart too: the journal is
    /// checked once). Returns whether one was written. Never throws, except for cancellation.
    /// </summary>
    public async Task<bool> WriteIfDueAsync(PortfolioStatus? portfolio, string? spec, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (portfolio is null || now - portfolio.At > MaxAge)
            return false;

        var hour = HourOf(now);
        try
        {
            if (!_checkedJournal)
            {
                var day = DateOnly.FromDateTime(hour.UtcDateTime);
                var last = await journal.QueryAsync(new JournalQuery(day, day, Kind, Limit: 1), cancellationToken).ConfigureAwait(false);
                if (last.Count > 0)
                    _lastHour = HourOf(last[0].At);
                _checkedJournal = true;
            }

            if (_lastHour == hour)
                return false;
            await journal.AppendAsync(Entry(portfolio, spec, now), cancellationToken).ConfigureAwait(false);
            _lastHour = hour;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogWriteFailed(logger, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// The journal entry for one snapshot. Amounts are in the quote currency, as invariant strings; each holding has a
    /// <c>value:&lt;symbol&gt;</c> and, when known, a <c>base:&lt;symbol&gt;</c> (the coins that can be sold).
    /// </summary>
    public JournalEntry Entry(PortfolioStatus portfolio, string? spec, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(portfolio);
        var inv = CultureInfo.InvariantCulture;
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = Source,
            ["currency"] = portfolio.Currency,
            ["valuedAt"] = portfolio.At.ToString("O", inv),
            ["equity"] = portfolio.Equity.ToString(inv),
            ["cash"] = portfolio.Cash.ToString(inv),
            ["peakEquity"] = portfolio.PeakEquity.ToString(inv),
            ["pnlToday"] = portfolio.PnlToday.ToString(inv),
            ["exposure"] = portfolio.ExposureQuote.ToString(inv),
            ["holdings"] = portfolio.Holdings.Count.ToString(inv),
        };
        foreach (var holding in portfolio.Holdings)
        {
            data[$"value:{holding.Symbol}"] = holding.ValueQuote.ToString(inv);
            if (holding.SellableBase is { } sellable)
                data[$"base:{holding.Symbol}"] = sellable.ToString(inv);
        }

        var held = portfolio.Holdings.Sum(h => h.ValueQuote);
        var summary = string.Create(inv,
            $"Equity {portfolio.Equity:0.00} {portfolio.Currency}: cash {portfolio.Cash:0.00}, {portfolio.Holdings.Count} holding(s) worth {held:0.00} ({Source}).");
        return new JournalEntry(JournalIds.New(at, Kind), at, Kind, "engine", null, spec, summary, data);
    }

    private static DateTimeOffset HourOf(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Balance snapshot could not be written ({Error}); trying again at the next status write")]
    private static partial void LogWriteFailed(ILogger logger, string error);
}
