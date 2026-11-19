using FreightRag.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace FreightRag.Tests.Api;

public class RateLimitSettingsTests
{
    private static IConfiguration Config(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:IngestPermitsPerMinute"] = value })
        .Build();

    [Fact]
    public void Defaults_to_30_uploads_per_minute()
    {
        Assert.Equal(30, RateLimitSettings.Load(Config(null)).IngestPermitsPerMinute);
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
