# HTTP API

The API is a thin adapter over the same use case as the console ingest. The full, interactive reference
is on the [REST API](../rest/index.html) page (generated from the OpenAPI document that the tests keep in
sync with the code). A running API also serves it at `http://localhost:8080/scalar/v1`.

## Authentication

Send your tenant's key in the `X-Api-Key` header. The key decides the tenant — there is no tenant in any
URL. Keys are stored in User Secrets (`ApiKeys:<tenant-slug>`), at least 32 characters, and compared as
SHA-256 hashes in constant time. Missing or wrong key → `401`.

## Endpoints

| Method & path | Success | Errors |
|---|---|---|
| `POST /api/v1/documents` (multipart field `file`) | `201` stored · `200` skipped (same bytes already stored) | `400` bad file name / not .txt · `413` > 10 MB · `415` not multipart · `422` unknown type, empty, not UTF-8, contains NUL · `429` rate limit |
| `GET /api/v1/documents?page=&pageSize=` | `200` page of your documents, newest first | `400` invalid paging |
| `GET /api/v1/documents/{id}` | `200` document with its chunks | `404` missing **or another tenant's** |
| `GET /health/live`, `GET /health/ready` | `200` | `503` when Postgres or Ollama is down (ready) |

Errors are [RFC 9457 problem details](https://www.rfc-editor.org/rfc/rfc9457) (`application/problem+json`).
Uploads are limited per tenant (default 30 per minute, `RateLimiting:IngestPermitsPerMinute`), because
each one calls the embedding model. A `429` carries a `Retry-After` header (seconds) telling you when to retry.

## Examples

```bash
KEY='<your tenant key>'
curl -H "X-Api-Key: $KEY" -F "file=@data/tenant-nile-freight/invoice-NFL-2026-1187.txt;type=text/plain" \
     http://localhost:8080/api/v1/documents
curl -H "X-Api-Key: $KEY" "http://localhost:8080/api/v1/documents?page=1&pageSize=10"
curl -H "X-Api-Key: $KEY" http://localhost:8080/api/v1/documents/<id>
```

## Production notes

This is a learning project. Before exposing the API beyond `localhost`: serve it over HTTPS, replace
static API keys with a real identity provider, and set `OpenApi:Enabled=false` if you don't want the
API description and UI public.
