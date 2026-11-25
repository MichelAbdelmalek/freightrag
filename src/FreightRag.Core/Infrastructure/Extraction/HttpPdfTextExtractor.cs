using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FreightRag.Core.Application.Ports;
using FreightRag.Core.Domain;

namespace FreightRag.Core.Infrastructure.Extraction;

/// <summary>
/// Adapter for the isolated extractor service (POST /v1/extract). It sends the PDF bytes and nothing else, and it
/// never logs a response body. Refusal codes are checked against an allow-list so a misbehaving service cannot put
/// arbitrary text into our error responses.
/// </summary>
public sealed class HttpPdfTextExtractor(HttpClient http) : IPdfTextExtractor
{
    private static readonly HashSet<string> RefusalCodes =
    [
        "not_pdf", "corrupt_pdf", "encrypted_pdf", "too_many_pages", "page_too_large", "no_text", "unreadable",
        "extraction_limit_exceeded",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PdfExtraction> ExtractAsync(ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken)
    {
        using var content = new ReadOnlyMemoryContent(pdf);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("/v1/extract", content, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PdfExtractorUnavailableException(ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PdfExtractorUnavailableException(ex); // the HTTP timeout, not the caller giving up
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                throw new PdfExtractorBusyException(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30));

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType
                or HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge)
                return new PdfExtraction.Refused(await ReadCodeAsync(response, cancellationToken));

            if (!response.IsSuccessStatusCode)
                throw new PdfExtractorUnavailableException();

            try
            {
                var body = await response.Content.ReadFromJsonAsync<ExtractBody>(Json, cancellationToken)
                           ?? throw new JsonException("Empty body.");
                var r = body.Report ?? throw new JsonException("Missing report.");
                var report = ExtractionReport.Create(
                    r.PageCount, r.OcrPageCount, r.TextLayerPageCount, r.CharacterCount, r.MeanOcrConfidence,
                    r.HeadingCount, r.NumericCorrections, r.LowConfidenceNumericTokens, r.DurationMs,
                    (r.Pages ?? []).Select(p => new PageReport(p.Page, p.Method ?? "", p.CharacterCount, p.MeanConfidence, p.Dpi)).ToList(),
                    r.Warnings ?? []);
                return new PdfExtraction.Extracted(body.Text ?? throw new JsonException("Missing text."), report);
            }
            catch (Exception ex) when (ex is JsonException or DomainException)
            {
                throw new PdfExtractorUnavailableException(ex); // the service answered with something we cannot trust
            }
        }
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Json, cancellationToken);
            return problem?.Code is { } code && RefusalCodes.Contains(code) ? code : "corrupt_pdf";
        }
        catch (JsonException)
        {
            return "corrupt_pdf";
        }
    }

    private sealed record ProblemBody(string? Code);

    private sealed record ExtractBody(string? Text, ReportBody? Report);

    private sealed record ReportBody(
        int PageCount, int OcrPageCount, int TextLayerPageCount, int CharacterCount, double? MeanOcrConfidence,
        int HeadingCount, int NumericCorrections, int LowConfidenceNumericTokens, int DurationMs,
        List<PageBody>? Pages, List<string>? Warnings);

    private sealed record PageBody(int Page, string? Method, int CharacterCount, double? MeanConfidence, int Dpi);
}
