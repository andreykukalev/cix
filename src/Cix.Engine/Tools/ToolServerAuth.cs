using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Cix.Engine.Tools;

/// <summary>How callers of the tool server are identified (ADR-0013).</summary>
public enum ToolServerAuth
{
    /// <summary>
    /// The default, for Docker Compose and <c>dotnet run</c>: the server listens on loopback only, so only processes sharing the
    /// engine's network reach it, and agents name themselves with <c>X-Cix-Agent</c>.
    /// </summary>
    Loopback,

    /// <summary>
    /// Azure: callers present an Entra access token from their managed identity, and the agent role comes from that identity,
    /// never from a header. Operator tools are off.
    /// </summary>
    Entra,
}

/// <summary>Bound from "ToolServer:Entra". Used only when <c>ToolServer:Auth</c> is <see cref="ToolServerAuth.Entra"/>.</summary>
public sealed class EntraToolServerOptions
{
    public const string Scheme = "cix-entra";

    /// <summary>The roles an identity can be given: one per agent job, each with its own tools (ToolCallContext).</summary>
    public static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.Ordinal)
    {
        "trader", ToolCallContext.RequestsAgent, ToolCallContext.AnalystAgent,
    };

    /// <summary>
    /// The app role every caller's token must carry (its <c>roles</c> claim). The engine's app registration defines it and
    /// requires an assignment, held by the three job identities only (tools/azure/provision.ps1), so no other identity in the
    /// tenant can even get a token for the engine; the identity allowlist below is checked as well.
    /// </summary>
    public const string AppRole = "CIX.Agent";

    /// <summary>The Entra tenant (directory) ID; tokens from any other tenant are refused.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The engine's application ID URI (for example <c>api://cix-engine-dev</c>), which the jobs request tokens for. Managed
    /// identity tokens for an app registration are v1 tokens, whose audience is this URI.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Managed identities allowed in, by object (principal) ID, each with its agent role: <c>trader</c>,
    /// <c>trader-requests</c> or <c>analyst</c>. Anyone else with a valid token for the audience gets 403.
    /// </summary>
    public Dictionary<string, string> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tests only: validates tokens against this key instead of Entra's published keys.</summary>
    internal SecurityKey? TestSigningKey { get; set; }

    public void Validate()
    {
        if (!Guid.TryParse(TenantId, out _))
            throw new InvalidOperationException("ToolServer:Entra:TenantId must be the tenant's ID (a GUID).");
        if (string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException("ToolServer:Entra:Audience must be the engine's application ID URI.");
        if (Agents.Count == 0)
            throw new InvalidOperationException("ToolServer:Entra:Agents must list at least one managed identity.");
        foreach (var (id, role) in Agents)
        {
            if (!Guid.TryParse(id, out _))
                throw new InvalidOperationException("ToolServer:Entra:Agents keys must be managed identity object IDs (GUIDs).");
            if (!Roles.Contains(role))
                throw new InvalidOperationException($"ToolServer:Entra:Agents values must be one of: {string.Join(", ", Roles.Order(StringComparer.Ordinal))}.");
        }
    }

    /// <summary>The agent role of a validated token's identity, or null if that identity isn't listed or lacks <see cref="AppRole"/>.</summary>
    public string? RoleFor(ClaimsPrincipal? principal) =>
        principal is not null && principal.HasClaim("roles", AppRole)
        && principal.FindFirst("oid")?.Value is { } oid && Agents.TryGetValue(oid, out var role) ? role : null;

    /// <summary>Token validation: Entra's keys for the tenant, both token versions' issuers, the audience, and the lifetime.</summary>
    public void Configure(JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.MapInboundClaims = false; // keep "oid" as Entra wrote it
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [$"https://sts.windows.net/{TenantId}/", $"https://login.microsoftonline.com/{TenantId}/v2.0"],
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };
        if (TestSigningKey is { } key)
        {
            options.Configuration = new OpenIdConnectConfiguration();
            options.TokenValidationParameters.IssuerSigningKey = key;
            return;
        }

        options.Authority = $"https://login.microsoftonline.com/{TenantId}/v2.0";
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256]; // what Entra signs with
    }
}
