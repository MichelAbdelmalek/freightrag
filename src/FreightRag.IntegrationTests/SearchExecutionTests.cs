using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Persistence;
using Npgsql;

namespace FreightRag.IntegrationTests;

/// <summary>
/// With EF Core, two things that were explicit in hand-written SQL became implicit: that SET LOCAL runs inside the
/// search's own transaction on the same connection (otherwise it silently does nothing), and that the generated SQL
/// still does the nearest-neighbour step on chunks before the join. These tests check both on the real command.
/// </summary>
public sealed class SearchExecutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SearchCommandProbe.Observation> SearchWithProbe(TestDatabase database, Guid tenant, ChunkSearchOptions? options = null)
    {
        var probe = new SearchCommandProbe();
        await new EfChunkSearch(database.ContextsWith(probe), options).RetrieveCandidatesAsync(
            new TenantId(tenant), TestDatabase.Model, new EmbeddingVector(Vectors.Query()), SearchText.Create("query"),
            SearchMode.Vector, CandidateCount.Create(null), Ct);
        return probe.Observations[0];
    }

    [Fact]
    public async Task SET_LOCAL_is_in_effect_inside_the_search_transaction_on_the_query_connection()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Seed.DocumentAsync(database, Seed.TenantA, "doc", Vectors.Crowd(20, seed: 3));

        var seen = await SearchWithProbe(database, Seed.TenantA, new ChunkSearchOptions(MaxScanTuples: 12_345));

        Assert.True(seen.InTransaction, "the search query must run inside the transaction that holds the SET LOCAL");
        Assert.Equal("strict_order", seen.IterativeScan);
        Assert.Equal("12345", seen.MaxScanTuples);
        Assert.Equal("on", seen.EnableIndexScan); // the exact fallback is not active on the first pass
    }

    [Fact]
    public async Task For_a_large_tenant_the_real_generated_SQL_uses_the_HNSW_index_before_the_join()
    {
        // At a few hundred rows PostgreSQL rightly prefers the tenant btree + an exact sort (cheaper than walking the
        // graph). From a few thousand rows on, HNSW wins — measured: 5,000 rows → HNSW, also with default settings.
        await using var database = await TestDatabase.CreateAsync();
        await Seed.BulkDocumentAsync(database, Seed.TenantA, "big", Vectors.Crowd(5_000, seed: 1).ToList());
        await Seed.DocumentAsync(database, Seed.TenantB, "small", Vectors.Distant(10, seed: 2));

        var seen = await SearchWithProbe(database, Seed.TenantA);
        var plan = string.Join("\n", seen.Plan);

        // Limit → Index Scan using chunks_embedding_hnsw, below the join: the k nearest are found on chunks alone.
        var limit = seen.Plan.FindIndex(l => l.Contains("Limit", StringComparison.Ordinal));
        var hnsw = seen.Plan.FindIndex(l => l.Contains("Index Scan using chunks_embedding_hnsw", StringComparison.Ordinal));
        var join = seen.Plan.FindIndex(l => l.Contains("Nested Loop", StringComparison.Ordinal) || l.Contains("Join", StringComparison.Ordinal));
        Assert.True(hnsw > limit && limit > join && join >= 0,
            "expected … Join → Limit → Index Scan using chunks_embedding_hnsw. PLAN:\n" + plan);
    }

    [Fact]
    public async Task For_a_few_hundred_rows_the_planner_prefers_an_exact_btree_sort()
    {
        // Same SQL, smaller tenant: exact results via the chunks_tenant btree. Correct, and cheaper at this size.
        await using var database = await TestDatabase.CreateAsync();
        await Seed.DocumentAsync(database, Seed.TenantA, "crowd", Vectors.Crowd(520, seed: 1));
        await database.ExecuteScriptAsync("ANALYZE chunks; ANALYZE documents");

        var seen = await SearchWithProbe(database, Seed.TenantA);

        Assert.Contains(seen.Plan, l => l.Contains("chunks_tenant", StringComparison.Ordinal));
        Assert.DoesNotContain(seen.Plan, l => l.Contains("chunks_embedding_hnsw", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_settings_do_not_leak_to_the_next_user_of_the_pooled_connection()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Seed.DocumentAsync(database, Seed.TenantA, "doc", Vectors.Crowd(20, seed: 4));
        await SearchWithProbe(database, Seed.TenantA, new ChunkSearchOptions(MaxScanTuples: 777));

        // Same pool, so very likely the same physical connection: SET LOCAL must have ended with the transaction.
        await using var connection = await database.DataSource.OpenConnectionAsync(Ct);
        await using var check = new NpgsqlCommand(
            "SELECT current_setting('hnsw.iterative_scan'), current_setting('hnsw.max_scan_tuples')", connection);
        await using var reader = await check.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);

        Assert.Equal("off", reader.GetString(0));
        Assert.Equal("20000", reader.GetString(1));
    }
}
