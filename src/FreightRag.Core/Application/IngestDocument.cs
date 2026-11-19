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
    SectionChunker chunker)
{
    // Strict decoder: invalid UTF-8 throws instead of silently becoming '�'.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<IngestResult> HandleAsync(IngestDocumentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!tenants.TryResolve(command.TenantSlug, out var tenantId))
            return new IngestResult.Rejected($"No tenant mapping for '{command.TenantSlug}'.");

        if (!DocType.TryFromFileName(command.FileName, out var docType))
            return new IngestResult.Rejected(
                $"Unknown document type for '{command.FileName}'. Expected a name starting with: "
                + string.Join(", ", DocType.All.Select(t => t.Name)) + ".");

        // 1. Idempotency check BEFORE the expensive embedding call.
        var sourceHash = SourceHash.FromContent(command.Content.Span);
        if (await repository.ExistsAsync(tenantId, sourceHash, cancellationToken))
            return new IngestResult.Skipped("Already ingested for this tenant.");

        // 2. Decode and build the aggregate (identifiers + chunks).
        string text;
        try
        {
            text = StrictUtf8.GetString(command.Content.Span).TrimStart('\uFEFF'); // drop a byte-order mark
        }
        catch (DecoderFallbackException)
        {
            return new IngestResult.Rejected($"'{command.FileName}' is not valid UTF-8 text.");
        }

        // Postgres TEXT cannot store U+0000. Reject now, before paying for embeddings, instead of
        // failing the INSERT afterwards.
        if (text.Contains('\0'))
            return new IngestResult.Rejected($"'{command.FileName}' contains NUL characters, which cannot be stored.");

        FreightDocument document;
        try
        {
            document = FreightDocument.Create(tenantId, command.FileName, docType, sourceHash, text, chunker);
        }
        catch (DomainException ex)
        {
            return new IngestResult.Rejected(ex.Message);
        }

        // 3. Embed every chunk in one batch, then let the aggregate check and attach the vectors.
        var vectors = await embeddings.EmbedDocumentChunksAsync(
            document.Chunks.Select(c => c.Content).ToList(), cancellationToken);
        document.AttachEmbeddings(vectors, embeddings.Model);

        // 4. Store document + chunks in one transaction.
        var added = await repository.AddAsync(document, cancellationToken);
        return added
            ? new IngestResult.Ingested(document.Id, document.Chunks.Count)
            : new IngestResult.Skipped("Ingested concurrently by another run.");
    }
}
