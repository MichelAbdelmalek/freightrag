using FreightRag.Core.Application.Ports;
using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Queries;

/// <summary>Outcome of reading one document. Closed set: found, not found, or a bad field request.</summary>
public abstract record GetDocumentResult
{
    private GetDocumentResult() { }

    public sealed record Found(DocumentDetails Details, ExtractedFields Fields) : GetDocumentResult;

    /// <summary>Missing, or another tenant's: indistinguishable on purpose.</summary>
    public sealed record NotFound : GetDocumentResult;

    public sealed record InvalidFields(FieldSelectionError Error) : GetDocumentResult;
}

/// <summary>
/// Reads a document with its fields. Fields are not stored: they are read again from the stored text, which is cheap and
/// deterministic, so asking later gives the same answer as the upload did. Order matters: the tenant check comes first, so a
/// bad "fields" value can never reveal that an id exists in another tenant.
/// </summary>
public sealed class GetDocumentHandler(IDocumentQueries queries)
{
    public async Task<GetDocumentResult> HandleAsync(TenantId tenantId, Guid documentId, string? fields, CancellationToken cancellationToken)
    {
        var details = await queries.GetAsync(tenantId, documentId, cancellationToken);
        if (details is null)
            return new GetDocumentResult.NotFound();

        // A stored document always has one of the four types; if not, there are simply no fields to read.
        if (!DocType.TryFromName(details.DocType, out var type))
            return new GetDocumentResult.Found(details, new ExtractedFields([]));

        if (!FieldSelection.TryParse(fields, type, out var selection, out var error))
            return new GetDocumentResult.InvalidFields(error!);

        var text = StoredDocumentText.Reassemble(details.Chunks.OrderBy(c => c.Index).Select(c => c.Content));
        return new GetDocumentResult.Found(details, DocumentFieldExtractor.Extract(text, selection!));
    }
}
