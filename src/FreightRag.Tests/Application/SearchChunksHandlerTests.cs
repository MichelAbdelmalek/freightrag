using FreightRag.Core.Application;
using FreightRag.Core.Application.Search;
using FreightRag.Core.Domain;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Application;

public class SearchChunksHandlerTests
{
    private static readonly TenantId Nile = new(Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097"));
    private static readonly TenantId Delta = new(Guid.Parse("14a2de5d-56c5-4eda-a737-d957f4580b90"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryDocumentRepository _repository = new();
    private readonly FakeEmbeddingService _embeddings = new();
    private readonly InMemoryChunkSearch _search;
    private readonly DictionaryTenantDirectory _tenants = new(new() { [TestData.NileFreight] = Nile, [TestData.DeltaHaul] = Delta });

    public SearchChunksHandlerTests() => _search = new InMemoryChunkSearch(_repository);

    private SearchChunksHandler Handler() => new(_tenants, _embeddings, _search);

    private async Task Ingest(string tenant, string file) =>
        await new IngestDocumentHandler(_tenants, _repository, _embeddings, new SectionChunker())
            .HandleAsync(new IngestDocumentCommand(tenant, file, TestData.ReadBytes(tenant, file)), Ct);

    [Fact]
    public async Task Embeds_the_query_and_searches_the_callers_tenant_with_the_same_model()
    {
        await Ingest(TestData.NileFreight, "rate-confirmation-48213.txt");

        var result = await Handler().HandleAsync(new SearchChunksQuery(TestData.NileFreight, " detention rate ", 3), Ct);

        var found = Assert.IsType<SearchResult.Found>(result);
        Assert.Equal(3, found.Hits.Count);
        Assert.Equal("detention rate", Assert.Single(_embeddings.QueryCalls).Value); // trimmed
        var call = Assert.Single(_search.Calls);
        Assert.Equal(Nile, call.Tenant);
        Assert.Equal(_embeddings.Model, call.Model); // query and chunks must come from the same model
        Assert.Equal(3, call.TopK.Value);
    }

    [Fact]
    public async Task Uses_top_5_by_default_and_returns_hits_ordered_by_distance()
    {
        await Ingest(TestData.NileFreight, "rate-confirmation-48213.txt");

        var found = Assert.IsType<SearchResult.Found>(
            await Handler().HandleAsync(new SearchChunksQuery(TestData.NileFreight, "rate", null), Ct));

        Assert.Equal(5, found.Hits.Count);
        Assert.Equal(found.Hits.OrderBy(h => h.Distance).Select(h => h.Distance), found.Hits.Select(h => h.Distance));
    }

    [Fact]
    public async Task Never_returns_another_tenants_chunks()
    {
        await Ingest(TestData.DeltaHaul, "rate-confirmation-77104.txt");

        var found = Assert.IsType<SearchResult.Found>(
            await Handler().HandleAsync(new SearchChunksQuery(TestData.NileFreight, "rate", 20), Ct));

        Assert.Empty(found.Hits);
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("   ", 5)]
    [InlineData("rate", 0)]
    [InlineData("rate", 21)]
    public async Task Invalid_input_is_rejected_before_calling_the_embedding_model(string query, int topK)
    {
        var result = await Handler().HandleAsync(new SearchChunksQuery(TestData.NileFreight, query, topK), Ct);

        Assert.IsType<SearchResult.Invalid>(result);
        Assert.Empty(_embeddings.QueryCalls);
        Assert.Empty(_search.Calls);
    }

    [Fact]
    public async Task A_query_over_1000_characters_is_invalid()
    {
        var result = await Handler().HandleAsync(new SearchChunksQuery(TestData.NileFreight, new string('q', 1001), 5), Ct);

        Assert.Contains("1000", Assert.IsType<SearchResult.Invalid>(result).Reason);
    }

    [Fact]
    public async Task An_unknown_tenant_is_invalid()
    {
        var result = await Handler().HandleAsync(new SearchChunksQuery("tenant-ghost", "rate", 5), Ct);

        Assert.IsType<SearchResult.Invalid>(result);
        Assert.Empty(_embeddings.QueryCalls);
    }
}
