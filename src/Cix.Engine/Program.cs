using Cix.Domain.Risk;
using Cix.Engine;
using Cix.Engine.Execution;
using Cix.Engine.MarketData;
using Cix.Engine.Paper;
using Cix.Engine.Persistence;
using Cix.Engine.Scanning;
using Cix.Engine.Strategy;
using Cix.Engine.Tools;
using Cix.Exchange.RevolutX;

var builder = WebApplication.CreateBuilder(args);

// Docker secrets (files under /run/secrets) for the few settings that are secrets; anything else there is ignored.
builder.Configuration.AddCixSecretFiles(builder.Configuration["SecretsDirectory"] ?? SecretFiles.DefaultDirectory);

// Human-only file, copied next to the binary (see CLAUDE.md rule 3).
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "risk-limits.json"), optional: false, reloadOnChange: false);

var limits = builder.Configuration.GetSection("Risk").Get<RiskLimitsOptions>()?.ToRiskLimits()
    ?? throw new InvalidOperationException("Missing 'Risk' configuration section.");

// Paper unless the owner configures Live, and Live only in Production (CLAUDE.md rule 6).
var mode = TradingModeResolver.Resolve(builder.Configuration, builder.Environment.EnvironmentName);

// Fails fast if configured limits exceed the hard ceilings.
builder.Services.AddSingleton(limits);
builder.Services.AddSingleton(new RiskGate(limits));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new TradingContext(mode));

// Paper mode registers no order-capable client; see ExchangeRegistration.
builder.Services.AddCixExchange(
    mode,
    builder.Configuration.GetSection(RevolutXOptions.SectionName).Get<RevolutXOptions>() ?? new RevolutXOptions(),
    builder.Configuration.GetSection(PaperOptions.SectionName).Get<PaperOptions>() ?? new PaperOptions());

// The curated universe (ADR-0006): the scan covers it plus the risk allowlist; only allowlisted symbols are polled and tradable.
var scanner = builder.Configuration.GetSection(ScannerOptions.SectionName).Get<ScannerOptions>() ?? new ScannerOptions();
var universe = new MarketUniverse(scanner.EffectiveUniverse, limits.AllowedSymbols);

// Polls quotes and candles for the allowlisted symbols; fails startup if the schedule exceeds the rate limit.
builder.Services.AddCixMarketData(
    builder.Configuration.GetSection(MarketDataOptions.SectionName).Get<MarketDataOptions>() ?? new MarketDataOptions(),
    universe.Tradable);

// The only order path: every placement goes through the risk gate (ApprovedOrder makes skipping it a compile error).
builder.Services.AddCixExecution(
    mode,
    builder.Configuration.GetSection(ExecutionOptions.SectionName).Get<ExecutionOptions>() ?? new ExecutionOptions(),
    builder.Configuration.GetSection(SafetyOptions.SectionName).Get<SafetyOptions>() ?? new SafetyOptions());

// The MCP tool server: agents' only way in. Loopback only; operator tools need the key from user secrets.
var toolServer = builder.Configuration.GetSection(ToolServerOptions.SectionName).Get<ToolServerOptions>() ?? new ToolServerOptions();
builder.Services.AddCixToolServer(
    mode,
    toolServer,
    builder.Configuration.GetSection(StrategyOptions.SectionName).Get<StrategyOptions>() ?? new StrategyOptions(),
    builder.Configuration.GetSection(JournalOptions.SectionName).Get<JournalOptions>() ?? new JournalOptions());
builder.WebHost.ListenOnLoopback(builder.Configuration, toolServer);

// Deterministic scans of the universe every few minutes; signals are journaled and served by scan_market. Never trades.
builder.Services.AddCixScanner(scanner, universe);

// Cosmos DB (emulator locally, Azure later) for the journal, engine state and paper account; off unless configured.
builder.Services.AddCixCosmos(
    builder.Configuration.GetSection(CosmosOptions.SectionName).Get<CosmosOptions>() ?? new CosmosOptions(),
    mode);

builder.Services.AddHostedService<HeartbeatService>();

var app = builder.Build();
await app.Services.InitializeCixPersistenceAsync(CancellationToken.None);
_ = app.Services.GetRequiredService<MarketScanService>().Parameters; // a bad strategy spec stops startup here
app.UseCixToolServer();
await app.RunAsync();
