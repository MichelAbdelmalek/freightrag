using NpgsqlTypes;
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

    /// <summary>
    /// Full-text representation of <see cref="Content"/> (stemmed lexemes with positions). A STORED generated column:
    /// PostgreSQL computes it from content on every insert/update — the application never writes it.
    /// </summary>
    public NpgsqlTsVector ContentTsv { get; set; } = null!;
    public DocumentRecord? Document { get; set; }
}
