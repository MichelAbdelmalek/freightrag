---
_layout: landing
---

# FreightRAG

A learning project: a **multi-tenant RAG** (retrieval-augmented generation) system for logistics
documents — rate confirmations, bills of lading, invoices and driver handbooks — built with .NET 10,
Postgres + pgvector and a local embedding model (Ollama, `nomic-embed-text`).

```
document ──► chunk by section ──► embed (768 dims) ──► Postgres + pgvector ──► search (ranked chunks) ──► answer (cited, or abstains)
```

## Where to start

| If you want to… | Read |
|---|---|
| Run it (everything is in Docker) | [Getting started](articles/getting-started.md) |
| Understand the DDD layers | [Architecture](articles/architecture.md) |
| Understand chunking and context headers (the RAG part) | [Chunking](articles/chunking.md) |
| Understand search: prefixes, top-k, overfiltering | [Retrieval](articles/retrieval.md) |
| Understand hybrid search: full-text, AND vs OR, RRF | [Hybrid search](articles/hybrid-search.md) |
| Understand answer generation: prompt, citations, guard, abstaining | [Answer service](articles/answer-service.md) |
| Change the schema (EF Core code-first) | [Database and migrations](articles/database.md) |
| Call the HTTP API | [HTTP API guide](articles/http-api.md) and the interactive [REST API reference](rest/index.html) |
| Browse the C# types | [C# reference](api/index.md) |
