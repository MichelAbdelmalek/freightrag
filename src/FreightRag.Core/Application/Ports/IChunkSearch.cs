using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Ports;

/// <summary>Port: retrieves search candidates from stored chunks.</summary>
public interface IChunkSearch
{
    /// <summary>
    /// Runs the legs <paramref name="mode"/> asks for — vector (nearest by <paramref name="query"/> embedding) and/or
    /// keyword (full-text match of <paramref name="text"/>) — each returning at most <paramref name="candidates"/>
    /// chunks of <paramref name="tenantId"/> embedded with <paramref name="model"/>, best first. Both legs read the same
    /// snapshot (one transaction). Every candidate carries its cosine distance to <paramref name="query"/>.
    /// </summary>
    Task<CandidateLists> RetrieveCandidatesAsync(
        TenantId tenantId, EmbeddingModelName model, EmbeddingVector query, SearchText text, SearchMode mode,
        CandidateCount candidates, CancellationToken cancellationToken);
}
