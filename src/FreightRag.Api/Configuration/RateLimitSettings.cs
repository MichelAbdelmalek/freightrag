namespace FreightRag.Api.Configuration;

/// <summary>
/// Per-tenant request limits, validated at startup (a typo must not turn every request into a 500).
/// Both uploads and searches call the embedding model, so both are limited.
/// </summary>
public sealed record RateLimitSettings(int IngestPermitsPerMinute, int SearchPermitsPerMinute)
{
    private const int Max = 1_000_000;

    public static RateLimitSettings Load(IConfiguration configuration) => new(
        Read(configuration, "RateLimiting:IngestPermitsPerMinute", defaultValue: 30),
        Read(configuration, "RateLimiting:SearchPermitsPerMinute", defaultValue: 60));

    private static int Read(IConfiguration configuration, string key, int defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (!int.TryParse(raw, out var permits) || permits is < 1 or > Max)
            throw new InvalidOperationException($"{key} must be a whole number between 1 and {Max}.");
        return permits;
    }
}
