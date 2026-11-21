using FreightRag.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace FreightRag.Tests.Api;

public class SearchSettingsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Defaults_to_20_candidates_k_60_and_20000_scan_tuples()
    {
        var settings = SearchSettings.Load(Config([]));

        Assert.Equal(20, settings.Candidates.Value);
        Assert.Equal(60, settings.Fusion.K);
        Assert.Equal(20_000, settings.ChunkSearch.MaxScanTuples);
    }

    [Fact]
    public void Reads_configured_values()
    {
        var settings = SearchSettings.Load(Config(new()
        {
            ["Search:CandidateCount"] = "50", ["Search:RrfK"] = "10", ["Search:MaxScanTuples"] = "40000",
        }));

        Assert.Equal((50, 10, 40_000), (settings.Candidates.Value, settings.Fusion.K, settings.ChunkSearch.MaxScanTuples));
    }

    [Theory]
    [InlineData("Search:CandidateCount", "10")]
    [InlineData("Search:CandidateCount", "abc")]
    [InlineData("Search:RrfK", "0")]
    [InlineData("Search:MaxScanTuples", "0")]
    public void Invalid_values_are_a_startup_error_naming_the_setting(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SearchSettings.Load(Config(new() { [key] = value })));
        Assert.Contains(key, ex.Message);
    }
}
