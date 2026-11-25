"""Compares judged runs of the answer service (for example: straight, retry, graph) on the same dataset.

Pure functions over saved files: nothing here calls a service. A run is its answers file (what the service said, with
timings and retry diagnostics) plus its judged file (outcomes, fact recall). Refuses to compare runs that are not
comparable: different datasets, or one file that mixes flows.
"""

import json
import math
import statistics
from dataclasses import dataclass
from pathlib import Path


class CompareError(Exception):
    pass


@dataclass(frozen=True)
class Run:
    name: str
    flow: str
    dataset_sha: str
    records: dict[str, dict]      # id -> answers-file record
    outcomes: dict[str, str]      # id -> answered | false_refusal | false_answer | correct_abstention | error
    summary: dict                 # the judged file's summary
    isolation_failures: int


@dataclass(frozen=True)
class RunStats:
    name: str
    flow: str
    counts: dict[str, int]
    fact_recall_answered: float | None
    fact_recall_all: float | None
    faithfulness_mean: float | None
    median_ms: float
    p90_ms: float
    retried_median_ms: float | None
    mean_model_calls: float
    isolation_failures: int


@dataclass(frozen=True)
class Comparison:
    stats: list[RunStats]
    changed: list[dict]           # per question whose outcome or answer differs from the first run
    parity_breaks: dict[str, list[str]]   # run name -> ids answered in the first run but not answered identically here
    flow_differences: dict[str, list[str]]  # run name -> ids whose status or reason differs from the second run (if any)


def load_run(judged_path: Path) -> Run:
    judged = json.loads(judged_path.read_text(encoding="utf-8"))
    answers_path = judged_path.with_name(judged_path.name.removesuffix(".judged.json") + ".json")
    answers = json.loads(answers_path.read_text(encoding="utf-8"))
    flows = {r.get("flow") or "straight" for r in answers["questions"] if r["status"] != "error"}
    if len(flows) > 1:
        raise CompareError(f"{answers_path.name} mixes flows ({', '.join(sorted(flows))}); regenerate it with one ANSWER_FLOW.")
    sha = judged["dataset_sha256_when_generated"]
    return Run(name=answers_path.stem, flow=flows.pop() if flows else "straight", dataset_sha=sha,
               records={r["id"]: r for r in answers["questions"]},
               outcomes={q["id"]: q["outcome"] for q in judged["questions"]}, summary=judged["summary"],
               isolation_failures=len(answers.get("isolation_failures", [])))


def _percentile(values: list[int], p: float) -> float:
    ordered = sorted(values)
    return float(ordered[max(0, math.ceil(p * len(ordered)) - 1)])


def _stats(run: Run) -> RunStats:
    times = [r["client_ms"] for r in run.records.values() if "client_ms" in r]
    retried = [r["client_ms"] for r in run.records.values() if r.get("attempts") == 2 and "client_ms" in r]
    calls = [r["modelCalls"] if r.get("modelCalls") is not None else (1 if r.get("llmMs") else 0) for r in run.records.values()
             if r["status"] != "error"]
    return RunStats(
        name=run.name, flow=run.flow, counts=run.summary["abstention"]["counts"],
        fact_recall_answered=run.summary.get("fact_recall_answered"), fact_recall_all=run.summary.get("fact_recall_all"),
        faithfulness_mean=run.summary.get("faithfulness_mean"),
        median_ms=statistics.median(times) if times else 0.0, p90_ms=_percentile(times, 0.9) if times else 0.0,
        retried_median_ms=statistics.median(retried) if retried else None,
        mean_model_calls=sum(calls) / len(calls) if calls else 0.0, isolation_failures=run.isolation_failures)


