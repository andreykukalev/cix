using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.MarketData;
using Cix.Domain.Risk;
using Cix.Engine.MarketData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>Simulated exchange: a candle every interval up to the current time (the last one still open), served in pages.</summary>
internal sealed class SimulatedMarket(TimeProvider time, int pageSize = 100) : IMarketDataSource, IQuoteSource
{
    public static readonly DateTimeOffset Genesis = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public List<(string Kind, DateTimeOffset At)> Requests { get; } = [];
    public bool Failing { get; set; }

    /// <summary>How far behind the clock the exchange stamps an order book (its snapshot time).</summary>
    public TimeSpan QuoteLag { get; set; }

    public Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken)
    {
        Requests.Add(($"{intervalMinutes}m", time.GetUtcNow()));
        if (Failing)
            throw new ExchangeException("Simulated outage.", 503, null);

        var interval = TimeSpan.FromMinutes(intervalMinutes);
        var start = since <= Genesis ? Genesis : Genesis + (interval * Math.Ceiling((since - Genesis) / interval));
        var candles = new List<Candle>();
        for (var t = start; t <= time.GetUtcNow() && candles.Count < pageSize; t += interval)
        {
            var price = 100m + (decimal)(t - Genesis).TotalHours;
            candles.Add(new Candle(t, price, price + 1m, price - 1m, price, 1m));
        }

        return Task.FromResult<IReadOnlyList<Candle>>(candles);
    }

    public Task<MarketQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        Requests.Add(("quote", time.GetUtcNow()));
        if (Failing)
            throw new HttpRequestException("Simulated network failure.");

        var asOf = time.GetUtcNow() - QuoteLag;
        var mid = 100m + (decimal)(asOf - Genesis).TotalHours;
        return Task.FromResult(new MarketQuote(symbol, mid - 0.05m, 1m, mid + 0.05m, 1m, asOf));
    }
}

/// <summary>Simulated tickers endpoint: every requested symbol's bid and ask (no sizes) in one request, plus a pair nobody asked for.</summary>
internal sealed class SimulatedTickers(TimeProvider time) : IBulkQuoteSource
{
    public List<(IReadOnlyCollection<string> Symbols, DateTimeOffset At)> Requests { get; } = [];
    public bool Failing { get; set; }

    /// <summary>How far behind the clock the exchange stamps the tickers.</summary>
    public TimeSpan Lag { get; set; }

    /// <summary>Symbols the exchange doesn't list.</summary>
    public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<MarketQuote>> GetQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken)
    {
        Requests.Add((symbols, time.GetUtcNow()));
        if (Failing)
            throw new HttpRequestException("Simulated network failure.");

        var asOf = time.GetUtcNow() - Lag;
        var mid = 100m + (decimal)(asOf - SimulatedMarket.Genesis).TotalHours;
        var quotes = symbols.Where(s => !Missing.Contains(s)).Select(s => new MarketQuote(s, mid - 0.05m, 0m, mid + 0.05m, 0m, asOf)).ToList();
        quotes.Add(new MarketQuote("XYZ-EUR", 1m, 0m, 1.1m, 0m, asOf));
        return Task.FromResult<IReadOnlyList<MarketQuote>>(quotes);
    }
}

internal sealed class RecordingListener : IQuoteListener
{
    public List<MarketQuote> Quotes { get; } = [];

    public void OnQuote(MarketQuote quote) => Quotes.Add(quote);
}

internal sealed class ThrowingListener : IQuoteListener
{
    public void OnQuote(MarketQuote quote) => throw new InvalidOperationException("listener bug");
}

