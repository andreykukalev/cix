using Cix.Domain.Risk;
using Cix.Engine;
using Cix.Engine.AgentRuns;
using Cix.Engine.Analysis;
using Cix.Engine.Execution;
using Cix.Engine.Manual;
using Cix.Engine.MarketData;
using Cix.Engine.Paper;
using Cix.Engine.Persistence;
using Cix.Engine.Scanning;
using Cix.Engine.Status;
using Cix.Engine.Strategy;
using Cix.Engine.Tools;
using Cix.Exchange.RevolutX;

var builder = WebApplication.CreateBuilder(args);

// Paper unless the owner configures Live, and Live only in Production (CLAUDE.md rule 6). Resolved before the secret files are
// read, which can't set it, because the mode decides which of them may be.
var mode = TradingModeResolver.Resolve(builder.Configuration, builder.Environment.EnvironmentName);

// A Paper engine holds no trading credentials. If the Revolut X keys are among its secret files in Azure, as in the live
// environment until the owner switches its engine to Live (docs/live-trading.md), it stands by: it answers /healthz and runs
// nothing else. Elsewhere it refuses to start (AddCixSecretFiles).
var secretsDirectory = builder.Configuration["SecretsDirectory"] ?? SecretFiles.DefaultDirectory;
if (StandbyHost.HeldKeys(mode, builder.Environment.EnvironmentName, secretsDirectory) is { Count: > 0 } held)
{
    await StandbyHost.RunAsync(builder, held);
    return;
}

// Docker secrets (files under /run/secrets) for the few settings that are secrets; anything else there is ignored. The Revolut X
// keys are read in Live mode only.
builder.Configuration.AddCixSecretFiles(secretsDirectory, mode);

// Human-only file for this mode, copied next to the binary (see CLAUDE.md rule 3): risk-limits.json for paper, risk-limits.live.json
// for live. Read on its own: no environment variable, command line or other settings file can add to the limits or loosen them.
var limits = RiskLimitsOptions.LoadFor(mode, AppContext.BaseDirectory, builder.Configuration);

// The watched pairs: empty at start, then the saved list from the dashboard plus the coins the account holds (ADR-0021), always
// within the limits file's one rule (a quote currency and a cap). No pair list lives in configuration.
var tradable = new TradableSymbols(limits);

// Fails fast if configured limits exceed the hard ceilings.
builder.Services.AddSingleton(limits);
builder.Services.AddSingleton(tradable);
builder.Services.AddSingleton(new RiskGate(limits, tradable: tradable));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new TradingContext(mode));

// Only the engine holding the lease trades (ADR-0013). Without Cosmos DB there is one engine and nothing to share; with it,
// AddCixCosmos replaces this. Registered first, so it stops last and releases the lease after everything else has stopped.
builder.Services.AddSingleton<IEngineLease>(NoEngineLease.Instance);
builder.Services.AddHostedService<EngineLeaseService>();

// Paper mode registers no order-capable client; see ExchangeRegistration.
builder.Services.AddCixExchange(
    mode,
    builder.Configuration.GetSection(RevolutXOptions.SectionName).Get<RevolutXOptions>() ?? new RevolutXOptions(),
    builder.Configuration.GetSection(PaperOptions.SectionName).Get<PaperOptions>() ?? new PaperOptions());

// The scan covers the watched pairs and nothing else (ADR-0021 removed the curated universe of ADR-0006).
ScannerOptions.ThrowIfUniverseConfigured(builder.Configuration);
var scanner = builder.Configuration.GetSection(ScannerOptions.SectionName).Get<ScannerOptions>() ?? new ScannerOptions();
var universe = new MarketUniverse(tradable);

// Tickers for every watched pair, order books for the active ones, candles for all; fails startup if the schedule for the most
// there may be exceeds the rate limit (ADR-0021).
builder.Services.AddCixMarketData(
    builder.Configuration.GetSection(MarketDataOptions.SectionName).Get<MarketDataOptions>() ?? new MarketDataOptions(),
    universe,
    limits.TradableCap,
    signedQuotes: mode == TradingMode.Live);

// The only order path: every placement goes through the risk gate (ApprovedOrder makes skipping it a compile error). Its state
// (the kill switch, loss baselines) goes to Cosmos DB when that is on (AddCixCosmos, below), else to a file in Live.
var cosmos = builder.Configuration.GetSection(CosmosOptions.SectionName).Get<CosmosOptions>() ?? new CosmosOptions();

