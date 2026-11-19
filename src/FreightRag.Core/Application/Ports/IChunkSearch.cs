using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Ports;

/// <summary>Port: nearest-neighbour search over stored chunks.</summary>
public interface IChunkSearch
{
    /// <summary>
    /// Returns only chunks of <paramref name="tenantId"/> embedded with <paramref name="model"/>, ordered by
    /// cosine distance to <paramref name="query"/> (closest first), at most <paramref name="topK"/>.
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        TenantId tenantId, EmbeddingModelName model, EmbeddingVector query, TopK topK, CancellationToken cancellationToken);
}