public sealed class MarketDataPollerTests
{
    private const string Btc = "BTC-EUR";
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 12, 0, 30, TimeSpan.Zero);

    /// <summary>The v1 schedule (5-second quotes plus 1-minute candles): exercises both candle series and catch-up paging.</summary>
    private static MarketDataOptions WithMinuteCandles() =>
        new() { QuoteInterval = TimeSpan.FromSeconds(5), MinuteCandleInterval = TimeSpan.FromMinutes(1) };

    private sealed class Rig
    {
        public Rig(int pageSize = 100, MarketDataOptions? options = null, params IQuoteListener[] listeners)
        {
            Time = new MutableTime(Start);
            Market = new SimulatedMarket(Time, pageSize);
            State = new MarketState(Time);
            Options = options ?? WithMinuteCandles();
            Poller = new MarketDataPoller(Market, Market, Store, State, listeners, Options, ["btc/eur"], Time, NullLogger<MarketDataPoller>.Instance);
        }

        public MutableTime Time { get; }
        public SimulatedMarket Market { get; }
        public MarketDataOptions Options { get; }
        public InMemoryCandleStore Store { get; } = new();
        public MarketState State { get; }
        public MarketDataPoller Poller { get; }

        /// <summary>Calls the poller once a second, like the hosted service. Returns the requests made.</summary>
        public async Task<int> RunFor(TimeSpan duration)
        {
            var requests = 0;
            var end = Time.GetUtcNow() + duration;
            while (Time.GetUtcNow() < end)
            {
                requests += await Poller.PollOnceAsync(CancellationToken.None);
                Time.Advance(TimeSpan.FromSeconds(1));
            }

            return requests;
        }
    }

    [Fact]
    public async Task FirstPoll_LoadsLookback_AndStoresOnlyClosedCandles()
    {
        var rig = new Rig();

        await rig.RunFor(TimeSpan.FromSeconds(2)); // one candle series per poll: 1m, then 1h

        var now = rig.Time.GetUtcNow();
        var hourly = rig.Store.Recent(Btc, 60, 1000);
        var minutes = rig.Store.Recent(Btc, 1, 1000);
        Assert.Equal(7 * 24, hourly.Count); // 12:00 is still open
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 11, 0, 0, TimeSpan.Zero), hourly[^1].Start);
        Assert.Equal(6 * 60, minutes.Count);
        Assert.All(hourly, c => Assert.True(c.Start + TimeSpan.FromHours(1) <= now));
        Assert.All(minutes, c => Assert.True(c.Start + TimeSpan.FromMinutes(1) <= now));
        Assert.NotNull(rig.State.Quote(Btc));
    }

    [Fact]
    public async Task AfterDowntime_CatchesUpInPages_WithoutGapsOrDuplicates()
    {
        var rig = new Rig(pageSize: 50);
        await rig.Poller.PollOnceAsync(CancellationToken.None); // 360 minute candles need 8 pages: 5 now, the rest next time

        rig.Time.Advance(TimeSpan.FromHours(3)); // the engine was down
        for (var i = 0; i < 5; i++)
        {
            await rig.Poller.PollOnceAsync(CancellationToken.None);
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var minutes = rig.Store.Recent(Btc, 1, 10_000);
        Assert.Equal(minutes.Count, minutes.Select(c => c.Start).Distinct().Count());
        for (var i = 1; i < minutes.Count; i++)
            Assert.Equal(TimeSpan.FromMinutes(1), minutes[i].Start - minutes[i - 1].Start);

        var lastClosed = rig.Time.GetUtcNow().AddMinutes(-1).AddSeconds(-30) - TimeSpan.FromMinutes(1); // last poll was a minute ago
        Assert.Equal(lastClosed, minutes[^1].Start);
        var hourly = rig.Store.Recent(Btc, 60, 10_000);
        for (var i = 1; i < hourly.Count; i++)
            Assert.Equal(TimeSpan.FromHours(1), hourly[i].Start - hourly[i - 1].Start);
    }

    [Fact]
    public async Task SteadyState_StaysWithinThePublicRateBudget()
    {
        var rig = new Rig();
        await rig.RunFor(TimeSpan.FromMinutes(2)); // warm-up, including the lookback load
        rig.Market.Requests.Clear();

        var requests = await rig.RunFor(TimeSpan.FromMinutes(10));

        Assert.Equal(rig.Market.Requests.Count, requests);
        var perMinute = rig.Market.Requests.GroupBy(r => r.At.Ticks / TimeSpan.TicksPerMinute).Max(g => g.Count());
        Assert.True(perMinute <= MarketDataOptions.PublicRequestsPerMinute * rig.Options.MaxBudgetShare, $"{perMinute} requests in one minute");
        var perSecond = rig.Market.Requests.GroupBy(r => r.At).Max(g => g.Count());
        Assert.True(perSecond <= 3, $"{perSecond} requests in one second"); // the transport limiter spaces these to 1/s
        Assert.Equal(120, rig.Market.Requests.Count(r => r.Kind == "quote")); // every 5 seconds
    }

    [Fact]
    public async Task Failures_AreRetriedAtTheNextInterval_AndNeverStopPolling()
    {
        var rig = new Rig();
        rig.Market.Failing = true;

        var failed = await rig.RunFor(TimeSpan.FromSeconds(30));

        Assert.True(failed > 0);
        Assert.Null(rig.State.Quote(Btc));
        Assert.Equal(6, rig.Market.Requests.Count(r => r.Kind == "quote")); // one attempt per interval, no hot loop

        rig.Market.Failing = false;
        await rig.RunFor(TimeSpan.FromMinutes(1));

        Assert.NotNull(rig.State.Quote(Btc));
        Assert.NotEmpty(rig.Store.Recent(Btc, 1, 10));
    }

    [Fact]
    public async Task CandleCatchUp_NeverDelaysQuotesBeyondOnePoll()
    {
        var rig = new Rig(pageSize: 10); // slow catch-up: many pages per series

        await rig.RunFor(TimeSpan.FromMinutes(1));

        var quotes = rig.Market.Requests.Where(r => r.Kind == "quote").Select(r => r.At).ToList();
        Assert.Equal(12, quotes.Count);
        Assert.All(quotes.Zip(quotes.Skip(1)), p => Assert.Equal(TimeSpan.FromSeconds(5), p.Second - p.First));
        Assert.True(rig.Market.Requests.GroupBy(r => r.At).Max(g => g.Count(r => r.Kind != "quote")) <= rig.Options.MaxCatchUpPages);
    }

    private static MarketDataOptions LiveSchedule() =>
        new() { QuoteInterval = TimeSpan.FromSeconds(20), TickerInterval = TimeSpan.FromSeconds(2), HourCandleInterval = TimeSpan.FromMinutes(5) };

    /// <summary>Polls once a second for <paramref name="duration"/>, like the hosted service.</summary>
    private static async Task PollFor(MarketDataPoller poller, MutableTime time, TimeSpan duration)
    {
        var end = time.GetUtcNow() + duration;
        while (time.GetUtcNow() < end)
        {
            await poller.PollOnceAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task Tickers_RefreshEverySymbolInOneRequestEveryTwoSeconds_WhileOrderBooksKeepTheirSchedule()
    {
        var rig = new Rig(options: LiveSchedule());
        var tickers = new SimulatedTickers(rig.Time);
        var listener = new RecordingListener();
        var poller = new MarketDataPoller(rig.Market, rig.Market, rig.Store, rig.State, [listener], rig.Options, ["BTC-EUR", "ETH-EUR"], rig.Time, NullLogger<MarketDataPoller>.Instance, tickers, signedQuotes: true);

        await PollFor(poller, rig.Time, TimeSpan.FromSeconds(20));

        Assert.Equal(10, tickers.Requests.Count); // one request for both symbols every 2 s
        Assert.All(tickers.Requests, r => Assert.Equal(new[] { "BTC-EUR", "ETH-EUR" }, r.Symbols.Order(StringComparer.Ordinal).ToArray()));
        Assert.Equal(2, rig.Market.Requests.Count(r => r.Kind == "quote")); // the order books still once per 20 s per symbol
        var btc = rig.State.Quote(Btc)!;
        Assert.Equal((Start + TimeSpan.FromSeconds(18), 0m), (btc.AsOf, btc.BidSize)); // the latest ticker, which has no size
        Assert.Null(rig.State.Quote("XYZ-EUR")); // a pair outside the universe is dropped
        Assert.Equal(22, listener.Quotes.Count); // 2 order books + 10 tickers × 2 symbols
        Assert.DoesNotContain(listener.Quotes, q => q.Symbol == "XYZ-EUR");
    }

    [Fact]
    public async Task Tickers_Failing_LeaveTheOrderBookQuotesRunning_AndALaggingTicker_NeverReplacesANewerBook()
    {
        var rig = new Rig(options: LiveSchedule());
        var tickers = new SimulatedTickers(rig.Time) { Failing = true };
        var poller = new MarketDataPoller(rig.Market, rig.Market, rig.Store, rig.State, [], rig.Options, [Btc], rig.Time, NullLogger<MarketDataPoller>.Instance, tickers, signedQuotes: true);

        await poller.PollOnceAsync(CancellationToken.None);
        var fromBook = rig.State.Quote(Btc)!;

        tickers.Failing = false;
        tickers.Lag = TimeSpan.FromSeconds(30); // the exchange's ticker snapshot is older than the book we just read
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await poller.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1m, fromBook.BidSize); // the order book came through the failed tickers request
        Assert.Equal(fromBook, rig.State.Quote(Btc)); // the lagging ticker was rejected as older
        Assert.Equal(2, tickers.Requests.Count); // tried again on schedule despite the failure

        tickers.Lag = TimeSpan.Zero;
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(0m, rig.State.Quote(Btc)!.BidSize); // a fresh ticker replaces it
    }

    [Fact]
    public async Task ARefreshedBook_StampedBeforeTheLatestTicker_IsStillReturned_SoAStopCanBeConfirmed()
    {
        var rig = new Rig(options: LiveSchedule());
        var tickers = new SimulatedTickers(rig.Time);
        var listener = new RecordingListener();
        var paper = new RecordingListener();
        var poller = new MarketDataPoller(rig.Market, rig.Market, rig.Store, rig.State, [listener, paper], rig.Options, [Btc], rig.Time, NullLogger<MarketDataPoller>.Instance, tickers, signedQuotes: true);
        rig.Market.QuoteLag = TimeSpan.FromMilliseconds(500); // the exchange stamps the book snapshot a little before the ticker capture

        await poller.PollOnceAsync(CancellationToken.None); // ticker at T, then the scheduled book at T - 0.5 s
        var heard = listener.Quotes.Count;
        var paperHeard = paper.Quotes.Count;
        var book = await poller.RefreshQuoteAsync(Btc, CancellationToken.None, except: listener);

        Assert.NotNull(book); // the protection gets the book it asked for, whatever the two endpoints' stamps say
        Assert.Equal(1m, book.BidSize);
        Assert.Equal(Start - TimeSpan.FromMilliseconds(500), book.AsOf);
        Assert.Equal(0m, rig.State.Quote(Btc)!.BidSize); // the state keeps the later-stamped ticker for the risk gate
        Assert.Equal(heard, listener.Quotes.Count); // the protection that asked reads the result itself and isn't told again

        // The other listeners (the paper exchange) still get the book: it fills against books only, so it must never be left
        // on an older one because a size-less ticker carries a later stamp.
        Assert.Equal(paperHeard + 1, paper.Quotes.Count);
        Assert.Equal(1m, paper.Quotes[^1].BidSize);
        Assert.Equal(book.AsOf, paper.Quotes[^1].AsOf);
    }

    [Fact]
    public async Task ARefreshedBook_FromTheFuture_IsStillRefused()
    {
        var rig = new Rig(options: LiveSchedule());
        var poller = new MarketDataPoller(rig.Market, rig.Market, rig.Store, rig.State, [], rig.Options, [Btc], rig.Time, NullLogger<MarketDataPoller>.Instance);
        rig.Market.QuoteLag = TimeSpan.FromSeconds(-5);

        Assert.Null(await poller.RefreshQuoteAsync(Btc, CancellationToken.None));
        Assert.Null(rig.State.Quote(Btc));
    }

    [Fact]
    public void TickerInterval_OutsideOneSecondToOneMinute_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => new MarketDataOptions { TickerInterval = TimeSpan.FromMilliseconds(500) }.Validate(1));
        Assert.Throws<InvalidOperationException>(() => new MarketDataOptions { TickerInterval = TimeSpan.FromMinutes(2) }.Validate(1));
        new MarketDataOptions { TickerInterval = null }.Validate(1); // off is allowed
    }

    [Fact]
    public async Task PaperTickers_RunEveryFiveSeconds_AndOrderBooksGoOnlyToTheActiveCoins()
    {
        var rig = new Rig(options: LiveSchedule());
        var tickers = new SimulatedTickers(rig.Time);
        var active = new HashSet<string>(StringComparer.Ordinal) { "BTC-EUR" }; // held, or an order in it
        var poller = new MarketDataPoller(rig.Market, rig.Market, rig.Store, rig.State, [], rig.Options, ["BTC-EUR", "ETH-EUR"], rig.Time,
            NullLogger<MarketDataPoller>.Instance, tickers, signedQuotes: false, active: () => active);

        await PollFor(poller, rig.Time, TimeSpan.FromSeconds(40));

        Assert.Equal(8, tickers.Requests.Count); // the public tickers every 5 s, both symbols in each
        Assert.Equal(2, rig.Market.Requests.Count(r => r.Kind == "quote")); // BTC's book at 0 s and 20 s; ETH never
        Assert.Equal(new[] { "BTC-EUR" }, poller.BookSymbols());
        Assert.Equal(0m, rig.State.Quote("ETH-EUR")!.BidSize); // ETH is priced from the tickers alone
        Assert.NotNull(rig.State.Quote("ETH-EUR"));

        active.Add("ETH-EUR"); // bought: its book is wanted from the next poll
        await poller.PollOnceAsync(CancellationToken.None); // at 40 s: BTC's book is due again, and ETH's first one
        Assert.Equal(4, rig.Market.Requests.Count(r => r.Kind == "quote"));
        Assert.Equal(1m, rig.State.Quote("ETH-EUR")!.BidSize);
    }

    [Fact]
    public async Task ACoinTheTickersLeaveOut_KeepsItsOrderBook_EvenWhenNobodyHoldsIt()
    {
        var rig = new Rig(options: LiveSchedule());
        var tickers = new SimulatedTickers(rig.Time);
        tickers.Missing.Add("ETH-EUR");
        var poller = new MarketDataPoller(rig.Market, rig.Market, rig.Store, rig.State, [], rig.Options, ["BTC-EUR", "ETH-EUR"], rig.Time,
            NullLogger<MarketDataPoller>.Instance, tickers, signedQuotes: false, active: () => new HashSet<string>());

        await poller.PollOnceAsync(CancellationToken.None); // the first tickers answer says which coins it leaves out
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await poller.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "ETH-EUR" }, poller.BookSymbols());
        Assert.Equal(1, rig.Market.Requests.Count(r => r.Kind == "quote"));
        Assert.Equal(1m, rig.State.Quote("ETH-EUR")!.BidSize); // its only price is the book
        Assert.Equal(0m, rig.State.Quote("BTC-EUR")!.BidSize);
    }

    [Fact]
    public async Task NothingWatched_PollsNothing()
    {
        var rig = new Rig(options: LiveSchedule());
        var tickers = new SimulatedTickers(rig.Time);
        var symbols = new List<string>();
        var poller = new MarketDataPoller(rig.Market, rig.Market, rig.Store, rig.State, [], rig.Options, () => symbols, maxSymbols: 50, rig.Time,
            NullLogger<MarketDataPoller>.Instance, tickers, signedQuotes: false, active: () => new HashSet<string>());

        Assert.Equal(0, await poller.PollOnceAsync(CancellationToken.None));
        Assert.Empty(tickers.Requests);
        Assert.Empty(rig.Market.Requests);

        symbols.Add("BTC-EUR"); // the owner added the first coin
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await poller.PollOnceAsync(CancellationToken.None) > 0);
        Assert.Single(tickers.Requests);
    }

    [Fact]
    public async Task RefreshQuote_UpdatesTheStateAndTheOtherListeners_ButNotTheOneThatAsked()
    {
        var asking = new RecordingListener();
        var other = new RecordingListener();
        var rig = new Rig(listeners: [asking, other]);

        var quote = await rig.Poller.RefreshQuoteAsync(Btc, CancellationToken.None, except: asking);

        Assert.NotNull(quote);
        Assert.Equal(quote, rig.State.Quote(Btc));
        Assert.Equal([quote], other.Quotes);
        Assert.Empty(asking.Quotes); // the protection's own confirming fetch mustn't raise the stop it is already selling
    }

    [Fact]
    public async Task FailureMidPaging_CountsEveryRequest_AndResumesWithoutGaps()
    {
        var rig = new Rig(pageSize: 50);
        var market = new FailingAfter(rig.Market, successes: 3);
        var poller = new MarketDataPoller(market, rig.Market, rig.Store, rig.State, [], rig.Options, [Btc], rig.Time, NullLogger<MarketDataPoller>.Instance);

        var requests = await poller.PollOnceAsync(CancellationToken.None);

        Assert.Equal(5, requests); // 1 quote + 3 good pages + the failed one
        Assert.Equal(150, rig.Store.Recent(Btc, 1, 1000).Count);

        market.Recover();
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await poller.PollOnceAsync(CancellationToken.None); // 1h first load (never ran)
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await poller.PollOnceAsync(CancellationToken.None); // 1m resumes from the last stored candle

        var minutes = rig.Store.Recent(Btc, 1, 1000);
        for (var i = 1; i < minutes.Count; i++)
            Assert.Equal(TimeSpan.FromMinutes(1), minutes[i].Start - minutes[i - 1].Start);
        Assert.Equal(361, minutes.Count); // 06:00 to 12:00, the 12:01 candle still open
    }

    private sealed class FailingAfter(IMarketDataSource inner, int successes) : IMarketDataSource
    {
        private int _left = successes;

        public void Recover() => _left = int.MaxValue;

        public Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken) => inner.GetPairsAsync(cancellationToken);

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken) =>
            _left-- > 0 ? inner.GetCandlesAsync(symbol, intervalMinutes, since, cancellationToken) : throw new HttpRequestException("Connection reset.");
    }

    [Fact]
    public async Task FeatureService_ServesFeaturesForAnySymbolSpelling()
    {
        var rig = new Rig();
        await rig.RunFor(TimeSpan.FromSeconds(2));
        var service = new FeatureService(rig.Store, rig.State, rig.Options, rig.Time);

        var features = service.GetFeatures("btc/eur");

        Assert.Equal(Btc, features.Symbol);
        Assert.Equal(FeatureService.HourlyCandlesNeeded, features.HourlyCandles);
        Assert.False(features.IsStale);
        Assert.Equal(Trend.Up, features.Trend);
        Assert.Equal(0, service.GetFeatures("DOGE-EUR").HourlyCandles);
    }

    [Fact]
    public async Task Quotes_ReachListeners_EvenWhenAnotherListenerThrows()
    {
        var listener = new RecordingListener();
        var rig = new Rig(100, null, new ThrowingListener(), listener);

        await rig.RunFor(TimeSpan.FromSeconds(11));

        Assert.Equal(3, listener.Quotes.Count);
        Assert.All(listener.Quotes, q => Assert.Equal(Btc, q.Symbol));
    }

    [Fact]
    public async Task OutageLongerThanTheRiskLimit_MakesTheGateRejectOrders()
    {
        var rig = new Rig();
        await rig.Poller.PollOnceAsync(CancellationToken.None);
        rig.Market.Failing = true;
        await rig.RunFor(TimeSpan.FromMinutes(6));

        var market = rig.State.Snapshot(Btc);
        var gate = new RiskGate(
            new RiskLimits(200m, 40m, 100m, 15m, 0.15m, 4, 0.01m, new HashSet<string> { Btc }),
            rig.Time);
        var portfolio = new PortfolioSnapshot(200m, 200m, 0m, 0m, new Dictionary<string, decimal>(), 0, false);
        var verdict = gate.Evaluate(new OrderIntent(Btc, OrderSide.Buy, 0.1m, market.ReferencePrice, "c-1"), portfolio, market);

        Assert.Equal(RiskOutcome.Rejected, verdict.Outcome);
        Assert.Equal("stale-market-data", verdict.Rule);
    }

    [Fact]
    public void NoQuoteYet_SnapshotHasNoPrice()
    {
        var snapshot = new MarketState(TimeProvider.System).Snapshot("btc-eur");

        Assert.Equal(Btc, snapshot.Symbol);
        Assert.Equal(0m, snapshot.ReferencePrice);
    }

    [Fact]
    public void AScheduleThatCannotFitTheCap_FailsAtStartup()
    {
        var time = new MutableTime(Start);
        var market = new SimulatedMarket(time);
        var options = new MarketDataOptions { HourCandleInterval = TimeSpan.FromMinutes(1) }; // 50 coins' candles alone are 50 a minute

        Assert.Throws<InvalidOperationException>(() => new MarketDataPoller(
            market, market, new InMemoryCandleStore(), new MarketState(time), [], options, () => ["BTC-EUR"], maxSymbols: 50, time, NullLogger<MarketDataPoller>.Instance));
        options.Validate(20); // 20 + 12 tickers + 6 (one book) = 38
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    public void Options_BudgetShareOutOfRange_Throws(double share) =>
        Assert.Throws<InvalidOperationException>(() => new MarketDataOptions { MaxBudgetShare = (decimal)share }.Validate(1));

    [Fact]
    public void DefaultSchedule_FitsFiftyWatchedCoins()
    {
        var options = new MarketDataOptions();
        Assert.Equal(10m, options.CandleRequestsPerMinute(50));                             // 0.2 hourly-candle polls per coin
        Assert.Equal(28m, options.PlannedPublicRequestsPerMinute(50, books: 1, signedQuotes: false)); // + 12 tickers + one 10-second book
        options.Validate(50);
        options.Validate(50, signedQuotes: true);                                           // live: 30 tickers + 50 × 6 books = 330 of 800
    }

    [Fact]
    public void ShippedSettings_PollBooksEveryTwentySeconds_AndFitFiftyCoins_StretchingTheBooksWhenManyAreActive()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Cix.slnx")))
            root = root.Parent ?? throw new InvalidOperationException("Repository root not found.");
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root.FullName, "src", "Cix.Engine", "appsettings.json"))
            .Build();
        var options = new MarketDataOptions();
        config.GetSection(MarketDataOptions.SectionName).Bind(options);

        Assert.Equal(TimeSpan.FromSeconds(20), options.QuoteInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), options.TickerInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.PublicTickerInterval);
        Assert.Equal(25m, options.PlannedPublicRequestsPerMinute(50, books: 1, signedQuotes: false)); // 10 candles + 12 tickers + 3
        options.Validate(50);
        options.Validate(50, signedQuotes: true);
        // Paper leaves 48 - 10 - 12 = 26 books a minute: 8 active coins keep 20 s, 13 get 30 s, 26 get a minute.
        Assert.Equal(TimeSpan.FromSeconds(20), options.BookInterval(50, active: 8, signedQuotes: false));
        Assert.Equal(TimeSpan.FromSeconds(30), options.BookInterval(50, active: 13, signedQuotes: false));
        Assert.Equal(TimeSpan.FromSeconds(60), options.BookInterval(50, active: 26, signedQuotes: false));
        Assert.Equal(TimeSpan.FromSeconds(20), options.BookInterval(50, active: 0, signedQuotes: false));
        Assert.Equal(TimeSpan.FromSeconds(20), options.BookInterval(50, active: 50, signedQuotes: true)); // live: 150 + 30 of 800
    }

    [Fact]
    public void Options_MinuteCandlesCountTowardsTheBudget() =>
        Assert.Equal(3.6m, WithMinuteCandles().CandleRequestsPerMinute(3)); // 1 + 0.2 per coin

    [Fact]
    public async Task DefaultSchedule_FetchesNoMinuteCandles_AndQuotesEveryTenSeconds()
    {
        var rig = new Rig(options: new MarketDataOptions());

        await rig.RunFor(TimeSpan.FromMinutes(1));

        Assert.Empty(rig.Store.Recent(Btc, 1, 10));
        Assert.Equal(7 * 24, rig.Store.Recent(Btc, 60, 1000).Count);
        Assert.DoesNotContain(rig.Market.Requests, r => r.Kind == "1m");
        Assert.Equal(6, rig.Market.Requests.Count(r => r.Kind == "quote"));
    }
}

