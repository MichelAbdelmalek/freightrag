# FreightRAG answer service

The "G" in RAG: takes a question, retrieves chunks from the .NET search API (hybrid mode, top 5) and asks a local model
to answer **only from those chunks, citing them**, or to abstain.

```
POST /api/v1/answer   X-Api-Key: <tenant key>   { "question": "…" }
→ { "status": "answered" | "insufficient_context", "answer": "… [1]" | null, "citations": [ … ] }
```

The pipeline is five small pure steps (`search`, `guard`, `build_prompt`, `generate`, `validate`) wired in
`freightrag_answer/pipeline.py`. Why each exists, what is checked in code and what is not:
[`docs/articles/answer-service.md`](../docs/articles/answer-service.md).

## Run it

Needs the API running and the model pulled into your Ollama container (one time, about 5 GB):

```bash
docker exec freightrag-ollama ollama pull qwen3:8b
docker compose up -d --build answer      # http://127.0.0.1:8090
```

The service forwards the caller's API key to the search API and never stores or logs it; no secrets are mounted into
its container. Get a key the way `evals/README.md` does, and send it as `X-Api-Key`.

| Variable | Default | Meaning |
|---|---|---|
| `SEARCH_API_URL` | `http://api:8080` | the .NET API |
| `OLLAMA_URL` | `http://host.docker.internal:11434` | Ollama |
| `CHAT_MODEL` | `qwen3:8b` | any Ollama chat model that supports JSON-schema output |
| `NUM_CTX` | `4096` | context window (prompt + answer); a prompt that cannot fit is refused, not truncated |
| `NUM_PREDICT` | `512` | most tokens in an answer; must be smaller than `NUM_CTX` |
| `TOP_K` | `5` | chunks retrieved (1-20) |
| `SEARCH_TIMEOUT_S`, `LLM_TIMEOUT_S`, `CONNECT_TIMEOUT_S` | `30`, `180`, `5` | HTTP timeouts, in seconds |

`CHAT_MODEL` and `NUM_CTX` can be overridden from your shell: `CHAT_MODEL=qwen3:4b docker compose up -d answer`.
Bad values fail at startup and name the variable.

## Tests and dependencies

```bash
docker compose --profile answer run --build --rm answer-tests
```

The tests use a fake search client and a fake model: no network, no Ollama. They cover the guard (including the
load-48999 case), prompt building (tag-like text in a chunk stays inside its source), citation validation, the context
check, key forwarding, and that neither the key, the question, the answer nor chunk content is ever logged.

Python 3.14 and every package are pinned (`uv.lock`); `exclude-newer` in `pyproject.toml` makes uv ignore anything
published after a cutoff date, so the two-week rule also covers transitive dependencies. To update: move that date,
run `uv lock` in the image (`docker build --target tools -t freightrag-answer-tools answer` then
`docker run --rm -v "$PWD/answer":/answer -w /answer freightrag-answer-tools uv lock`), and commit `uv.lock`.
