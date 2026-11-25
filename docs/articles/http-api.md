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
| `POST /api/v1/documents` (multipart field `file`) | `201` stored · `200` skipped (same bytes already stored) | `400` bad file name / not .txt or .pdf · `413` > 10 MB · `415` not multipart · `422` unknown type, empty, not UTF-8, contains NUL, or a PDF problem (`not_pdf`, `corrupt_pdf`, `encrypted_pdf`, `too_many_pages`, `page_too_large`, `no_text`, `unreadable`) · `429` rate limit · `503` PDF extractor busy or down |
| `GET /api/v1/documents?page=&pageSize=` | `200` page of your documents, newest first | `400` invalid paging |
| `GET /api/v1/documents/{id}` | `200` document with its chunks | `404` missing **or another tenant's** |
| `POST /api/v1/search` `{ "query", "topK", "mode" }` | `200` chunks ordered by `fusedScore`, each with cosine `distance`, `vectorRank`, `keywordRank` | `400` empty / > 1000 chars / topK not 1–20 / mode not `vector`, `keyword` or `hybrid` · `429` rate limit |
| `GET /health/live`, `GET /health/ready` | `200` | `503` when Postgres or Ollama is down (ready) |

Errors are [RFC 9457 problem details](https://www.rfc-editor.org/rfc/rfc9457) (`application/problem+json`); upload errors also carry a
machine-readable `code`. PDFs (scanned or not) are read by a separate extractor: see [PDF ingestion](pdf-ingestion.md).
Uploads are limited per tenant (default 30 per minute, `RateLimiting:IngestPermitsPerMinute`), because
each one calls the embedding model. Searches have their own budget (default 60 per minute, `RateLimiting:SearchPermitsPerMinute`): each one embeds the
question. A `429` carries a `Retry-After` header (seconds) telling you when to retry.

## Examples

```bash
KEY='<your tenant key>'
curl -H "X-Api-Key: $KEY" -F "file=@data/tenant-nile-freight/invoice-NFL-2026-1187.txt;type=text/plain" \
     http://localhost:8080/api/v1/documents
curl -H "X-Api-Key: $KEY" -F "file=@rate-confirmation-48213.pdf;type=application/pdf" \
     http://localhost:8080/api/v1/documents   # a scanned PDF is OCR'd; the response has an extraction report
curl -H "X-Api-Key: $KEY" "http://localhost:8080/api/v1/documents?page=1&pageSize=10"
curl -H "X-Api-Key: $KEY" http://localhost:8080/api/v1/documents/<id>
curl -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
     -d '{"query":"detention rate for load 48213","topK":3}' http://localhost:8080/api/v1/search
curl -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
     -d '{"query":"NFL-2026-1187","topK":3,"mode":"keyword"}' http://localhost:8080/api/v1/search
```

`mode` defaults to `hybrid` (vector and full-text search fused with Reciprocal Rank Fusion); `vector` and `keyword`
run one leg only — see [Hybrid search](hybrid-search.md). Search results are ranked chunks only (see
[Retrieval](retrieval.md)). Their `content` is tenant-supplied,
untrusted text: treat it as data, never as instructions.

## Production notes

This is a learning project. Before exposing the API beyond `localhost`: serve it over HTTPS, replace
static API keys with a real identity provider, and set `OpenApi:Enabled=false` if you don't want the
API description and UI public.
