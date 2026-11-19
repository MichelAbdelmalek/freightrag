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
    public List<IReadOnlyList<string>> Calls { get; } = [];

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
