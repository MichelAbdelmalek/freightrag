"""Shared fixtures for the answer-evaluation tests."""

import json

import httpx

from freightrag_eval.judge import FaithfulnessResult, Unsupported

NILE_KEY = "nile-key-not-a-secret-0123456789abcdefgh"
DELTA_KEY = "delta-key-not-a-secret-0123456789abcdefg"
ENV = {"FREIGHTRAG_API_KEY_TENANT_NILE_FREIGHT": NILE_KEY, "FREIGHTRAG_API_KEY_TENANT_DELTA_HAUL": DELTA_KEY}

ROWS = [
    {"id": "q1", "tenant": "tenant-nile-freight", "question": "detention rate?", "kind": "answerable", "note": "",
     "expected": [{"document_title": "rate-48213", "section": "RATE"}], "answer_facts": ["$50", "2 free hours"]},
    {"id": "q2", "tenant": "tenant-delta-haul", "question": "reefer temperature?", "kind": "answerable", "note": "",
     "expected": [{"document_title": "rate-77104", "section": "FREIGHT"}], "answer_facts": ["-10"]},
    {"id": "q3", "tenant": "tenant-nile-freight", "question": "fuel card policy?", "kind": "unanswerable", "note": "",
     "expected": []},
]

# Which documents each tenant owns, and their chunks (by index).
DOCS = {NILE_KEY: {"doc-n": {0: "CHUNK-N0 header\nDetention: $50.00 per hour", 1: "CHUNK-N1 unrelated"}},
        DELTA_KEY: {"doc-d": {0: "CHUNK-D0 header\nTemperature: -10 degrees F"}}}


def dataset(tmp_path, rows=ROWS):
    path = tmp_path / "set.jsonl"
    path.write_text("\n".join(json.dumps(r) for r in rows) + "\n", encoding="utf-8")
    return path


def citation(doc, index=0, source=1, title="t", section="S"):
    return {"source": source, "documentId": doc, "title": title, "section": section, "chunkIndex": index}


def answered(text, *citations, reason=None):
    return {"status": "answered", "answer": text, "citations": list(citations),
            "diagnostics": {"reason": reason, "chunkCount": 5, "searchMs": 40, "llmMs": 900, "totalMs": 950}}


def abstained(reason="identifier_not_found"):
    return {"status": "insufficient_context", "answer": None, "citations": [],
            "diagnostics": {"reason": reason, "chunkCount": 5, "searchMs": 40, "llmMs": 0, "totalMs": 40}}


def services(replies, requests=None, answer_status=None, ready=True):
    """One transport that plays both the .NET API and the answer service."""
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/health/ready":
            return httpx.Response(200 if ready else 503)
        key = request.headers["X-Api-Key"]
        if request.url.path == "/api/v1/answer":
            question = json.loads(request.content)["question"]
            if requests is not None:
                requests.append(("answer", key, question))
            if answer_status and question in answer_status:
                return httpx.Response(answer_status[question])
            return httpx.Response(200, json=replies[question])
        if request.url.path == "/api/v1/documents":
            return httpx.Response(200, json={"items": [{"id": d} for d in DOCS[key]], "page": 1, "pageSize": 100,
                                             "totalCount": len(DOCS[key]), "totalPages": 1})
        doc_id = request.url.path.rsplit("/", 1)[1]
        if requests is not None:
            requests.append(("doc", key, doc_id))
        chunks = DOCS[key].get(doc_id)
        if chunks is None:
            return httpx.Response(404)
        return httpx.Response(200, json={"id": doc_id, "chunks": [{"index": i, "content": c} for i, c in chunks.items()]})
    return httpx.MockTransport(handler)


class FakeJudge:
    """Records exactly what it was asked to judge; scores from a canned table keyed by answer text."""

    def __init__(self, results=None):
        self.calls: list[tuple[str, str, list[str]]] = []
        self.results = results or {}
        self.closed = False

    def faithfulness(self, question, answer, contexts):
        self.calls.append((question, answer, list(contexts)))
        return self.results.get(answer, FaithfulnessResult(1.0, 1, ()))

    def close(self):
        self.closed = True


def unfaithful(claim="made-up claim", reason="not in the chunk"):
    return FaithfulnessResult(0.5, 2, (Unsupported(claim, reason),))
