namespace FreightRag.Api.Configuration;

/// <summary>Upload rate limit per tenant, validated at startup (a typo must not turn every upload into a 500).</summary>
public sealed record RateLimitSettings(int IngestPermitsPerMinute)
{
    private const string Key = "RateLimiting:IngestPermitsPerMinute";
    private const int Default = 30;
    private const int Max = 1_000_000;

    public static RateLimitSettings Load(IConfiguration configuration)
    {
        var raw = configuration[Key];
        if (string.IsNullOrWhiteSpace(raw))
            return new RateLimitSettings(Default);
        if (!int.TryParse(raw, out var permits) || permits is < 1 or > Max)
            throw new InvalidOperationException($"{Key} must be a whole number between 1 and {Max}.");
        return new RateLimitSettings(permits);
    }
}
