using Cix.Domain.Risk;
using Cix.Engine;

var builder = Host.CreateApplicationBuilder(args);

// Human-only file, copied next to the binary (see CLAUDE.md rule 3).
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "risk-limits.json"), optional: false, reloadOnChange: false);

var limits = builder.Configuration.GetSection("Risk").Get<RiskLimitsOptions>()?.ToRiskLimits()
    ?? throw new InvalidOperationException("Missing 'Risk' configuration section.");

// Fails fast if configured limits exceed the hard ceilings.
builder.Services.AddSingleton(new RiskGate(limits));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<HeartbeatService>();

var host = builder.Build();
host.Run();
