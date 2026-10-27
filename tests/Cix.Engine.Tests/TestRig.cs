using Cix.Domain;
using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine.Execution;
using Cix.Engine.Journal;
using Cix.Engine.MarketData;
using Cix.Engine.Paper;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cix.Engine.Tests;

/// <summary>
/// One engine process wired to a paper exchange. Pass another rig's <see cref="Paper"/>, <see cref="Time"/> and store
/// to <see cref="Restart"/> to simulate a process restart: the exchange and the state file survive, memory doesn't.
/// </summary>
internal sealed class Rig
{
    public const string Btc = "BTC-EUR";
    public static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public Rig(
        decimal eur = 200m,
        decimal btc = 0m,
        IEngineStateStore? store = null,
        IMarketDataSource? pairSource = null,
        SafetyOptions? safety = null,
        PaperExchange? paper = null,
        MutableTime? time = null,
        InMemoryJournal? journal = null,
        Persistence.IEngineLease? lease = null,
        TradableSymbols? tradable = null)
    {
        Tradable = tradable;
        var limits = tradable?.Limits ?? Limits;
        Journal = journal ?? new InMemoryJournal();
        Time = time ?? new MutableTime(T0);
        Safety = safety ?? new SafetyOptions();
        if (paper is null)
        {
            var options = new PaperOptions();
            options.InitialBalances["EUR"] = eur;
            options.InitialBalances["BTC"] = btc;
            paper = new PaperExchange(new FakeMarketData(), options, Time);
        }

        Paper = paper;
        Exchange = new FlakyExchange(Paper);
        State = new MarketState(Time);
        Store = store ?? new InMemoryEngineStateStore();
        KillSwitch = new KillSwitch(Store, Time);
        Pairs = new PairCatalog(pairSource ?? new FakeMarketData(), Time);
        var pairs = Pairs;
        Portfolio = new PortfolioService(Exchange, pairs, State, KillSwitch, Store, limits, Time, tradable);
        Execution = new ExecutionService(
            Exchange, new RiskGate(limits, Time, tradable), Portfolio, State, pairs, KillSwitch, Store, Time,
            NullLogger<ExecutionService>.Instance, (_, _) => Task.CompletedTask, Safety, Journal, lease);
        Monitor = new SafetyMonitor(Execution, KillSwitch, State, limits, Safety, Time, NullLogger<SafetyMonitor>.Instance, tradable);
        Quote(60_000.00m, 60_010.00m);
    }

    public static RiskLimits Limits { get; } = new(200m, 40m, 100m, 15m, 0.15m, 4, 0.01m, new HashSet<string> { Btc });

    /// <summary>The run-time pair list, when the test gives one; otherwise <see cref="Limits"/>' allowlist is tradable.</summary>
    public TradableSymbols? Tradable { get; }

    public MutableTime Time { get; }
    public InMemoryJournal Journal { get; }
    public SafetyOptions Safety { get; }
    public MarketState State { get; }
    public PaperExchange Paper { get; }
    public FlakyExchange Exchange { get; }
    public IEngineStateStore Store { get; }
    public KillSwitch KillSwitch { get; }
    public PairCatalog Pairs { get; }
    public PortfolioService Portfolio { get; }
    public ExecutionService Execution { get; }
    public SafetyMonitor Monitor { get; }

    /// <summary>A new process on the same exchange, clock and journal, with the state reloaded by <paramref name="reload"/>.</summary>
    public Rig Restart(Func<IEngineStateStore> reload) =>
        new(store: reload(), safety: Safety, paper: Paper, time: Time, journal: Journal);

    /// <summary>A fresh quote at the current time, to both the engine's market state and the paper exchange.</summary>
    public void Quote(decimal bid, decimal ask)
    {
        var quote = new MarketQuote(Btc, bid, 0.01m, ask, 0.01m, Time.GetUtcNow());
        State.Update(quote);
        Paper.OnQuote(quote);
    }

    public Task<ExecutionResult> Buy(decimal size, decimal price, string id = "c-1") =>
        Execution.PlaceAsync(new OrderIntent(Btc, OrderSide.Buy, size, price, id), postOnly: false, CancellationToken.None);

    public Task<PortfolioSnapshot> Snapshot() => Portfolio.GetSnapshotAsync(0, [], CancellationToken.None);
}
