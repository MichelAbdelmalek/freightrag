using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Ports;

/// <summary>
/// Port for reading stored documents. Every method takes the caller's <see cref="TenantId"/>:
/// tenant isolation is part of the contract, so an implementation can't "forget" the filter.
/// </summary>
public interface IDocumentQueries
{
    /// <summary>The tenant's documents, newest first.</summary>
    Task<PagedResult<DocumentSummary>> ListAsync(TenantId tenantId, PageRequest page, CancellationToken cancellationToken);

    /// <summary>The document, or null if it doesn't exist <b>or belongs to another tenant</b> (indistinguishable on purpose).</summary>
    Task<DocumentDetails?> GetAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken);
}
