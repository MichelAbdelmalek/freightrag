"""Judge calibration: run the faithfulness judge on hand-labelled cases and report how often it agrees with the labels.

A judge is a model too, and an uncalibrated judge's numbers mean nothing. The cases include answers that are
deliberately unfaithful (a wrong number, an added claim, a claim that comes from a chunk the answer did not cite).
"""

import json
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

from freightrag_eval.judge import FaithfulnessResult, Judge

LABELS = ("faithful", "unfaithful")
_FIELDS = {"id", "question", "answer", "cited_chunks", "label", "note"}


class CalibrationError(Exception):
    pass


@dataclass(frozen=True)
class Case:
    id: str
    question: str
    answer: str
    cited_chunks: list[str]
    label: str
    note: str


def load(path: str | Path) -> list[Case]:
    cases, seen = [], set()
    for number, line in enumerate(Path(path).read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
            if set(row) != _FIELDS:
                raise ValueError(f"fields must be exactly {sorted(_FIELDS)}")
            if row["label"] not in LABELS:
                raise ValueError(f"label must be one of {list(LABELS)}")
            if not row["cited_chunks"] or not all(isinstance(c, str) and c.strip() for c in row["cited_chunks"]):
                raise ValueError("cited_chunks must be a non-empty list of texts")
            case = Case(row["id"], row["question"], row["answer"], row["cited_chunks"], row["label"], row["note"])
        except (json.JSONDecodeError, ValueError, TypeError, KeyError) as error:
            raise CalibrationError(f"{path}, line {number}: {error}") from error
        if case.id in seen:
            raise CalibrationError(f"{path}, line {number}: duplicate id {case.id!r}")
        seen.add(case.id)
        cases.append(case)
    return cases


def predict(result: FaithfulnessResult) -> str:
    """Faithful means no claim was marked unsupported: a single invented figure on a rate confirmation is the
    failure that matters, so the bar is every claim, not most claims."""
    if result.error or result.score is None:
        return "error"
    return "faithful" if not result.unsupported else "unfaithful"


@dataclass(frozen=True)
class Outcome:
    case: Case
    predicted: str
    result: FaithfulnessResult


@dataclass(frozen=True)
class CalibrationReport:
    outcomes: list[Outcome]

    @property
    def scored(self) -> list[Outcome]:
        return [o for o in self.outcomes if o.predicted != "error"]

    @property
    def agreement(self) -> float | None:
        return sum(o.predicted == o.case.label for o in self.scored) / len(self.scored) if self.scored else None

    @property
    def confusion(self) -> dict[tuple[str, str], int]:
        return dict(Counter((o.case.label, o.predicted) for o in self.scored))

    @property
    def disagreements(self) -> list[Outcome]:
        return [o for o in self.scored if o.predicted != o.case.label]

    @property
    def errors(self) -> list[Outcome]:
        return [o for o in self.outcomes if o.predicted == "error"]


def run(cases: list[Case], judge: Judge, progress=None) -> CalibrationReport:
    outcomes = []
    for case in cases:
        result = judge.faithfulness(case.question, case.answer, list(case.cited_chunks))
        outcomes.append(Outcome(case, predict(result), result))
        if progress:
            progress(outcomes[-1])
    return CalibrationReport(outcomes)


def report_text(report: CalibrationReport) -> str:
    lines = [f"{'case':<5} {'label':<11} {'judge':<11} {'score':>5}  note", "-" * 100]
    for o in report.outcomes:
        score = "—" if o.result.score is None else f"{o.result.score:.2f}"
        mark = "" if o.predicted == o.case.label else "   <-- DISAGREES"
        lines.append(f"{o.case.id:<5} {o.case.label:<11} {o.predicted:<11} {score:>5}  {o.case.note[:60]}{mark}")
    agree = report.agreement
    lines += ["", "judge agreement with your labels: " + ("n/a" if agree is None else
              f"{sum(o.predicted == o.case.label for o in report.scored)}/{len(report.scored)} = {agree:.0%}")]
    lines.append("confusion (your label -> judge): " + (", ".join(
        f"{lab}->{pred}: {n}" for (lab, pred), n in sorted(report.confusion.items())) or "none"))
    if report.errors:
        lines.append(f"judge errors (not counted): {', '.join(f'{o.case.id} ({o.result.error})' for o in report.errors)}")
    for o in report.disagreements:
        flagged = "; ".join(u.claim for u in o.result.unsupported) or "nothing flagged"
        lines.append(f"  {o.case.id}: labelled {o.case.label}, judge said {o.predicted}. Unsupported claims flagged: {flagged}")
    return "\n".join(lines)
