# Getting started

Everything runs in Docker — no .NET SDK is needed on your machine. Postgres (with pgvector) and Ollama
run in their own containers, published on `127.0.0.1:55432` and `127.0.0.1:11434`.

## 1. One-time setup

Pull the embedding model:

```bash
docker exec freightrag-ollama ollama pull nomic-embed-text
```

The schema is created and upgraded by the EF Core migrations (after storing the password, step 2):

```bash
docker compose run --build --rm migrate
```

## 2. Secrets (User Secrets, never in files you commit)

Secrets live only in .NET User Secrets. They are sent to the `user-secrets` tool on **stdin**, so they
never appear in `ps`, `docker inspect` or your shell history.

```bash
mkdir -p -m 700 ~/.microsoft/usersecrets/bc38a00e-bd57-4c78-9333-1f52ff1438ed
read -rs PW && printf '{"Database:Password":"%s"}' "$PW" | docker compose --profile tools run --rm -T user-secrets set && unset PW
```

For the HTTP API, add one key per tenant (store the printed keys in your password manager):

```bash
for t in tenant-nile-freight tenant-delta-haul; do
  KEY="$(openssl rand -base64 32)"
  printf '{"ApiKeys:%s":"%s"}' "$t" "$KEY" | docker compose --profile tools run --rm -T user-secrets set
  echo "$t: $KEY"
done
```

## Test keys (for trying the API)

This is a learning project with synthetic freight documents, so its two test API keys are published here on purpose.
**Do not reuse them anywhere real**: anyone who can read this page can use them, and they stay in the git history.

| Tenant | `X-Api-Key` |
|---|---|
| `tenant-nile-freight` | `5MFVYAQlQzZn0FmFT7GmKIymfFewhbk1n8l/RrTRx44=` |
| `tenant-delta-haul` | `6CWnwl4+5fJnlxQbKbXE/eOSDGCXmarFjDNoiTlPulQ=` |

In the interactive docs at <http://localhost:8080/scalar/v1> the Nile Freight key is already filled in under
**Authentication**. To act as Delta Haul, paste the other key there. A search or answer with one tenant's key never returns
the other tenant's documents.

## 3. Run

| Command | What it does |
|---|---|
| `docker compose run --build --rm migrate` | Applies the EF Core migrations (safe to repeat) |
| `docker compose run --build --rm tests` | Unit and API tests (no database or Ollama needed) |
| `docker compose run --build --rm integration-tests` | Integration tests against a throwaway pgvector database |
| `docker compose run --build --rm ingest` | Ingests every `data/<tenant>/*.txt` file; safe to repeat |
| `docker compose up --build api` | HTTP API on <http://localhost:8080> — interactive docs at <http://localhost:8080/scalar/v1> |
| `docker compose up --build docs` | This documentation site on <http://localhost:8081> |
