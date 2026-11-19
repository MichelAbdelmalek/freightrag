using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Embedding;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Infrastructure;

public class OllamaEmbeddingServiceTests
{
    private static readonly EmbeddingModelName Model = new("nomic-embed-text");

    [Fact]
    public async Task Prefixes_every_chunk_with_search_document_and_sends_one_batch()
    {
        var generator = new FakeEmbeddingGenerator();
        var service = new OllamaEmbeddingService(generator, Model);

        var vectors = await service.EmbedDocumentChunksAsync(["chunk one", "chunk two"], TestContext.Current.CancellationToken);

        var call = Assert.Single(generator.Calls);
        Assert.Equal(["search_document: chunk one", "search_document: chunk two"], call);
        Assert.Equal(2, vectors.Count);
    }

    [Fact]
    public async Task Fails_clearly_when_an_embedding_does_not_have_768_dimensions()
    {
        var service = new OllamaEmbeddingService(new FakeEmbeddingGenerator(dimensions: 1024), Model);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EmbedDocumentChunksAsync(["a", "b"], TestContext.Current.CancellationToken));

        Assert.Contains("chunk 0", ex.Message);
        Assert.Contains("1024", ex.Message);
        Assert.Contains("768", ex.Message);
    }

    [Fact]
    public async Task Fails_clearly_when_the_number_of_embeddings_does_not_match()
    {
        var service = new OllamaEmbeddingService(new FakeEmbeddingGenerator(overrideCount: 1), Model);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EmbedDocumentChunksAsync(["a", "b"], TestContext.Current.CancellationToken));

        Assert.Contains("2 chunks", ex.Message);
        Assert.Contains("1 embeddings", ex.Message);
    }

    [Fact]
    public async Task Does_not_call_the_model_for_an_empty_batch()
    {
        var generator = new FakeEmbeddingGenerator();

        var vectors = await new OllamaEmbeddingService(generator, Model).EmbedDocumentChunksAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(vectors);
        Assert.Empty(generator.Calls);
    }

    [Fact]
    public void Exposes_the_configured_model_name()
    {
        Assert.Equal(Model, new OllamaEmbeddingService(new FakeEmbeddingGenerator(), Model).Model);
    }

    [Fact]
    public async Task A_query_is_prefixed_with_search_query()
    {
        // nomic-embed-text is trained with asymmetric prefixes: questions use "search_query: ",
        // stored passages "search_document: ". Mixing them up silently degrades retrieval.
        var generator = new FakeEmbeddingGenerator();
        var service = new OllamaEmbeddingService(generator, Model);

        var vector = await service.EmbedQueryAsync(SearchText.Create("  detention for load 48213 "), TestContext.Current.CancellationToken);

        Assert.Equal(["search_query: detention for load 48213"], Assert.Single(generator.Calls));
        Assert.Equal(EmbeddingVector.Dimensions, vector.Values.Length);
    }

    [Fact]
    public async Task A_query_embedding_must_have_768_dimensions()
    {
        var service = new OllamaEmbeddingService(new FakeEmbeddingGenerator(dimensions: 384), Model);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EmbedQueryAsync(SearchText.Create("rate"), TestContext.Current.CancellationToken));
        Assert.Contains("384", ex.Message);
        Assert.Contains("768", ex.Message);
    }
}
