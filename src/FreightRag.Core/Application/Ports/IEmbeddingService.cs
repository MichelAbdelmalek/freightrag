using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Ports;

/// <summary>Port: turns chunk texts into embedding vectors (one per input, same order).</summary>
public interface IEmbeddingService
{
    /// <summary>Embeds all chunks of one document in a single batch.</summary>
    Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentChunksAsync(
        IReadOnlyList<string> chunkContents, CancellationToken cancellationToken);
}
