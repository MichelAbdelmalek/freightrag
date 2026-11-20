import pytest

from freightrag_eval.metrics import Expected, Hit, coverage, first_match_rank, reciprocal_rank, score_question, summarize


def hit(rank, title, section, distance=0.3, document_id="d"):
    return Hit(rank=rank, document_id=document_id, title=title, section=section, distance=distance)


RATE = Expected("rate-confirmation-48213", "RATE")
CHARGES = Expected("invoice-NFL-2026-1187", "CHARGES")


def test_a_hit_matches_on_title_and_section_together():
    hits = [hit(1, "rate-confirmation-48213", "PICKUP"), hit(2, "invoice-NFL-2026-1187", "RATE"), hit(3, "rate-confirmation-48213", "RATE")]
    assert first_match_rank(hits, [RATE]) == 3


def test_no_match_gives_no_rank_and_zero_reciprocal_rank():
    assert first_match_rank([hit(1, "x", "y")], [RATE]) is None
    assert reciprocal_rank(None) == 0.0


@pytest.mark.parametrize(("rank", "expected"), [(1, 1.0), (2, 0.5), (3, 1 / 3), (5, 0.2)])
def test_reciprocal_rank(rank, expected):
    assert reciprocal_rank(rank) == pytest.approx(expected)


def test_coverage_counts_distinct_expected_items_found():
    hits = [hit(1, "invoice-NFL-2026-1187", "CHARGES"), hit(2, "invoice-NFL-2026-1187", "CHARGES"), hit(3, "x", "y")]
    assert coverage(hits, [CHARGES, RATE]) == (1, 2)


def test_score_of_an_answerable_question():
    hits = [hit(1, "x", "y", 0.25), hit(2, "rate-confirmation-48213", "RATE", 0.28)]
    score = score_question("q1", "answerable", hits, [RATE])
    assert (score.rank, score.hit_at_1, score.hit_at_k, score.reciprocal_rank) == (2, False, True, 0.5)
    assert score.top1_distance == 0.25


def test_score_of_an_unanswerable_question_has_no_rank():
    score = score_question("q14", "unanswerable", [hit(1, "x", "y", 0.41)], [])
    assert score.rank is None
    assert score.top1_distance == 0.41


def test_summary_averages_over_answerable_questions_only():
    scores = [
        score_question("a", "answerable", [hit(1, *("t", "s"))], [Expected("t", "s")]),          # rank 1
        score_question("b", "answerable", [hit(1, "x", "y"), hit(2, "t", "s")], [Expected("t", "s")]),  # rank 2
        score_question("c", "answerable", [hit(1, "x", "y")], [Expected("t", "s")]),          # miss
        score_question("u", "unanswerable", [hit(1, "x", "y")], []),
    ]
    summary = summarize(scores)
    assert summary.answerable == 3
    assert summary.recall_at_1 == pytest.approx(1 / 3)
    assert summary.recall_at_k == pytest.approx(2 / 3)
    assert summary.mrr == pytest.approx((1 + 0.5 + 0) / 3)
    assert summary.unanswerable == 1


def test_summary_reports_multi_chunk_coverage_separately():
    both = [Expected("t", "a"), Expected("t", "b")]
    scores = [score_question("m", "answerable", [hit(1, "t", "a"), hit(2, "x", "y")], both)]
    assert summarize(scores).multi_chunk_coverage == {"m": (1, 2)}
