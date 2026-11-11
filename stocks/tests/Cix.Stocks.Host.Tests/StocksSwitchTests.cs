namespace Cix.Stocks.Host.Tests;

public class StocksSwitchTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 11, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NeverPressedMeansStarted()
    {
        Assert.Equal(AdvisorSwitch.NeverPressed, await StocksSwitch.CurrentAsync(new InMemoryStocksStore(), CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(100)]
    public async Task TheNewestPressDecidesHoweverManyThereAre(int presses)
    {
        var store = new InMemoryStocksStore();
        for (var n = 1; n <= presses; n++)
            store.Press(n % 2 == 1 ? StocksSwitch.Pause : StocksSwitch.Resume, At.AddMinutes(n));

        var current = await StocksSwitch.CurrentAsync(store, CancellationToken.None);

        var stoppedAt = presses % 2 == 0 ? At.AddMinutes(presses - 1) : (DateTimeOffset?)null; // a Start knows its Stop
        Assert.Equal(new AdvisorSwitch(presses % 2 == 1, presses, At.AddMinutes(presses), StoppedAt: stoppedAt), current);
    }

    [Theory]
    [InlineData("""{"id":"switch-3","value":{"sequence":3,"action":"halt","submittedAt":"2026-11-11T16:00:00Z"}}""")] // not a known action
    [InlineData("""{"id":"switch-3","value":{"sequence":2,"action":"pause","submittedAt":"2026-11-11T16:00:00Z"}}""")] // another number
    [InlineData("""{"id":"switch-3","value":"pause"}""")]
    [InlineData("""{"id":"switch-3"}""")]
    [InlineData("not json")]
    [InlineData("""{"id":"switch-3","value":{"sequence":3,"action":"pause"}}""")] // no time

    public async Task ANewestPressThatCantBeReadCountsAsStarted(string document)
    {
        var store = new InMemoryStocksStore();
        store.Press(StocksSwitch.Pause, At);
        store.Press(StocksSwitch.Resume, At.AddMinutes(1));
        store.PressJson(3, document);

        Assert.Equal(new AdvisorSwitch(false, 3, null, Unreadable: true), await StocksSwitch.CurrentAsync(store, CancellationToken.None));

        store.Press(StocksSwitch.Pause, At.AddMinutes(3)); // the next press replaces it
        Assert.Equal(new AdvisorSwitch(true, 4, At.AddMinutes(3)), await StocksSwitch.CurrentAsync(store, CancellationToken.None));
    }

    [Fact]
    public void AnOversizedDocumentIsNoSwitch()
    {
        var huge = $$$"""{"id":"switch-1","value":{"sequence":1,"action":"pause","submittedAt":"2026-11-11T16:00:00Z","pad":"{{{new string('x', 5000)}}}"}}""";
        Assert.Null(SwitchRead.Parse(System.Text.Encoding.UTF8.GetBytes(huge)).Switch);
    }

    [Fact]
    public async Task TheSearchReadsOnlyAFewDocuments()
    {
        var store = new CountingStore();
        for (var n = 1; n <= 100; n++)
            store.Inner.Press(n % 2 == 1 ? StocksSwitch.Pause : StocksSwitch.Resume, At.AddMinutes(n));

        Assert.Equal(100, (await StocksSwitch.CurrentAsync(store, CancellationToken.None)).Sequence);
        Assert.InRange(store.Reads, 1, 16);
    }

    [Fact]
    public void ABriefFromBeforeAStopOfADayOrMoreIsOutdatedByTheStart()
    {
        var brief = At.AddHours(-1);
        Assert.False(AdvisorSwitch.NeverPressed.Outdates(brief));
        Assert.False(new AdvisorSwitch(true, 1, At).Outdates(brief)); // stopped: the run doesn't get that far
        Assert.True(new AdvisorSwitch(false, 2, At.AddDays(2), StoppedAt: At).Outdates(brief));
        Assert.True(new AdvisorSwitch(false, 2, At.AddDays(1), StoppedAt: At).Outdates(brief));
        Assert.False(new AdvisorSwitch(false, 2, At.AddMinutes(5), StoppedAt: At).Outdates(brief)); // a mistaken press
        Assert.True(new AdvisorSwitch(false, 2, At.AddMinutes(5)).Outdates(brief)); // its Stop can't be read: wait
        Assert.False(new AdvisorSwitch(false, 2, At.AddDays(2), StoppedAt: At).Outdates(At.AddDays(3))); // the night's after the Start
    }

    /// <summary>Counts the switch reads; nothing else is used.</summary>
    private sealed class CountingStore : IStocksStore
    {
        public InMemoryStocksStore Inner { get; } = new();

        public int Reads { get; private set; }

        public Task<SwitchRead?> GetSwitchAsync(int sequence, CancellationToken cancellationToken)
        {
            Reads++;
            return Inner.GetSwitchAsync(sequence, cancellationToken);
        }

        public Task<CachedBars?> GetBarsAsync(string symbol, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveBarsAsync(CachedBars bars, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Versioned<PaperState>?> GetStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> SaveStateAsync(PaperState state, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddTradeAsync(string id, TradeLine trade, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddAdviceAsync(string id, AdviceDocument advice, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdviceDocument?> GetAdviceAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveLatestAdviceAsync(AdviceDocument advice, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Versioned<SearchState>?> GetSearchStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> SaveSearchStateAsync(SearchState state, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredUniverse?> GetUniverseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveUniverseAsync(StoredUniverse universe, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddCandidatesAsync(string id, CandidatesDocument candidates, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CandidatesDocument?> GetCandidatesAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveLatestCandidatesAsync(CandidatesDocument candidates, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddAnalysisAsync(string id, AnalysisDocument analysis, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AnalysisDocument?> GetAnalysisAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveLatestAnalysisAsync(AnalysisDocument analysis, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AnalystBrief?> GetBriefAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveBriefAsync(AnalystBrief brief, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Versioned<OwnerState>?> GetOwnerStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> SaveOwnerStateAsync(OwnerState state, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SubmissionRead?> GetPortfolioSubmissionAsync(int sequence, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
