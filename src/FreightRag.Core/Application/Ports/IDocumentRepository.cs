using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Ports;

/// <summary>
/// Port (DDD/hexagonal): how the application stores freight documents. The application owns this
/// interface; Infrastructure provides the Postgres implementation, tests provide an in-memory one.
/// </summary>
public interface IDocumentRepository
{
    /// <summary>True if this tenant already has a document with exactly these bytes.</summary>
    Task<bool> ExistsAsync(TenantId tenantId, SourceHash sourceHash, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the document and all its chunks atomically (all or nothing).
    /// Returns false — and stores nothing — if (tenant, source hash) already exists, e.g. because a
    /// concurrent run inserted it between <see cref="ExistsAsync"/> and this call.
    /// </summary>
    Task<bool> AddAsync(FreightDocument document, CancellationToken cancellationToken);
}
