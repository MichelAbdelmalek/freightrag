"""Stage 1: ask the answer service every question and save what it said, with the chunks it cited.

The judge (stage 2) reads this file and never calls a service, so judging can be repeated, changed or calibrated
without regenerating, and every judgement traces to a saved answer. The cited chunk text is fetched here, from the
API with the tenant's own key, because "the chunks the answer cited" is exactly what faithfulness is judged against.
"""

import json
import time
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

import httpx

from freightrag_eval.client import ApiError, SearchClient
from freightrag_eval.dataset import Question


class AnswerClient:
    """Calls POST /api/v1/answer. A failure of one question is a result, not an exception: the run goes on."""

    def __init__(self, base_url: str, transport: httpx.BaseTransport | None = None, timeout: float = 900.0):
        # Long timeout: a local model on a CPU can need minutes for one answer.
        self._http = httpx.Client(base_url=base_url, transport=transport, timeout=timeout)

    def close(self) -> None:
        self._http.close()

    def ready(self) -> bool:
        try:
            return self._http.get("/health/ready", timeout=5.0).status_code == 200
        except httpx.TransportError:
            return False

    def answer(self, key: str, question: str) -> dict:
        started = time.perf_counter()
        try:
            response = self._http.post("/api/v1/answer", headers={"X-Api-Key": key}, json={"question": question})
        except httpx.TransportError as error:
            return {"status": "error", "error": type(error).__name__, "client_ms": _ms(started)}
        if response.status_code != 200:
            return {"status": "error", "http_status": response.status_code, "client_ms": _ms(started)}
        try:
            body = response.json()
            diagnostics = body.get("diagnostics") or {}
            return {
                "status": body["status"], "answer": body["answer"], "client_ms": _ms(started),
                "citations": [{"source": c["source"], "documentId": c["documentId"], "title": c["title"],
                               "section": c["section"], "chunkIndex": c["chunkIndex"]} for c in body["citations"]],
                "reason": diagnostics.get("reason"), "chunkCount": diagnostics.get("chunkCount"),
                "searchMs": diagnostics.get("searchMs"), "llmMs": diagnostics.get("llmMs"),
                "totalMs": diagnostics.get("totalMs"),
                # retry-flow fields: absent (None) when the service runs the straight flow of an older version
                "flow": diagnostics.get("flow"), "attempts": diagnostics.get("attempts"),
                "firstReason": diagnostics.get("firstReason"), "rewriteStatus": diagnostics.get("rewriteStatus"),
                "rewriteMs": diagnostics.get("rewriteMs"), "modelCalls": diagnostics.get("modelCalls"),
            }
        except (ValueError, KeyError, TypeError):
            return {"status": "error", "error": "unreadable_response", "client_ms": _ms(started)}


def _ms(started: float) -> int:
    return int((time.perf_counter() - started) * 1000)


@dataclass
class Generation:
    records: list[dict]
    isolation_failures: list[str]


def select(questions: list[Question], ids: list[str] | None, limit: int | None) -> list[Question]:
    if ids:
        known = {q.id for q in questions}
        unknown = [i for i in ids if i not in known]
        if unknown:
            raise ValueError(f"unknown question id(s): {', '.join(unknown)}")
        questions = [q for q in questions if q.id in set(ids)]
    return questions[:limit] if limit is not None else questions


def generate(questions: list[Question], keys: dict[str, str], answers: AnswerClient, api: SearchClient,
             progress=None, previous: Generation | None = None, checkpoint=None) -> Generation:
    """Asks every question that has no good answer yet. With `previous` (a resumed run) a question is kept as it is
    when it was answered or abstained and its text is unchanged; missing, failed or edited questions are asked again.
    `checkpoint(records, failures)` is called after every question so a crash keeps what was already collected."""
    by_id = {r["id"]: r for r in (previous.records if previous else [])}
    failures = list(previous.isolation_failures) if previous else []

    def kept(q: Question) -> bool:
        old = by_id.get(q.id)
        return bool(old and old["status"] != "error" and old["question"] == q.question and old["tenant"] == q.tenant)

    todo = [q for q in questions if not kept(q)]
    own = {tenant: api.document_ids(keys[tenant]) for tenant in {q.tenant for q in todo}}
    chunk_cache: dict[tuple[str, str], dict[int, str] | None] = {}

    for q in todo:
        failures = [f for f in failures if not f.startswith(f"{q.id}:")]    # this question is being asked afresh
        record = {"id": q.id, "tenant": q.tenant, "kind": q.kind, "question": q.question,
                  **answers.answer(keys[q.tenant], q.question)}
        for citation in record.get("citations", []):
            doc = citation["documentId"]
            if doc not in own[q.tenant]:      # the service cited a document this tenant cannot see: never fetch it
                failures.append(f"{q.id}: {q.tenant} was given a citation to document {doc} ({citation['title']})")
                citation["chunk_text"] = None
                continue
            if (q.tenant, doc) not in chunk_cache:
                chunk_cache[(q.tenant, doc)] = api.document_chunks(keys[q.tenant], doc)
            chunks = chunk_cache[(q.tenant, doc)]
            citation["chunk_text"] = None if chunks is None else chunks.get(citation["chunkIndex"])
        by_id[q.id] = record                  # replaces an earlier failed record in place, or appends
        if progress:
            progress(record)
        if checkpoint:
            checkpoint(list(by_id.values()), list(failures))
    return Generation(list(by_id.values()), failures)


def new_path(directory: Path) -> Path:
    directory.mkdir(parents=True, exist_ok=True)
    return directory / f"answers-{datetime.now(UTC).strftime('%Y%m%dT%H%M%SZ')}.json"


def load_previous(path: Path) -> Generation:
    payload = json.loads(path.read_text(encoding="utf-8"))
    return Generation(payload["questions"], payload.get("isolation_failures", []))


def save(path: Path, *, dataset_sha256: str, answer_url: str, api_url: str, records: list[dict],
         failures: list[str], complete: bool) -> None:
    """results/answers-<UTC>.json. Keys are never written; neither is anything but the answers and cited chunks.
    Written to a temporary file and renamed, so a crash mid-write cannot leave a corrupt file."""
    payload = {"run_at": datetime.now(UTC).isoformat(timespec="seconds"), "complete": complete,
               "dataset_sha256": dataset_sha256, "answer_url": answer_url, "api_url": api_url,
               "isolation_failures": failures, "questions": records}
    temporary = path.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    temporary.replace(path)


__all__ = ["AnswerClient", "ApiError", "Generation", "generate", "load_previous", "new_path", "save", "select"]
