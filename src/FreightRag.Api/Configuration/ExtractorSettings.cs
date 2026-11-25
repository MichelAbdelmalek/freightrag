namespace FreightRag.Api.Configuration;

/// <summary>Where the isolated PDF extractor lives. Validated at startup so a typo fails fast, not per request.</summary>
public sealed record ExtractorSettings(Uri Url, TimeSpan Timeout)
{
    public static ExtractorSettings Load(IConfiguration configuration)
    {
        var rawUrl = configuration["Extractor:Url"];
        if (string.IsNullOrWhiteSpace(rawUrl))
            rawUrl = "http://extract:8091";
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Extractor:Url must be an absolute http(s) URL.");

        var rawSeconds = configuration["Extractor:TimeoutSeconds"];
        var seconds = 240;
        if (!string.IsNullOrWhiteSpace(rawSeconds) && (!int.TryParse(rawSeconds, out seconds) || seconds is < 10 or > 900))
            throw new InvalidOperationException("Extractor:TimeoutSeconds must be a whole number between 10 and 900.");

        return new ExtractorSettings(url, TimeSpan.FromSeconds(seconds));
    }
}
