import pytest

from freightrag_eval.abstention import classify, summarize


@pytest.mark.parametrize(("kind", "status", "outcome"), [
    ("answerable", "answered", "answered"),
    ("answerable", "insufficient_context", "false_refusal"),
    ("unanswerable", "answered", "false_answer"),
    ("unanswerable", "insufficient_context", "correct_abstention"),
    ("answerable", "error", "error"),
    ("unanswerable", "weird", "error"),
])
def test_classify_covers_the_four_cells_and_errors(kind, status, outcome):
    assert classify(kind, status) == outcome


def test_summary_counts_rates_and_false_answers():
    outcomes = {"q1": "answered", "q2": "answered", "q3": "false_refusal", "q4": "false_answer",
                "q5": "correct_abstention", "q6": "correct_abstention", "q7": "correct_abstention", "q8": "error"}
    s = summarize(outcomes)
    assert s.counts == {"answered": 2, "false_refusal": 1, "false_answer": 1, "correct_abstention": 3, "error": 1}
    assert (s.answerable, s.unanswerable) == (3, 4)                       # the error is in neither denominator
    assert s.false_refusal_rate == pytest.approx(1 / 3)
    assert s.false_answer_rate == pytest.approx(1 / 4)
    assert s.false_answer_ids == ["q4"]


def test_rates_are_undefined_without_questions_of_that_kind():
    s = summarize({"q1": "answered"})
    assert s.false_answer_rate is None and s.false_refusal_rate == 0.0
