using FreightRag.Core.Application.Ports;
using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Search;

/// <summary>Query: find the chunks of the caller's tenant that best match a question.</summary>
/// <param name="TenantSlug">From the authenticated caller (API key), never from the request body.</param>
/// <param name="Query">The question; 1–1000 characters after trimming.</param>
/// <param name="TopK">How many chunks to return, 1–20 (default 5).</param>
/// <param name="Mode">"vector", "keyword" or "hybrid" (default).</param>
public sealed record SearchChunksQuery(string TenantSlug, string? Query, int? TopK, string? Mode = null);

/// <summary>Outcome of a search. Infrastructure failures (Ollama/DB down) are exceptions instead.</summary>
public abstract record SearchResult
{
    private SearchResult() { }

    /// <summary>Hits ordered by fused score (best first), plus how many candidates each leg produced.</summary>
    public sealed record Found(IReadOnlyList<SearchHit> Hits, int VectorCandidates, int KeywordCandidates) : SearchResult;

    /// <summary>The input was not acceptable; nothing was embedded or searched.</summary>
    public sealed record Invalid(string Reason) : SearchResult;
}

/// <summary>
/// Use case: validate → resolve tenant → embed the question → retrieve candidates from the legs the mode asks for
/// (vector by meaning, keyword by words) → fuse their rankings with Reciprocal Rank Fusion → top K.
/// The query is embedded in every mode: even keyword-only results report their cosine distance.
/// </summary>
public sealed class SearchChunksHandler(
    ITenantDirectory tenants,
    IEmbeddingService embeddings,
    IChunkSearch search,
    ReciprocalRankFusion fusion,
    CandidateCount candidates)
{
    private const string VectorList = "vector";
    private const string KeywordList = "keyword";

    public async Task<SearchResult> HandleAsync(SearchChunksQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        SearchText text;
        TopK topK;
        SearchMode mode;
        try
        {
            text = SearchText.Create(query.Query);
            topK = TopK.Create(query.TopK);
            mode = SearchMode.Parse(query.Mode);
        }
        catch (DomainException ex)
        {
            return new SearchResult.Invalid(ex.Message);
        }

        if (!tenants.TryResolve(query.TenantSlug, out var tenantId))
            return new SearchResult.Invalid($"No tenant mapping for '{query.TenantSlug}'.");

        var vector = await embeddings.EmbedQueryAsync(text, cancellationToken);
        var lists = await search.RetrieveCandidatesAsync(tenantId, embeddings.Model, vector, text, mode, candidates, cancellationToken);

        // Only the legs the mode asked for take part, even if an implementation returned more.
        var vectorList = mode.UsesVector ? lists.Vector : [];
        var keywordList = mode.UsesKeyword ? lists.Keyword : [];

        var byId = new Dictionary<Guid, ChunkCandidate>();
        foreach (var candidate in vectorList.Concat(keywordList))
            byId.TryAdd(candidate.ChunkId, candidate);

        var ranked = new List<RankedList>(2);
        if (mode.UsesVector)
            ranked.Add(new RankedList(VectorList, vectorList.Select(c => c.ChunkId).ToList()));
        if (mode.UsesKeyword)
            ranked.Add(new RankedList(KeywordList, keywordList.Select(c => c.ChunkId).ToList()));

        var hits = fusion.Fuse(ranked)
            .Take(topK.Value)
            .Select(f =>
            {
                var c = byId[f.ChunkId];
                return new SearchHit(c.ChunkId, c.DocumentId, c.Title, c.DocType, c.ChunkIndex, c.Content, c.Distance,
                    f.RankIn(VectorList), f.RankIn(KeywordList), f.Score);
            })
            .ToList();

        return new SearchResult.Found(hits, vectorList.Count, keywordList.Count);
    }
}
