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

## Answer evaluation

Retrieval above asks "did search find the right chunks?". This asks the next question: **does the answer service say
the right things, abstain when it should, and cite honestly?** (Background: `docs/articles/answer-evaluation.md`.)

It is two separate stages, so answers are generated once and judged as often as needed:

```bash
docker compose up -d --build answer       # the answer service (needs qwen3:8b pulled into Ollama)
docker compose --profile eval run --rm answer-eval generate                  # all 45 questions
docker compose --profile eval run --rm answer-eval generate --ids q01,q39    # or --limit 5
docker compose --profile eval run --rm answer-eval generate --resume results/answers-<UTC>.json
docker compose --profile eval run --rm answer-eval judge results/answers-<UTC>.json
docker compose --profile eval run --rm answer-eval judge results/answers-<UTC>.json --skip-faithfulness
docker compose --profile eval run --rm answer-eval calibrate
```

The tenant keys are exported exactly as for the retrieval eval and are never printed or stored.

**generate** asks `POST /api/v1/answer` every question and saves `results/answers-<UTC>.json` (git-ignored): the
status, answer, citations, reason category, timings, the dataset's SHA-256 and, for every citation, the **text of the
cited chunk**, fetched with that tenant's own key. A citation to a document the tenant cannot see is a tenant
isolation failure (exit code 1). One failing question is recorded and the run goes on. The file is saved after every
question (a crash keeps what was collected, marked `"complete": false`), and `--resume <file>` asks only the missing or
failed questions again and updates the same file; answers are kept only while the question text is unchanged. Long runs
on a laptop do fail (a model server killed for lack of memory, a Docker restart), which is why this exists.

**judge** reads a saved file and never calls the answer service:

- **Fact check** (deterministic): each question's `answer_facts` (figures, names, identifiers) must appear in the
  answer, ignoring case, whitespace and number formatting (`$50` = `$50.00` = `$50.0`). Reported per question and overall:
  over answered questions, and end to end where a refusal scores 0.
- **Abstention table**: answered / false refusal (answerable, but `insufficient_context`) / **false answer**
  (unanswerable, but answered) / correct abstention. False answers are listed by id first and last, and make the exit
  code 1.
- **Faithfulness** (DeepEval `FaithfulnessMetric`): the judge extracts the claims in the answer and checks each against
  **only the chunks the answer cited**. Reported: the score (supported claims / all claims) and the unsupported claims
  with the judge's reason. `--skip-faithfulness` skips it for a fast deterministic pass.

The judged results are saved next to the answers file as `answers-<UTC>.judged.json`.

**compare** reads two or more judged files (one per `ANSWER_FLOW`, same dataset) and prints them side by side: false
answers and refusals, fact recall, median and p90 latency, model calls per question, the questions whose outcome changed
and first-attempt parity (every question the first run answered must be answered identically). It refuses runs on
different datasets or a file that mixes flows, and exits 1 on any false answer or isolation failure:

```bash
docker compose --profile eval run --rm answer-eval compare results/answers-<A>.judged.json results/answers-<B>.judged.json
```

**calibrate** runs the faithfulness judge on `judge_calibration.jsonl` (hand-labelled answers, including a wrong number,
an added claim and a claim from an uncited chunk) and reports its agreement with the labels. Run it before trusting the
faithfulness numbers.

### The judge model

Default: local Ollama `qwen3:8b`, temperature 0. That is the **same model that writes the answers**, so it may be lenient
about its own style (self-preference bias); calibration shows how much to trust it. A hosted judge needs the settings
below. The key is only ever read from `JUDGE_API_KEY`, and the cited chunks and answers are then sent to that provider.

| Variable | Default | Meaning |
|---|---|---|
| `JUDGE_PROVIDER` | `ollama` | `ollama` or `openai` (any OpenAI-compatible endpoint) |
| `JUDGE_MODEL` | `qwen3:8b` | model name |
| `JUDGE_BASE_URL` | `http://host.docker.internal:11434` | required for `openai`, e.g. `https://api.openai.com/v1` |
| `JUDGE_API_KEY` | — | required for `openai`; environment only, never printed, logged or saved |
| `JUDGE_TIMEOUT_S` | `900` | per judge call |

DeepEval is configured to stay local: telemetry is off (`DEEPEVAL_TELEMETRY_OPT_OUT=1`), it is told not to load `.env`
files (`DEEPEVAL_DISABLE_DOTENV=1`, it does by default) and not to write to disk (`DEEPEVAL_FILE_SYSTEM=READ_ONLY`), all
set in `evals/Dockerfile` and again in code before it is imported. We call `metric.measure()` directly and never
DeepEval's `evaluate()`, login or dashboard, so nothing is uploaded. Tests refuse any network access and assert none is
attempted.

## Tests and dependencies

```bash
docker compose --profile eval run --build --rm retrieval-eval-tests
```

Unit tests cover parsing, metrics (including `acceptable`), dataset validation, key handling, the mode sent to the
API, the comparison report and the isolation check, plus fact matching, the abstention table, stage 1 and stage 2 (with
a fake judge: only cited chunks reach it), the judge adapters and calibration. No network: a fixture blocks sockets and
records any attempt. DeepEval's pytest plugin is switched off in `pyproject.toml`.

Python 3.14 and every package are pinned (`uv.lock`); `exclude-newer` in `pyproject.toml` makes uv ignore anything
published after a cutoff date, so the two-week rule also covers transitive dependencies. **The cutoff ages**: set
it to about *now minus 14 days* (moving it forward only makes newer releases eligible; it installs nothing by itself).
If it lags behind, Dependabot's `uv` job fails with "no version of X" for any release that is older than its 14-day
cooldown but newer than the cutoff, as happened with fastapi 0.162.2. To update: move that date,
run `uv lock` (in the image: `docker build --target tools -t freightrag-evals-tools evals` then
`docker run --rm -v "$PWD/evals":/evals -w /evals freightrag-evals-tools uv lock`), and commit `uv.lock`.
