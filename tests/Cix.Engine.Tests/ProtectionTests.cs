using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.Paper;
using Cix.Engine.Persistence;
using Cix.Engine.Scanning;
using Cix.Engine.Status;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>Market data for several pairs, all with BTC-EUR's rules.</summary>
internal sealed class MultiPairMarketData(IReadOnlyList<string> symbols) : IMarketDataSource
{
    public Task<IReadOnlyList<PairInfo>> GetPairsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PairInfo>>(symbols
            .Select(s => new PairInfo(s, s.Split('-')[0], s.Split('-')[1], 0.00000001m, 0.01m, 0.00000001m, 200m, 0.1m, Active: true))
            .ToList());

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Candle>>([]);
}

internal sealed class FakeLease : IEngineLease
{
    public bool IsHeld { get; set; } = true;

    public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(IsHeld);

    public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>An engine state store whose writes can be made to fail, like a database outage.</summary>
internal sealed class FailingStateStore : IEngineStateStore
{
    private readonly InMemoryEngineStateStore _inner = new();

    public bool FailWrites { get; set; }

    public EngineState Current => _inner.Current;

    public EngineState Update(Func<EngineState, EngineState> change) =>
        FailWrites ? throw new IOException("The state could not be written.") : _inner.Update(change);
}

/// <summary>
/// A <see cref="ProtectionService"/> on a <see cref="Rig"/>, with the fresh order book under the test's control and delays that
/// move the clock instead of waiting. The default fresh book is the engine's latest quote stamped now, fed to the paper exchange
/// too, as the poller would.
/// </summary>
internal sealed class ProtectionRig
{
    public const string Btc = Rig.Btc;

    private int _books;

    public ProtectionRig(Rig rig, ProtectionOptions? options = null)
    {
        Rig = rig;
        Options = options ?? new ProtectionOptions();
        Trigger = new ProtectionTrigger(rig.Time);
        Service = new ProtectionService(
            rig.Execution, Trigger, rig.State, FreshQuoteAsync, rig.Journal, rig.Tradable?.Limits ?? Rig.Limits, Options, rig.Time,
            NullLogger<ProtectionService>.Instance, delay: (d, _) => { rig.Time.Advance(d); return Task.CompletedTask; }, strategy: () => "safe.v2");
    }

    public Rig Rig { get; }
    public ProtectionOptions Options { get; }
    public ProtectionTrigger Trigger { get; }
    public ProtectionService Service { get; }
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>The fresh book by symbol and fetch number (1-based, across all symbols); null leaves the default.</summary>
    public Func<string, int, MarketQuote?>? Book { get; set; }

    /// <summary>Fetching a fresh book fails, like an exchange outage.</summary>
    public bool FreshFails { get; set; }

    /// <summary>How many fresh books were fetched.</summary>
    public int Books => _books;

    public MarketQuote Normal(string symbol) => new(symbol, 60_000m, 0.01m, 60_010m, 0.01m, Rig.Time.GetUtcNow());

    /// <summary>A book whose bid is far below the mid: deeper than any emergency band allows.</summary>
    public MarketQuote Wide(string symbol) => new(symbol, 58_000m, 0.01m, 62_000m, 0.01m, Rig.Time.GetUtcNow());

    /// <summary>A quote to the engine's market state and the paper exchange, as the poller delivers one.</summary>
    public void Feed(MarketQuote quote)
    {
        Rig.State.Update(quote);
        Rig.Paper.OnQuote(quote);
    }

