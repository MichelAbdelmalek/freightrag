namespace FreightRag.Core.Application.Queries;

// Read models: flat, query-shaped data for display. They are deliberately NOT the domain aggregate
// (FreightDocument) — reads don't need invariants, and loading 768 floats per chunk to show a list
// would be wasteful. This split is "CQRS-lite": commands go through the domain, queries don't.

/// <summary>One row in a document list.</summary>
public sealed record DocumentSummary(Guid Id, string Title, string DocType, DateTimeOffset CreatedAt, int ChunkCount);

/// <summary>A stored chunk as text (the embedding vector is not returned).</summary>
public sealed record ChunkText(int Index, string Content);

/// <summary>A document with all its chunks, in order.</summary>
public sealed record DocumentDetails(
    Guid Id, string Title, string DocType, DateTimeOffset CreatedAt, IReadOnlyList<ChunkText> Chunks);

/// <summary>A chunk retrieved by one search leg, before fusion.</summary>
/// <param name="Distance">Cosine distance between the query embedding and the chunk (computed by every leg).</param>
public sealed record ChunkCandidate(
    Guid ChunkId, Guid DocumentId, string Title, string DocType, int ChunkIndex, string Content, double Distance);

/// <summary>What each retrieval leg returned, best first. A leg that did not run returns an empty list.</summary>
public sealed record CandidateLists(IReadOnlyList<ChunkCandidate> Vector, IReadOnlyList<ChunkCandidate> Keyword);

/// <summary>One search result after fusion.</summary>
/// <param name="Distance">Cosine distance between query and chunk embedding: 0 = same direction, 2 = opposite. Lower is closer.</param>
/// <param name="VectorRank">1-based rank in the vector leg, or null if the chunk was not among its candidates.</param>
/// <param name="KeywordRank">1-based rank in the keyword leg, or null if the chunk was not among its candidates.</param>
/// <param name="FusedScore">Reciprocal Rank Fusion score; results are ordered by it.</param>
public sealed record SearchHit(
    Guid ChunkId, Guid DocumentId, string Title, string DocType, int ChunkIndex, string Content, double Distance,
    int? VectorRank, int? KeywordRank, double FusedScore);
