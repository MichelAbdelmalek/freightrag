using FreightRag.Core.Domain;

namespace FreightRag.Tests.Domain;

public class ExtractionReportTests
{
    private static ExtractionReport Make(int pages = 1, double? confidence = 90, IReadOnlyList<PageReport>? pageReports = null) =>
        ExtractionReport.Create(pages, 1, 0, 100, confidence, 2, 0, 0, 500, pageReports ?? [new PageReport(1, PageReport.Ocr, 100, 90, 300)], []);

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void The_page_count_must_be_1_to_20(int pages) => Assert.Throws<DomainException>(() => Make(pages));

    [Theory]
    [InlineData(-1)]
    [InlineData(100.5)]
    public void Confidence_must_be_0_to_100(double confidence) => Assert.Throws<DomainException>(() => Make(confidence: confidence));

    [Fact]
    public void A_text_layer_page_has_no_ocr_confidence() =>
        Assert.Throws<DomainException>(() => Make(pageReports: [new PageReport(1, PageReport.TextLayer, 10, 90, 0)]));

    [Fact]
    public void Low_confidence_uses_the_threshold_and_is_false_when_no_page_needed_ocr()
    {
        Assert.True(Make(confidence: 70).IsLowConfidence(80));
        Assert.False(Make(confidence: 90).IsLowConfidence(80));
        Assert.False(Make(confidence: null, pageReports: [new PageReport(1, PageReport.TextLayer, 10, null, 0)]).IsLowConfidence(80));
    }
}
