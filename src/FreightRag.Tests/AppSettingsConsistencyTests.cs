using System.Text.Json.Nodes;

namespace FreightRag.Tests;

/// <summary>
/// The console ingest, the API and the migrator each ship their own appsettings.json. The embedding model, the
/// database and the tenant map must be identical in all three: the migrator backfills chunks.embedding_model with
/// its model name, and the API only searches chunks of its own model — a mismatch would hide every existing chunk.
/// </summary>
public class AppSettingsConsistencyTests
{
    private static readonly string[] Hosts = ["FreightRag.Ingest", "FreightRag.Api", "FreightRag.Migrator"];

    public static TheoryData<string> SharedSections() => new() { "Ollama", "Database", "Tenants" };

    [Theory]
    [MemberData(nameof(SharedSections))]
    public void All_hosts_agree_on(string section)
    {
        var values = Hosts.ToDictionary(h => h, h => Load(h)[section]?.ToJsonString());

        Assert.All(values, v => Assert.False(string.IsNullOrEmpty(v.Value), $"{v.Key} has no {section} section"));
        Assert.Single(values.Values.Distinct());
    }

    private static JsonNode Load(string host)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", host, "appsettings.json");
            if (File.Exists(path))
                return JsonNode.Parse(File.ReadAllText(path))!;
        }
        throw new InvalidOperationException($"appsettings.json of {host} not found.");
    }
}
