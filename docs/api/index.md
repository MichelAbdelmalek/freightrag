# C# reference

Generated from the XML documentation comments in the source.

- **`FreightRag.Core.Domain`** — the business model: `FreightDocument` (aggregate root), `DocumentChunk`,
  value objects (`TenantId`, `SourceHash`, `DocType`, `DocumentIdentifiers`, `EmbeddingVector`) and the
  `SectionChunker` domain service. Pure C#, no I/O.
- **`FreightRag.Core.Application`** — the `IngestDocumentHandler` use case, the ports it depends on
  (`IDocumentRepository`, `IEmbeddingService`, `ITenantDirectory`, `IDocumentQueries`) and read models.
- **`FreightRag.Core.Infrastructure`** — adapters: Postgres/pgvector, Ollama embeddings, configuration.
- **`FreightRag.Api`** — the HTTP adapter: endpoints, API-key authentication, OpenAPI transformers.
