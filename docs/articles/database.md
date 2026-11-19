# Database and migrations (code-first)

The schema is defined **in code**: the EF Core model (`FreightRagDbContext`) and its migrations in
`src/FreightRag.Core/Infrastructure/Persistence/Migrations` are the source of truth. Data access uses EF Core too.

| Migration | What it does |
|---|---|
| `InitialSchema` | `documents`, `chunks`, constraints, the HNSW index — identical to the original `db/001_schema.sql` (an integration test compares the two schemas) |
| `AddEmbeddingModel` | adds `chunks.embedding_model`: nullable → backfill with the configured model → `NOT NULL` |

## Applying migrations

Migrations are an explicit step, never run at application startup:

```bash
docker compose run --build --rm migrate
```

The migrator has its own `appsettings.json` (and reads the same User Secrets as the other hosts). Its
`Ollama:EmbeddingModel` must match the ingest and API hosts — a unit test checks that the three files agree on the
`Ollama`, `Database` and `Tenants` sections. Before migrating it stores the configured embedding model in a session
setting with a parameterized
`set_config('freightrag.embedding_model', @model, false)`; the `AddEmbeddingModel` backfill reads it. If rows need a
value and the setting is missing, the migration **fails** instead of guessing.

### Databases created before EF Core

A database created by `db/001_schema.sql` has the tables but no (or an empty) `__EFMigrationsHistory`. The migrator
detects that, records `InitialSchema` as already applied ("baseline"), then applies the rest. The baseline runs in one
transaction under an advisory lock, so two migrators at once, or an interrupted run, can't leave a half-adopted database.

## Adding a migration

Change the model, then generate the migration in the SDK container (no local SDK needed):

```bash
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -v "$PWD":/repo -w /repo mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e sh -c 'dotnet tool restore && dotnet ef migrations add <Name> --project src/FreightRag.Core --startup-project src/FreightRag.Migrator --output-dir Infrastructure/Persistence/Migrations --namespace FreightRag.Core.Infrastructure.Persistence.Migrations'
```

Then regenerate the snapshots (`UPDATE_DB_SCRIPTS=1`, README) to get the new `db/NNN_*.sql`.

Review the generated C#: for anything that touches existing rows (like a new `NOT NULL` column), hand-edit it the way
`AddEmbeddingModel` does. A unit test fails if the model changed without a migration.

## Generated SQL scripts in `db/`

Every migration after `InitialSchema` has a **generated** idempotent script, `db/NNN_snake_name.sql` (NNN = its
position; `db/001_schema.sql` is the original hand-written schema). The scripts start with the same "adopt a pre-EF
database" SQL the migrator uses, so a DBA can apply them by hand to either kind of database (after setting
`freightrag.embedding_model` for the backfill). A unit test fails if a script is missing, extra or out of date
(ignoring the EF version stamp, so EF patch updates don't break CI); regenerate with `UPDATE_DB_SCRIPTS=1` (README).
