using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FreightRag.Tests.Infrastructure.Persistence;

/// <summary>
/// Pins the SQL EF Core generates for the search, without a database (ToQueryString only renders it).
/// The shape matters for correctness and performance: the nearest-neighbour search must run on chunks alone,
/// filtered by tenant and model, with ORDER BY distance LIMIT k (so the HNSW index can serve it), and only then
/// be joined to documents — which is filtered by tenant again.
/// </summary>
public class EfChunkSearchQueryTests
{
    private static string Sql()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var tenant = new TenantId(Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097"));
        var query = new EmbeddingVector(new float[EmbeddingVector.Dimensions]);
        var rendered = EfChunkSearch.BuildQuery(db, tenant, new EmbeddingModelName("nomic-embed-text"), query, TopK.Create(5))
            .ToQueryString();
        // ToQueryString starts with "-- @name='value'" comment lines describing the parameters; keep only the SQL.
        return string.Join('\n', rendered.Split('\n').Where(line => !line.StartsWith("--", StringComparison.Ordinal)));
    }

    [Fact]
    public void Parameter_values_travel_separately_from_the_sql()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var rendered = EfChunkSearch.BuildQuery(db, new TenantId(Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097")),
            new EmbeddingModelName("nomic-embed-text"), new EmbeddingVector(new float[EmbeddingVector.Dimensions]), TopK.Create(5)).ToQueryString();

        // The values appear only in the parameter list EF prints above the statement.
        Assert.Contains("-- @tenant='de1c81e9-4aa5-4f07-87bd-c5d6f48ff097'", rendered);
        Assert.Contains("-- @modelName='nomic-embed-text'", rendered);
    }

    [Fact]
    public void Nearest_neighbour_search_runs_in_a_subquery_with_order_by_distance_and_limit()
    {
        var sql = Sql();

        var subquery = sql[sql.IndexOf("FROM (", StringComparison.Ordinal)..sql.IndexOf(") AS", StringComparison.Ordinal)];
        Assert.Contains("FROM chunks", subquery);
        Assert.Contains("tenant_id = @", subquery);
        Assert.Contains("embedding_model = @", subquery);
        Assert.Matches(@"ORDER BY \w+\.embedding <=> @\w+", subquery); // <=> is cosine distance (vector_cosine_ops)
        Assert.Contains("LIMIT @", subquery);
        Assert.DoesNotContain("documents", subquery); // the join happens after the k nearest are found
    }

    [Fact]
    public void Documents_are_joined_afterwards_and_filtered_by_tenant_too()
    {
        var sql = Sql();
        var outer = sql[sql.IndexOf(") AS", StringComparison.Ordinal)..];

        // Generated today (pinned shape):
        //   SELECT … FROM (SELECT … FROM chunks AS c WHERE c.tenant_id = @tenant AND c.embedding_model = @modelName
        //                  ORDER BY c.embedding <=> @vector LIMIT @p) AS c0
        //   INNER JOIN (SELECT … FROM documents AS d WHERE d.tenant_id = @tenant) AS d0 ON c0.document_id = d0.id
        //   ORDER BY c0.embedding <=> @vector, c0.id
        Assert.Contains("INNER JOIN (", outer);
        Assert.Contains("FROM documents", outer);
        Assert.Matches(@"\w+\.tenant_id = @tenant", outer);
    }

    [Fact]
    public void Values_are_parameters_not_literals()
    {
        var sql = Sql();

        Assert.DoesNotContain("de1c81e9", sql);
        Assert.DoesNotContain("'nomic-embed-text'", sql);
    }
}
