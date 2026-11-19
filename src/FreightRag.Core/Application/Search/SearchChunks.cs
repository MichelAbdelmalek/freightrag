using FreightRag.Core.Application.Ports;
using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Search;

/// <summary>Query: find the chunks of the caller's tenant closest in meaning to a question.</summary>
/// <param name="TenantSlug">From the authenticated caller (API key), never from the request body.</param>
/// <param name="Query">The question; 1–1000 characters after trimming.</param>
/// <param name="TopK">How many chunks to return, 1–20 (default 5).</param>
public sealed record SearchChunksQuery(string TenantSlug, string? Query, int? TopK);

/// <summary>Outcome of a search. Infrastructure failures (Ollama/DB down) are exceptions instead.</summary>
public abstract record SearchResult
{
    private SearchResult() { }

    /// <summary>Hits ordered by distance (closest first). Can be empty: there is no relevance threshold.</summary>
    public sealed record Found(IReadOnlyList<SearchHit> Hits) : SearchResult;

    /// <summary>The input was not acceptable; nothing was embedded or searched.</summary>
    public sealed record Invalid(string Reason) : SearchResult;
}

/// <summary>
/// Use case: validate → resolve tenant → embed the question with the configured model → nearest-neighbour search
/// restricted to that tenant and that model. Returns ranked chunks only; answering is a separate service's job.
/// </summary>
public sealed class SearchChunksHandler(ITenantDirectory tenants, IEmbeddingService embeddings, IChunkSearch search)
{
    public async Task<SearchResult> HandleAsync(SearchChunksQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        SearchText text;
        TopK topK;
        try
        {
            text = SearchText.Create(query.Query);
            topK = TopK.Create(query.TopK);
        }
        catch (DomainException ex)
        {
            return new SearchResult.Invalid(ex.Message);
        }

        if (!tenants.TryResolve(query.TenantSlug, out var tenantId))
            return new SearchResult.Invalid($"No tenant mapping for '{query.TenantSlug}'.");

        // Same model as the stored chunks: vectors from different models are not comparable.
        var vector = await embeddings.EmbedQueryAsync(text, cancellationToken);
        var hits = await search.SearchAsync(tenantId, embeddings.Model, vector, topK, cancellationToken);
        return new SearchResult.Found(hits);
    }
}
