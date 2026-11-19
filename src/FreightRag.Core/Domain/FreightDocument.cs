namespace FreightRag.Core.Domain;

/// <summary>
/// Aggregate root: a tenant's freight document together with its chunks.
/// It is stored as one unit (one transaction), and it guards the rule that a document can only
/// be stored once every chunk has an embedding.
/// </summary>
public sealed class FreightDocument
{
    private readonly List<DocumentChunk> _chunks;

    private FreightDocument(
        Guid id, TenantId tenantId, string title, DocType docType, SourceHash sourceHash,
        DocumentIdentifiers identifiers, List<DocumentChunk> chunks)
    {
        Id = id;
        TenantId = tenantId;
        Title = title;
        DocType = docType;
        SourceHash = sourceHash;
        Identifiers = identifiers;
        _chunks = chunks;
    }

    public Guid Id { get; }
    public TenantId TenantId { get; }

    /// <summary>The file name without extension, e.g. "rate-confirmation-48213".</summary>
    public string Title { get; }

    public DocType DocType { get; }
    public SourceHash SourceHash { get; }
    public DocumentIdentifiers Identifiers { get; }
    public IReadOnlyList<DocumentChunk> Chunks => _chunks;

    public bool IsReadyToStore => _chunks.All(c => c.Embedding is not null);

    /// <summary>Builds the aggregate: extracts identifiers and chunks the text.</summary>
    public static FreightDocument Create(
        TenantId tenantId, string fileName, DocType docType, SourceHash sourceHash, string text, SectionChunker chunker)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(docType);
        ArgumentNullException.ThrowIfNull(sourceHash);
        ArgumentNullException.ThrowIfNull(chunker);

        var title = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(title))
            throw new DomainException("A document needs a file name.");

        var identifiers = DocumentIdentifiers.Extract(text);
        var chunks = chunker.Split(text, docType, identifiers).ToList();
        if (chunks.Count == 0)
            throw new DomainException($"Document '{title}' has no content to chunk.");

        // Version 7 GUIDs are time-ordered, which keeps the primary-key index compact.
        return new FreightDocument(Guid.CreateVersion7(), tenantId, title, docType, sourceHash, identifiers, chunks);
    }

    /// <summary>Attaches one embedding per chunk, matched by position, all produced by <paramref name="model"/>. Allowed once.</summary>
    public void AttachEmbeddings(IReadOnlyList<EmbeddingVector> embeddings, EmbeddingModelName model)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(model);
        if (_chunks.Any(c => c.Embedding is not null))
            throw new DomainException($"Document '{Title}' already has embeddings.");
        if (embeddings.Count != _chunks.Count)
            throw new DomainException(
                $"Document '{Title}' has {_chunks.Count} chunks but {embeddings.Count} embeddings were supplied.");

        for (var i = 0; i < _chunks.Count; i++)
            _chunks[i].AttachEmbedding(embeddings[i] ?? throw new DomainException($"Embedding {i} is missing."), model);
    }
}
