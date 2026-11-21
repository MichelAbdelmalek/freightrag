# Retrieval (search)

RAG has two halves: **retrieval** finds the passages relevant to a question, **generation** writes an answer
from them. FreightRAG's .NET side does retrieval only: `POST /api/v1/search` returns ranked chunks. Answer
generation will live in a separate Python/LangGraph service that calls this endpoint.

```
question ──► embed ("search_query: …") ──► k nearest chunks of *your* tenant ──► ranked list (+ distance)
```

This page covers the **vector leg**. By default the search is **hybrid**: a full-text keyword leg runs next to it in
the same transaction and the two rankings are fused with Reciprocal Rank Fusion — see
[Hybrid search](hybrid-search.md). `"mode": "vector"` runs only what this page describes.

## Same model, asymmetric prefixes

A question and a stored chunk can only be compared if the **same embedding model** produced both vectors;
vectors from different models live in different spaces and their distances are meaningless. That is why every
chunk stores `embedding_model`, and every search filters on the configured model.

`nomic-embed-text` was also trained with **task prefixes**: stored passages are embedded as
`search_document: <text>` and questions as `search_query: <text>`. The prefixes tell the model which side of the
search a text is on; leaving them out (or mixing them up) quietly degrades results. Neither prefix is ever stored.

## Top-k and distance

In vector mode the search returns the **k nearest** chunks (`topK`, 1–20, default 5), closest first, each with its **cosine
distance**: `0` means the same direction (same meaning), `1` unrelated, `2` opposite. It is computed by pgvector's
`<=>` operator, which the `chunks_embedding_hnsw` index (built with `vector_cosine_ops`) accelerates.

## Why there is no relevance threshold yet

It is tempting to drop results "further than 0.5". But raw distances are **not calibrated**: a good match for one
question can sit at 0.25 and for another at 0.45, depending on wording, length and the model. A fixed cut-off would
silently return nothing for some questions and junk for others. The answer-generation service will decide what to
keep, ideally with evaluation data (questions with known good answers) to choose a threshold — or with a re-ranker.
Until then, the API always returns the k nearest chunks, even when none is relevant.

## Overfiltering and iterative scans

An HNSW index is an *approximate* nearest-neighbour graph. A query walks the graph and returns its best
`hnsw.ef_search` candidates (default 40); **only then** are `WHERE` filters applied. In a multi-tenant table that
is a trap: if the 40 closest chunks all belong to other tenants (or to another model), the filter removes them and
your tenant gets fewer than k results — maybe none. This is called **overfiltering**.

pgvector 0.8 added **iterative index scans**. With

```sql
SET LOCAL hnsw.iterative_scan = strict_order;
```

the index keeps walking the graph until k rows pass the filter, and `strict_order` keeps the results in exact
distance order. The walk is bounded by `hnsw.max_scan_tuples` (20,000; configurable as `Search:MaxScanTuples`). If a
much bigger tenant owns more than that many closer chunks, the scan gives up early — so when the approximate pass
returns fewer than k results although the tenant has more chunks, the search **falls back to an exact scan** of the
tenant's chunks. The fallback states its plan explicitly for that transaction — index scans off (so HNSW is impossible),
bitmap and sequential scans on — so it stays exact whatever the server's settings.

The fallback is a slower query, so it is **visible**: a Warning log event `ExactSearchFallback` (tenant id, how many
results the approximate pass found, how many chunks the tenant has, the `max_scan_tuples` in force — never the query)
and a counter `freightrag.search.exact_fallbacks` on the meter `FreightRag.Search`
(`dotnet-counters monitor --counters FreightRag.Search`, or OpenTelemetry later). If it fires often, raise
`Search:MaxScanTuples` or change the data layout (for example per-tenant partitions); a sudden spike can also mean
someone is deliberately triggering expensive searches. `SET LOCAL` limits the setting to the search's own
transaction. The integration tests show both sides: with iterative scan off, a small tenant next to a big one gets
fewer than k results; with it on, it gets exactly k.

Iterative scans fix *filtering*, not *approximation*: HNSW only returns points it can reach while walking its graph,
so recall is high but not guaranteed to be 100 %. (The integration tests first used artificial single-axis vectors;
the resulting degenerate graph left some points unreachable. Real, dense embeddings don't behave like that, so the
tests now use dense, seeded random vectors.) For a production system, measure recall on your own data and tune
`hnsw.ef_search` / the index's `m` and `ef_construction` if needed.

### The planner can also avoid the problem

With the btree index on `tenant_id` (`chunks_tenant`), PostgreSQL can answer a tenant's search exactly — read that
tenant's rows and sort them by distance — and skip HNSW altogether. Measured on the real query EF Core sends:

| Tenant size | Plan |
|---|---|
| 50 or 520 chunks | `Limit → Sort → chunks_tenant` (exact; sorting a few hundred rows is cheaper than walking the graph) |
| 5,000 chunks | `Limit → Index Scan using chunks_embedding_hnsw`, below the join |

Both are correct; HNSW (and therefore iterative scans) matters once tenants hold thousands of chunks. The integration
tests capture the command EF actually sends, run `EXPLAIN` on it with its real parameters on the same connection and
transaction, and also read `current_setting('hnsw.iterative_scan')` there — proving the `SET LOCAL` reaches the query.

## The query shape

The search runs the nearest-neighbour step on `chunks` alone — filtered by tenant and model, `ORDER BY embedding
<=> @q LIMIT @k` — and joins `documents` afterwards, filtered by tenant again:

```sql
SELECT … FROM (
    SELECT … FROM chunks AS c
    WHERE c.tenant_id = @tenant AND c.embedding_model = @model
    ORDER BY c.embedding <=> @vector
    LIMIT @k
) AS c0
INNER JOIN (SELECT … FROM documents AS d WHERE d.tenant_id = @tenant) AS d0 ON c0.document_id = d0.id
ORDER BY c0.embedding <=> @vector, c0.id
```

EF Core generates this from LINQ (a unit test pins it). It is equivalent to a CTE: PostgreSQL inlines
non-materialized CTEs into exactly this form. Every value is a parameter.

## Untrusted content

Chunk content is whatever the tenant uploaded. When it is placed in an LLM prompt, it must be treated as **data,
never as instructions** — a document can contain text such as "ignore previous instructions…" (prompt injection).
The OpenAPI document says so on the `content` field, so the answer-generation service is warned at the contract.

## Privacy in logs

Questions can contain confidential details, so the API never logs the query text. It logs one line per search with
the tenant id, mode, query length, topK, how many candidates each leg returned, result count and duration — enough to monitor usage and latency.
