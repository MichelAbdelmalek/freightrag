using FreightRag.Core.Application;
using FreightRag.Core.Application.Ports;
using FreightRag.Core.Application.Queries;
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
    private readonly DictionaryTenantDirectory _tenants = new(new() { [TestData.NileFreight] = Nile, [TestData.DeltaHaul] = Delta });

    private SearchChunksHandler Handler(IChunkSearch search) =>
        new(_tenants, _embeddings, search, new ReciprocalRankFusion(), CandidateCount.Create(null));

    private async Task Ingest(string tenant, string file) =>
        await new IngestDocumentHandler(_tenants, _repository, _embeddings, new SectionChunker(), new FakePdfTextExtractor())
            .HandleAsync(new IngestDocumentCommand(tenant, file, TestData.ReadBytes(tenant, file)), Ct);

    // ---------- with the in-memory search (end to end through the use case) ----------

    [Fact]
    public async Task Embeds_the_query_and_retrieves_candidates_for_the_callers_tenant_model_and_mode()
    {
        await Ingest(TestData.NileFreight, "rate-confirmation-48213.txt");
        var search = new InMemoryChunkSearch(_repository);

        var result = await Handler(search).HandleAsync(new SearchChunksQuery(TestData.NileFreight, " detention rate ", 3, "keyword"), Ct);

        Assert.NotEmpty(Assert.IsType<SearchResult.Found>(result).Hits);
        Assert.Equal("detention rate", Assert.Single(_embeddings.QueryCalls).Value); // trimmed, embedded in every mode
        var call = Assert.Single(search.Calls);
        Assert.Equal(Nile, call.Tenant);
        Assert.Equal(_embeddings.Model, call.Model);
        Assert.Equal(SearchMode.Keyword, call.Mode);
        Assert.Equal("detention rate", call.Text.Value);
        Assert.Equal(CandidateCount.Default, call.Candidates.Value);
    }

    [Fact]
    public async Task Defaults_to_hybrid_mode_and_top_5()
    {
        await Ingest(TestData.NileFreight, "rate-confirmation-48213.txt");
        var search = new InMemoryChunkSearch(_repository);

        var found = Assert.IsType<SearchResult.Found>(
            await Handler(search).HandleAsync(new SearchChunksQuery(TestData.NileFreight, "detention rate", null, null), Ct));

        Assert.Equal(5, found.Hits.Count);
        Assert.Equal(SearchMode.Hybrid, Assert.Single(search.Calls).Mode);
    }

    [Fact]
    public async Task Never_returns_another_tenants_chunks()
    {
        await Ingest(TestData.DeltaHaul, "rate-confirmation-77104.txt");

        var found = Assert.IsType<SearchResult.Found>(await Handler(new InMemoryChunkSearch(_repository))
            .HandleAsync(new SearchChunksQuery(TestData.NileFreight, "rate 77104", 20, "hybrid"), Ct));

        Assert.Empty(found.Hits);
    }

    [Theory]
    [InlineData("", 5, null)]
    [InlineData("   ", 5, null)]
    [InlineData("rate", 0, null)]
    [InlineData("rate", 21, null)]
    [InlineData("rate", 5, "bm25")]
    public async Task Invalid_input_is_rejected_before_calling_the_embedding_model(string query, int topK, string? mode)
    {
        var search = new InMemoryChunkSearch(_repository);

        var result = await Handler(search).HandleAsync(new SearchChunksQuery(TestData.NileFreight, query, topK, mode), Ct);

        Assert.IsType<SearchResult.Invalid>(result);
        Assert.Empty(_embeddings.QueryCalls);
        Assert.Empty(search.Calls);
    }

    [Fact]
    public async Task An_unknown_tenant_is_invalid()
    {
        var result = await Handler(new InMemoryChunkSearch(_repository)).HandleAsync(new SearchChunksQuery("tenant-ghost", "rate", 5, null), Ct);

        Assert.IsType<SearchResult.Invalid>(result);
        Assert.Empty(_embeddings.QueryCalls);
    }

    // ---------- fusion, with hand-made candidate lists ----------

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static ChunkCandidate Candidate(Guid id, double distance) =>
        new(id, Guid.Empty, $"doc-{id.ToString()[^1]}", "invoice", 0, $"Document: invoice | Section: S\n{id}", distance);

    private sealed class StubSearch(CandidateLists lists) : IChunkSearch
    {
        public Task<CandidateLists> RetrieveCandidatesAsync(TenantId tenantId, EmbeddingModelName model, EmbeddingVector query,
            SearchText text, SearchMode mode, CandidateCount candidates, CancellationToken cancellationToken) => Task.FromResult(lists);
    }

    // vector: A (0.10), B (0.20)      keyword: C, A      → A = 1/61 + 1/62, C = 1/61, B = 1/62
    private static readonly CandidateLists Both = new(
        Vector: [Candidate(A, 0.10), Candidate(B, 0.20)],
        Keyword: [Candidate(C, 0.40), Candidate(A, 0.10)]);

    [Fact]
    public async Task Hybrid_fuses_both_lists_and_reports_each_legs_rank()
    {
        var found = Assert.IsType<SearchResult.Found>(
            await Handler(new StubSearch(Both)).HandleAsync(new SearchChunksQuery(TestData.NileFreight, "q", 5, "hybrid"), Ct));

        Assert.Equal([A, C, B], found.Hits.Select(h => h.ChunkId));
        var a = found.Hits[0];
        Assert.Equal((1, 2), (a.VectorRank, a.KeywordRank));
        Assert.Equal(1.0 / 61 + 1.0 / 62, a.FusedScore, precision: 12);
        Assert.Equal((null, (int?)1), (found.Hits[1].VectorRank, found.Hits[1].KeywordRank)); // C: keyword only
        Assert.Equal(0.40, found.Hits[1].Distance); // distance reported even for a keyword-only chunk
        Assert.Equal((2, 2), (found.VectorCandidates, found.KeywordCandidates));
    }

    [Fact]
    public async Task Vector_mode_ignores_a_keyword_list_even_if_one_is_returned()
    {
        var found = Assert.IsType<SearchResult.Found>(
            await Handler(new StubSearch(Both)).HandleAsync(new SearchChunksQuery(TestData.NileFreight, "q", 5, "vector"), Ct));

        Assert.Equal([A, B], found.Hits.Select(h => h.ChunkId));
        Assert.All(found.Hits, h => Assert.Null(h.KeywordRank));
        Assert.Equal(0, found.KeywordCandidates);
    }

    [Fact]
    public async Task Keyword_mode_ranks_by_the_keyword_list_and_keeps_distances()
    {
        var found = Assert.IsType<SearchResult.Found>(
            await Handler(new StubSearch(Both)).HandleAsync(new SearchChunksQuery(TestData.NileFreight, "q", 5, "keyword"), Ct));

        Assert.Equal([C, A], found.Hits.Select(h => h.ChunkId));
        Assert.All(found.Hits, h => Assert.Null(h.VectorRank));
        Assert.Equal([0.40, 0.10], found.Hits.Select(h => h.Distance));
    }

    [Fact]
    public async Task Only_the_top_k_fused_results_are_returned()
    {
        var found = Assert.IsType<SearchResult.Found>(
            await Handler(new StubSearch(Both)).HandleAsync(new SearchChunksQuery(TestData.NileFreight, "q", 2, "hybrid"), Ct));

        Assert.Equal([A, C], found.Hits.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task An_empty_keyword_list_leaves_the_vector_order()
    {
        var vectorOnly = new CandidateLists(Vector: [Candidate(A, 0.1), Candidate(B, 0.2)], Keyword: []);

        var found = Assert.IsType<SearchResult.Found>(
            await Handler(new StubSearch(vectorOnly)).HandleAsync(new SearchChunksQuery(TestData.NileFreight, "the", 5, "hybrid"), Ct));

        Assert.Equal([A, B], found.Hits.Select(h => h.ChunkId));
        Assert.All(found.Hits, h => Assert.Null(h.KeywordRank));
    }
}
