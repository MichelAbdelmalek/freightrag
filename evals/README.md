# Retrieval evaluation

Search *works*; this measures whether it finds the **right** chunks — before any LLM is put on top. An LLM given
the wrong chunks still writes a confident answer, and without this you can't tell which part failed.

## The two numbers

**recall@k** — for what share of questions is a correct chunk *anywhere* in the top k results?
`recall@5 = 0.9` means 9 out of 10 questions had a correct chunk in their top 5. It answers "did we find it?".

**MRR (mean reciprocal rank)** — *how high* the first correct chunk ranks. Rank 1 scores 1.0, rank 2 scores 0.5,
rank 3 scores 0.33, a miss scores 0; MRR is the average over all questions. It answers "did we put it at the top?" —
which matters because an LLM pays most attention to the first chunks it is given.

Both are computed over the *answerable* questions. Questions that need two chunks (e.g. "why is the invoice higher
than the agreed rate?") also report how many of the expected chunks made it into the top k.

## How a "correct chunk" is identified

Not by chunk id (ids change on every re-ingest) but by **document title + section name**, which are stable. The
section comes from the context header every chunk starts with: `Document: … | Section: RATE`.

## The question set

`retrieval_set.jsonl` — **45 questions** (37 answerable, 8 unanswerable) over both tenants, one per line: `id`,
`tenant`, `question`, `expected` (list of `{document_title, section}`), `kind` (`answerable` / `unanswerable`), `note`
(what the question tests — synonyms, identifiers among near-duplicate documents, two-chunk answers, …), and an optional
`acceptable` list.

`expected` vs `acceptable`: `expected` lists the chunks the question should retrieve; `acceptable` lists chunks that
also answer it — e.g. q19 asks for the temperature on load 48255, which the rate confirmation states and the bill of
lading repeats. Either kind counts as a hit for rank, recall and MRR; multi-chunk coverage counts `expected` only.
q11 and q19 use it (each with the bill of lading for the same load). A chunk that merely *mentions* the topic — e.g.
"loads arriving above 0 °F may be rejected" — is not acceptable: it would lead an answer astray.

The **unanswerable** questions have no correct chunk. Search still returns k chunks (there is no relevance threshold
yet), so the eval records the top-1 distance — the first data point for a future "relevant enough?" decision.

**Tenant isolation is checked on every question**: before searching, the eval lists each tenant's own documents
with that tenant's key; a hit from any other document fails the run (exit code 1).

## Retrieval modes

The API has three modes (see `docs/articles/hybrid-search.md`): `vector` (embeddings, by meaning), `keyword`
(PostgreSQL full-text search, by words) and `hybrid` (both, fused with Reciprocal Rank Fusion — the default).

- `--mode vector|keyword|hybrid` — evaluate one mode (default `hybrid`).
- `--compare` — run all three and print recall@1, recall@5, MRR and multi-chunk coverage side by side, followed by
  each question's rank in every mode. This is how to see *which* questions a mode wins or loses.

## Running it

The API must be running (with Ollama and the migrated database):

```bash
docker compose up -d api
```

The eval reads one API key per tenant from environment variables named after the tenant slug. Export them from your
User Secrets without printing them:

```bash
S=~/.microsoft/usersecrets/bc38a00e-bd57-4c78-9333-1f52ff1438ed/secrets.json
export FREIGHTRAG_API_KEY_TENANT_NILE_FREIGHT="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8-sig"))["ApiKeys:tenant-nile-freight"])' "$S")"
export FREIGHTRAG_API_KEY_TENANT_DELTA_HAUL="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8-sig"))["ApiKeys:tenant-delta-haul"])' "$S")"
```

```bash
docker compose --profile eval run --build --rm retrieval-eval
docker compose --profile eval run --rm retrieval-eval --compare
```

compose passes the variables through by name only, so the values never appear in files, command lines or shell
history (they are visible to `docker inspect` only while the short-lived container runs). The eval never prints or
stores a key.

Output: one line per question (rank of the first correct chunk or `miss`, top-1 distance, top-1 title › section),
then recall@1, recall@5 and MRR — or, with `--compare`, the side-by-side tables. Each run is saved to
`results/<UTC timestamp>.json` (git-ignored) with the dataset's SHA-256 and the mode(s), and per hit its vector rank,
keyword rank and fused score, so runs can be compared over time. Chunk content is not stored.

Exit codes: `0` run completed · `1` tenant isolation failure · `2` setup error (missing key, invalid dataset, API down).

## Tests and dependencies

```bash
docker compose --profile eval run --build --rm retrieval-eval-tests
```

Unit tests cover parsing, metrics (including `acceptable`), dataset validation, key handling, the mode sent to the
API, the comparison report and the isolation check (no network).

Python 3.14 and every package are pinned (`uv.lock`); `exclude-newer` in `pyproject.toml` makes uv ignore anything
published after a cutoff date, so the two-week rule also covers transitive dependencies. To update: move that date,
run `uv lock` (in the image: `docker build --target tools -t freightrag-evals-tools evals` then
`docker run --rm -v "$PWD/evals":/evals -w /evals freightrag-evals-tools uv lock`), and commit `uv.lock`.
