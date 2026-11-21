"""Console tables and results file."""

import json
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path

from freightrag_eval.dataset import Question
from freightrag_eval.metrics import Hit, QuestionScore, Summary


@dataclass(frozen=True)
class ModeRun:
    """Everything one retrieval mode produced for the question set."""
    mode: str
    scores: list[QuestionScore]
    hits: dict[str, list[Hit]]
    summary: Summary
    isolation_failures: list[str]


def _rank_text(q: Question, s: QuestionScore) -> str:
    return "n/a" if q.kind == "unanswerable" else (str(s.rank) if s.rank else "miss")


def table(questions: list[Question], scores: list[QuestionScore], hits: dict[str, list[Hit]]) -> str:
    lines = [f"{'id':<5} {'kind':<13} {'rank':>5} {'top1 dist':>9}  top-1 result", "-" * 100]
    for q, s in zip(questions, scores, strict=True):
        top = hits[q.id][0] if hits[q.id] else None
        extra = f"  ({s.found}/{s.total} expected)" if s.total > 1 else ""
        top_text = f"{top.title} › {top.section}" if top else "—"
        dist = f"{s.top1_distance:.4f}" if s.top1_distance is not None else "—"
        lines.append(f"{q.id:<5} {q.kind:<13} {_rank_text(q, s):>5} {dist:>9}  {top_text}{extra}")
    return "\n".join(lines)


def summary_text(summary: Summary, top_k: int) -> str:
    lines = [
        f"answerable questions: {summary.answerable}   unanswerable: {summary.unanswerable}",
        f"recall@1 = {summary.recall_at_1:.3f}   recall@{top_k} = {summary.recall_at_k:.3f}   MRR = {summary.mrr:.3f}",
    ]
    for qid, (found, total) in summary.multi_chunk_coverage.items():
        lines.append(f"multi-chunk {qid}: {found}/{total} expected chunks in the top {top_k}")
    return "\n".join(lines)


def compare_table(questions: list[Question], runs: list[ModeRun]) -> str:
    """Per-question rank of the first correct chunk, one column per mode."""
    header = f"{'id':<5} {'kind':<13} " + " ".join(f"{r.mode:>8}" for r in runs) + "  question"
    lines = [header, "-" * 100]
    for i, q in enumerate(questions):
        ranks = " ".join(f"{_rank_text(q, r.scores[i]):>8}" for r in runs)
        lines.append(f"{q.id:<5} {q.kind:<13} {ranks}  {q.question[:60]}")
    return "\n".join(lines)


def compare_summary(runs: list[ModeRun], top_k: int) -> str:
    """The headline metrics of every mode side by side."""
    def row(label: str, values: list[str]) -> str:
        return f"{label:<22}" + "".join(f"{v:>10}" for v in values)

    lines = [row("metric", [r.mode for r in runs]), "-" * (22 + 10 * len(runs))]
    lines.append(row("recall@1", [f"{r.summary.recall_at_1:.3f}" for r in runs]))
    lines.append(row(f"recall@{top_k}", [f"{r.summary.recall_at_k:.3f}" for r in runs]))
    lines.append(row("MRR", [f"{r.summary.mrr:.3f}" for r in runs]))
    for qid in runs[0].summary.multi_chunk_coverage:
        lines.append(row(f"multi-chunk {qid}", [
            "{}/{}".format(*r.summary.multi_chunk_coverage[qid]) for r in runs]))
    return "\n".join(lines)


def write_results(directory: Path, *, dataset_sha256: str, api_url: str, top_k: int, questions: list[Question],
                  runs: list[ModeRun]) -> Path:
    """One JSON file per run (one entry per mode), for comparing runs over time. Chunk content is not stored."""
    now = datetime.now(UTC)
    payload = {
        "run_at": now.isoformat(timespec="seconds"),
        "dataset_sha256": dataset_sha256,
        "api_url": api_url,
        "top_k": top_k,
        "modes": [r.mode for r in runs],
        "runs": {r.mode: _run_payload(questions, r) for r in runs},
    }
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / f"{now.strftime('%Y%m%dT%H%M%SZ')}.json"
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return path


def _run_payload(questions: list[Question], run: ModeRun) -> dict:
    summary = run.summary
    return {
        "summary": {**asdict(summary), "multi_chunk_coverage": {k: list(v) for k, v in summary.multi_chunk_coverage.items()}},
        "isolation_failures": run.isolation_failures,
        "questions": [
            {
                **asdict(score),
                "tenant": q.tenant,
                "question": q.question,
                "hits": [{"rank": h.rank, "title": h.title, "section": h.section, "distance": h.distance,
                          "vector_rank": h.vector_rank, "keyword_rank": h.keyword_rank, "fused_score": h.fused_score}
                         for h in run.hits[q.id]],
            }
            for q, score in zip(questions, run.scores, strict=True)
        ],
    }
