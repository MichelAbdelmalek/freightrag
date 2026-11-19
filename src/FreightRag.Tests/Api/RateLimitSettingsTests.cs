using FreightRag.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace FreightRag.Tests.Api;

public class RateLimitSettingsTests
{
    private static IConfiguration Config(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:IngestPermitsPerMinute"] = value })
        .Build();

    [Fact]
    public void Defaults_to_30_uploads_and_60_searches_per_minute()
    {
        var settings = RateLimitSettings.Load(Config(null));

        Assert.Equal(30, settings.IngestPermitsPerMinute);
        Assert.Equal(60, settings.SearchPermitsPerMinute);
    }

    [Fact]
    public void An_invalid_search_limit_is_a_startup_error()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:SearchPermitsPerMinute"] = "0" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => RateLimitSettings.Load(config));
        Assert.Contains("RateLimiting:SearchPermitsPerMinute", ex.Message);
    }

    [Fact]
    public void Reads_the_configured_value()
    {
        Assert.Equal(5, RateLimitSettings.Load(Config("5")).IngestPermitsPerMinute);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1000001")]
    public void Invalid_values_are_a_startup_error(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RateLimitSettings.Load(Config(value)));
        Assert.Contains("RateLimiting:IngestPermitsPerMinute", ex.Message);
    }
}
