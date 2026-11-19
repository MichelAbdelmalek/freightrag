using Microsoft.Extensions.AI;
using OllamaSharp;

namespace FreightRag.Core.Infrastructure.Embedding;

/// <summary>Creates the OllamaSharp client, exposed only as the Microsoft.Extensions.AI abstraction.</summary>
public static class OllamaClientFactory
{
    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri ollamaUrl, string model)
    {
        ArgumentNullException.ThrowIfNull(ollamaUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        // OllamaApiClient implements IEmbeddingGenerator<string, Embedding<float>> directly.
        return new OllamaApiClient(ollamaUrl, model);
    }
}
