"""Retrieval metrics, as pure functions.

recall@k  — for what share of questions is a correct chunk anywhere in the top k?
MRR       — mean reciprocal rank: 1/rank of the first correct chunk (rank 1 → 1.0, rank 2 → 0.5, miss → 0),
            averaged over questions. Recall says "did we find it?", MRR says "did we put it at the top?".
Both are computed over *answerable* questions only; unanswerable ones are reported separately.

"expected" vs "acceptable": a question's expected chunks are the ones it should retrieve; acceptable chunks also
answer it (e.g. a bill of lading repeating the temperature from the rate confirmation). Either counts as a hit for
rank, recall and MRR. Multi-chunk coverage counts expected chunks only.
"""

from collections.abc import Sequence

from dataclasses import dataclass, field


@dataclass(frozen=True)
class Expected:
    """A chunk the question should retrieve, identified by stable values (not by chunk id)."""
    document_title: str
    section: str


@dataclass(frozen=True)
class Hit:
    """One search result as the eval sees it."""
    rank: int  # 1-based position in the results
    document_id: str
    title: str
    section: str | None
    distance: float
    vector_rank: int | None = None   # position in the vector leg's candidates (None: not a vector candidate)
    keyword_rank: int | None = None  # position in the keyword leg's candidates (None: not a keyword candidate)
    fused_score: float | None = None


def matches(hit: Hit, expected: Expected) -> bool:
    return hit.title == expected.document_title and hit.section == expected.section


def first_match_rank(hits: list[Hit], expected: list[Expected]) -> int | None:
    """Rank of the first hit matching any expected item, or None."""
    return next((h.rank for h in hits if any(matches(h, e) for e in expected)), None)


def reciprocal_rank(rank: int | None) -> float:
    return 0.0 if rank is None else 1.0 / rank


def coverage(hits: list[Hit], expected: list[Expected]) -> tuple[int, int]:
    """(expected items found among the hits, expected items in total) — for questions needing several chunks."""
    found = sum(1 for e in expected if any(matches(h, e) for h in hits))
    return found, len(expected)


@dataclass(frozen=True)
class QuestionScore:
    id: str
    kind: str
    rank: int | None
    hit_at_1: bool
    hit_at_k: bool
    reciprocal_rank: float
    found: int
    total: int
    top1_distance: float | None


def score_question(question_id: str, kind: str, hits: list[Hit], expected: list[Expected],
                   acceptable: Sequence[Expected] = ()) -> QuestionScore:
    correct = [*expected, *acceptable]
    rank = first_match_rank(hits, correct) if correct else None
    found, total = coverage(hits, expected)
    return QuestionScore(
        id=question_id,
        kind=kind,
        rank=rank,
        hit_at_1=rank == 1,
        hit_at_k=rank is not None,
        reciprocal_rank=reciprocal_rank(rank),
        found=found,
        total=total,
        top1_distance=hits[0].distance if hits else None,
    )


@dataclass(frozen=True)
class Summary:
    answerable: int
    unanswerable: int
    recall_at_1: float
    recall_at_k: float
    mrr: float
    multi_chunk_coverage: dict[str, tuple[int, int]] = field(default_factory=dict)


def summarize(scores: list[QuestionScore]) -> Summary:
    answerable = [s for s in scores if s.kind == "answerable"]
    n = len(answerable) or 1  # avoid dividing by zero on an all-unanswerable set
    return Summary(
        answerable=len(answerable),
        unanswerable=sum(s.kind == "unanswerable" for s in scores),
        recall_at_1=sum(s.hit_at_1 for s in answerable) / n,
        recall_at_k=sum(s.hit_at_k for s in answerable) / n,
        mrr=sum(s.reciprocal_rank for s in answerable) / n,
        multi_chunk_coverage={s.id: (s.found, s.total) for s in answerable if s.total > 1},
    )
