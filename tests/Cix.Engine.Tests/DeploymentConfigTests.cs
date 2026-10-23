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
            File.WriteAllText(Path.Combine(dir, "ToolServer__OperatorKey"), key + "\n");
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
}
