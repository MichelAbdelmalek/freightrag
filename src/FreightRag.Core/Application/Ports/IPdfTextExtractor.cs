using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Ports;

/// <summary>
/// Port: turns a PDF into plain text (reading the text layer, or OCR for scanned pages). It receives the bytes and
/// nothing else: no tenant, no file name. The implementation lives in Infrastructure (an isolated extractor service).
/// </summary>
public interface IPdfTextExtractor
{
    /// <exception cref="PdfExtractorBusyException">The extractor is working and its queue is full.</exception>
    /// <exception cref="PdfExtractorUnavailableException">The extractor could not be reached.</exception>
    Task<PdfExtraction> ExtractAsync(ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken);
}

/// <summary>Outcome of one extraction. A refusal is a normal result (bad input), not an error.</summary>
public abstract record PdfExtraction
{
    private PdfExtraction() { }

    public sealed record Extracted(string Text, ExtractionReport Report) : PdfExtraction;

    /// <summary>The PDF itself cannot be used. <paramref name="Code"/> is a fixed category such as encrypted_pdf.</summary>
    public sealed record Refused(string Code) : PdfExtraction;
}

public sealed class PdfExtractorBusyException(TimeSpan retryAfter) : Exception("The PDF extractor is busy.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

public sealed class PdfExtractorUnavailableException(Exception? inner = null)
    : Exception("The PDF extractor is not available.", inner);
