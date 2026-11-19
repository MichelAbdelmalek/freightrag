using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Ports;

/// <summary>Port: turns text into embedding vectors.</summary>
public interface IEmbeddingService
{
    /// <summary>The model that produces the vectors; stored with every chunk and used to filter searches.</summary>
    EmbeddingModelName Model { get; }

    /// <summary>Embeds all chunks of one document in a single batch (one vector per input, same order).</summary>
    Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentChunksAsync(
        IReadOnlyList<string> chunkContents, CancellationToken cancellationToken);

    /// <summary>Embeds a search question with the same model (and the model's query-side conventions).</summary>
    Task<EmbeddingVector> EmbedQueryAsync(SearchText query, CancellationToken cancellationToken);
}
