namespace FreightRag.Core.Domain;

/// <summary>
/// Entity inside the <see cref="FreightDocument"/> aggregate: one retrievable piece of a document.
/// <see cref="Content"/> is exactly what gets stored (header line + body); the embedding is
/// attached later through the aggregate root, never directly.
/// </summary>
public sealed class DocumentChunk
{
    internal DocumentChunk(int index, string sectionName, string content)
    {
        Index = index;
        SectionName = sectionName;
        Content = content;
    }

    /// <summary>0-based position in the document (chunks.chunk_index).</summary>
    public int Index { get; }

    public string SectionName { get; }

    public string Content { get; }

    public EmbeddingVector? Embedding { get; private set; }

    internal void AttachEmbedding(EmbeddingVector embedding) => Embedding = embedding;
}
