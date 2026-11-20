"""Console table and results file."""

import json
from dataclasses import asdict
from datetime import UTC, datetime
from pathlib import Path

from freightrag_eval.dataset import Question
from freightrag_eval.metrics import Hit, QuestionScore, Summary


def table(questions: list[Question], scores: list[QuestionScore], hits: dict[str, list[Hit]]) -> str:
    lines = [f"{'id':<5} {'kind':<13} {'rank':>5} {'top1 dist':>9}  top-1 result", "-" * 100]
    for q, s in zip(questions, scores, strict=True):
        top = hits[q.id][0] if hits[q.id] else None
        rank = "n/a" if q.kind == "unanswerable" else (str(s.rank) if s.rank else "miss")
        extra = f"  ({s.found}/{s.total} expected)" if s.total > 1 else ""
        top_text = f"{top.title} › {top.section}" if top else "—"
        dist = f"{s.top1_distance:.4f}" if s.top1_distance is not None else "—"
        lines.append(f"{q.id:<5} {q.kind:<13} {rank:>5} {dist:>9}  {top_text}{extra}")
    return "\n".join(lines)


def summary_text(summary: Summary, top_k: int) -> str:
    lines = [
        f"answerable questions: {summary.answerable}   unanswerable: {summary.unanswerable}",
        f"recall@1 = {summary.recall_at_1:.3f}   recall@{top_k} = {summary.recall_at_k:.3f}   MRR = {summary.mrr:.3f}",
    ]
    for qid, (found, total) in summary.multi_chunk_coverage.items():
        lines.append(f"multi-chunk {qid}: {found}/{total} expected chunks in the top {top_k}")
    return "\n".join(lines)


def write_results(directory: Path, *, dataset_sha256: str, api_url: str, top_k: int, questions: list[Question],
                  scores: list[QuestionScore], hits: dict[str, list[Hit]], summary: Summary,
                  isolation_failures: list[str]) -> Path:
    """One JSON file per run, for comparing runs over time. Chunk content is deliberately not stored."""
    now = datetime.now(UTC)
    payload = {
        "run_at": now.isoformat(timespec="seconds"),
        "dataset_sha256": dataset_sha256,
        "api_url": api_url,
        "top_k": top_k,
        "summary": {**asdict(summary), "multi_chunk_coverage": {k: list(v) for k, v in summary.multi_chunk_coverage.items()}},
        "isolation_failures": isolation_failures,
        "questions": [
            {
                **asdict(score),
                "tenant": q.tenant,
                "question": q.question,
                "hits": [{"rank": h.rank, "title": h.title, "section": h.section, "distance": h.distance} for h in hits[q.id]],
            }
            for q, score in zip(questions, scores, strict=True)
        ],
    }
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / f"{now.strftime('%Y%m%dT%H%M%SZ')}.json"
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return path
