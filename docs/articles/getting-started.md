# Getting started

Everything runs in Docker — no .NET SDK is needed on your machine. Postgres (with pgvector) and Ollama
run in their own containers, published on `127.0.0.1:55432` and `127.0.0.1:11434`.

## 1. One-time setup

Apply the schema to a fresh database and pull the embedding model:

```bash
docker exec -i freightrag-db psql -U freightrag -d freightrag < db/001_schema.sql
docker exec freightrag-ollama ollama pull nomic-embed-text
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

## 3. Run

| Command | What it does |
|---|---|
| `docker compose run --build --rm tests` | Unit and API tests (no database or Ollama needed) |
| `docker compose run --build --rm ingest` | Ingests every `data/<tenant>/*.txt` file; safe to repeat |
| `docker compose up --build api` | HTTP API on <http://localhost:8080> — interactive docs at <http://localhost:8080/scalar/v1> |
| `docker compose up --build docs` | This documentation site on <http://localhost:8081> |
