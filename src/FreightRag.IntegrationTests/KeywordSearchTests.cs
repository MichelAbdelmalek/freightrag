using FreightRag.Core.Application.Ports;
using FreightRag.Core.Application.Queries;
using FreightRag.Core.Application.Search;
using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Persistence;
using FreightRag.Core.Infrastructure.Tenants;

namespace FreightRag.IntegrationTests;

/// <summary>The keyword (full-text) leg and hybrid fusion against real PostgreSQL.</summary>
public sealed class KeywordSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<CandidateLists> Candidates(TestDatabase database, Guid tenant, string text, SearchMode mode,
        string? model = null, EfChunkSearch? search = null) =>
        (search ?? new EfChunkSearch(database.Contexts)).RetrieveCandidatesAsync(
            new TenantId(tenant), new EmbeddingModelName(model ?? TestDatabase.Model.Value),
            new EmbeddingVector(Vectors.Query()), SearchText.Create(text), mode, CandidateCount.Create(null), Ct);

    /// <summary>The real use case (validation, fusion) over the real EfChunkSearch, with a fixed query embedding.</summary>
    private static async Task<IReadOnlyList<SearchHit>> SearchThroughUseCase(TestDatabase database, string tenantSlug, string text, string mode)
    {
        var tenants = new ConfiguredTenantDirectory(new Dictionary<string, Guid> { ["tenant-a"] = Seed.TenantA, ["tenant-b"] = Seed.TenantB });
        var handler = new SearchChunksHandler(tenants, new FixedQueryEmbedding(), new EfChunkSearch(database.Contexts),
            new ReciprocalRankFusion(), CandidateCount.Create(null));
        return Assert.IsType<SearchResult.Found>(await handler.HandleAsync(new SearchChunksQuery(tenantSlug, text, 20, mode), Ct)).Hits;
    }

    private sealed class FixedQueryEmbedding : IEmbeddingService
    {
        public EmbeddingModelName Model => TestDatabase.Model;
        public Task<EmbeddingVector> EmbedQueryAsync(SearchText query, CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingVector(Vectors.Query()));
        public Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentChunksAsync(IReadOnlyList<string> chunkContents, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static (string, float[]) Chunk(string content, int seed) => (content, Vectors.AroundQuery(new Random(seed), 0.5f, 1f));

    [Fact]
    public async Task Keyword_leg_tenant_isolation_another_tenants_exact_identifier_never_reaches_this_tenant()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Seed.DocumentWithContentAsync(database, Seed.TenantA, "invoice-NFL-2026-1187",
            [Chunk("Document: invoice | Invoice: NFL-2026-1187 | Section: CHARGES\nTotal due: $2,705.00", 1)]);
        var theirs = await Seed.DocumentWithContentAsync(database, Seed.TenantB, "invoice-DH-26-0412",
            [Chunk("Document: invoice | Invoice: DH-26-0412 | Section: CHARGES\nInvoice number: DH-26-0412\nTotal due: $4,210.00", 2)]);

        var forA = await Candidates(database, Seed.TenantA, "invoice DH-26-0412", SearchMode.Keyword);
        var forB = await Candidates(database, Seed.TenantB, "invoice DH-26-0412", SearchMode.Keyword);

        Assert.DoesNotContain(forA.Keyword, c => c.DocumentId == theirs.Id);
        Assert.Equal(theirs.Id, forB.Keyword[0].DocumentId); // the query does match it — for its own tenant
    }

    [Fact]
    public async Task OR_rewrite_keeps_ampersands_inside_lexemes_so_a_url_with_a_query_string_still_matches()
    {
        await using var database = await TestDatabase.CreateAsync();
        var exact = await Seed.DocumentWithContentAsync(database, Seed.TenantA, "exact",
            [Chunk("Document: rate-confirmation | Section: TRACKING\nTrack at https://portal.example.com/track?id=1&ref=2", 3)]);
        await Seed.DocumentWithContentAsync(database, Seed.TenantA, "other",
            [Chunk("Document: rate-confirmation | Section: HELP\nPortal https://portal.example.com/help and https://portal.example.com/faq", 4)]);

        // The query parses to 'portal.example.com/track?id=1&ref=2' & 'portal.example.com' & '/track?id=1&ref=2'.
        // Rewriting every '&' would corrupt the two URL lexemes, leaving only the host to match — and "other" mentions
        // the host twice, so it would rank first. Rewriting only the ' & ' operator keeps them, so the exact URL wins.
        var lists = await Candidates(database, Seed.TenantA, "https://portal.example.com/track?id=1&ref=2", SearchMode.Keyword);

        Assert.Equal(2, lists.Keyword.Count);
        Assert.Equal(exact.Id, lists.Keyword[0].DocumentId);
    }

    [Fact]
    public async Task Keyword_leg_model_isolation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var other = await Seed.DocumentWithContentAsync(database, Seed.TenantA, "bol-other-model",
            [Chunk("Document: bill-of-lading | Section: CARRIER\nSeal number: CE-559201", 3)], model: "other-embed-model");
        var current = await Seed.DocumentWithContentAsync(database, Seed.TenantA, "bol-current-model",
            [Chunk("Document: bill-of-lading | Section: CARRIER\nSeal number: CE-777000", 4)]);

        var lists = await Candidates(database, Seed.TenantA, "seal number CE-559201", SearchMode.Keyword);

        Assert.DoesNotContain(lists.Keyword, c => c.DocumentId == other.Id);
        Assert.Equal(current.Id, Assert.Single(lists.Keyword).DocumentId); // matched through "seal number" (OR)
        var withOtherModel = await Candidates(database, Seed.TenantA, "seal number CE-559201", SearchMode.Keyword, model: "other-embed-model");
        Assert.Equal(other.Id, withOtherModel.Keyword[0].DocumentId);
    }

    [Fact]
    public async Task An_identifier_query_ranks_the_exact_match_first_in_keyword_mode()
    {
        // Near-duplicates: same template, only the invoice number differs — the case vector search struggles with.
        await using var database = await TestDatabase.CreateAsync();
        var ids = new Dictionary<string, Guid>();
        foreach (var (number, i) in new[] { ("NFL-2026-1187", 1), ("NFL-2026-1219", 2), ("NFL-2026-1231", 3) })
            ids[number] = (await Seed.DocumentWithContentAsync(database, Seed.TenantA, $"invoice-{number}",
                [Chunk($"Document: invoice | Load: 4821{i} | Invoice: {number} | Section: CHARGES\nLine haul: $2,450.00\nTotal due: $2,705.00", 10 + i)])).Id;

        var hits = await SearchThroughUseCase(database, "tenant-a", "What is the total due on invoice NFL-2026-1219?", "keyword");

        Assert.Equal(ids["NFL-2026-1219"], hits[0].DocumentId);
        Assert.Equal(1, hits[0].KeywordRank);          // the keyword leg put it first …
        Assert.Null(hits[0].VectorRank);                // … and only the keyword leg ran
        Assert.True(hits[0].Distance >= 0);             // distance is still reported
    }

    [Fact]
    public async Task OR_semantics_a_chunk_sharing_only_one_word_is_still_returned()
    {
        await using var database = await TestDatabase.CreateAsync();
        var partial = await Seed.DocumentWithContentAsync(database, Seed.TenantA, "handbook",
            [Chunk("Document: driver-handbook | Section: 3. DETENTION REPORTING\nDetention is billable after 2 hours.", 5)]);

        // Shares only "detention" (stem 'detent'); with AND semantics nothing would match.
        var lists = await Candidates(database, Seed.TenantA, "detention rate for load 77777", SearchMode.Keyword);

        Assert.Equal(partial.Id, Assert.Single(lists.Keyword).DocumentId);
    }

    [Theory]
    [InlineData("the of and")]
    [InlineData("?!? &&& ||| ()")]
    public async Task A_stop_word_or_symbol_only_query_gives_no_keyword_results_and_hybrid_falls_back_to_vector(string text)
    {
        await using var database = await TestDatabase.CreateAsync();
        await Seed.DocumentWithContentAsync(database, Seed.TenantA, "doc",
            [Chunk("Document: invoice | Section: CHARGES\nThe total of the invoice and the rate", 6), Chunk("Document: invoice | Section: RATE\nLine haul", 7)]);

        var lists = await Candidates(database, Seed.TenantA, text, SearchMode.Hybrid);
        var hits = await SearchThroughUseCase(database, "tenant-a", text, "hybrid");

        Assert.Empty(lists.Keyword);                    // empty tsquery: no matches, no error
        Assert.Equal(2, lists.Vector.Count);
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Null(h.KeywordRank));
        Assert.All(hits, h => Assert.NotNull(h.VectorRank));
    }

    [Fact]
    public async Task Hybrid_fuses_both_legs_and_the_identifier_match_carries_both_ranks()
    {
        await using var database = await TestDatabase.CreateAsync();
        var target = await Seed.DocumentWithContentAsync(database, Seed.TenantA, "invoice-NFL-2026-1219",
            [Chunk("Document: invoice | Invoice: NFL-2026-1219 | Section: CHARGES\nTotal due: $3,105.00", 20)]);
        await Seed.DocumentWithContentAsync(database, Seed.TenantA, "other-docs",
            Enumerable.Range(0, 8).Select(i => Chunk($"Document: rate-confirmation | Section: S{i}\nPickup window {i}", 30 + i)));

        var hits = await SearchThroughUseCase(database, "tenant-a", "total due on invoice NFL-2026-1219", "hybrid");

        var hit = Assert.Single(hits, h => h.DocumentId == target.Id);
        Assert.Equal(1, hit.KeywordRank);               // the mechanism ran: found by the keyword leg …
        Assert.NotNull(hit.VectorRank);                 // … and by the vector leg
        Assert.Equal(target.Id, hits[0].DocumentId);    // agreement of both legs puts it first
        Assert.Equal(hits.Select(h => h.FusedScore).OrderDescending(), hits.Select(h => h.FusedScore));
    }

    [Fact]
    public async Task Both_legs_run_in_one_repeatable_read_transaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Seed.DocumentWithContentAsync(database, Seed.TenantA, "doc", [Chunk("Document: invoice | Section: CHARGES\nTotal due", 8)]);
        var probe = new SearchCommandProbe();

        await Candidates(database, Seed.TenantA, "total due", SearchMode.Hybrid, search: new EfChunkSearch(database.ContextsWith(probe)));

        Assert.Equal(2, probe.Observations.Count);
        var (vector, keyword) = (probe.Observations[0], probe.Observations[1]);
        Assert.DoesNotContain("plainto_tsquery", vector.CommandText);
        Assert.Contains("plainto_tsquery", keyword.CommandText);
        Assert.True(vector.InTransaction && keyword.InTransaction);
        Assert.Same(vector.Transaction, keyword.Transaction);
        Assert.Equal("repeatable read", vector.Isolation); // one snapshot for both legs (READ COMMITTED takes one per statement)
        Assert.Equal("strict_order", vector.IterativeScan);
    }

    [Fact]
    public async Task The_keyword_query_uses_the_GIN_index_on_a_large_tenant()
    {
        await using var database = await TestDatabase.CreateAsync();
        // Realistic shape: many documents with a few chunks each, and the searched word in only one chunk.
        // (With every chunk in one document the planner rightly walks that document's chunks instead.)
        // The HNSW index is dropped only to keep the bulk insert fast; this test is about the keyword leg.
        await database.ExecuteScriptAsync($"""
            DROP INDEX chunks_embedding_hnsw;
            INSERT INTO documents (id, tenant_id, title, doc_type, source_hash)
            SELECT md5('doc' || d)::uuid, '{Seed.TenantA}', 'bulk-' || d, 'invoice', 'hash-' || d
            FROM generate_series(1, 1000) AS d;
            INSERT INTO chunks (document_id, tenant_id, chunk_index, content, embedding, embedding_model)
            SELECT md5('doc' || d)::uuid, '{Seed.TenantA}', i,
                   'Document: invoice | Section: CHARGES' || chr(10) || 'Linehaul freight charge ' || d || ' fuel surcharge ' || i,
                   array_fill(0.01::real, ARRAY[{Vectors.Dimensions}])::vector, '{TestDatabase.Model.Value}'
            FROM generate_series(1, 1000) AS d, generate_series(0, 4) AS i;
            """);
        await Seed.DocumentWithContentAsync(database, Seed.TenantA, "rare", [Chunk("Document: invoice | Section: CHARGES\nLumper fee waived", 40)]);
        // Rows inserted after the GIN index was built sit in its pending list (fastupdate), which the planner
        // prices as an unsorted scan; VACUUM merges it into the index, as autovacuum does in a real database.
        await database.ExecuteScriptAsync("VACUUM ANALYZE documents");
        await database.ExecuteScriptAsync("VACUUM ANALYZE chunks");
        var probe = new SearchCommandProbe();

        var lists = await Candidates(database, Seed.TenantA, "lumper", SearchMode.Keyword, search: new EfChunkSearch(database.ContextsWith(probe)));

        Assert.Single(lists.Keyword);
        Assert.True(probe.Observations[0].Plan.Any(l => l.Contains("chunks_content_tsv", StringComparison.Ordinal)),
            "Expected the GIN index in the plan:\n" + string.Join("\n", probe.Observations[0].Plan));
    }
}
