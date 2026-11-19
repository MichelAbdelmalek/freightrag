using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;

namespace FreightRag.IntegrationTests;

/// <summary>Nearest-neighbour search against real pgvector (HNSW index, iterative scans).</summary>
public sealed class SearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<IReadOnlyList<Core.Application.Queries.SearchHit>> Search(TestDatabase database, Guid tenant, int k, string? model = null) =>
        new EfChunkSearch(database.Contexts).SearchAsync(
            new TenantId(tenant), new EmbeddingModelName(model ?? TestDatabase.Model.Value),
            new EmbeddingVector(Vectors.Query()), TopK.Create(k), Ct);

    [Fact]
    public async Task a_Tenant_isolation_the_globally_closest_chunk_of_another_tenant_is_never_returned()
    {
        await using var database = await TestDatabase.CreateAsync();
        var mine = await Seed.DocumentAsync(database, Seed.TenantA, "tenant-a-doc",
            [Vectors.Near(1, 0.5f), Vectors.Near(2, 1f), Vectors.Near(3, 2f)]);
        // Tenant B owns a chunk identical to the query: distance 0, the best match in the whole table.
        var theirs = await Seed.DocumentAsync(database, Seed.TenantB, "tenant-b-doc", [Vectors.Query()]);

        var hits = await Search(database, Seed.TenantA, k: 5);

        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.Equal(mine.Id, h.DocumentId));
        Assert.DoesNotContain(hits, h => h.DocumentId == theirs.Id);
        Assert.Equal(hits.OrderBy(h => h.Distance).Select(h => h.Distance), hits.Select(h => h.Distance));
        Assert.Equal("tenant-a-doc", hits[0].Title);
    }

    /// <summary>
    /// With the btree index on tenant_id, PostgreSQL may answer a small tenant's search exactly (fetch the tenant's
    /// rows, sort by distance) instead of using HNSW — see Small_tenants_can_get_an_exact_plan_from_the_tenant_index.
    /// To test what happens when HNSW *is* used (large tenants, or filters the btree can't serve), these tests remove
    /// that index in their throwaway database, so HNSW is the only ordered path.
    /// </summary>
    private static async Task HnswOnly(TestDatabase database) =>
        await database.ExecuteScriptAsync("DROP INDEX chunks_tenant");

    private static async Task<List<string>> ExplainSearch(TestDatabase database, Guid tenant)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(Ct);
        await using var explain = new NpgsqlCommand(
            "EXPLAIN SELECT id FROM chunks WHERE tenant_id = $1 AND embedding_model = $2 ORDER BY embedding <=> $3 LIMIT 5", connection);
        explain.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = tenant });
        explain.Parameters.Add(new NpgsqlParameter<string> { TypedValue = TestDatabase.Model.Value });
        explain.Parameters.Add(new NpgsqlParameter { Value = new Vector(Vectors.Query()) });
        var plan = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            plan.Add(reader.GetString(0));
        return plan;
    }

    [Fact]
    public async Task b_Overfiltering_a_small_tenant_still_gets_topK_results_thanks_to_iterative_scan()
    {
        await using var database = await TestDatabase.CreateAsync();
        await HnswOnly(database);
        // Tenant A: 520 chunks crowding the query's neighbourhood. Tenant B: 10 chunks further away.
        await Seed.DocumentAsync(database, Seed.TenantA, "crowd", Vectors.Crowd(520, seed: 1));
        var small = await Seed.DocumentAsync(database, Seed.TenantB, "small", Vectors.Distant(10, seed: 2));

        var hits = await Search(database, Seed.TenantB, k: 5);

        Assert.Equal(5, hits.Count);
        Assert.All(hits, h => Assert.Equal(small.Id, h.DocumentId));
    }

    [Fact]
    public async Task b_When_the_iterative_scan_gives_up_early_the_search_falls_back_to_an_exact_scan()
    {
        // Iterative scans stop after hnsw.max_scan_tuples (20,000 by default). A tenant whose chunks are all further
        // away than 20,000 chunks of a bigger tenant would get nothing. Here the limit is tiny (50) to show it with
        // little data: the approximate pass comes back short, the tenant has more chunks, so an exact scan runs.
        await using var database = await TestDatabase.CreateAsync();
        await HnswOnly(database);
        await Seed.DocumentAsync(database, Seed.TenantA, "crowd", Vectors.Crowd(520, seed: 1));
        var small = await Seed.DocumentAsync(database, Seed.TenantB, "small", Vectors.Distant(10, seed: 2));

        var hits = await new EfChunkSearch(database.Contexts, new ChunkSearchOptions(MaxScanTuples: 50)).SearchAsync(
            new TenantId(Seed.TenantB), TestDatabase.Model, new EmbeddingVector(Vectors.Query()), TopK.Create(5), Ct);

        Assert.Equal(5, hits.Count);
        Assert.All(hits, h => Assert.Equal(small.Id, h.DocumentId));
        Assert.Equal(hits.OrderBy(h => h.Distance).Select(h => h.Distance), hits.Select(h => h.Distance));
    }

    [Fact]
    public async Task b_Control_without_iterative_scan_the_same_search_loses_results()
    {
        // Why the SET LOCAL matters: HNSW hands over its ef_search (40) nearest candidates — all tenant A here —
        // and the tenant filter then removes them, leaving tenant B with fewer than k (typically none).
        await using var database = await TestDatabase.CreateAsync();
        await HnswOnly(database);
        await Seed.DocumentAsync(database, Seed.TenantA, "crowd", Vectors.Crowd(520, seed: 1));
        await Seed.DocumentAsync(database, Seed.TenantB, "small", Vectors.Distant(10, seed: 2));

        await using var db = database.Contexts.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = off", Ct);
        var hits = await EfChunkSearch.BuildQuery(db, new TenantId(Seed.TenantB), TestDatabase.Model,
            new EmbeddingVector(Vectors.Query()), TopK.Create(5)).ToListAsync(Ct);

        Assert.True(hits.Count < 5, $"Expected overfiltering without iterative scan, got {hits.Count} results.");
    }

    [Fact]
    public async Task b_Without_the_tenant_index_the_search_uses_the_hnsw_index()
    {
        await using var database = await TestDatabase.CreateAsync();
        await HnswOnly(database);
        await Seed.DocumentAsync(database, Seed.TenantA, "crowd", Enumerable.Range(0, 50).Select(i => Vectors.Near(i, 0.1f)));

        Assert.Contains(await ExplainSearch(database, Seed.TenantA), line => line.Contains("chunks_embedding_hnsw"));
    }

    [Fact]
    public async Task Small_tenants_can_get_an_exact_plan_from_the_tenant_index()
    {
        // With the real schema, a selective tenant filter lets the planner read just that tenant's rows through the
        // chunks_tenant btree and sort them by exact distance — no approximation, no overfiltering. Correct results
        // either way; this documents why overfiltering mostly shows up for large tenants.
        await using var database = await TestDatabase.CreateAsync();
        await Seed.DocumentAsync(database, Seed.TenantA, "crowd", Vectors.Crowd(520, seed: 1));
        var small = await Seed.DocumentAsync(database, Seed.TenantB, "small", Vectors.Distant(10, seed: 2));

        var plan = await ExplainSearch(database, Seed.TenantB);
        var hits = await Search(database, Seed.TenantB, k: 5);

        Assert.Contains(plan, line => line.Contains("chunks_tenant"));
        Assert.Equal(5, hits.Count);
        Assert.All(hits, h => Assert.Equal(small.Id, h.DocumentId));
    }

    [Fact]
    public async Task c_Chunks_embedded_with_a_different_model_are_never_returned()
    {
        await using var database = await TestDatabase.CreateAsync();
        var current = await Seed.DocumentAsync(database, Seed.TenantA, "current-model", [Vectors.Near(1, 1f)]);
        // Same tenant, vector identical to the query — but produced by another model, so not comparable.
        var other = await Seed.DocumentAsync(database, Seed.TenantA, "other-model", [Vectors.Query()], model: "other-embed-model");

        var hits = await Search(database, Seed.TenantA, k: 5);

        Assert.Equal(current.Id, Assert.Single(hits).DocumentId);
        Assert.DoesNotContain(hits, h => h.DocumentId == other.Id);
    }

    [Fact]
    public async Task Distance_is_cosine_distance_zero_for_the_same_direction()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Seed.DocumentAsync(database, Seed.TenantA, "doc", [Vectors.Query(), Vectors.Far(5)]);

        var hits = await Search(database, Seed.TenantA, k: 2);

        Assert.Equal(0, hits[0].Distance, precision: 6);
        Assert.InRange(hits[1].Distance, 0.5, 1.0); // mostly orthogonal
    }

    [Fact]
    public async Task An_empty_tenant_gets_an_empty_list_not_an_error()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Seed.DocumentAsync(database, Seed.TenantA, "doc", [Vectors.Query()]);

        Assert.Empty(await Search(database, Seed.TenantB, k: 5));
    }
}
