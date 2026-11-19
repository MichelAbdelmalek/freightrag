# FreightRAG

A learning project: a **multi-tenant RAG** (retrieval-augmented generation) system for logistics
documents. This first slice is the **ingestion pipeline**: it splits documents into chunks, embeds
them with a local model, and stores them in Postgres + pgvector so they can be searched by meaning later.

```
data/<tenant>/*.txt ──► chunk by section ──► embed (Ollama, nomic-embed-text, 768 dims) ──► Postgres + pgvector
```

## Prerequisites

Everything runs in Docker — **no .NET SDK is needed on your machine**.

- Docker Desktop (or Docker Engine with Compose v2)
- Postgres 17 + pgvector on `localhost:55432` (container `freightrag-db`), database `freightrag`,
  user `freightrag`. The app never creates or migrates tables; on a **fresh** database apply the schema once:
  ```bash
  docker exec -i freightrag-db psql -U freightrag -d freightrag < db/001_schema.sql
  ```
- Ollama on `localhost:11434` (container `freightrag-ollama`) with the embedding model pulled:
  ```bash
  docker exec freightrag-ollama ollama pull nomic-embed-text
  ```

The app container reaches both through `host.docker.internal` (see `src/FreightRag.Ingest/appsettings.json`).

## 1. Store the database password (User Secrets)

The password is read **only** from .NET User Secrets (key `Database:Password`). It is never in
`appsettings.json`, environment variables, command-line arguments or the image. The `user-secrets`
compose service runs the `dotnet user-secrets` tool from the SDK image; only this project's secrets
folder is mounted into it.

Create this project's secrets folder (private to you) so Docker doesn't create it:

```bash
mkdir -p -m 700 ~/.microsoft/usersecrets/bc38a00e-bd57-4c78-9333-1f52ff1438ed
```

Type the password without echo (it is not saved in shell history):

```bash
read -rs PW
```

Send it on **stdin** as JSON, so it never appears in `ps` or `docker inspect` (if the password contains
`"` or `\`, escape them as `\"` and `\\`):

```bash
printf '{"Database:Password":"%s"}' "$PW" | docker compose --profile tools run --rm -T user-secrets set && unset PW
```

The same command with a placeholder password, to see the shape:

```bash
printf '{"Database:Password":"%s"}' 'change-me' | docker compose --profile tools run --rm -T user-secrets set
```

With a local .NET 10 SDK the equivalent is
`dotnet user-secrets set "Database:Password" "change-me" --project src/FreightRag.Ingest`.

## 2. Run the tests

```bash
docker compose run --build --rm tests
```

Unit tests only — no database or Ollama needed. They use the real files in `data/`.

## 3. Ingest the documents

```bash
docker compose run --build --rm ingest
```

`data/` is mounted read-only at `/data`. Running it a second time skips every file (same tenant +
same SHA-256 = already ingested). Outside Docker (with a local SDK and `localhost` in appsettings) the
equivalent is `dotnet run --project src/FreightRag.Ingest -- --data-dir data`.

Exit codes: `0` success · `1` some files failed (or an unexpected error) · `2` bad arguments/configuration · `130` cancelled (Ctrl+C).

## How it works

### Solution layout (DDD inside three projects)

| Project | Role |
|---|---|
| `FreightRag.Core` | `Domain/` pure business model · `Application/` use case + ports (interfaces) · `Infrastructure/` Postgres and Ollama adapters |
| `FreightRag.Ingest` | Console app: composition root, configuration, folder walking, summary |
| `FreightRag.Tests` | xUnit v3 tests, written test-first; `ArchitectureTests` enforce Domain → nothing, Application → Domain only |

Domain model: `FreightDocument` (aggregate root) owns its `DocumentChunk`s; value objects `TenantId`,
`SourceHash`, `DocType`, `DocumentIdentifiers`, `EmbeddingVector`; domain service `SectionChunker`.
The use case `IngestDocumentHandler` talks to the outside world only through
`IDocumentRepository`, `IEmbeddingService` and `ITenantDirectory`.

### Chunking

- A **section** starts at an ALL-CAPS line (`RATE`, `PICKUP`) or a numbered caps heading
  (`3. DETENTION REPORTING`). A heading must be ≤ 80 chars, mostly letters and not end with `.`, so a
  code such as `SAV-778431` or a shouted sentence is not a heading.
- **No text is ever dropped.** In a run of consecutive ALL-CAPS lines only the first is the heading and
  the rest are body (so `SHIPPER` / `BAYOU FROZEN FOODS` / `1500 CLINTON DR` stays together), and a
  heading with no body (a document title) becomes a chunk containing its own text. A test checks that
  every line of every real document ends up in some chunk.
- A section body over ~1,000 characters is split on paragraph (blank-line) boundaries, then on line
  boundaries. A line is never cut.
- Every chunk starts with a **context header**, e.g.
  `Document: rate-confirmation | Load: 48213 | Section: RATE`.
  Embedded together with the text, it lets a question like *"detention for load 48213"* find the right
  chunk even though the RATE section itself never mentions the load number.

### Embedding

`nomic-embed-text` expects a task prefix: chunks are sent as `search_document: <content>` (queries will
use `search_query: `). The prefix is not stored. All chunks of a document go in one batch call, and any
vector that is not exactly 768-dimensional stops that file with a clear error.

### Storage and idempotency

`source_hash` is the SHA-256 of the file's raw bytes. If `(tenant_id, source_hash)` exists the file is
skipped before calling the model. Otherwise the document row and all chunk rows are inserted in **one
transaction** (`ON CONFLICT DO NOTHING` also covers two runs racing). All SQL is parameterized.

## Security notes

- **Tenant isolation is enforced by application code** (`tenant_id` on every row). The future query
  side must always filter by `tenant_id`; consider Postgres Row-Level Security as a second line of defence.
- User Secrets are **plain-text JSON** in `~/.microsoft/usersecrets/` — fine for local development, not
  for production (use a secret manager / vault there).
- Logs show `host:port/db as user`, never the password or connection string.
- `.env` is git-ignored **and** docker-ignored, so it can never end up in an image layer.
- The ingest container runs as the non-root `app` user, with data and **only this project's** secrets
  folder mounted read-only.
- Symbolic links inside `data/` are rejected (a link could smuggle another tenant's file in under the
  wrong `tenant_id`), and files are read with a hard 10 MB limit.
- Traffic to Postgres/Ollama is unencrypted, acceptable only because both are bound to `127.0.0.1`.
  For anything non-local, require TLS (`SSL Mode=VerifyFull`) and put Ollama behind authentication.

## Pinned versions

Images: `mcr.microsoft.com/dotnet/sdk:10.0.403-noble`, `mcr.microsoft.com/dotnet/runtime:10.0.14-noble`
(pinned by digest in `Dockerfile`/`compose.yaml`). Packages: `src/Directory.Packages.props`.
Rule: stable releases only, published at least two weeks before adoption.
