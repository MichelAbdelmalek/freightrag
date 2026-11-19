using FreightRag.Core.Application.Ports;
using FreightRag.Core.Domain;
using Microsoft.Extensions.AI;

namespace FreightRag.Core.Infrastructure.Embedding;

/// <summary>
/// Adapter: implements the application's <see cref="IEmbeddingService"/> port on top of the
/// Microsoft.Extensions.AI abstraction <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.
/// In production the generator is OllamaSharp's OllamaApiClient; in tests it is a fake.
/// </summary>
public sealed class OllamaEmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator) : IEmbeddingService
{
    /// <summary>
    /// nomic-embed-text is trained with task prefixes: stored documents use "search_document: ",
    /// queries (later) use "search_query: ". The prefix is only sent to the model, never stored.
    /// </summary>
    public const string DocumentPrefix = "search_document: ";

    public async Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentChunksAsync(
        IReadOnlyList<string> chunkContents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunkContents);
        if (chunkContents.Count == 0)
            return [];

        var inputs = chunkContents.Select(c => DocumentPrefix + c).ToList();

        // One call for the whole document; OllamaSharp sends it as a single /api/embed request.
        var embeddings = await generator.GenerateAsync(inputs, cancellationToken: cancellationToken);

        if (embeddings.Count != chunkContents.Count)
            throw new InvalidOperationException(
                $"Embedding model returned {embeddings.Count} embeddings for {chunkContents.Count} chunks.");

        var vectors = new List<EmbeddingVector>(embeddings.Count);
        for (var i = 0; i < embeddings.Count; i++)
        {
            var vector = embeddings[i].Vector;
            if (vector.Length != EmbeddingVector.Dimensions)
                throw new InvalidOperationException(
                    $"Embedding for chunk {i} has {vector.Length} dimensions, expected {EmbeddingVector.Dimensions}. " +
                    "Check that the configured model is nomic-embed-text (the database column is vector(768)).");
            vectors.Add(new EmbeddingVector(vector.Span));
        }
        return vectors;
    }
}
