using Cix.Domain.Risk;
using Cix.Engine;
using Cix.Engine.Execution;
using Cix.Engine.MarketData;
using Cix.Engine.Paper;
using Cix.Engine.Strategy;
using Cix.Engine.Tools;
using Cix.Exchange.RevolutX;

var builder = WebApplication.CreateBuilder(args);

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

// Polls quotes and candles for the symbols the risk gate allows; fails startup if the schedule exceeds the rate limit.
builder.Services.AddCixMarketData(
    builder.Configuration.GetSection(MarketDataOptions.SectionName).Get<MarketDataOptions>() ?? new MarketDataOptions(),
    limits.AllowedSymbols);

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

builder.Services.AddHostedService<HeartbeatService>();

var app = builder.Build();
app.UseCixToolServer();
app.Run();
