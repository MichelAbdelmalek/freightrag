"""The abstention table: did the service answer when it should and refuse when it should?

                      answered                 insufficient_context
  answerable          answered (attempt)       FALSE REFUSAL
  unanswerable        FALSE ANSWER             correct abstention

"answered" only means it attempted an answer; whether the answer is right is the fact check's job. A false answer is
the costliest cell: a confident reply to a question the documents cannot answer.
"""

from collections import Counter
from dataclasses import dataclass, field

ANSWERED, FALSE_REFUSAL, FALSE_ANSWER, CORRECT_ABSTENTION, ERROR = (
    "answered", "false_refusal", "false_answer", "correct_abstention", "error")


def classify(kind: str, status: str) -> str:
    if status not in ("answered", "insufficient_context"):
        return ERROR
    if kind == "answerable":
        return ANSWERED if status == "answered" else FALSE_REFUSAL
    return FALSE_ANSWER if status == "answered" else CORRECT_ABSTENTION


@dataclass(frozen=True)
class AbstentionSummary:
    counts: dict[str, int]
    answerable: int                 # excluding errors
    unanswerable: int               # excluding errors
    false_refusal_rate: float | None
    false_answer_rate: float | None
    false_answer_ids: list[str] = field(default_factory=list)


def summarize(outcomes: dict[str, str]) -> AbstentionSummary:
    """outcomes: question id -> outcome from classify()."""
    counts = Counter(outcomes.values())
    answerable = counts[ANSWERED] + counts[FALSE_REFUSAL]
    unanswerable = counts[FALSE_ANSWER] + counts[CORRECT_ABSTENTION]
    return AbstentionSummary(
        counts={k: counts[k] for k in (ANSWERED, FALSE_REFUSAL, FALSE_ANSWER, CORRECT_ABSTENTION, ERROR)},
        answerable=answerable, unanswerable=unanswerable,
        false_refusal_rate=counts[FALSE_REFUSAL] / answerable if answerable else None,
        false_answer_rate=counts[FALSE_ANSWER] / unanswerable if unanswerable else None,
        false_answer_ids=[qid for qid, o in outcomes.items() if o == FALSE_ANSWER],
    )
