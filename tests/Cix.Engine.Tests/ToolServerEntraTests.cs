using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Cix.Engine.AgentRuns;
using Cix.Engine.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;

namespace Cix.Engine.Tests;

/// <summary>The tool server with Entra authentication (ADR-0013): tokens signed with a test key in place of Entra's.</summary>
public sealed class ToolServerEntraTests
{
    private static readonly string Tenant = Guid.NewGuid().ToString();
    private static readonly string TraderId = Guid.NewGuid().ToString();
    private static readonly string RequestsId = Guid.NewGuid().ToString();
    private const string Audience = "api://cix-engine-test";

    // Built at runtime: not a secret, and scanners needn't think so.
    private static readonly SymmetricSecurityKey Key = new(RandomNumberGenerator.GetBytes(32));

    private static ToolServerOptions Options() => new()
    {
        Url = "http://0.0.0.0:5077",
        Auth = ToolServerAuth.Entra,
        Entra = new EntraToolServerOptions
        {
            TenantId = Tenant,
            Audience = Audience,
            Agents = new(StringComparer.OrdinalIgnoreCase) { [TraderId] = "trader", [RequestsId] = "trader-requests" },
            TestSigningKey = Key,
        },
    };

    private static ToolServerOptions Changed(Action<ToolServerOptions> change)
    {
        var options = Options();
        change(options);
        return options;
    }

