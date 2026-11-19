---
_layout: landing
---

# FreightRAG

A learning project: a **multi-tenant RAG** (retrieval-augmented generation) system for logistics
documents — rate confirmations, bills of lading, invoices and driver handbooks — built with .NET 10,
Postgres + pgvector and a local embedding model (Ollama, `nomic-embed-text`).

```
document ──► chunk by section ──► embed (768 dims) ──► Postgres + pgvector ──► (next) retrieve + answer
```

## Where to start

| If you want to… | Read |
|---|---|
| Run it (everything is in Docker) | [Getting started](articles/getting-started.md) |
| Understand the DDD layers | [Architecture](articles/architecture.md) |
| Understand chunking and context headers (the RAG part) | [Chunking](articles/chunking.md) |
| Call the HTTP API | [HTTP API guide](articles/http-api.md) and the interactive [REST API reference](rest/index.html) |
| Browse the C# types | [C# reference](api/index.md) |
