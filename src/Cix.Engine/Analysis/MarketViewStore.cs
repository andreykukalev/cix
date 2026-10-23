using System.Globalization;
using Cix.Domain.Scanning;
using Cix.Engine.Journal;
using Cix.Engine.Strategy;

namespace Cix.Engine.Analysis;

/// <summary>Bound from the "Analyst" section: which spec the market analyst follows. No spec means the analyst's tools refuse.</summary>
public sealed class AnalystOptions
{
    public const string SectionName = "Analyst";

    public string? Name { get; set; }

    public int? Version { get; set; }
}

/// <summary>The market analyst's spec, separate from the trading spec so each agent's tools and instructions stay its own.</summary>
public interface IAnalystSpecStore
{
    StrategySpec? Active { get; }
}

/// <summary>
/// Loads the analyst's spec. It is checked as a paper spec in every mode: the analyst has no order tools, so its spec needn't
/// be promoted to live for the engine to run live (drafts still never run).
/// </summary>
public sealed class FileAnalystSpecStore(AnalystOptions options, StrategyOptions strategy) : IAnalystSpecStore
{
    public StrategySpec? Active { get; } = Load(options, strategy);

    private static StrategySpec? Load(AnalystOptions options, StrategyOptions strategy) =>
        string.IsNullOrWhiteSpace(options.Name) || options.Version is null
            ? null
            : new FileStrategySpecStore(
                new StrategyOptions { Directory = strategy.Directory, Name = options.Name, Version = options.Version }, TradingMode.Paper).Active;
}

public sealed class FixedAnalystSpec(StrategySpec? spec) : IAnalystSpecStore
{
    public StrategySpec? Active => spec;
}

/// <summary>
/// The current market view. Every view is appended to the journal (kind <see cref="Kind"/>), which is also where the latest one
/// is restored from after a restart, so no other store is needed.
/// </summary>
public sealed partial class MarketViewStore(IJournal journal, TimeProvider time, ILogger<MarketViewStore> logger)
{
    public const string Kind = "market-view";

    /// <summary>After this long in risk-off without a break, every new risk-off view logs a warning for the owner.</summary>
    public static readonly TimeSpan LongRiskOff = TimeSpan.FromHours(24);

    private volatile MarketView? _current;
    private DateTimeOffset? _riskOffSince;

    /// <summary>The latest view, active or not. Callers check <see cref="MarketView.IsActive"/>.</summary>
    public MarketView? Current => _current;

    /// <summary>Records <paramref name="view"/> in the journal, then makes it current.</summary>
    public async Task RecordAsync(MarketView view, JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(entry);
        await journal.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        // Track unbroken risk-off: a source planted where the analyst looks could renew it run after run.
        var previous = _current;
        if (view.Regime != MarketRegime.RiskOff)
            _riskOffSince = null;
        else if (previous is null || !previous.BlocksEntries(view.RecordedAt) || _riskOffSince is null)
            _riskOffSince = view.RecordedAt;
        _current = view;

        if (RiskOffDuration(view.RecordedAt) is { } span && span >= LongRiskOff)
            LogLongRiskOff(logger, Math.Round(span.TotalHours, 1));
    }

    /// <summary>How long risk-off has held without a break, or null when the current view isn't risk-off.</summary>
    public TimeSpan? RiskOffDuration(DateTimeOffset now) =>
        _current?.BlocksEntries(now) == true && _riskOffSince is { } since ? now - since : null;

    /// <summary>Restores the newest journaled view if it is still active. Anything unreadable is ignored: no view is neutral.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        try
        {
            var latest = await journal.QueryAsync(
                new JournalQuery(From: DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1), Kind: Kind, Limit: 1), cancellationToken).ConfigureAwait(false);
            if (latest.Count == 1 && Parse(latest[0]) is { } view && view.IsActive(now))
            {
                _current = view;
                _riskOffSince = view.Regime == MarketRegime.RiskOff ? view.RecordedAt : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogRestoreFailed(logger, ex.Message);
        }
    }

    public static Dictionary<string, string> ToData(MarketView view, IEnumerable<string> sources)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["regime"] = RegimeName(view.Regime),
            ["validUntil"] = view.ValidUntil.ToString("O", CultureInfo.InvariantCulture),
            ["avoid"] = string.Join(',', view.Flags.Select(f => f.Symbol)),
            ["sources"] = string.Join(' ', sources),
        };
    }

    public static MarketView? Parse(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var data = entry.Data;
        if (data is null
            || !TryRegime(data.GetValueOrDefault("regime"), out var regime)
            || !DateTimeOffset.TryParse(data.GetValueOrDefault("validUntil"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until))
            return null;
        var flags = (data.GetValueOrDefault("avoid") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => new SymbolFlag(s, until))
            .ToList();
        return new MarketView(regime, entry.At, until, flags);
    }

    public static string RegimeName(MarketRegime regime) => regime switch
    {
        MarketRegime.RiskOn => "risk-on",
        MarketRegime.RiskOff => "risk-off",
        _ => "neutral",
    };

    public static bool TryRegime(string? value, out MarketRegime regime)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "risk-on": regime = MarketRegime.RiskOn; return true;
            case "neutral": regime = MarketRegime.Neutral; return true;
            case "risk-off": regime = MarketRegime.RiskOff; return true;
            default: regime = MarketRegime.Neutral; return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Risk-off has blocked new entries for {Hours} hours without a break. Check the analyst's sources in the journal (market-view entries).")]
    private static partial void LogLongRiskOff(ILogger logger, double hours);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't restore the market view from the journal: {Detail}")]
    private static partial void LogRestoreFailed(ILogger logger, string detail);
}

public static class AnalystRegistration
{
    /// <summary>Registers the analyst's spec (optional) and the market view store.</summary>
    public static IServiceCollection AddCixAnalyst(this IServiceCollection services, AnalystOptions options, StrategyOptions strategy)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAnalystSpecStore>(new FileAnalystSpecStore(options, strategy));
        services.AddSingleton<MarketViewStore>();
        return services;
    }
}
