using System.Text.Json.Nodes;

namespace Cix.Engine.Tests;

/// <summary>
/// The strategy spec files as the tests that need the older specs (good v1, safe v1 and v2, balanced v1 and v2, risky v1 to v3)
/// expect them. The repository's files keep only the current presets marked live; the older ones are drafts, so the tests read
/// a copy with the statuses those specs had while they were offered, written once per run to a temporary folder.
/// </summary>
internal static class StrategyFixtures
{
    private static readonly Dictionary<string, string> LegacyStatus = new(StringComparer.Ordinal)
    {
        ["safe.v1"] = "paper", ["safe.v2"] = "live",
        ["balanced.v1"] = "paper", ["balanced.v2"] = "live",
        ["risky.v1"] = "paper", ["risky.v2"] = "paper", ["risky.v3"] = "live",
        ["good.v1"] = "paper",
    };

    private static readonly Lazy<string> Copy = new(Build);

    /// <summary>The folder with the fixture copies, in the layout of the engine's <c>strategies</c> folder.</summary>
    internal static string Folder => Copy.Value;

    internal static string Path(string name, int version) => System.IO.Path.Combine(Folder, $"{name}.v{version}.json");

    private static string Build()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cix-strategy-fixtures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(System.IO.Path.Combine(AppContext.BaseDirectory, "strategies"), "*.json"))
        {
            var target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(file));
            if (LegacyStatus.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(file), out var status))
            {
                var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                node["status"] = status;
                File.WriteAllText(target, node.ToJsonString());
            }
            else
                File.Copy(file, target);
        }

        return folder;
    }
}