    private Task<MarketQuote?> FreshQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _books);
        var scripted = Book?.Invoke(symbol, n); // always called, so a test can act on each fetch even while fetches fail
        if (FreshFails)
            throw new ExchangeException("Service unavailable.", 503);
        var quote = scripted ?? (Rig.State.Quote(symbol) is { } latest ? latest with { AsOf = Rig.Time.GetUtcNow() } : null);
        if (quote is not null)
            Feed(quote);
        return Task.FromResult(quote);
    }

    /// <summary>A held position's plan in the engine state, as a filled buy leaves it.</summary>
    public void Plan(string symbol, decimal stop, decimal target = 70_000m) =>
        Rig.Store.Update(s =>
        {
            var plans = s.Plans.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            plans[symbol] = new PositionPlanState(symbol, PositionPlanState.Open, "c-entry-" + symbol, stop, target, Rig.T0.AddHours(-2), 62_000m, Rig.T0.AddHours(-2));
            return s with { Positions = plans };
        });

    public StopHit Hit(string symbol, decimal stop) => new(symbol, stop, Rig.State.Quote(symbol)!, Rig.Time.GetUtcNow());

    /// <summary>Runs one coin's exit to its end, with its stop armed.</summary>
    public Task<ExitOutcome> ExitAsync(string symbol, decimal stop)
    {
        if (Trigger.StopFor(symbol) is null)
            Trigger.Arm(new Dictionary<string, decimal>(Trigger.Armed, StringComparer.Ordinal) { [symbol] = stop });
        return Service.RunExitAsync(Hit(symbol, stop), Cts.Token);
    }

    /// <summary>The journal's entries for the orders the exits sent (one per try).</summary>
    public IReadOnlyList<JournalEntry> Orders(string symbol = Btc) =>
        Rig.Journal.Entries.Where(e => e.Kind == "order" && e.Data?.GetValueOrDefault("tool") == ProtectionService.Source && e.Data["symbol"] == symbol).ToList();

    /// <summary>The journal's entry for how the coin's exit ended, or null.</summary>
    public JournalEntry? Ended(string symbol = Btc) =>
        Rig.Journal.Entries.LastOrDefault(e => e.Kind == ProtectionService.Source && e.Data?.GetValueOrDefault("symbol") == symbol);

    public async Task<decimal> Held(string symbol = Btc)
    {
        var balances = await Rig.Paper.GetBalancesAsync(CancellationToken.None);
        return balances.Where(b => b.Currency == symbol.Split('-')[0]).Sum(b => b.Total);
    }

    public static decimal D(string? value) => decimal.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The engine's stop-loss exits run on every quote, independently of the strategy, the scan and the agents, and no halt stops
/// them: Andrei's crash-protection brief (thread "Build crash protection independent of strategy").
/// </summary>
public sealed class ProtectionTests
{
    private const string Btc = Rig.Btc;

    [Fact]
    public async Task AStopHit_IsConfirmedOnAFreshBook_AndSoldAtOnce_WithItsLatenciesJournaled()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        p.Plan(Btc, stop: 61_000m); // the bid, 60,000, is already below it

        var started = await p.Service.RefreshAsync(CancellationToken.None);
        await p.Service.WhenIdleAsync();

