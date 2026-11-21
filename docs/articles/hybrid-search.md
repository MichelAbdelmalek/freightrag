# Hybrid search: full-text + vectors, fused with RRF

Vector search finds chunks by **meaning**: "how long can the truck wait before we pay?" finds the detention clause
although it shares no word with it. It is weak exactly where freight questions are strict — **identifiers**. To an
embedding model, `rate-confirmation-48213` and `rate-confirmation-48240` are almost the same text, so "detention rate
for load 48213" can rank the wrong load's RATE section first.

Full-text search finds chunks by **words**. It knows nothing about meaning, but `48213` either is in a chunk or it
isn't. **Hybrid search** runs both and merges the two rankings:

```
                ┌─► vector leg  (embedding, cosine distance)        ─► up to 20 candidates ─┐
question ──────►│                                                                          ├─► RRF ─► top k
                └─► keyword leg (tsquery @@ content_tsv, ts_rank_cd) ─► up to 20 candidates ─┘
```

`POST /api/v1/search` takes `"mode": "vector" | "keyword" | "hybrid"` (default `hybrid`). Every result carries
`vectorRank` and `keywordRank` (its position in each leg's candidates, `null` if that leg did not return it),
`fusedScore`, and the cosine `distance` (computed for keyword-only results too). Both legs run in one
**REPEATABLE READ** transaction, so every statement reads the same snapshot of the data. (Under PostgreSQL's default
READ COMMITTED each statement takes a new snapshot, and a document ingested between the two legs could appear in one
leg only.) The transaction only reads, so it cannot fail with a serialization error.

## How PostgreSQL full-text search works

### tsvector: the document side

`to_tsvector('english', text)` turns text into a sorted list of **lexemes** with positions:

```sql
SELECT to_tsvector('english', 'Detention: $50.00 per hour after 2 free hours');
-- '2':6 '50.00':2 'detent':1 'free':7 'hour':4,8 'per':3
```

- **Stemming** — "Detention" → `detent`, "hours" → `hour`, so "detention" matches "detentions" and "hour" matches
  "hours". The `english` configuration uses the Snowball stemmer, which works on word endings, not meaning:
  "detained" becomes `detain`, a different lexeme from `detent`. Matching related words is the vector leg's job.
- **Stop words** — "after" (and "the", "is", "for", …) are dropped; they occur everywhere and carry no signal. The
  positions still count them (note the gap between 5 and 6), which is what phrase and proximity ranking rely on.
- **Lower-casing and parsing** — the parser recognises numbers, words, e-mails, hyphenated words and so on.

`chunks.content_tsv` stores this vector as a **generated column**:

```sql
content_tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
CREATE INDEX chunks_content_tsv ON chunks USING GIN (content_tsv);
```

PostgreSQL computes it on every insert and update, so it can never drift from `content`, and the migration that adds
it fills it for existing rows during the `ALTER TABLE` — no backfill code. (EF Core: `HasGeneratedTsVectorColumn`.)
A **GIN** (generalized inverted) index maps each lexeme to the rows containing it — the same idea as the index at the
back of a book — so `content_tsv @@ query` doesn't read every chunk.

### Identifiers

The parser splits codes at hyphens, and documents and questions go through the same parser, so exact identifiers
match:

| Text | Lexemes |
|---|---|
| `NFL-2026-1187` | `'nfl' & '-2026' & '-1187'` |
| `SAV-778431` | `'sav' & '-778431'` |
| `48213` | `'48213'` |

One side effect: the number after a hyphen keeps its minus sign, so a question that says "BOL 778431" (without
`SAV-`) produces `'778431'`, which does **not** match the document's `'-778431'`. The vector leg still has a chance;
this is one reason to fuse the two instead of choosing one.

### tsquery: the question side, AND vs OR

`plainto_tsquery('english', 'What is the detention rate for load 48213?')` gives

```
'detent' & 'rate' & 'load' & '48213'
```

— **AND**: a chunk must contain *every* word. That is right for a search box ("show me exactly this") but wrong for
retrieval: a question is phrased by a person, the answer chunk rarely contains all of its words, and "load" may only
be in the header of a different chunk. So the keyword leg turns the ANDs into **ORs**:

```sql
replace(plainto_tsquery('english', @q)::text, ' & ', ' | ')::tsquery
-- 'detent' | 'rate' | 'load' | '48213'
```

The replacement includes the spaces around `&` on purpose. The parser keeps a URL such as
`https://portal.example.com/track?id=1&ref=2` as whole lexemes (`'/track?id=1&ref=2'`). A bare `'&'` → `'|'` would
corrupt them so they could never match. PostgreSQL always prints the operator as `' & '`, and a lexeme never contains
whitespace, so only operators are rewritten. (An integration test searches for exactly such a URL.)

A chunk matching any word is a candidate; the ranking (next section) puts chunks that match more — and rarer — words
first. OR alone would return too much; ranking plus fusion is what keeps it precise.

### Why plainto_tsquery, never to_tsquery on user text

`to_tsquery` parses an **operator language**: `&`, `|`, `!`, `<->`, `:*`, parentheses. Passing it a user's question
lets the user write query syntax — at best a syntax error (a 500), at worst a query that is far more expensive or
matches differently than intended. `plainto_tsquery` treats the input as plain text: punctuation is ignored and the
only operator it produces is `&`. Only *its output* — which we control — is rewritten and cast to `tsquery`. The
question is always a bound parameter, never part of the SQL string.

A question of only stop words or symbols ("the", "???") gives an **empty** tsquery: PostgreSQL emits a NOTICE, matches
nothing, and raises no error. The keyword leg returns no candidates and hybrid mode returns the vector results
unchanged (all `keywordRank` are `null`).

## Ranking: ts_rank_cd vs BM25

The keyword leg orders matches by `ts_rank_cd(content_tsv, query) DESC`, then chunk id (for a stable order).

- **ts_rank_cd** ("cover density") scores a chunk higher when the query's lexemes occur, and occur **close together**.
  It uses the positions stored in the tsvector.
- **BM25**, the standard ranking in search engines (Lucene, Elasticsearch), works from different signals.
  - **Rare terms count more** (inverse document frequency). `48213` is rarer than `rate`, so it should matter more.
  - **Term-frequency saturation**: the tenth occurrence of a word adds little.
  - **Length normalisation**: a long chunk isn't rewarded just for being long.

PostgreSQL has **no built-in BM25**. `ts_rank_cd` knows nothing about how rare a word is across the table. Extensions
add BM25 to PostgreSQL (e.g. ParadeDB's `pg_search`, or `pg_textsearch`), but this project uses only stock PostgreSQL
plus pgvector. For short, similar-length chunks the difference is smaller than it sounds, and the eval
(`evals/`, `--compare`) is how to decide whether it is worth adding.

## Reciprocal Rank Fusion (RRF)

The two legs score on **different scales**. A cosine distance of 0.31 and a `ts_rank_cd` of 0.1 cannot be added or
compared. Normalising them (min-max, z-scores) is fragile, because the scales shift from question to question. RRF
ignores the scores and uses only the **ranks**:

```
score(chunk) = Σ over the lists it appears in of  1 / (k + rank)        rank starts at 1, k = 60
```

- A chunk ranked well in **both** lists beats a chunk ranked first in only one.
- **k** damps the advantage of the very top ranks. With k = 60, rank 1 (1/61 = 0.0164) is barely ahead of rank 2
  (1/62 = 0.0161), so one leg alone cannot dominate. k = 60 comes from the original paper (Cormack, Clarke and
  Büttcher, SIGIR 2009). It is configurable as `Search:RrfK`.
- Ties are broken by chunk id, so the same data always gives the same order.

`ReciprocalRankFusion` in the domain layer is a pure function of the two id lists. Unit tests check it against these
hand-computed values. Single-leg modes fuse one list, so `fusedScore` is always present and means the same thing.

### Worked example

Below is an illustrative case for "What is the detention rate for load 48213?". Each leg returns up to
`Search:CandidateCount` candidates (default 20).

| Chunk | Vector rank | Keyword rank | RRF score |
|---|---|---|---|
| rate-confirmation-48213 › RATE | 3 | 1 | 1/63 + 1/61 = 0.015873 + 0.016393 = **0.032266** |
| rate-confirmation-48240 › RATE (near-duplicate) | 1 | 4 | 1/61 + 1/64 = 0.016393 + 0.015625 = **0.032018** |
| driver-handbook-excerpt › 3. DETENTION REPORTING | 2 | — | 1/62 = **0.016129** |
| invoice mentioning load 48213 | — | 2 | 1/62 = **0.016129** (tie → lower chunk id first) |

The vector leg liked the wrong load best: it is nearly the same text. The keyword leg put the right load first. Many
chunks contain `48213`, because the context header (`Load: 48213 | …`) is on every chunk of that load's rate
confirmation, bill of lading and invoice. But this chunk also contains `detent` and `rate`, close together, so
`ts_rank_cd` scores it highest. After fusion the right chunk wins. It was strong in
both lists, and the near-duplicate was strong in only one. The chunks found by only one leg follow.

The real numbers for our 45 questions come from `--compare`, which prints recall@1, recall@5, MRR and multi-chunk
coverage for each mode side by side, plus every question's rank in each mode.

## The keyword query

```sql
WITH q AS (SELECT replace(plainto_tsquery('english', @q)::text, ' & ', ' | ')::tsquery AS query)
SELECT c.id, c.document_id, d.title, d.doc_type, c.chunk_index, c.content,
       c.embedding <=> @vector AS distance
FROM chunks AS c
CROSS JOIN q
JOIN documents AS d ON d.id = c.document_id AND d.tenant_id = @tenant
WHERE c.tenant_id = @tenant AND c.embedding_model = @model AND c.content_tsv @@ q.query
ORDER BY ts_rank_cd(c.content_tsv, q.query) DESC, c.id
LIMIT @candidates
```

- **Tenant isolation**: both `chunks` and `documents` are filtered by the caller's tenant. The tenant comes only from
  the API key, never from the request body.
- **Model filter**: a chunk embedded by another model could match the words, but its `distance` would be meaningless.
  The keyword leg is filtered like the vector leg, and integration tests check both filters.
- Every value is a parameter (EF Core `SqlQuery` with an interpolated string becomes `@p0…`). A unit test pins the
  shape and checks that `to_tsquery(` never receives a parameter.

### Does it use the GIN index?

It does on realistic data. In an integration test with 1,000 documents, 5,000 chunks, and a word found in one chunk,
`EXPLAIN` shows `chunks_content_tsv`. Two things decide whether the index is used:

- **Data shape.** If every chunk belongs to one document, PostgreSQL correctly prefers to read that document's chunks
  through `(document_id, chunk_index)`.
- **The GIN pending list.** GIN indexes buffer new entries in an unsorted *pending list* (`fastupdate`). The planner
  prices it as a scan, so right after a bulk load the GIN index can look expensive. `VACUUM`, or autovacuum in a
  normal database, merges the list into the index. A plain `ANALYZE` does not.

On a few hundred chunks, any plan is fast. Check the plan once a tenant holds thousands of chunks.
