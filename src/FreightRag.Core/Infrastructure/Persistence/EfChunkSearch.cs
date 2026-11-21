using System.Data;
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
/// Candidate retrieval: nearest-neighbour search with pgvector (vector leg) and PostgreSQL full-text search
/// (keyword leg), through EF Core.
///
/// Why the transaction and SET LOCAL: the HNSW index returns its <c>hnsw.ef_search</c> (default 40) best
/// candidates and only then are the WHERE filters (tenant, model) applied. If most close neighbours belong to
/// other tenants, fewer than k rows survive — "overfiltering". With <c>hnsw.iterative_scan = strict_order</c>
/// (pgvector ≥ 0.8) the index keeps scanning until k rows pass the filter, still in exact distance order.
/// SET LOCAL limits the setting to this transaction. The scan still stops after <c>hnsw.max_scan_tuples</c>;
/// if that leaves too few results, an exact scan of the tenant's chunks answers instead (see VectorLegAsync).
/// The keyword leg (full-text search) runs in the same transaction; see KeywordQuery.
/// </summary>
public sealed partial class EfChunkSearch(
    IDbContextFactory<FreightRagDbContext> contexts,
    ChunkSearchOptions? options = null,
    ILogger<EfChunkSearch>? logger = null) : IChunkSearch
{
    private readonly ChunkSearchOptions _options = options ?? new ChunkSearchOptions();
    private readonly ILogger<EfChunkSearch> _logger = logger ?? NullLogger<EfChunkSearch>.Instance;

    public async Task<CandidateLists> RetrieveCandidatesAsync(
        TenantId tenantId, EmbeddingModelName model, EmbeddingVector query, SearchText text, SearchMode mode,
        CandidateCount candidates, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        // One REPEATABLE READ transaction: every statement of both legs reads the same snapshot (READ COMMITTED would
        // take a new one per statement, so an ingest committing in between could show up in one leg only), and the
        // SET LOCALs below apply to the vector leg. It only reads, so it can never fail with a serialization error.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        IReadOnlyList<ChunkCandidate> vector = mode.UsesVector
            ? await VectorLegAsync(db, tenantId, model, query, candidates.Value, cancellationToken)
            : [];
        IReadOnlyList<ChunkCandidate> keyword = mode.UsesKeyword
            ? await KeywordLegAsync(db, tenantId, model, query, text, candidates.Value, cancellationToken)
            : [];

        await transaction.CommitAsync(cancellationToken);
        return new CandidateLists(vector, keyword);
    }

    private async Task<IReadOnlyList<ChunkCandidate>> VectorLegAsync(
        FreightRagDbContext db, TenantId tenantId, EmbeddingModelName model, EmbeddingVector query, int limit,
        CancellationToken cancellationToken)
    {
        // SET cannot take parameters, so the constant setting uses SET LOCAL and the configurable one uses
        // set_config(name, value, is_local => true) — the parameterized equivalent of SET LOCAL.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = strict_order", cancellationToken);
        var maxScanTuples = _options.MaxScanTuples.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('hnsw.max_scan_tuples', {maxScanTuples}, true)", cancellationToken);

        var hits = await VectorQuery(db, tenantId, model, query, limit).ToListAsync(cancellationToken);

        // An iterative scan gives up after max_scan_tuples. If it came back short although the tenant has more
        // chunks of this model (e.g. a bigger tenant owns the 20,000 nearest neighbours), answer exactly instead:
        // PostgreSQL reads this tenant's rows (btree on tenant_id, or a full scan) and sorts them by distance.
        if (hits.Count < limit)
        {
            var available = await db.Chunks.CountAsync(
                c => c.TenantId == tenantId.Value && c.EmbeddingModel == model.Value, cancellationToken);
            if (available > hits.Count)
            {
                // Visible on purpose: each fallback is a slower query. See SearchTelemetry.ExactFallbacks.
                SearchTelemetry.ExactFallbacks.Add(1, new KeyValuePair<string, object?>("tenant.id", tenantId.Value.ToString()));
                LogExactFallback(_logger, tenantId.Value, hits.Count, limit, available, _options.MaxScanTuples);

                // Make the exact path explicit and independent of server/database settings: no index scans (so HNSW is
                // impossible), but sequential and bitmap scans allowed (tenant btree via bitmap, or a full scan).
                await db.Database.ExecuteSqlRawAsync(
                    "SET LOCAL enable_indexscan = off; SET LOCAL enable_bitmapscan = on; SET LOCAL enable_seqscan = on",
                    cancellationToken);
                hits = await VectorQuery(db, tenantId, model, query, limit).ToListAsync(cancellationToken);
            }
        }
        return hits;
    }

    private static async Task<IReadOnlyList<ChunkCandidate>> KeywordLegAsync(
        FreightRagDbContext db, TenantId tenantId, EmbeddingModelName model, EmbeddingVector query, SearchText text,
        int limit, CancellationToken cancellationToken)
    {
        var rows = await KeywordQuery(db, tenantId, model, query, text, limit).ToListAsync(cancellationToken);
        return rows.Select(r => new ChunkCandidate(r.ChunkId, r.DocumentId, r.Title, r.DocType, r.ChunkIndex, r.Content, r.Distance))
            .ToList();
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
    internal static IQueryable<ChunkCandidate> VectorQuery(
        FreightRagDbContext db, TenantId tenantId, EmbeddingModelName model, EmbeddingVector query, int limit)
    {
        var tenant = tenantId.Value;
        var modelName = model.Value;
        var vector = new Vector(query.Values);
        var k = limit;

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
               select new ChunkCandidate(n.Id, n.DocumentId, d.Title, d.DocType, n.ChunkIndex, n.Content, n.Distance);
    }

    /// <summary>
    /// The keyword leg: PostgreSQL full-text search. Hand-written SQL because the query construction must be exactly
    ///
    ///     replace(plainto_tsquery('english', @text)::text, ' &amp; ', ' | ')::tsquery
    ///
    /// plainto_tsquery treats the user's text as plain words (operators such as !, &lt;-&gt;, :* or quotes are not
    /// interpreted — user text never reaches to_tsquery) and joins the lexemes with AND (' &amp; '). Rewriting that
    /// operator, spaces included, to ' | ' gives OR semantics; a bare &amp; inside a quoted lexeme (the parser keeps URLs
    /// such as '/track?id=1&amp;ref=2' whole) stays intact. A chunk sharing only some words still matches, and ts_rank_cd
    /// ranks chunks with more (and closer) matches higher. Stop words / symbols only → an empty tsquery that matches nothing, without an error.
    /// Every value is a parameter (EF Core turns the interpolations into @p0, @p1, …).
    /// </summary>
    internal static IQueryable<KeywordRow> KeywordQuery(
        FreightRagDbContext db, TenantId tenantId, EmbeddingModelName model, EmbeddingVector query, SearchText text, int limit)
    {
        var tenant = tenantId.Value;
        var modelName = model.Value;
        var vector = new Vector(query.Values);
        var words = text.Value;
        return db.Database.SqlQuery<KeywordRow>($"""
            WITH q AS (SELECT replace(plainto_tsquery('english', {words})::text, ' & ', ' | ')::tsquery AS query)
            SELECT c.id AS "ChunkId", c.document_id AS "DocumentId", d.title AS "Title", d.doc_type AS "DocType",
                   c.chunk_index AS "ChunkIndex", c.content AS "Content", c.embedding <=> {vector} AS "Distance"
            FROM chunks AS c
            CROSS JOIN q
            JOIN documents AS d ON d.id = c.document_id AND d.tenant_id = {tenant}
            WHERE c.tenant_id = {tenant} AND c.embedding_model = {modelName} AND c.content_tsv @@ q.query
            ORDER BY ts_rank_cd(c.content_tsv, q.query) DESC, c.id
            LIMIT {limit}
            """);
    }

    /// <summary>Row shape of <see cref="KeywordQuery"/> (column aliases match these property names).</summary>
    internal sealed class KeywordRow
    {
        public Guid ChunkId { get; set; }
        public Guid DocumentId { get; set; }
        public string Title { get; set; } = "";
        public string DocType { get; set; } = "";
        public int ChunkIndex { get; set; }
        public string Content { get; set; } = "";
        public double Distance { get; set; }
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