// The watched pairs and the coins the account holds are restored and adopted from Cosmos DB (ADR-0021). Without it a live engine
// would watch nothing, so the stops of what it holds would go unwatched: it refuses to start instead.
if (mode == TradingMode.Live && !cosmos.Enabled)
    throw new InvalidOperationException("Live trading needs Cosmos DB (Cosmos:Enabled): the watched pairs and the coins the account holds are restored from it (ADR-0021).");
builder.Services.AddCixExecution(
    mode,
    builder.Configuration.GetSection(ExecutionOptions.SectionName).Get<ExecutionOptions>() ?? new ExecutionOptions(),
    builder.Configuration.GetSection(SafetyOptions.SectionName).Get<SafetyOptions>() ?? new SafetyOptions(),
    durableState: cosmos.Enabled);

// The engine's own stop-loss exits: checked on every quote and sold at once, whatever the strategy, the agents or a pause do.
builder.Services.AddCixProtection(builder.Configuration.GetSection(ProtectionOptions.SectionName).Get<ProtectionOptions>() ?? new ProtectionOptions());

// The MCP tool server: agents' only way in. Loopback only (operator tools need the key from user secrets), or in Azure
// Entra tokens from the agent jobs' managed identities, with no operator tools (ADR-0013).
var toolServer = builder.Configuration.GetSection(ToolServerOptions.SectionName).Get<ToolServerOptions>() ?? new ToolServerOptions();
builder.Services.AddCixToolServer(
    mode,
    toolServer,
    builder.Configuration.GetSection(StrategyOptions.SectionName).Get<StrategyOptions>() ?? new StrategyOptions(),
    builder.Configuration.GetSection(JournalOptions.SectionName).Get<JournalOptions>() ?? new JournalOptions());
builder.WebHost.ListenForAgents(builder.Configuration, toolServer);

// The market analyst's spec and its market view (risk-off or flagged symbols block new entries, never exits). #25.
var strategyOptions = builder.Configuration.GetSection(StrategyOptions.SectionName).Get<StrategyOptions>() ?? new StrategyOptions();
builder.Services.AddCixAnalyst(
    builder.Configuration.GetSection(AnalystOptions.SectionName).Get<AnalystOptions>() ?? new AnalystOptions(),
    strategyOptions);

// Deterministic scans of the universe every few minutes; signals are journaled and served by scan_market. Never trades.
builder.Services.AddCixScanner(scanner, universe);

// The owner's manual buy and sell requests from the dashboard (ADR-0011): every order still goes through the risk gate.
builder.Services.AddCixManualRequests(
    builder.Configuration.GetSection(ManualRequestOptions.SectionName).Get<ManualRequestOptions>() ?? new ManualRequestOptions());

// A readable status record every minute (heartbeat, last sync, portfolio) and dependency health for /healthz.
builder.Services.AddCixStatus();

// In Azure the engine decides when the agents run and queues each run for its job (ADR-0013); Compose keeps its shell loops.
builder.Services.AddCixAgentRuns(
    builder.Configuration.GetSection(AgentRunOptions.SectionName).Get<AgentRunOptions>() ?? new AgentRunOptions(),
    toolServer.Auth,
    durableState: cosmos.Enabled);

// Cosmos DB (emulator locally, or Azure) for the journal, engine state, status and paper account; off unless configured.
builder.Services.AddCixCosmos(cosmos, mode);

builder.Services.AddHostedService<HeartbeatService>();

var app = builder.Build();

// After a reboot the database may still be starting: wait for it (with backoff) rather than crash and be restarted by Docker.
// The tool server, and so /healthz, only starts listening once this is done, so "healthy" means ready to trade.
await StartupRetry.RunAsync(
    ct => app.Services.InitializeCixPersistenceAsync(ct),
    "the database",
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cix.Engine.Startup"),
    TimeProvider.System,
    CancellationToken.None);
_ = app.Services.GetRequiredService<MarketScanService>().Parameters; // a bad strategy spec stops startup here
await app.Services.GetRequiredService<MarketViewStore>().RestoreAsync(CancellationToken.None);
app.UseCixToolServer();
await app.RunAsync();