        Assert.Equal(1, started);
        Assert.Equal(0m, await p.Held());
        var order = Assert.Single(p.Orders());
        Assert.Equal(("placed", 0.001m, "1"), (order.Data!["status"], ProtectionRig.D(order.Data["filledSize"]), order.Data["attempt"]));
        Assert.Equal(60_000m, ProtectionRig.D(order.Data["averagePrice"])); // the bid: the limit sits lower, the exchange fills at the best bid
        Assert.Equal(Math.Round((61_000m - 60_000m) / 61_000m, 6), ProtectionRig.D(order.Data["slippageVsStop"]));
        Assert.All(new[] { "detectionMs", "confirmMs", "submitMs", "fillMs", "sinceHitMs" }, key => Assert.True(order.Data.ContainsKey(key), key));
        Assert.Equal("safe.v2", order.SpecVersion);
        var ended = p.Ended()!;
        Assert.Equal(("exited", "1", 0.001m), (ended.Data!["outcome"], ended.Data["tries"], ProtectionRig.D(ended.Data["soldSize"])));
        Assert.Empty(p.Service.Blocked);
        Assert.Empty(p.Service.Exiting);
    }

    [Fact]
    public async Task TheSellIsPricedAtTheEmergencyBandBelowTheMid_NotAtTheBid()
    {
        var limits = Rig.Limits with { MaxEmergencySlippageFraction = 0.03m };
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m, tradable: new TradableSymbols(limits)));

        await p.ExitAsync(Btc, stop: 61_000m);

        // 3% under the mid (60,005), rounded up to the cent: a limit the fresh bids are well above, so it fills at them.
        Assert.Equal(58_204.85m, ProtectionRig.D(Assert.Single(p.Orders()).Data!["requestedPrice"]));
        Assert.Equal(0m, await p.Held());
    }

    [Fact]
    public async Task AWick_TheFreshBookDoesNotConfirm_SellsNothing()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        p.Book = (s, _) => new MarketQuote(s, 61_500m, 0.01m, 61_510m, 0.01m, p.Rig.Time.GetUtcNow()); // back above the stop

        var outcome = await p.ExitAsync(Btc, stop: 61_000m); // the hit itself saw the bid at 60,000

        Assert.Equal(ExitOutcome.NotConfirmed, outcome);
        Assert.Empty(p.Orders());
        Assert.Equal(0.001m, await p.Held());
        Assert.Equal("notconfirmed", p.Ended()!.Data!["outcome"]);
    }

    [Fact]
    public async Task PartialFills_AreSoldAgainOnANewBook_UntilNothingIsLeft()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.025m)); // the bid shows 0.01

        var outcome = await p.ExitAsync(Btc, stop: 61_000m);

        Assert.Equal(ExitOutcome.Exited, outcome);
        Assert.Equal(0m, await p.Held());
        var orders = p.Orders();
        Assert.Equal(new[] { 0.01m, 0.01m, 0.005m }, orders.Select(o => ProtectionRig.D(o.Data!["filledSize"])).ToArray());
        Assert.All(orders, o => Assert.Equal("placed", o.Data!["status"]));
        Assert.All(orders.Zip(orders.Skip(1)), pair => Assert.True(pair.Second.At - pair.First.At >= p.Options.RetryDelay)); // one try per coin every 2 s
        Assert.Equal(("exited", "3", 0.025m), (p.Ended()!.Data!["outcome"], p.Ended()!.Data!["tries"], ProtectionRig.D(p.Ended()!.Data!["soldSize"])));
        Assert.Empty(p.Service.Blocked);
    }

    [Fact]
    public async Task AnExchangeThatLetsTheRestOfAnIocOrderRest_HasItCancelledBeforeTheNextTry_AndNothingStaysOpen()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.025m)); // the bid shows 0.01
        p.Rig.Paper.HonoursImmediateOrCancel = false;

        var outcome = await p.ExitAsync(Btc, stop: 61_000m);

        Assert.Equal(ExitOutcome.Exited, outcome);
        Assert.Equal(0m, await p.Held());
        Assert.Empty(await p.Rig.Paper.GetOpenOrdersAsync(Btc, CancellationToken.None)); // nothing rests behind a "finished" exit
        // The first sell fills 0.01 and its rest of 0.015 is left open. The next fresh book fills 0.01 of that rest at the bid (as a
        // real exchange would), the sweep before the second try cancels the remaining 0.005, and the second sell finds the level
        // taken; its rest fills on the third book and the third sweep leaves nothing, so the exit ends with the position gone.
        var orders = p.Orders();
        Assert.Equal(new[] { 0.01m, 0m }, orders.Select(o => ProtectionRig.D(o.Data!["filledSize"])).ToArray());
        Assert.All(orders, o => Assert.Equal("ioc-rested", o.Data!["problem"]));
        Assert.Equal(("exited", "2"), (p.Ended()!.Data!["outcome"], p.Ended()!.Data!["tries"]));
        Assert.Empty(p.Service.Blocked);
    }

    [Fact]
    public async Task AGapBelowTheBand_BlocksTheExitAfterFiveTries_WhichSellsByItselfWhenTheBookComesBack()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        IReadOnlyList<BlockedExit>? whileBlocked = null;
        p.Book = (s, n) =>
        {
            if (n == 6)
                whileBlocked = p.Service.Blocked;
            return n <= 5 ? p.Wide(s) : p.Normal(s);
        };
        var startedAt = p.Rig.Time.GetUtcNow();

        var outcome = await p.ExitAsync(Btc, stop: 61_000m);

        Assert.Equal(ExitOutcome.Exited, outcome);
        var blocked = Assert.Single(whileBlocked!);
        Assert.Equal(("BTC-EUR", "spread-too-wide", 5), (blocked.Symbol, blocked.Problem, blocked.Attempts));
        var entry = Assert.Single(p.Rig.Journal.Entries, e => e.Kind == "exit-blocked");
        Assert.Equal(("spread-too-wide", "5", "0"), (entry.Data!["problem"], entry.Data["attempts"], entry.Data["ordersSent"]));
        Assert.Single(p.Orders()); // nothing was sent into the wide book; one sell once it closed
        Assert.Equal(0m, await p.Held());
        Assert.Empty(p.Service.Blocked); // cleared by the sell, with no Resume
        Assert.True(p.Rig.Time.GetUtcNow() - startedAt >= 4 * p.Options.RetryDelay + p.Options.BlockedRetryInterval); // quick tries, then a minute
    }

    [Fact]
    public async Task TheOwnersPause_DoesNotStopTheStopLoss_ButStillBlocksBuys()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        await p.Rig.Execution.EngageKillSwitchAsync("Paused by the owner from the dashboard (request p-1).", "owner", CancellationToken.None);
        p.Plan(Btc, stop: 61_000m);

        Assert.Equal(1, await p.Service.RefreshAsync(CancellationToken.None));
        await p.Service.WhenIdleAsync();

        Assert.Equal(0m, await p.Held());
        Assert.Equal("placed", Assert.Single(p.Orders()).Data!["status"]);
        Assert.True(p.Rig.KillSwitch.IsEngaged); // the pause stays
        Assert.Equal("kill-switch", (await p.Rig.Buy(0.0001m, 59_950m)).Rule);
    }

    [Fact]
    public async Task TheCriticalDrawdownState_StopsEverythingNew_ButTheStopLossesKeepSelling()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        p.Rig.Store.Update(s => s with { PeakEquity = 400m }); // 35% down: the 30% hard ceiling
        var check = await p.Rig.Monitor.CheckOnceAsync(CancellationToken.None);
        p.Plan(Btc, stop: 61_000m);

        await p.Service.RefreshAsync(CancellationToken.None);
        await p.Service.WhenIdleAsync();

        Assert.StartsWith("Critical", check.KillSwitchReason, StringComparison.Ordinal);
        Assert.Equal(SafetyMonitor.CriticalBy, p.Rig.KillSwitch.State.By);
        Assert.Equal(0m, await p.Held());
        Assert.Equal("kill-switch", (await p.Rig.Buy(0.0001m, 59_950m)).Rule);
        Assert.Single(p.Orders()); // only the position at its stop went: no sell-off
    }

    [Fact]
    public async Task StaleOrInvalidQuotes_NeverStartASell()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        p.Plan(Btc, stop: 61_000m);
        p.Rig.Time.Advance(TimeSpan.FromMinutes(2)); // the only quote is 2 minutes old

        Assert.Equal(0, await p.Service.RefreshAsync(CancellationToken.None)); // armed, but a stale quote starts nothing
        Assert.Equal(61_000m, p.Trigger.StopFor(Btc));

        p.Trigger.OnQuote(new MarketQuote(Btc, 60_000m, 0.01m, 59_000m, 0.01m, p.Rig.Time.GetUtcNow())); // crossed book: invalid
        Assert.False(p.Trigger.Hits.TryRead(out _));

        // A stale fresh book never prices the sell either: the try counts as failed and the next fresh book sells.
        p.Book = (s, n) => n == 1 ? p.Normal(s) with { AsOf = p.Rig.Time.GetUtcNow().AddMinutes(-2) } : p.Normal(s);
        var outcome = await p.ExitAsync(Btc, stop: 61_000m);

        Assert.Equal(ExitOutcome.Exited, outcome);
        Assert.Single(p.Orders());
        Assert.Equal(3, p.Books); // the stale one, the one that sold, and the one that showed nothing left
    }

    [Fact]
    public async Task ARepeatedHit_StartsOneExit_SoACoinIsNeverSoldTwice()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        p.Trigger.Arm(new Dictionary<string, decimal> { [Btc] = 61_000m });
        var hit = p.Hit(Btc, 61_000m);
        bool? second = null;
        p.Book = (_, n) =>
        {
            if (n == 1) // while the first exit is confirming its book, the next quote reports the same hit
                second = p.Service.Start(hit with { DetectedAt = hit.DetectedAt.AddSeconds(1) });
            return null;
        };

        var first = p.Service.Start(hit);
        await p.Service.WhenIdleAsync();

        Assert.True(first);
        Assert.False(second);
        Assert.Single(p.Orders());
        Assert.Empty(await p.Rig.Paper.GetOpenOrdersAsync(null, CancellationToken.None)); // immediate-or-cancel: nothing rests
        Assert.Equal(0m, await p.Held());
    }

    [Fact]
    public async Task AnUncertainPlacement_IsNeverResentBlindly_TheNextTryReadsTheBalanceFirst()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        p.Rig.Exchange.UncertainAfterSending = true; // the order reaches the exchange and fills; the engine gets a timeout
        p.Book = (s, n) =>
        {
            if (n == 2)
                p.Rig.Exchange.UncertainAfterSending = null;
            return null;
        };

        var outcome = await p.ExitAsync(Btc, stop: 61_000m);

        Assert.Equal(ExitOutcome.Exited, outcome);
        var order = Assert.Single(p.Orders());
        Assert.Equal(("uncertain", "placement-uncertain"), (order.Data!["status"], order.Data["problem"]));
        Assert.Equal(0m, await p.Held()); // sold once, by the first order
        Assert.Equal(1, (await p.Rig.Paper.GetBalancesAsync(CancellationToken.None)).Count(b => b.Currency == "EUR")); // one account, one sell
    }

    [Fact]
    public async Task AnExchangeRefusal_IsTriedAgainOnAFreshBook()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        p.Rig.Exchange.RefuseNext = true;

        var outcome = await p.ExitAsync(Btc, stop: 61_000m);

        Assert.Equal(ExitOutcome.Exited, outcome);
        Assert.Equal(new[] { "failed", "placed" }, p.Orders().Select(o => o.Data!["status"]).ToArray());
        Assert.Equal("exchange-refused", p.Orders()[0].Data!["problem"]);
        Assert.Equal(0m, await p.Held());
    }

    [Fact]
    public async Task AnApiOutage_BlocksTheExit_AndItSellsByItselfWhenTheApiIsBack_WithoutResume()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        p.Rig.Exchange.FailReads = true; // open orders can't be read: the exit can't free the coins or see what is held
        IReadOnlyList<BlockedExit>? whileBlocked = null;
        p.Book = (s, n) =>
        {
            if (n == 6)
            {
                whileBlocked = p.Service.Blocked;
                p.Rig.Exchange.FailReads = false;
            }

            return null;
        };

        var outcome = await p.ExitAsync(Btc, stop: 61_000m);

        Assert.Equal(ExitOutcome.Exited, outcome);
        Assert.Equal("orders-unavailable", Assert.Single(whileBlocked!).Problem);
        Assert.Single(p.Rig.Journal.Entries, e => e.Kind == "exit-blocked");
        Assert.False(p.Rig.KillSwitch.IsEngaged); // no halt was needed, and none is in the way
        Assert.Empty(p.Service.Blocked);
        Assert.Equal(0m, await p.Held());
    }

    [Fact]
    public async Task AFreshBookThatCannotBeFetched_IsATryThatFailed_NotASell_AndABlockedExitOutlivesAShutdown()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        p.FreshFails = true; // the exchange answers nothing
        using var shutdown = new CancellationTokenSource();
        p.Book = (_, n) =>
        {
            if (n == 7)
                shutdown.Cancel(); // the engine stops while the exit is still blocked
            return null;
        };
        p.Trigger.Arm(new Dictionary<string, decimal> { [Btc] = 61_000m });

        var outcome = await p.Service.RunExitAsync(p.Hit(Btc, 61_000m), shutdown.Token);

        Assert.Equal(ExitOutcome.Stopped, outcome);
        Assert.Empty(p.Orders());
        Assert.Equal(0.001m, await p.Held());
        var blocked = Assert.Single(p.Service.Blocked);
        Assert.Equal(("quote-unavailable", 7), (blocked.Problem, blocked.Attempts));
        Assert.Single(p.Rig.Journal.Entries, e => e.Kind == "exit-blocked"); // reported once, not every minute
    }

    [Fact]
    public async Task ARestart_PicksTheStopUpFromTheState_AndSellsOnce()
    {
        var first = new Rig(eur: 200m, btc: 0.001m);
        new ProtectionRig(first).Plan(Btc, stop: 61_000m);
        first.Quote(60_000m, 60_010m); // the price fell below the stop while nothing was watching

        var second = first.Restart(() => first.Store);
        var p = new ProtectionRig(second);
        Assert.Equal(1, await p.Service.RefreshAsync(CancellationToken.None));
        await p.Service.WhenIdleAsync();

        Assert.Equal(0m, await p.Held());
        Assert.Single(p.Orders());
        Assert.Equal(0, await p.Service.RefreshAsync(CancellationToken.None)); // sold: no position, no stop, nothing to start
    }

    [Fact]
    public async Task ACrashRightAfterTheSellWasSent_LeavesNothingToResellAfterTheRestart()
    {
        var first = new Rig(eur: 200m, btc: 0.001m);
        var p1 = new ProtectionRig(first);
        p1.Plan(Btc, stop: 61_000m);
        first.Exchange.CrashAfterSending = true; // the exchange fills the sell; the process dies before hearing back

        await Assert.ThrowsAsync<SimulatedCrashException>(() => p1.ExitAsync(Btc, 61_000m));

        var second = first.Restart(() => first.Store);
        var p2 = new ProtectionRig(second);
        Assert.Equal(0, await p2.Service.RefreshAsync(CancellationToken.None)); // the balance shows it sold: nothing to protect
        Assert.Equal(0m, await p2.Held());
        Assert.Empty(p2.Trigger.Armed);
    }

    [Fact]
    public async Task LosingTheEngineLease_EndsTheExit_WithoutSelling()
    {
        var lease = new FakeLease();
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m, lease: lease));
        lease.IsHeld = false;

        var outcome = await p.ExitAsync(Btc, stop: 61_000m);

        Assert.Equal(ExitOutcome.LeaseLost, outcome);
        Assert.Empty(p.Orders());
        Assert.Equal(0.001m, await p.Held());
    }

    [Fact]
    public async Task AnotherCoinsStaleQuote_DoesNotBlockThisCoinsStopLoss()
    {
        var symbols = new[] { "AAA-EUR", "BBB-EUR" };
        var limits = new RiskLimits(200m, 40m, 100m, 15m, 0.15m, 4, 0.01m, symbols.ToHashSet(StringComparer.Ordinal));
        var (rig, p) = MultiRig(symbols, limits, baseEach: 0.001m);
        p.Plan("AAA-EUR", stop: 61_000m);
        p.Plan("BBB-EUR", stop: 50_000m);
        rig.Time.Advance(TimeSpan.FromMinutes(10)); // every quote is stale now
        p.Feed(p.Normal("AAA-EUR")); // AAA has a fresh one again; BBB doesn't

        Assert.Null(await rig.Execution.GetPortfolioAsync(CancellationToken.None)); // the whole portfolio can't be valued
        Assert.Equal(1, await p.Service.RefreshAsync(CancellationToken.None)); // AAA's stop is sold regardless
        await p.Service.WhenIdleAsync();

        Assert.Equal(0m, await p.Held("AAA-EUR"));
        Assert.Equal(0.001m, await p.Held("BBB-EUR"));
    }

    [Fact]
    public async Task ADatabaseWriteFailure_DoesNotStopTheStopLoss()
    {
        var store = new FailingStateStore();
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m, store: store));
        p.Plan(Btc, stop: 61_000m);
        store.FailWrites = true;

        var outcome = await p.ExitAsync(Btc, stop: 61_000m);

        Assert.Equal(ExitOutcome.Exited, outcome);
        Assert.Equal(0m, await p.Held());
        Assert.Equal("placed", Assert.Single(p.Orders()).Data!["status"]);
    }

    [Fact]
    public async Task FifteenStopsAtOnce_AreAllSold_PacedUnderTheExchangesLimit_AndNotAgainstTheTradingRate()
    {
        var symbols = Enumerable.Range(1, 15).Select(i => $"C{i:00}-EUR").ToArray();
        var limits = new RiskLimits(500m, 100m, 100m, 50m, 0.15m, 4, 0.01m, symbols.ToHashSet(StringComparer.Ordinal));
        var (rig, p) = MultiRig(symbols, limits, baseEach: 0.0005m);
        foreach (var symbol in symbols)
            p.Plan(symbol, stop: 61_000m);

        var started = await p.Service.RefreshAsync(CancellationToken.None);
        await p.Service.WhenIdleAsync();

        Assert.Equal(15, started);
        foreach (var symbol in symbols)
        {
            Assert.Equal(0m, await p.Held(symbol));
            Assert.Equal("placed", Assert.Single(p.Orders(symbol)).Data!["status"]);
        }

        // Never more than the configured orders in any one second, across all coins.
        var sent = symbols.SelectMany(s => p.Orders(s)).Select(o => o.At).OrderBy(t => t).ToList();
        for (var i = 0; i + p.Options.MaxOrdersPerSecond < sent.Count; i++)
            Assert.True(sent[i + p.Options.MaxOrdersPerSecond] - sent[i] >= TimeSpan.FromSeconds(1), "paced at most 5 a second");

        // The 15 protective sells don't use up the trading rate (4 a minute here): the strategy can still buy.
        var buy = await rig.Execution.PlaceAsync(new OrderIntent(symbols[0], OrderSide.Buy, 0.0001m, 59_950m, "buy-after"), false, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Placed, buy.Status);
        Assert.Empty(p.Service.Blocked);
    }

    [Fact]
    public async Task TheScan_LeavesStopsToTheProtectionService_AndStillSellsTargets()
    {
        var tools = new ToolRig(ToolRig.Observer with { AllowedTools = ["scan_market"] }, btc: 0.001m);
        ScannerServiceTests.SeedHourly(tools, 20m);
        var scanner = new MarketScanService(
            tools.Universe, tools.Features, tools.Candles, tools.Rig.Pairs, tools.Rig.Execution, tools.Rig.KillSwitch, tools.Journal, tools.Specs,
            new ScannerOptions(), tools.Rig.Time, NullLogger<MarketScanService>.Instance, tools.Views, protection: new ProtectionOptions());
        var p = new ProtectionRig(tools.Rig);

        p.Plan(Btc, stop: 61_000m); // at the stop
        var atStop = await scanner.ScanAsync(CancellationToken.None);
        var stopsSold = await scanner.ApplyProtectiveExitsAsync(atStop, CancellationToken.None);

        p.Plan(Btc, stop: 50_000m, target: 59_000m); // at the target
        var atTarget = await scanner.ScanAsync(CancellationToken.None);
        var targetsSold = await scanner.ApplyProtectiveExitsAsync(atTarget, CancellationToken.None);

        Assert.Equal("stop", Assert.Single(atStop.Exits).Reason);
        Assert.Equal(0, stopsSold); // the protection service's job now
        Assert.Equal("target", Assert.Single(atTarget.Exits).Reason);
        Assert.Equal(1, targetsSold);
        Assert.True(scanner.StopsOnEveryQuote);
    }

    [Fact]
    public async Task TheStatus_ReportsTheArmedStops()
    {
        var p = new ProtectionRig(new Rig(eur: 200m, btc: 0.001m));
        p.Plan(Btc, stop: 55_000m); // not reached
        await p.Service.RefreshAsync(CancellationToken.None);
        var status = new EngineStatusService(
            p.Rig.Execution, p.Rig.KillSwitch, p.Rig.State, Rig.Limits, new TradingContext(TradingMode.Paper), new InMemoryEngineStatusStore(),
            new DependencyHealth(), p.Rig.Time, NullLogger<EngineStatusService>.Instance, protection: p.Service).Build();

        Assert.Equal((1, (DateTimeOffset?)p.Rig.Time.GetUtcNow(), 0, 0), (status.Protection!.Armed, status.Protection.ArmedAt, status.Protection.Exiting.Count, status.Protection.Blocked.Count));
        Assert.Equal("Critical: drawdown reached the hard ceiling. Nothing new until you resume; stop-losses keep selling.", EngineStatusService.HaltCategoryFor(SafetyMonitor.CriticalBy));
    }

    [Fact]
    public void TheTrigger_FiresOnlyAtOrBelowAnArmedStop()
    {
        var time = new MutableTime(Rig.T0);
        var trigger = new ProtectionTrigger(time);
        trigger.Arm(new Dictionary<string, decimal> { ["btc/eur"] = 61_000m });

        trigger.OnQuote(new MarketQuote(Btc, 61_000.01m, 1m, 61_010m, 1m, Rig.T0));
        Assert.False(trigger.Hits.TryRead(out _));
        trigger.OnQuote(new MarketQuote(Btc, 61_000m, 1m, 61_010m, 1m, Rig.T0));
        Assert.True(trigger.Hits.TryRead(out var hit));
        Assert.Equal((Btc, 61_000m, 61_000m), (hit!.Symbol, hit.Stop, hit.Quote.Bid));

        trigger.Arm(new Dictionary<string, decimal>()); // disarmed: the position is gone
        trigger.OnQuote(new MarketQuote(Btc, 50_000m, 1m, 50_010m, 1m, Rig.T0));
        Assert.False(trigger.Hits.TryRead(out _));
    }

    [Fact]
    public void TheOptions_CannotQuietlyDisableTheProtection()
    {
        new ProtectionOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new ProtectionOptions { QuickAttempts = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ProtectionOptions { RetryDelay = TimeSpan.FromSeconds(1) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ProtectionOptions { BlockedRetryInterval = TimeSpan.FromHours(1) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ProtectionOptions { MaxOrdersPerSecond = 10 }.Validate()); // the exchange's own limit
        Assert.Throws<InvalidOperationException>(() => new ProtectionOptions { MaxQuoteAge = TimeSpan.FromMinutes(6) }.Validate());
    }

    /// <summary>A rig trading <paramref name="symbols"/>, each held <paramref name="baseEach"/> and quoted like BTC-EUR (60,000 / 60,010).</summary>
    private static (Rig Rig, ProtectionRig Protection) MultiRig(IReadOnlyList<string> symbols, RiskLimits limits, decimal baseEach)
    {
        var time = new MutableTime(Rig.T0);
        var market = new MultiPairMarketData(symbols);
        var options = new PaperOptions();
        options.InitialBalances["EUR"] = 100m;
        foreach (var symbol in symbols)
            options.InitialBalances[symbol.Split('-')[0]] = baseEach;
        var rig = new Rig(pairSource: market, paper: new PaperExchange(market, options, time), time: time, tradable: new TradableSymbols(limits));
        var p = new ProtectionRig(rig);
        foreach (var symbol in symbols)
            p.Feed(p.Normal(symbol));
        return (rig, p);
    }
}
