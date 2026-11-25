using FreightRag.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace FreightRag.Tests.Api;

public class ExtractorSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Defaults_to_the_compose_service_with_a_timeout_above_the_extractors_own_deadline()
    {
        var settings = ExtractorSettings.Load(Config());

        Assert.Equal("http://extract:8091/", settings.Url.ToString());
        Assert.True(settings.Timeout > TimeSpan.FromSeconds(180));
    }

    [Theory]
    [InlineData("Extractor:Url", "not a url")]
    [InlineData("Extractor:Url", "ftp://extract")]
    [InlineData("Extractor:TimeoutSeconds", "abc")]
    [InlineData("Extractor:TimeoutSeconds", "5")]
    [InlineData("Extractor:TimeoutSeconds", "100000")]
    public void Invalid_values_fail_at_startup_and_name_the_setting(string key, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ExtractorSettings.Load(Config((key, value))));

        Assert.Contains(key, error.Message);
    }
}
