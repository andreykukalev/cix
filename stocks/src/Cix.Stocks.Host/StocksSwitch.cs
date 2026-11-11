using System.Text.Json;

namespace Cix.Stocks.Host;

/// <summary>
/// One press of Stop or Start on the Stocks page (ADR-0032): <c>switch-N</c> in the stocks database's <c>control</c>
/// container, numbered from 1 with no gaps, created by the dashboard's writer and never changed. The newest decides.
/// </summary>
public sealed record SwitchSubmission(int Sequence, string Action, DateTimeOffset SubmittedAt);

/// <summary>A switch document that exists: its value, or null when it can't be read as one.</summary>
public sealed record SwitchRead(SwitchSubmission? Switch)
{
    private static readonly SwitchRead Unreadable = new((SwitchSubmission?)null);

    /// <summary>A switch is a hundred bytes: anything far larger isn't one.</summary>
    private const int MaxBytes = 4 * 1024;

    /// <summary>A stored document <c>{ id, value }</c> read as a switch; anything else is a read with no switch, never an exception.</summary>
    public static SwitchRead Parse(ReadOnlySpan<byte> document)
    {
        if (document.Length > MaxBytes)
            return Unreadable;
        try
        {
            using var json = JsonDocument.Parse(document.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("value", out var value))
                return Unreadable;
            return new SwitchRead(value.Deserialize<SwitchSubmission>(StocksJson.Options));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        {
            return Unreadable;
        }
    }

    /// <summary>A stream's document, read up to just past the limit.</summary>
    public static async Task<SwitchRead> ParseAsync(Stream document, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[MaxBytes + 1];
        int read;
        while ((read = await document.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
                return Unreadable;
        }

        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }
}

/// <summary>
/// Whether the owner has stopped the stocks advisory: <paramref name="Sequence"/> is the newest switch (0: none ever),
/// <paramref name="Since"/> when it was pressed. A newest switch that can't be read counts as started, the state before
/// the first press, and is <paramref name="Unreadable"/>: the next press replaces it. For a Start, <paramref name="StoppedAt"/>
/// is when the Stop before it was pressed (null when that one can't be read).
/// </summary>
public sealed record AdvisorSwitch(bool Paused, int Sequence, DateTimeOffset? Since, bool Unreadable = false, DateTimeOffset? StoppedAt = null)
{
    /// <summary>A stop shorter than this missed no nightly run's brief worth skipping: the brief before it is still analysed.</summary>
    public static readonly TimeSpan QuickStop = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether a brief written at <paramref name="generatedAt"/> predates a stop that lasted: the first nightly run after the
    /// Start writes the next one. A quick Stop and Start (a mistaken press) leaves the brief to be analysed.
    /// </summary>
    public bool Outdates(DateTimeOffset generatedAt) =>
        !Paused && Since is { } started && generatedAt < started && (StoppedAt is not { } stopped || started - stopped >= QuickStop);

    public static readonly AdvisorSwitch NeverPressed = new(false, 0, null);
}

/// <summary>
/// The owner's Stop and Start for the whole stocks advisory (ADR-0032). While stopped the nightly run, the market search,
/// the analyst's brief and both of the analyst's passes do nothing: no EODHD request, no Claude run, nothing written. What
/// was saved stays, and the page shows it as of the last run. After Start the next nightly run carries on from there, and
/// after a stop of a day or more the analyst waits for that run's brief: one written before the Start is never analysed.
/// A read that fails (not a missing or unreadable press) fails the run: the jobs don't guess.
/// </summary>
public static class StocksSwitch
{
    public const string Pause = "pause";
    public const string Resume = "resume";

    /// <summary>No owner presses this often: past it the search stops looking.</summary>
    public const int MaxSequence = 1 << 20;

    /// <summary>
    /// The newest switch, found in a few reads: the switches are numbered from 1 with no gaps, so it doubles until one is
    /// missing, then halves the range between the last there and the first missing.
    /// </summary>
    public static async Task<AdvisorSwitch> CurrentAsync(IStocksStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (await store.GetSwitchAsync(1, cancellationToken).ConfigureAwait(false) is not { } newest)
            return AdvisorSwitch.NeverPressed;
        var found = 1;
        var missing = 2;
        while (missing <= MaxSequence && await store.GetSwitchAsync(missing, cancellationToken).ConfigureAwait(false) is { } read)
        {
            (found, newest) = (missing, read);
            missing *= 2;
        }

        while (missing - found > 1)
        {
            var middle = found + ((missing - found) / 2);
            if (await store.GetSwitchAsync(middle, cancellationToken).ConfigureAwait(false) is { } read)
                (found, newest) = (middle, read);
            else
                missing = middle;
        }

        if (!Readable(newest, found, out var s))
            return new AdvisorSwitch(false, found, null, Unreadable: true);
        if (s.Action == Pause)
            return new AdvisorSwitch(true, found, s.SubmittedAt);
        var before = found > 1 ? await store.GetSwitchAsync(found - 1, cancellationToken).ConfigureAwait(false) : null;
        var stoppedAt = before is not null && Readable(before, found - 1, out var stop) && stop.Action == Pause ? stop.SubmittedAt : (DateTimeOffset?)null;
        return new AdvisorSwitch(false, found, s.SubmittedAt, StoppedAt: stoppedAt);
    }

    private static bool Readable(SwitchRead read, int sequence, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SwitchSubmission? press)
    {
        press = read.Switch is { } s && s.Sequence == sequence && s.Action is Pause or Resume && s.SubmittedAt != default ? s : null;
        return press is not null;
    }
}