def compare(runs: list[Run]) -> Comparison:
    if len(runs) < 2:
        raise CompareError("Give at least two judged files to compare.")
    if len({r.dataset_sha for r in runs}) != 1:
        raise CompareError("These runs used different datasets (different SHA-256), so they are not comparable.")

    base = runs[0]
    changed = []
    for qid in base.outcomes:
        row = {"id": qid, "runs": []}
        differs = False
        for run in runs:
            record = run.records.get(qid, {})
            row["runs"].append({"outcome": run.outcomes.get(qid), "status": record.get("status"), "reason": record.get("reason"),
                                "first_reason": record.get("firstReason"), "rewrite_status": record.get("rewriteStatus")})
            differs |= (run.outcomes.get(qid) != base.outcomes.get(qid)
                        or run.records.get(qid, {}).get("answer") != base.records.get(qid, {}).get("answer"))
        if differs:
            changed.append(row)

    parity: dict[str, list[str]] = {}
    for run in runs[1:]:
        parity[run.name] = [qid for qid, rec in base.records.items()
                            if rec["status"] == "answered" and (run.records.get(qid, {}).get("status") != "answered"
                                                                or run.records[qid]["answer"] != rec["answer"])]
    flow_diff: dict[str, list[str]] = {}
    if len(runs) >= 3:
        second = runs[1]
        for run in runs[2:]:
            flow_diff[run.name] = [qid for qid, rec in second.records.items()
                                   if (rec["status"], rec.get("reason")) != (run.records.get(qid, {}).get("status"), run.records.get(qid, {}).get("reason"))]
    return Comparison([_stats(r) for r in runs], changed, parity, flow_diff)


def _pct(value: float | None) -> str:
    return "n/a" if value is None else f"{value:.1%}"


def report_text(result: Comparison) -> str:
    cols = result.stats
    width = max(14, *(len(c.flow) + 2 for c in cols))
    rows = [
        ("run file", [c.name[-20:] for c in cols]),
        ("flow", [c.flow for c in cols]),
        ("false answers (of unanswerable)", [str(c.counts["false_answer"]) for c in cols]),
        ("isolation failures", [str(c.isolation_failures) for c in cols]),
        ("false refusals", [str(c.counts["false_refusal"]) for c in cols]),
        ("answered", [str(c.counts["answered"]) for c in cols]),
        ("fact recall (answered)", [_pct(c.fact_recall_answered) for c in cols]),
        ("fact recall (end to end)", [_pct(c.fact_recall_all) for c in cols]),
        ("faithfulness mean (rough)", ["n/a" if c.faithfulness_mean is None else f"{c.faithfulness_mean:.3f}" for c in cols]),
        ("median client_ms", [f"{c.median_ms:.0f}" for c in cols]),
        ("p90 client_ms", [f"{c.p90_ms:.0f}" for c in cols]),
        ("retried median client_ms", ["n/a" if c.retried_median_ms is None else f"{c.retried_median_ms:.0f}" for c in cols]),
        ("mean model calls / question", [f"{c.mean_model_calls:.2f}" for c in cols]),
    ]
    lines = [f"{'measure':<34}" + "".join(f"{v:>{width + 8}}" for v in [c.flow for c in cols])]
    lines += [f"{label:<34}" + "".join(f"{v:>{width + 8}}" for v in values) for label, values in rows[1:]]
    lines.append("")
    lines.append("first-attempt parity (answered in the first run but not answered identically):")
    for name, ids in result.parity_breaks.items():
        lines.append(f"  {name}: {', '.join(ids) if ids else 'none'}")
    for name, ids in result.flow_differences.items():
        lines.append(f"status/reason differences vs the second run, {name}: {len(ids)} ({', '.join(ids) or 'none'})")
    lines.append("")
    lines.append(f"questions whose outcome or answer changed ({len(result.changed)}):")
    for row in result.changed:
        cells = " -> ".join(f"{r['outcome']}" + (f" [{r['first_reason']}/{r['rewrite_status']}]" if r["rewrite_status"] else "")
                            for r in row["runs"])
        lines.append(f"  {row['id']}: {cells}")
    return "\n".join(lines)
