# Architecture

FreightRAG uses **Domain-Driven Design** layering inside `FreightRag.Core`, with two thin hosts on top.

```
 FreightRag.Ingest (console)        FreightRag.Api (HTTP)
            │                                │
            └──────────────┬─────────────────┘
                           ▼
        Application: IngestDocumentHandler, IDocumentQueries   ← use cases + ports (interfaces)
                           ▼
        Domain: FreightDocument, SectionChunker, value objects ← business rules, pure C#
                           ▲
        Infrastructure: EF Core + pgvector, Ollama, configuration ← adapters implementing the ports
```

## Rules

- **Domain** depends on nothing: no Npgsql, no Ollama, no ASP.NET Core. It can't do I/O, so every rule
  in it is testable in microseconds.
- **Application** depends only on the Domain. It orchestrates: resolve tenant → hash → skip duplicates
  *before* the expensive embedding call → chunk → embed in one batch → store in one transaction.
- **Infrastructure** implements the ports. Swapping Ollama for another embedding provider means one new
  class implementing `IEmbeddingService`.
- **Hosts** (console, API) only wire things together and translate results (exit codes, HTTP status
  codes). Both call the *same* `IngestDocumentHandler`, so a file ingested by one is "already ingested"
  for the other.

`ArchitectureTests` enforce the first two rules on every build.

The database schema is code-first (EF Core migrations) — see [Database and migrations](database.md).
EF Core persistence records live in Infrastructure; the domain aggregate never sees them.

## Commands and queries

Writes go through the domain aggregate (`FreightDocument`), which guards invariants such as "every chunk
has a 768-dimension embedding before storing". Reads (`IDocumentQueries`) return flat read models
straight from SQL — no invariants to protect, and no need to load vectors to show a list.

## Multi-tenancy

Every row carries `tenant_id`, and every query takes a `TenantId`. In the HTTP API the tenant comes from
the API key, never from the URL, so one tenant can't even address another tenant's documents — a
foreign document id answers **404**, exactly like a missing one.

## PDF extraction (a separate, isolated service)

`extract/` is a small Python service that turns a PDF into text: it copies the text layer, or runs OCR (Tesseract) on scanned
pages. It is a separate container on purpose: PDFs are untrusted input and parsing them is native code, so it runs on an
internal Docker network with no internet, a read-only file system, a memory and CPU cap, and a worker process per PDF. The API
reaches it through the `IPdfTextExtractor` port (an adapter in Infrastructure), sends it only the bytes, and keeps the tenant
to itself. See [PDF ingestion](pdf-ingestion.md).

## Document fields (a pure domain service)

`DocumentFieldExtractor` (Domain) reads key/value fields by plain rules from the text of a document. The ingest use case calls it
on the same text it chunks; `GetDocumentHandler` calls it on the stored chunks put back together. Nothing is stored: the
result is returned and the rules can change without a migration. The catalog of fields per document type is
`DocumentFields`; `FieldSelection` is the validated "which fields" request. See [Document fields](document-fields.md).

