using FreightRag.Core.Application.Ports;

namespace FreightRag.Core.Infrastructure.Extraction;

/// <summary>For hosts that never receive PDFs (the console ingest reads only .txt files): refuses every PDF.</summary>
public sealed class UnavailablePdfTextExtractor : IPdfTextExtractor
{
    public Task<PdfExtraction> ExtractAsync(ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken) =>
        Task.FromResult<PdfExtraction>(new PdfExtraction.Refused("corrupt_pdf"));
}
