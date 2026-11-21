"""Console output and the judged results file for the answer evaluation."""

import json
from datetime import UTC, datetime
from pathlib import Path

from freightrag_eval.abstention import AbstentionSummary
from freightrag_eval.answer_judge import AnswerSummary, Judgement


def false_answer_banner(summary: AbstentionSummary) -> str:
    if not summary.false_answer_ids:
        return ""
    return ("!! FALSE ANSWERS: the service answered questions the documents cannot answer: "
            + ", ".join(summary.false_answer_ids))


def question_table(judgements: list[Judgement]) -> str:
    lines = [f"{'id':<5} {'status':<21} {'facts':>6} {'faithful':>8}  outcome", "-" * 70]
    for j in judgements:
        facts = "—" if j.facts is None else f"{j.facts.found}/{j.facts.total}"
        if j.faithfulness is None:
            faith = "—"
        elif j.faithfulness.error or j.faithfulness.score is None:
            faith = "error"
        else:
            faith = f"{j.faithfulness.score:.2f}"
        lines.append(f"{j.id:<5} {j.status:<21} {facts:>6} {faith:>8}  {j.outcome}")
    return "\n".join(lines)


def _pct(value: float | None) -> str:
    return "n/a" if value is None else f"{value:.1%}"


def findings(judgements: list[Judgement]) -> str:
    lines = []
    for j in judgements:
        if j.facts and j.facts.missing:
            lines.append(f"  {j.id}: missing facts: {', '.join(j.facts.missing)}")
        if j.faithfulness:
            for u in j.faithfulness.unsupported:
                lines.append(f"  {j.id}: UNSUPPORTED claim: {u.claim}" + (f" ({u.reason})" if u.reason else ""))
            if j.faithfulness.error:
                lines.append(f"  {j.id}: judge error: {j.faithfulness.error}")
    return "\n".join(["Findings:", *lines]) if lines else "Findings: none"


def summary_text(summary: AnswerSummary) -> str:
    a = summary.abstention
    c = a.counts
    lines = [
        "Abstention table (answerable = %d, unanswerable = %d; errors excluded: %d)" % (a.answerable, a.unanswerable, c["error"]),
        f"  answerable   -> answered: {c['answered']}   FALSE REFUSAL: {c['false_refusal']}   (false-refusal rate {_pct(a.false_refusal_rate)})",
        f"  unanswerable -> FALSE ANSWER: {c['false_answer']}   correct abstention: {c['correct_abstention']}   (false-answer rate {_pct(a.false_answer_rate)})",
        f"Fact recall: {_pct(summary.fact_recall_answered)} over answered questions, {_pct(summary.fact_recall_all)} end to end (a refusal counts as 0)",
    ]
    if summary.faithfulness_judged or summary.judge_errors:
        mean = "n/a" if summary.faithfulness_mean is None else f"{summary.faithfulness_mean:.3f}"
        lines.append(f"Faithfulness: mean {mean} over {summary.faithfulness_judged} judged answers, "
                     f"{summary.unsupported_claims} unsupported claims, {summary.judge_errors} judge errors")
    banner = false_answer_banner(a)
    return "\n".join(lines + ([banner] if banner else []))


def write_judged(answers_path: Path, *, answers: dict, dataset_sha256_now: str, judge_info: dict | None,
                 judgements: list[Judgement], summary: AnswerSummary) -> Path:
    """Saved next to the answers file: answers-<UTC>.judged.json. No keys; the judge's key is never part of judge_info."""
    now = datetime.now(UTC)
    rows = []
    for j in judgements:
        row = {"id": j.id, "kind": j.kind, "status": j.status, "outcome": j.outcome, "reason": j.reason}
        if j.facts:
            row["facts"] = {"found": j.facts.found, "total": j.facts.total, "missing": list(j.facts.missing)}
        if j.faithfulness:
            f = j.faithfulness
            row["faithfulness"] = {"score": f.score, "claims": f.claims, "uncertain": f.uncertain, "error": f.error,
                                   "unsupported": [{"claim": u.claim, "reason": u.reason} for u in f.unsupported]}
        rows.append(row)
    a = summary.abstention
    payload = {
        "judged_at": now.isoformat(timespec="seconds"), "answers_file": answers_path.name,
        "dataset_sha256_when_generated": answers.get("dataset_sha256"), "dataset_sha256_when_judged": dataset_sha256_now,
        "judge": judge_info,
        "summary": {"abstention": {"counts": a.counts, "false_refusal_rate": a.false_refusal_rate,
                                   "false_answer_rate": a.false_answer_rate, "false_answer_ids": a.false_answer_ids},
                    "fact_recall_answered": summary.fact_recall_answered, "fact_recall_all": summary.fact_recall_all,
                    "faithfulness_mean": summary.faithfulness_mean, "faithfulness_judged": summary.faithfulness_judged,
                    "unsupported_claims": summary.unsupported_claims, "judge_errors": summary.judge_errors},
        "questions": rows,
    }
    path = answers_path.with_suffix(".judged.json")
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return path
