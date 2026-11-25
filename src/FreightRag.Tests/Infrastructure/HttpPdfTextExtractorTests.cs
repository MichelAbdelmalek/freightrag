using System.Net;
using System.Text;
using FreightRag.Core.Application.Ports;
using FreightRag.Core.Infrastructure.Extraction;

namespace FreightRag.Tests.Infrastructure;

public class HttpPdfTextExtractorTests
{
    private const string Ok = """
        {"text":"RATE\nDetention: $50.00","report":{"pageCount":1,"ocrPageCount":1,"textLayerPageCount":0,"characterCount":22,
        "meanOcrConfidence":91.4,"lowConfidence":false,"headingCount":1,"numericCorrections":0,"lowConfidenceNumericTokens":0,
        "durationMs":800,"pages":[{"page":1,"method":"ocr","characterCount":22,"meanConfidence":91.4,"dpi":300}],"warnings":[]}}
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            Body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return respond(request);
        }
    }

    private static (HttpPdfTextExtractor, StubHandler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new HttpPdfTextExtractor(new HttpClient(handler) { BaseAddress = new Uri("http://extract:8091") }), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly byte[] Pdf = "%PDF-1.7 scan"u8.ToArray();

    [Fact]
    public async Task Posts_the_bytes_as_application_pdf_to_v1_extract_and_nothing_else()
    {
        var (extractor, stub) = Make(_ => Json(HttpStatusCode.OK, Ok));

        await extractor.ExtractAsync(Pdf, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, stub.Seen!.Method);
        Assert.Equal("http://extract:8091/v1/extract", stub.Seen.RequestUri!.ToString());
        Assert.Equal("application/pdf", stub.Seen.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(Pdf, stub.Body);
        Assert.False(stub.Seen.Headers.Contains("X-Api-Key")); // never forwards the caller's key
    }

    [Fact]
    public async Task A_200_becomes_the_text_and_a_validated_report()
    {
        var (extractor, _) = Make(_ => Json(HttpStatusCode.OK, Ok));

        var result = await extractor.ExtractAsync(Pdf, TestContext.Current.CancellationToken);

        var extracted = Assert.IsType<PdfExtraction.Extracted>(result);
        Assert.Equal("RATE\nDetention: $50.00", extracted.Text);
        Assert.Equal(91.4, extracted.Report.MeanOcrConfidence);
        Assert.Equal("ocr", Assert.Single(extracted.Report.Pages).Method);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "encrypted_pdf", "encrypted_pdf")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "too_many_pages", "too_many_pages")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "something_new", "corrupt_pdf")]   // unknown codes never pass through
    [InlineData(HttpStatusCode.UnsupportedMediaType, "evil <script>", "corrupt_pdf")]
    public async Task A_422_style_response_becomes_a_refusal_with_an_allow_listed_code(HttpStatusCode status, string sent, string expected)
    {
        var (extractor, _) = Make(_ => Json(status, $$"""{"code":"{{sent}}"}"""));

        var result = await extractor.ExtractAsync(Pdf, TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.IsType<PdfExtraction.Refused>(result).Code);
    }

    [Fact]
    public async Task A_503_means_busy_and_carries_retry_after()
    {
        var (extractor, _) = Make(_ =>
        {
            var response = Json(HttpStatusCode.ServiceUnavailable, "{}");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
            return response;
        });

        var busy = await Assert.ThrowsAsync<PdfExtractorBusyException>(() => extractor.ExtractAsync(Pdf, TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(30), busy.RetryAfter);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("server_error")]
    [InlineData("garbage_body")]
    [InlineData("invalid_report")]
    public async Task Failures_and_untrustworthy_answers_mean_unavailable(string kind)
    {
        var (extractor, _) = Make(_ => kind switch
        {
            "connection" => throw new HttpRequestException("refused"),
            "server_error" => Json(HttpStatusCode.InternalServerError, "{}"),
            "garbage_body" => Json(HttpStatusCode.OK, "not json"),
            _ => Json(HttpStatusCode.OK, Ok.Replace("\"pageCount\":1", "\"pageCount\":99")),   // violates the domain rules
        });

        await Assert.ThrowsAsync<PdfExtractorUnavailableException>(() => extractor.ExtractAsync(Pdf, TestContext.Current.CancellationToken));
    }
}
