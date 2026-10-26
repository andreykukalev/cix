using Azure.Core;
using Cix.Engine.Persistence;
using Microsoft.Extensions.Configuration;

namespace Cix.Engine.Tests;

public sealed class DeploymentConfigTests
{
    [Fact]
    public void SecretFiles_ReadOnlyTheAllowedNames()
    {
        var dir = Directory.CreateTempSubdirectory("cix-secrets-").FullName;
        try
        {
            var key = new string('k', 32); // built at runtime: not a secret
            File.WriteAllText(Path.Combine(dir, "ToolServer__OperatorKey"), key + Environment.NewLine);
            File.WriteAllText(Path.Combine(dir, "Trading__Mode"), "Live");
            File.WriteAllText(Path.Combine(dir, "Risk__MaxOrderQuote"), "100000");

            var config = new ConfigurationBuilder().AddCixSecretFiles(dir).Build();

            Assert.Equal(key, config["ToolServer:OperatorKey"]);
            Assert.Null(config["Trading:Mode"]);
            Assert.Null(config["Risk:MaxOrderQuote"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SecretFiles_MissingOrRelativeDirectory_IsIgnored()
    {
        var config = new ConfigurationBuilder()
            .AddCixSecretFiles(Path.Combine(Path.GetTempPath(), "cix-no-such-dir-" + Guid.NewGuid().ToString("N")))
            .AddCixSecretFiles("relative/secrets")
            .Build();

        Assert.Empty(config.AsEnumerable());
    }

    [Theory]
    [InlineData("https://localhost:8081", true)]
    [InlineData("https://127.0.0.1:8081", true)]
    [InlineData("https://cosmos:8081", true)]           // the Compose service
    [InlineData("https://othername:8081", false)]          // a single-label name could resolve via DNS suffixes
    [InlineData("https://cosmos.example.com:8081", false)]
    [InlineData("https://10.0.0.5:8081", false)]
    public void EmulatorEndpoint_MustBeLocal(string endpoint, bool allowed)
    {
        var options = new CosmosOptions { Enabled = true, Emulator = true, Endpoint = endpoint };

        if (allowed)
            options.Validate();
        else
            Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private const string IdentityClientId = "3f2b8c1e-5d4a-4e6f-9a7b-1c2d3e4f5a6b";

    [Fact]
    public void ManagedIdentity_ReplacesTheAccountKey()
    {
        // As Azure sets it: environment variables Cosmos__..., read through configuration.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cosmos:Enabled"] = "true",
            ["Cosmos:Endpoint"] = "https://cosmos-shared-x.documents.azure.com:443/",
            ["Cosmos:Database"] = "cix",
            ["Cosmos:ManagedIdentityClientId"] = IdentityClientId,
        }).Build();
        var options = config.GetSection(CosmosOptions.SectionName).Get<CosmosOptions>()!;

        options.Validate();
        Assert.True(options.UsesIdentity);
        Assert.Null(options.AccountKey);
        using var db = new CosmosDb(options, new NoTokens()); // builds the client only: nothing is called yet
        Assert.False(db.RequestsAvailable);
    }

    [Theory]
    [InlineData("engine", false, false)]          // a name, not the client ID
    [InlineData(IdentityClientId, true, false)]   // the emulator doesn't take Entra tokens
    [InlineData(IdentityClientId, false, true)]   // a key as well: which one would be used?
    public void ManagedIdentity_Refused(string clientId, bool emulator, bool withKey)
    {
        var options = new CosmosOptions
        {
            Enabled = true,
            Emulator = emulator,
            Endpoint = emulator ? "https://localhost:8081" : "https://cosmos-shared-x.documents.azure.com:443/",
            ManagedIdentityClientId = clientId,
            AccountKey = withKey ? new string('k', 88) : null, // built at runtime: not a secret
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void NoKeyAndNoIdentity_IsRefusedOutsideTheEmulator()
    {
        var options = new CosmosOptions { Enabled = true, Endpoint = "https://cosmos-shared-x.documents.azure.com:443/" };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ManagedIdentityClientId", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A credential that must never be asked: these tests make no calls.</summary>
    private sealed class NoTokens : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no token expected");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no token expected");
    }
}