    /// <summary>A token like a managed identity's. Its lifetime spans any clock a test machine might have.</summary>
    private static string Token(
        string oid, string? audience = Audience, string? issuer = null, SecurityKey? key = null, DateTime? expires = null,
        string[]? roles = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? $"https://sts.windows.net/{Tenant}/",
            Audience = audience,
            Claims = new Dictionary<string, object> { ["oid"] = oid, ["tid"] = Tenant, ["roles"] = roles ?? [EntraToolServerOptions.AppRole] },
            IssuedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            NotBefore = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Expires = expires ?? new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.HmacSha256),
        });

    private static async Task<(WebApplication App, string Address, ToolRig Tools)> StartAsync(AgentRunScheduler? runs = null)
    {
        var tools = new ToolRig();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<TimeProvider>(tools.Rig.Time);
        builder.Services.AddSingleton(tools.Features);
        builder.Services.AddSingleton(tools.Rig.Execution);
        builder.Services.AddSingleton(tools.Rig.KillSwitch);
        builder.Services.AddSingleton(Rig.Limits);
        builder.Services.AddSingleton(tools.Rig.Tradable);
        builder.Services.AddSingleton(tools.Scanner);
        builder.Services.AddSingleton(tools.Universe);
        builder.Services.AddSingleton(tools.Views);
        builder.Services.AddSingleton(tools.AnalystSpecs);
        if (runs is not null)
            builder.Services.AddSingleton(runs);
        builder.Services.AddCixToolServer(Options(), tools.Specs, tools.Journal);
        var app = builder.Build();
        app.UseCixToolServer();
        await app.StartAsync();
        return (app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First(), tools);
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string url, string? token, string? json = "{}", bool origin = false)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin)
            request.Headers.Add("Origin", "https://example.com");
        request.Content = json is null ? new StringContent("{}") : new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        if (json is null)
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpStatusCode> GetAsync(string url, string? token = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task OnlyListedIdentities_WithValidTokens_GetIn_AndHealthStaysOpen()
    {
        var (app, address, _) = await StartAsync();
        await using (app)
        {
            var signals = address + ToolServerRegistration.SignalsEndpoint;
            Assert.Equal(HttpStatusCode.OK, await GetAsync(address + ToolServerRegistration.HealthEndpoint));
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(address + "/anything-else")); // closed by default
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(signals));
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(signals, "not-a-token"));
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(signals, Token(TraderId, audience: "api://something-else")));
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(signals, Token(TraderId, issuer: $"https://sts.windows.net/{Guid.NewGuid()}/")));
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(signals, Token(TraderId, key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)))));
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(signals, Token(TraderId, expires: new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(signals, Token(Guid.NewGuid().ToString()))); // valid, but not one of ours
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(signals, Token(TraderId, roles: []))); // ours, but without the app role
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(signals, Token(TraderId, roles: ["Other.Role"])));
            Assert.Equal(HttpStatusCode.OK, await GetAsync(signals, Token(TraderId)));
            Assert.Equal(HttpStatusCode.OK, await GetAsync(signals, Token(TraderId, issuer: $"https://login.microsoftonline.com/{Tenant}/v2.0")));
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Jobs_ClaimAndReportOnlyTheirOwnRuns()
    {
        var setup = new AgentRunSchedulerTests.Setup();
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = setup.Queue.Sent.Single().Run; // a trader heartbeat
        var (app, address, _) = await StartAsync(setup.Scheduler);
        await using (app)
        {
            var claim = $"{address}{ToolServerRegistration.RunsEndpoint}/{run.Id}/claim";
            var outcome = $"{address}{ToolServerRegistration.RunsEndpoint}/{run.Id}/outcome";

            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(claim, token: null)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(claim, Token(TraderId), origin: true)).Status);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await PostAsync(claim, Token(TraderId), json: null)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(outcome, Token(TraderId), """{"outcome":"done"}""")).Status); // not claimed yet

            var other = await PostAsync(claim, Token(RequestsId)); // another job's identity
            Assert.Equal((HttpStatusCode.OK, false), (other.Status, JsonDocument.Parse(other.Body).RootElement.GetProperty("wanted").GetBoolean()));
            var mine = await PostAsync(claim, Token(TraderId));
            using (var body = JsonDocument.Parse(mine.Body))
            {
                Assert.True(body.RootElement.GetProperty("wanted").GetBoolean());
                Assert.Equal("heartbeat", body.RootElement.GetProperty("purpose").GetString());
            }

            var again = await PostAsync(claim, Token(TraderId)); // a repeated message
            Assert.False(JsonDocument.Parse(again.Body).RootElement.GetProperty("wanted").GetBoolean());

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(outcome, Token(TraderId), """{"outcome":"great"}""")).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(outcome, Token(RequestsId), """{"outcome":"done"}""")).Status);
            Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(outcome, Token(TraderId), """{"outcome":"done"}""")).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(outcome, Token(TraderId), """{"outcome":"not-run"}""")).Status);
            Assert.Equal((1, 0), (setup.Scheduler.State!.Runs, setup.Scheduler.State.Refunds));

            setup.Lease.IsHeld = false; // a deployment: the other engine has the lease
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(claim, Token(TraderId))).Status);
            await app.StopAsync();
        }

        var (plain, plainAddress, _) = await StartAsync();
        await using (plain)
        {
            // No scheduler (Compose, or the agent runs off): nothing to claim.
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync($"{plainAddress}{ToolServerRegistration.RunsEndpoint}/{run.Id}/claim", Token(TraderId))).Status);
            await plain.StopAsync();
        }
    }

    [Fact]
    public async Task InAzure_ToolCallsMustBelongToTheClaimedRun()
    {
        var setup = new AgentRunSchedulerTests.Setup();
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = setup.Queue.Sent.Single().Run; // a trader heartbeat
        var (app, address, tools) = await StartAsync(setup.Scheduler);
        await using (app)
        {
            Task<bool> RefusedAsync(string tool, string? runId, string? identity = null) => IsRefusedAsync(address, tool, runId, identity);

            Assert.True(await RefusedAsync("get_portfolio", run.Id)); // queued, not claimed yet
            Assert.Equal(HttpStatusCode.OK, (await PostAsync($"{address}{ToolServerRegistration.RunsEndpoint}/{run.Id}/claim", Token(TraderId))).Status);

            Assert.False(await RefusedAsync("get_portfolio", run.Id));
            Assert.False(await RefusedAsync("get_strategy_spec", run.Id));
            Assert.True(await RefusedAsync("get_strategy_spec", null)); // no run named: even the tool every spec allows
            Assert.True(await RefusedAsync("get_portfolio", "run-20990101-000000-abcdef")); // a run the engine never started
            Assert.True(await RefusedAsync("get_portfolio", run.Id, RequestsId)); // another job's identity
            setup.Lease.IsHeld = false; // another engine took over: this one's state may be stale
            Assert.True(await RefusedAsync("get_portfolio", run.Id));
            setup.Lease.IsHeld = true;

            Assert.Equal(HttpStatusCode.NoContent,
                (await PostAsync($"{address}{ToolServerRegistration.RunsEndpoint}/{run.Id}/outcome", Token(TraderId), """{"outcome":"done"}""")).Status);
            Assert.True(await RefusedAsync("get_portfolio", run.Id)); // reported: the run is over

            Assert.Equal(2, tools.Journal.Entries.Count(e => e.Kind == "tool-call" && e.RunId == run.Id));
            var denied = tools.Journal.Entries.Where(e => e.Kind == "tool-denied").ToList();
            Assert.Equal(6, denied.Count);
            Assert.All(denied, e => Assert.Contains("X-Cix-Run", e.Summary, StringComparison.Ordinal));
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task InAzure_WithAgentRunsOff_AgentToolsAreRefused()
    {
        var (app, address, tools) = await StartAsync(); // Entra, no scheduler: no run can be claimed, so none is in progress
        await using (app)
        {
            Assert.True(await IsRefusedAsync(address, "get_strategy_spec", "run-20261026-080000-abcdef"));
            Assert.True(await IsRefusedAsync(address, "get_portfolio", "run-20261026-080000-abcdef"));
            Assert.Equal(2, tools.Journal.Entries.Count(e => e.Kind == "tool-denied" && e.Summary.Contains("Mode is Off", StringComparison.Ordinal)));
            Assert.DoesNotContain(tools.Journal.Entries, e => e.Kind == "tool-call");
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ARunThatUsedTools_ReportedNotRun_CountsAsFailed()
    {
        var setup = new AgentRunSchedulerTests.Setup();
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = setup.Queue.Sent.Single().Run;
        var (app, address, _) = await StartAsync(setup.Scheduler);
        await using (app)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostAsync($"{address}{ToolServerRegistration.RunsEndpoint}/{run.Id}/claim", Token(TraderId))).Status);
            Assert.False(await IsRefusedAsync(address, "get_portfolio", run.Id));
            Assert.Equal(HttpStatusCode.NoContent,
                (await PostAsync($"{address}{ToolServerRegistration.RunsEndpoint}/{run.Id}/outcome", Token(TraderId), """{"outcome":"not-run"}""")).Status);

            Assert.Equal((1, 0), (setup.Scheduler.State!.Runs, setup.Scheduler.State.Refunds)); // no refund
            Assert.Contains("reported not-run", Assert.Single(setup.Steps("failed")).Summary, StringComparison.Ordinal);
            Assert.Empty(setup.Steps("not-run"));
            await app.StopAsync();
        }
    }

    /// <summary>Calls <paramref name="tool"/> as <paramref name="identity"/> (the trader by default) under run <paramref name="runId"/>.</summary>
    private static async Task<bool> IsRefusedAsync(string address, string tool, string? runId, string? identity = null)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token(identity ?? TraderId) };
        if (runId is not null)
            headers[ToolServerOptions.RunHeader] = runId;
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(address + ToolServerRegistration.Endpoint),
            AdditionalHeaders = headers,
        }));
        return (await client.CallToolAsync(tool, new Dictionary<string, object?>())).IsError == true;
    }

    [Fact]
    public async Task TheRoleComesFromTheToken_NotFromTheHeader()
    {
        var setup = new AgentRunSchedulerTests.Setup();
        setup.Signals.Inputs = new(true, true, string.Empty, "req-1"); // an owner request waiting
        await setup.Scheduler.PollOnceAsync(CancellationToken.None);
        var run = setup.Queue.Sent.Select(s => s.Run).Single(r => r.Kind == AgentRunKind.Requests);
        var (app, address, tools) = await StartAsync(setup.Scheduler);
        await using (app)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostAsync($"{address}{ToolServerRegistration.RunsEndpoint}/{run.Id}/claim", Token(RequestsId))).Status);
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(address + ToolServerRegistration.Endpoint),
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer " + Token(RequestsId),
                    [ToolServerOptions.AgentHeader] = "analyst", // claims to be someone else
                    [ToolServerOptions.OperatorKeyHeader] = ToolRig.OperatorKey,
                    [ToolServerOptions.RunHeader] = run.Id,
                },
            }));

            var spec = await client.CallToolAsync("get_strategy_spec", new Dictionary<string, object?>());
            var view = await client.CallToolAsync("record_market_view", new Dictionary<string, object?>
            {
                ["regime"] = "neutral",
                ["summary"] = "Calm markets, nothing scheduled today.",
                ["sources"] = new[] { "https://example.com/a" }, // a complete call: only the identity can refuse it
            });
            var journal = await client.CallToolAsync("query_journal", new Dictionary<string, object?>());

            Assert.NotEqual(true, spec.IsError);
            Assert.True(view.IsError); // analyst tools: not for this identity
            Assert.True(journal.IsError); // no operator tools with Entra, whatever the header says
            Assert.Equal(("trader-requests", run.Id), tools.Journal.Entries.Where(e => e.Kind == "tool-call").Select(e => (e.Actor, e.RunId)).Single());
            Assert.Equal(2, tools.Journal.Entries.Count(e => e.Kind == "tool-denied" && e.Actor == "trader-requests"));
            await app.StopAsync();
        }
    }

    [Fact]
    public void Context_WithEntra_UsesTheAuthenticatedRole_OrInvalid()
    {
        var tools = new ToolRig();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Request.Headers[ToolServerOptions.AgentHeader] = "analyst";
        var context = new ToolCallContext(http, tools.Specs, tools.Journal, Options(), tools.Rig.Time, NullLogger<ToolCallContext>.Instance, tools.AnalystSpecs);

        Assert.Equal("invalid", context.Agent); // no validated identity: never the header's name
        http.HttpContext.Items[ToolCallContext.AuthenticatedAgentItem] = "trader";
        Assert.Equal("trader", context.Agent);
        Assert.False(context.IsOperator);
    }

    [Fact]
    public void RoleFor_ReadsTheObjectId_AndNeedsTheAppRole()
    {
        var options = Options().Entra;
        var appRole = new Claim("roles", EntraToolServerOptions.AppRole);
        Assert.Equal("trader", options.RoleFor(new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", TraderId.ToUpperInvariant()), appRole]))));
        Assert.Null(options.RoleFor(new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", TraderId)]))));
        Assert.Null(options.RoleFor(new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", Guid.NewGuid().ToString()), appRole]))));
        Assert.Null(options.RoleFor(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(options.RoleFor(null));
    }

    [Fact]
    public void EntraOptions_AreValidated()
    {
        Options().Validate(); // any listening address, no operator key

        Assert.Throws<InvalidOperationException>(() => Changed(o => o.OperatorKey = ToolRig.OperatorKey).Validate());
        Assert.Throws<InvalidOperationException>(() => Changed(o => o.Entra.TenantId = "contoso").Validate());
        Assert.Throws<InvalidOperationException>(() => Changed(o => o.Entra.Audience = " ").Validate());
        Assert.Throws<InvalidOperationException>(() => Changed(o => o.Entra.Agents.Clear()).Validate());
        Assert.Throws<InvalidOperationException>(() => Changed(o => o.Entra.Agents["not-a-guid"] = "trader").Validate());
        Assert.Throws<InvalidOperationException>(() => Changed(o => o.Entra.Agents[Guid.NewGuid().ToString()] = "owner").Validate());
        Assert.Throws<InvalidOperationException>(() => Changed(o => o.Url = "https://0.0.0.0:5077").Validate());
    }
}
