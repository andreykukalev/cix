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

// Docker secrets (files under /run/secrets) for the few settings that are secrets; anything else there is ignored. The Revolut X
// keys are read in Live mode only; a Paper engine with them there refuses to start.
builder.Configuration.AddCixSecretFiles(builder.Configuration["SecretsDirectory"] ?? SecretFiles.DefaultDirectory, mode);

// Human-only file, copied next to the binary (see CLAUDE.md rule 3). Read on its own: no environment variable, command line or
// other settings file can add to the limits or loosen them.
var limits = RiskLimitsOptions.Load(Path.Combine(AppContext.BaseDirectory, "risk-limits.json"), builder.Configuration);

// The pairs tradable now: the allowlist at start, then the owner's list from the dashboard, always within the limits.
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

// The curated universe (ADR-0006): the scan covers it plus the tradable pairs; only tradable pairs are polled and traded.
var scanner = builder.Configuration.GetSection(ScannerOptions.SectionName).Get<ScannerOptions>() ?? new ScannerOptions();
var universe = new MarketUniverse(scanner.EffectiveUniverse, tradable);

// Polls quotes and candles for the tradable pairs; fails startup if the schedule for the most there may be exceeds the rate limit.
builder.Services.AddCixMarketData(
    builder.Configuration.GetSection(MarketDataOptions.SectionName).Get<MarketDataOptions>() ?? new MarketDataOptions(),
    universe,
    limits.TradableCap);

// The only order path: every placement goes through the risk gate (ApprovedOrder makes skipping it a compile error).
builder.Services.AddCixExecution(
    mode,
    builder.Configuration.GetSection(ExecutionOptions.SectionName).Get<ExecutionOptions>() ?? new ExecutionOptions(),
    builder.Configuration.GetSection(SafetyOptions.SectionName).Get<SafetyOptions>() ?? new SafetyOptions());

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
var cosmos = builder.Configuration.GetSection(CosmosOptions.SectionName).Get<CosmosOptions>() ?? new CosmosOptions();
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
