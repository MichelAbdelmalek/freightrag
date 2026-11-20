using FreightRag.Core.Application.Ports;
using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>
/// Nearest-neighbour search with pgvector through EF Core.
///
/// Why the transaction and SET LOCAL: the HNSW index returns its <c>hnsw.ef_search</c> (default 40) best
/// candidates and only then are the WHERE filters (tenant, model) applied. If most close neighbours belong to
/// other tenants, fewer than k rows survive — "overfiltering". With <c>hnsw.iterative_scan = strict_order</c>
/// (pgvector ≥ 0.8) the index keeps scanning until k rows pass the filter, still in exact distance order.
/// SET LOCAL limits the setting to this transaction. The scan still stops after <c>hnsw.max_scan_tuples</c>;
/// if that leaves too few results, an exact scan of the tenant's chunks answers instead (see SearchAsync).
/// </summary>
public sealed partial class EfChunkSearch(
    IDbContextFactory<FreightRagDbContext> contexts,
    ChunkSearchOptions? options = null,
    ILogger<EfChunkSearch>? logger = null) : IChunkSearch
{
    private readonly ChunkSearchOptions _options = options ?? new ChunkSearchOptions();
    private readonly ILogger<EfChunkSearch> _logger = logger ?? NullLogger<EfChunkSearch>.Instance;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        TenantId tenantId, EmbeddingModelName model, EmbeddingVector query, TopK topK, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // SET cannot take parameters, so the constant setting uses SET LOCAL and the configurable one uses
        // set_config(name, value, is_local => true) — the parameterized equivalent of SET LOCAL.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = strict_order", cancellationToken);
        var maxScanTuples = _options.MaxScanTuples.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('hnsw.max_scan_tuples', {maxScanTuples}, true)", cancellationToken);

        var hits = await BuildQuery(db, tenantId, model, query, topK).ToListAsync(cancellationToken);

        // An iterative scan gives up after max_scan_tuples. If it came back short although the tenant has more
        // chunks of this model (e.g. a bigger tenant owns the 20,000 nearest neighbours), answer exactly instead:
        // PostgreSQL reads this tenant's rows (btree on tenant_id, or a full scan) and sorts them by distance.
        if (hits.Count < topK.Value)
        {
            var available = await db.Chunks.CountAsync(
                c => c.TenantId == tenantId.Value && c.EmbeddingModel == model.Value, cancellationToken);
            if (available > hits.Count)
            {
                // Visible on purpose: each fallback is a slower query. See SearchTelemetry.ExactFallbacks.
                SearchTelemetry.ExactFallbacks.Add(1, new KeyValuePair<string, object?>("tenant.id", tenantId.Value.ToString()));
                LogExactFallback(_logger, tenantId.Value, hits.Count, topK.Value, available, _options.MaxScanTuples);

                // Make the exact path explicit and independent of server/database settings: no index scans (so HNSW is
                // impossible), but sequential and bitmap scans allowed (tenant btree via bitmap, or a full scan).
                await db.Database.ExecuteSqlRawAsync(
                    "SET LOCAL enable_indexscan = off; SET LOCAL enable_bitmapscan = on; SET LOCAL enable_seqscan = on",
                    cancellationToken);
                hits = await BuildQuery(db, tenantId, model, query, topK).ToListAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return hits;
    }

    [LoggerMessage(EventId = 2001, EventName = "ExactSearchFallback", Level = LogLevel.Warning,
        Message = "Exact search fallback for tenant {TenantId}: the approximate HNSW scan returned {ApproximateCount} of {TopK} results " +
                  "although {Available} chunks exist (hnsw.max_scan_tuples = {MaxScanTuples}). Frequent fallbacks mean the index settings or data layout need attention.")]
    private static partial void LogExactFallback(ILogger logger, Guid tenantId, int approximateCount, int topK, int available, int maxScanTuples);

    /// <summary>
    /// The query, separate so tests can inspect the generated SQL. EF renders the inner part as a subquery
    /// — the nearest-neighbour search on chunks alone — joined to documents afterwards (equivalent to a CTE:
    /// PostgreSQL inlines non-materialized CTEs into exactly this shape).
    /// </summary>
    internal static IQueryable<SearchHit> BuildQuery(
        FreightRagDbContext db, TenantId tenantId, EmbeddingModelName model, EmbeddingVector query, TopK topK)
    {
        var tenant = tenantId.Value;
        var modelName = model.Value;
        var vector = new Vector(query.Values);
        var k = topK.Value;

        var nearest = db.Chunks.AsNoTracking()
            .Where(c => c.TenantId == tenant && c.EmbeddingModel == modelName)
            .OrderBy(c => c.Embedding.CosineDistance(vector)) // <=> : cosine distance, served by chunks_embedding_hnsw
            .Take(k)
            .Select(c => new
            {
                c.Id,
                c.DocumentId,
                c.ChunkIndex,
                c.Content,
                Distance = c.Embedding.CosineDistance(vector),
            });

        return from n in nearest
               join d in db.Documents.AsNoTracking().Where(d => d.TenantId == tenant) on n.DocumentId equals d.Id
               orderby n.Distance, n.Id
               select new SearchHit(n.Id, n.DocumentId, d.Title, d.DocType, n.ChunkIndex, n.Content, n.Distance);
    }
}

/// <summary>Tuning for <see cref="EfChunkSearch"/>.</summary>
/// <param name="MaxScanTuples">
/// Upper bound on tuples an iterative HNSW scan visits (pgvector's hnsw.max_scan_tuples, default 20,000). Higher means
/// fewer exact fallbacks but slower worst cases.
/// </param>
public sealed record ChunkSearchOptions(int MaxScanTuples = 20_000)
{
    public int MaxScanTuples { get; } = MaxScanTuples is >= 1 and <= 10_000_000
        ? MaxScanTuples
        : throw new ArgumentOutOfRangeException(nameof(MaxScanTuples), "Search:MaxScanTuples must be between 1 and 10,000,000.");
}
