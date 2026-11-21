using System.Diagnostics.CodeAnalysis;
using FreightRag.Core.Application.Ports;
using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;
using Microsoft.Extensions.AI;

namespace FreightRag.Tests.Support;

/// <summary>In-memory stand-in for the Postgres repository: same contract, no database.</summary>
internal sealed class InMemoryDocumentRepository : IDocumentRepository
{
    public List<FreightDocument> Stored { get; } = [];

    /// <summary>Simulates another process inserting the same document between our check and our insert.</summary>
    public bool SimulateConcurrentInsert { get; init; }

    public Task<bool> ExistsAsync(TenantId tenantId, SourceHash sourceHash, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.Any(d => d.TenantId == tenantId && d.SourceHash == sourceHash));

    public Task<bool> AddAsync(FreightDocument document, CancellationToken cancellationToken)
    {
        if (!document.IsReadyToStore)
            throw new InvalidOperationException("Repository received a document without embeddings.");
        if (SimulateConcurrentInsert || Stored.Any(d => d.TenantId == document.TenantId && d.SourceHash == document.SourceHash))
            return Task.FromResult(false);
        Stored.Add(document);
        return Task.FromResult(true);
    }
}

/// <summary>Returns deterministic 768-dimension vectors and records every call.</summary>
internal sealed class FakeEmbeddingService : IEmbeddingService
{
    public static readonly EmbeddingModelName FakeModel = new("fake-embed-model");

    public List<IReadOnlyList<string>> Calls { get; } = [];
    public List<SearchText> QueryCalls { get; } = [];

    public EmbeddingModelName Model => FakeModel;

    public Task<EmbeddingVector> EmbedQueryAsync(SearchText query, CancellationToken cancellationToken)
    {
        QueryCalls.Add(query);
        var v = new float[EmbeddingVector.Dimensions];
        v[0] = 1; // every query points the same way; searches are ordered by the stored vectors
        return Task.FromResult(new EmbeddingVector(v));
    }

    public Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentChunksAsync(
        IReadOnlyList<string> chunkContents, CancellationToken cancellationToken)
    {
        Calls.Add(chunkContents);
        IReadOnlyList<EmbeddingVector> vectors = chunkContents
            .Select((_, i) => { var v = new float[EmbeddingVector.Dimensions]; v[0] = i; return new EmbeddingVector(v); })
            .ToList();
        return Task.FromResult(vectors);
    }
}

internal sealed class DictionaryTenantDirectory(Dictionary<string, TenantId> tenants) : ITenantDirectory
{
    public bool TryResolve(string tenantSlug, [NotNullWhen(true)] out TenantId? tenantId) =>
        tenants.TryGetValue(tenantSlug, out tenantId);
}

/// <summary>
/// Fake Microsoft.Extensions.AI embedding generator: lets us test the Ollama adapter without Ollama.
/// </summary>
internal sealed class FakeEmbeddingGenerator(int dimensions = EmbeddingVector.Dimensions, int? overrideCount = null)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<List<string>> Calls { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inputs = values.ToList();
        Calls.Add(inputs);
        var count = overrideCount ?? inputs.Count;
        var embeddings = Enumerable.Range(0, count).Select(_ => new Embedding<float>(new float[dimensions]));
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Read side over <see cref="InMemoryDocumentRepository"/>, with the same tenant rules as Postgres.</summary>
internal sealed class InMemoryDocumentQueries(InMemoryDocumentRepository repository) : IDocumentQueries
{
    public static readonly DateTimeOffset StoredAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    public Task<PagedResult<DocumentSummary>> ListAsync(TenantId tenantId, PageRequest page, CancellationToken cancellationToken)
    {
        var mine = repository.Stored.Where(d => d.TenantId == tenantId).Reverse().ToList(); // newest first
        var items = mine.Skip(page.Offset).Take(page.PageSize)
            .Select(d => new DocumentSummary(d.Id, d.Title, d.DocType.Name, StoredAt, d.Chunks.Count))
            .ToList();
        return Task.FromResult(new PagedResult<DocumentSummary>(items, page, mine.Count));
    }

    public Task<DocumentDetails?> GetAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        var doc = repository.Stored.SingleOrDefault(d => d.Id == documentId && d.TenantId == tenantId);
        return Task.FromResult(doc is null
            ? null
            : new DocumentDetails(doc.Id, doc.Title, doc.DocType.Name, StoredAt,
                doc.Chunks.Select(c => new ChunkText(c.Index, c.Content)).ToList()));
    }
}

/// <summary>
/// Candidate retrieval over <see cref="InMemoryDocumentRepository"/> with the same rules as Postgres: same tenant,
/// same embedding model. Vector leg: cosine distance. Keyword leg: a crude stand-in for full-text search — chunks
/// containing any query word (OR semantics), ranked by how many query words they contain.
/// </summary>
internal sealed class InMemoryChunkSearch(InMemoryDocumentRepository repository) : IChunkSearch
{
    public List<(TenantId Tenant, EmbeddingModelName Model, SearchText Text, SearchMode Mode, CandidateCount Candidates)> Calls { get; } = [];

    public Task<CandidateLists> RetrieveCandidatesAsync(TenantId tenantId, EmbeddingModelName model, EmbeddingVector query,
        SearchText text, SearchMode mode, CandidateCount candidates, CancellationToken cancellationToken)
    {
        Calls.Add((tenantId, model, text, mode, candidates));
        var mine = repository.Stored
            .Where(d => d.TenantId == tenantId)
            .SelectMany(d => d.Chunks.Where(c => c.EmbeddingModel == model).Select(c => (Doc: d, Chunk: c)))
            .Select(x => new ChunkCandidate(ChunkId(x.Doc.Id, x.Chunk.Index), x.Doc.Id, x.Doc.Title, x.Doc.DocType.Name,
                x.Chunk.Index, x.Chunk.Content, CosineDistance(query.Values.Span, x.Chunk.Embedding!.Values.Span)))
            .ToList();

        IReadOnlyList<ChunkCandidate> vector = mode.UsesVector
            ? mine.OrderBy(c => c.Distance).ThenBy(c => c.ChunkId).Take(candidates.Value).ToList()
            : [];

        var words = Words(text.Value);
        IReadOnlyList<ChunkCandidate> keyword = mode.UsesKeyword
            ? mine.Select(c => (Candidate: c, Matches: Words(c.Content).Intersect(words).Count()))
                .Where(x => x.Matches > 0)
                .OrderByDescending(x => x.Matches).ThenBy(x => x.Candidate.ChunkId)
                .Take(candidates.Value).Select(x => x.Candidate).ToList()
            : [];

        return Task.FromResult(new CandidateLists(vector, keyword));
    }

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant().Split([' ', '\n', ':', '|', ',', '.', '?', '$', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToHashSet();

    private static Guid ChunkId(Guid documentId, int index)
    {
        var bytes = documentId.ToByteArray();
        bytes[0] ^= (byte)index;
        return new Guid(bytes);
    }

    private static double CosineDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 1 : 1 - dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
