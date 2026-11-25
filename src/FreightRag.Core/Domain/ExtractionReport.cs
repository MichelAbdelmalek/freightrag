namespace FreightRag.Core.Domain;

/// <summary>How the text of one PDF page was obtained.</summary>
public sealed record PageReport(int Page, string Method, int CharacterCount, double? MeanConfidence, int Dpi)
{
    public const string TextLayer = "text_layer";
    public const string Ocr = "ocr";
}

/// <summary>
/// What the extractor did to turn a PDF into text. Numbers and codes only: it never holds document content,
/// so it is safe to return to the caller and to log.
/// </summary>
public sealed record ExtractionReport
{
    public const int MaxPages = 20;

    private ExtractionReport() { }

    public int PageCount { get; private init; }
    public int OcrPageCount { get; private init; }
    public int TextLayerPageCount { get; private init; }
    public int CharacterCount { get; private init; }

    /// <summary>Mean OCR word confidence (0-100); null when every page came from the text layer.</summary>
    public double? MeanOcrConfidence { get; private init; }

    public int HeadingCount { get; private init; }
    public int NumericCorrections { get; private init; }
    public int LowConfidenceNumericTokens { get; private init; }
    public int DurationMs { get; private init; }
    public IReadOnlyList<PageReport> Pages { get; private init; } = [];

    /// <summary>Codes such as low_confidence, no_headings, numeric_tokens_low_confidence.</summary>
    public IReadOnlyList<string> Warnings { get; private init; } = [];

    public bool LowConfidence => Warnings.Contains("low_confidence");

    public static ExtractionReport Create(
        int pageCount, int ocrPageCount, int textLayerPageCount, int characterCount, double? meanOcrConfidence,
        int headingCount, int numericCorrections, int lowConfidenceNumericTokens, int durationMs,
        IReadOnlyList<PageReport> pages, IReadOnlyList<string> warnings)
    {
        if (pageCount is < 1 or > MaxPages)
            throw new DomainException($"A PDF must have 1 to {MaxPages} pages, not {pageCount}.");
        if (new[] { ocrPageCount, textLayerPageCount, characterCount, headingCount, numericCorrections, lowConfidenceNumericTokens, durationMs }.Any(n => n < 0))
            throw new DomainException("Extraction counts cannot be negative.");
        if (meanOcrConfidence is < 0 or > 100)
            throw new DomainException("OCR confidence must be between 0 and 100.");
        if (pages.Any(p => p.MeanConfidence is < 0 or > 100))
            throw new DomainException("OCR confidence must be between 0 and 100.");
        if (pages.Any(p => p.Method == PageReport.TextLayer && p.MeanConfidence is not null))
            throw new DomainException("A text-layer page has no OCR confidence.");

        return new ExtractionReport
        {
            PageCount = pageCount, OcrPageCount = ocrPageCount, TextLayerPageCount = textLayerPageCount,
            CharacterCount = characterCount, MeanOcrConfidence = meanOcrConfidence, HeadingCount = headingCount,
            NumericCorrections = numericCorrections, LowConfidenceNumericTokens = lowConfidenceNumericTokens,
            DurationMs = durationMs, Pages = pages, Warnings = warnings,
        };
    }

    /// <summary>True when OCR was used and its mean confidence is below <paramref name="threshold"/>.</summary>
    public bool IsLowConfidence(double threshold) => MeanOcrConfidence is { } mean && mean < threshold;
}
