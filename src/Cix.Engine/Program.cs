using Cix.Domain.Exchange;
using Cix.Domain.Risk;
using Cix.Engine;
using Cix.Engine.Paper;
using Cix.Exchange.RevolutX;

var builder = Host.CreateApplicationBuilder(args);

// Human-only file, copied next to the binary (see CLAUDE.md rule 3).
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "risk-limits.json"), optional: false, reloadOnChange: false);

var limits = builder.Configuration.GetSection("Risk").Get<RiskLimitsOptions>()?.ToRiskLimits()
    ?? throw new InvalidOperationException("Missing 'Risk' configuration section.");

// Paper unless the owner explicitly configures Live (CLAUDE.md rule 6).
var mode = TradingModeResolver.Resolve(builder.Configuration);

// Fails fast if configured limits exceed the hard ceilings.
builder.Services.AddSingleton(new RiskGate(limits));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new TradingContext(mode));

// Exchange: the real client always supplies market data; orders go to it only in Live mode.
// Credentials come from user secrets or Key Vault; nothing is resolved until a component needs it.
builder.Services.AddSingleton(builder.Configuration.GetSection(RevolutXOptions.SectionName).Get<RevolutXOptions>() ?? new RevolutXOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection(PaperOptions.SectionName).Get<PaperOptions>() ?? new PaperOptions());
builder.Services.AddSingleton(sp => new RevolutXClient(new HttpClient(), sp.GetRequiredService<RevolutXOptions>(), time: sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new PaperExchange(
    sp.GetRequiredService<RevolutXClient>(),
    sp.GetRequiredService<PaperOptions>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IExchangeClient>(sp => mode == TradingMode.Live
    ? sp.GetRequiredService<RevolutXClient>()
    : sp.GetRequiredService<PaperExchange>());

builder.Services.AddHostedService<HeartbeatService>();

var host = builder.Build();
host.Run();
