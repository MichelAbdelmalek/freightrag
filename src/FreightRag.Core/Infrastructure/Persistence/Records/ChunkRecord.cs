using Pgvector;

namespace FreightRag.Core.Infrastructure.Persistence.Records;

/// <summary>Persistence shape of a row in <c>chunks</c>.</summary>
public sealed class ChunkRecord
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid TenantId { get; set; }
    public int ChunkIndex { get; set; }
    public string Content { get; set; } = "";
    public Vector Embedding { get; set; } = null!;

    /// <summary>The model that produced <see cref="Embedding"/>; vectors from different models are not comparable.</summary>
    public string EmbeddingModel { get; set; } = "";
    public DocumentRecord? Document { get; set; }
}
