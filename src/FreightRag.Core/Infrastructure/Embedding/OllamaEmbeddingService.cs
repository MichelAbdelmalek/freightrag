using FreightRag.Core.Application.Ports;
using FreightRag.Core.Domain;
using Microsoft.Extensions.AI;

namespace FreightRag.Core.Infrastructure.Embedding;

/// <summary>
/// Adapter: implements the application's <see cref="IEmbeddingService"/> port on top of the
/// Microsoft.Extensions.AI abstraction <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.
/// In production the generator is OllamaSharp's OllamaApiClient; in tests it is a fake.
/// </summary>
public sealed class OllamaEmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingModelName model)
    : IEmbeddingService
{
    /// <summary>
    /// nomic-embed-text is trained with task prefixes: stored documents use "search_document: ",
    /// queries (later) use "search_query: ". The prefix is only sent to the model, never stored.
    /// </summary>
    public const string DocumentPrefix = "search_document: ";

    /// <summary>The query-side counterpart of <see cref="DocumentPrefix"/>. Only sent to the model, never stored or logged.</summary>
    public const string QueryPrefix = "search_query: ";

    public EmbeddingModelName Model { get; } = model;

    public async Task<EmbeddingVector> EmbedQueryAsync(SearchText query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var embeddings = await generator.GenerateAsync([QueryPrefix + query.Value], cancellationToken: cancellationToken);
        if (embeddings.Count != 1)
            throw new InvalidOperationException($"Embedding model returned {embeddings.Count} embeddings for 1 query.");
        return ToVector(embeddings[0].Vector, "the query");
    }

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
            vectors.Add(ToVector(embeddings[i].Vector, $"chunk {i}"));
        return vectors;
    }

    private static EmbeddingVector ToVector(ReadOnlyMemory<float> vector, string what)
    {
        if (vector.Length != EmbeddingVector.Dimensions)
            throw new InvalidOperationException(
                $"Embedding for {what} has {vector.Length} dimensions, expected {EmbeddingVector.Dimensions}. " +
                "Check that the configured model is nomic-embed-text (the database column is vector(768)).");
        return new EmbeddingVector(vector.Span);
    }
}
