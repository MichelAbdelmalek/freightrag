using System.Text;
using FreightRag.Core.Application.Ports;
using FreightRag.Core.Domain;

namespace FreightRag.Core.Application;

/// <summary>Command: ingest one file for one tenant.</summary>
/// <param name="TenantSlug">The data sub-folder name, e.g. "tenant-nile-freight".</param>
/// <param name="FileName">The file name, e.g. "rate-confirmation-48213.txt" (doc type is inferred from it).</param>
/// <param name="Content">The raw file bytes (hashed as-is).</param>
public sealed record IngestDocumentCommand(string TenantSlug, string FileName, ReadOnlyMemory<byte> Content);

/// <summary>
/// Application service (use case): orchestrates the domain and the ports. It contains no business
/// rules of its own — those live in the domain — and no technology — that lives in Infrastructure.
/// </summary>
public sealed class IngestDocumentHandler(
    ITenantDirectory tenants,
    IDocumentRepository repository,
    IEmbeddingService embeddings,
    SectionChunker chunker,
    IPdfTextExtractor pdfExtractor)
{
    // Strict decoder: invalid UTF-8 throws instead of silently becoming '�'.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<IngestResult> HandleAsync(IngestDocumentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!tenants.TryResolve(command.TenantSlug, out var tenantId))
            return new IngestResult.Rejected($"No tenant mapping for '{command.TenantSlug}'.", "unknown_tenant");

        if (!DocType.TryFromFileName(command.FileName, out var docType))
            return new IngestResult.Rejected(
                $"Unknown document type for '{command.FileName}'. Expected a name starting with: "
                + string.Join(", ", DocType.All.Select(t => t.Name)) + ".", "unknown_doc_type");

        // A name without a known extension is treated as text, as before. A .pdf must really be a PDF.
        var format = SourceFormat.TryFromFileName(command.FileName, out var known) ? known : SourceFormat.Text;
        if (format == SourceFormat.Pdf && !SourceFormat.HasPdfSignature(command.Content.Span))
            return new IngestResult.Rejected($"'{command.FileName}' is not a PDF file (it does not start with %PDF-).", "not_pdf");

        // 1. Idempotency check BEFORE the expensive embedding call.
        var sourceHash = SourceHash.FromContent(command.Content.Span);
        if (await repository.ExistsAsync(tenantId, sourceHash, cancellationToken))
            return new IngestResult.Skipped("Already ingested for this tenant.");

        // 2. Get the text: decode it (.txt) or ask the isolated extractor (.pdf; OCR for scanned pages). Then build
        //    the aggregate (identifiers + chunks). The extractor gets only the bytes, never the tenant or the name.
        string text;
        ExtractionReport? extraction = null;
        if (format == SourceFormat.Pdf)
        {
            switch (await pdfExtractor.ExtractAsync(command.Content, cancellationToken))
            {
                case PdfExtraction.Extracted extracted:
                    text = extracted.Text;
                    extraction = extracted.Report;
                    break;
                case PdfExtraction.Refused refused:
                    return new IngestResult.Rejected(RefusalMessage(refused.Code, command.FileName), refused.Code);
                default:
                    throw new InvalidOperationException("Unexpected extraction result.");
            }
        }
        else
        {
            try
            {
                text = StrictUtf8.GetString(command.Content.Span).TrimStart('\uFEFF'); // drop a byte-order mark
            }
            catch (DecoderFallbackException)
            {
                return new IngestResult.Rejected($"'{command.FileName}' is not valid UTF-8 text.", "invalid_text");
            }
        }

        // Postgres TEXT cannot store U+0000. Reject now, before paying for embeddings, instead of
        // failing the INSERT afterwards.
        if (text.Contains('\0'))
            return new IngestResult.Rejected($"'{command.FileName}' contains NUL characters, which cannot be stored.", "invalid_text");

        FreightDocument document;
        try
        {
            document = FreightDocument.Create(tenantId, command.FileName, docType, sourceHash, text, chunker);
        }
        catch (DomainException ex)
        {
            return new IngestResult.Rejected(ex.Message, "no_content");
        }

        // 3. Embed every chunk in one batch, then let the aggregate check and attach the vectors.
        var vectors = await embeddings.EmbedDocumentChunksAsync(
            document.Chunks.Select(c => c.Content).ToList(), cancellationToken);
        document.AttachEmbeddings(vectors, embeddings.Model);

        // 4. Store document + chunks in one transaction.
        var added = await repository.AddAsync(document, cancellationToken);
        return added
            ? new IngestResult.Ingested(document.Id, document.Chunks.Count, format, extraction)
            : new IngestResult.Skipped("Ingested concurrently by another run.");
    }

    /// <summary>Fixed text per refusal code. It never includes anything read from the document.</summary>
    private static string RefusalMessage(string code, string fileName) => code switch
    {
        "encrypted_pdf" => $"'{fileName}' is encrypted or password-protected.",
        "too_many_pages" => $"'{fileName}' has more than {ExtractionReport.MaxPages} pages.",
        "page_too_large" => $"'{fileName}' has a page that is too large.",
        "no_text" => $"No text could be read from '{fileName}'.",
        "unreadable" => $"'{fileName}' is too blurry or too faint to read reliably.",
        "extraction_limit_exceeded" => $"'{fileName}' took too long or too much memory to read.",
        "not_pdf" => $"'{fileName}' is not a PDF file.",
        _ => $"'{fileName}' is a damaged or unsupported PDF.",
    };
}