public sealed class MarketStateAndStoreTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Update_IgnoresAnOlderQuote()
    {
        var state = new MarketState(new MutableTime(T));
        Assert.True(state.Update(new MarketQuote("BTC-EUR", 100m, 1m, 102m, 1m, T)));
        Assert.False(state.Update(new MarketQuote("BTC-EUR", 90m, 1m, 92m, 1m, T.AddSeconds(-5))));

        Assert.Equal(new MarketSnapshot("BTC-EUR", 101m, T), state.Snapshot("btc-eur"));
    }

    public static TheoryData<decimal, decimal, decimal> InvalidQuotes => new()
    {
        { 0m, 102m, 1m },     // zero bid would halve the mid
        { -1m, 102m, 1m },    // negative price
        { 103m, 102m, 1m },   // crossed book
        { 100m, 102m, -1m },  // negative size
    };

    [Theory]
    [MemberData(nameof(InvalidQuotes))]
    public void Update_RejectsInvalidQuotes_AndKeepsTheLastValidOne(decimal bid, decimal ask, decimal size)
    {
        var state = new MarketState(new MutableTime(T.AddSeconds(10)));
        state.Update(new MarketQuote("BTC-EUR", 100m, 1m, 102m, 1m, T));

        Assert.False(state.Update(new MarketQuote("BTC-EUR", bid, size, ask, 1m, T.AddSeconds(5))));
        Assert.Equal(new MarketSnapshot("BTC-EUR", 101m, T), state.Snapshot("BTC-EUR"));
    }

    [Fact]
    public void Update_RejectsAQuoteFromTheFuture_SoItCannotLookFreshOrBlockRealUpdates()
    {
        var state = new MarketState(new MutableTime(T));

        Assert.False(state.Update(new MarketQuote("BTC-EUR", 100m, 1m, 102m, 1m, T.AddMinutes(10))));
        Assert.True(state.Update(new MarketQuote("BTC-EUR", 100m, 1m, 102m, 1m, T)));
    }

    [Fact]
    public void CrossedBookFromTheSource_NeverReachesTheGateOrListeners()
    {
        var time = new MutableTime(T);
        var listener = new RecordingListener();
        var state = new MarketState(time);
        var poller = new MarketDataPoller(
            new FakeMarketData(), new FixedQuote(new MarketQuote("BTC-EUR", 0m, 1m, 100_000m, 1m, T)), new InMemoryCandleStore(), state,
            [listener], new MarketDataOptions(), ["BTC-EUR"], time, NullLogger<MarketDataPoller>.Instance);

        _ = poller.PollOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(0m, state.Snapshot("BTC-EUR").ReferencePrice);
        Assert.Empty(listener.Quotes);
    }

    private sealed class FixedQuote(MarketQuote quote) : IQuoteSource
    {
        public Task<MarketQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken) => Task.FromResult(quote);
    }

    [Fact]
    public void Upsert_DeduplicatesByStart_AndTrimsOldest()
    {
        var store = new InMemoryCandleStore(maxPerSeries: 3);
        Candle C(int i) => new(T.AddMinutes(i), 1m, 1m, 1m, 1m, 1m);

        Assert.Equal(2, store.Upsert("btc-eur", 1, [C(0), C(1)]));
        Assert.Equal(1, store.Upsert("BTC-EUR", 1, [C(1), C(2)]));
        Assert.Equal(1, store.Upsert("BTC-EUR", 1, [C(3)]));

        Assert.Equal(new[] { C(1), C(2), C(3) }, store.Recent("BTC-EUR", 1, 10));
        Assert.Equal(C(3), store.Latest("BTC-EUR", 1));
        Assert.Null(store.Latest("BTC-EUR", 60));
    }
}
