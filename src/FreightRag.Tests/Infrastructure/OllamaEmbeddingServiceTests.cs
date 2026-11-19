using FreightRag.Core.Infrastructure.Embedding;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Infrastructure;

public class OllamaEmbeddingServiceTests
{
    [Fact]
    public async Task Prefixes_every_chunk_with_search_document_and_sends_one_batch()
    {
        var generator = new FakeEmbeddingGenerator();
        var service = new OllamaEmbeddingService(generator);

        var vectors = await service.EmbedDocumentChunksAsync(["chunk one", "chunk two"], TestContext.Current.CancellationToken);

        var call = Assert.Single(generator.Calls);
        Assert.Equal(["search_document: chunk one", "search_document: chunk two"], call);
        Assert.Equal(2, vectors.Count);
    }

    [Fact]
    public async Task Fails_clearly_when_an_embedding_does_not_have_768_dimensions()
    {
        var service = new OllamaEmbeddingService(new FakeEmbeddingGenerator(dimensions: 1024));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EmbedDocumentChunksAsync(["a", "b"], TestContext.Current.CancellationToken));

        Assert.Contains("chunk 0", ex.Message);
        Assert.Contains("1024", ex.Message);
        Assert.Contains("768", ex.Message);
    }

    [Fact]
    public async Task Fails_clearly_when_the_number_of_embeddings_does_not_match()
    {
        var service = new OllamaEmbeddingService(new FakeEmbeddingGenerator(overrideCount: 1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EmbedDocumentChunksAsync(["a", "b"], TestContext.Current.CancellationToken));

        Assert.Contains("2 chunks", ex.Message);
        Assert.Contains("1 embeddings", ex.Message);
    }

    [Fact]
    public async Task Does_not_call_the_model_for_an_empty_batch()
    {
        var generator = new FakeEmbeddingGenerator();

        var vectors = await new OllamaEmbeddingService(generator).EmbedDocumentChunksAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(vectors);
        Assert.Empty(generator.Calls);
    }
}
