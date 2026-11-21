import json
from pathlib import Path

import pytest

from freightrag_eval import calibration
from freightrag_eval.calibration import CalibrationError, load, predict, report_text, run
from freightrag_eval.judge import FaithfulnessResult
from tests.support import FakeJudge, unfaithful

ROOT = Path(__file__).resolve().parent.parent


def case(cid="c1", label="faithful", answer="a", chunks=("chunk",)):
    return {"id": cid, "question": "q?", "answer": answer, "cited_chunks": list(chunks), "label": label, "note": "n"}


def write(tmp_path, *rows):
    path = tmp_path / "cal.jsonl"
    path.write_text("\n".join(json.dumps(r) for r in rows) + "\n", encoding="utf-8")
    return path


def test_the_committed_calibration_set_is_valid_and_covers_the_failure_modes():
    cases = load(ROOT / "judge_calibration.jsonl")
    assert len(cases) >= 10
    unfaithful_notes = " ".join(c.note.lower() for c in cases if c.label == "unfaithful")
    assert {c.label for c in cases} == {"faithful", "unfaithful"}
    assert "wrong number" in unfaithful_notes and "added claim" in unfaithful_notes and "uncited" in unfaithful_notes


@pytest.mark.parametrize("bad", [
    {"label": "maybe"}, {"cited_chunks": []}, {"cited_chunks": [" "]}, {"extra": 1},
])
def test_invalid_cases_are_rejected_with_the_line_number(tmp_path, bad):
    with pytest.raises(CalibrationError, match="line 1"):
        load(write(tmp_path, {**case(), **bad}))


def test_duplicate_ids_are_rejected(tmp_path):
    with pytest.raises(CalibrationError, match="duplicate"):
        load(write(tmp_path, case(), case()))


def test_a_single_unsupported_claim_makes_the_prediction_unfaithful():
    assert predict(FaithfulnessResult(1.0, 3, ())) == "faithful"
    assert predict(unfaithful()) == "unfaithful"
    assert predict(FaithfulnessResult(None, 0, (), error="judge_timeout")) == "error"


def test_agreement_confusion_matrix_and_disagreements(tmp_path):
    cases = load(write(tmp_path, case("c1", "faithful", "ok"), case("c2", "unfaithful", "bad"),
                       case("c3", "unfaithful", "missed"), case("c4", "faithful", "flagged")))
    fake = FakeJudge({"bad": unfaithful("invented"), "flagged": unfaithful("harmless")})   # c3 "missed" is judged faithful

    report = run(cases, fake)

    assert report.agreement == 0.5
    assert report.confusion == {("faithful", "faithful"): 1, ("unfaithful", "unfaithful"): 1,
                                ("unfaithful", "faithful"): 1, ("faithful", "unfaithful"): 1}
    assert [o.case.id for o in report.disagreements] == ["c3", "c4"]
    text = report_text(report)
    assert "2/4 = 50%" in text and "c3: labelled unfaithful, judge said faithful" in text
    assert "Unsupported claims flagged: harmless" in text


def test_judge_errors_are_reported_and_not_counted(tmp_path):
    cases = load(write(tmp_path, case("c1", "faithful", "boom"), case("c2", "faithful", "fine")))
    report = run(cases, FakeJudge({"boom": FaithfulnessResult(None, 0, (), error="judge_timeout")}))
    assert report.agreement == 1.0 and len(report.scored) == 1
    assert "c1 (judge_timeout)" in report_text(report)


def test_the_calibrate_command_runs_the_judge_on_the_committed_cases(capsys):
    import run_answer_eval as runner
    fake = FakeJudge()
    code = runner.main(["calibrate"], environ={}, judge_factory=lambda e: (fake, {"provider": "fake", "model": "m"}))
    out = capsys.readouterr().out
    assert code == 0 and len(fake.calls) >= 10
    assert "judge agreement with your labels" in out and "fake / m" in out
    assert fake.closed
