import json

import pytest

import run_answer_eval as runner
from freightrag_eval.compare import CompareError, compare, load_run, report_text

SHA = "a" * 64


def write_run(tmp_path, name, records, outcomes, flow="straight", sha=SHA, false_refusals=0, isolation=()):
    answers = {"run_at": "x", "complete": True, "dataset_sha256": sha, "isolation_failures": list(isolation),
               "questions": [{"id": i, "status": r[0], "answer": r[1], "reason": r[2], "client_ms": r[3], "flow": flow,
                              "attempts": r[4] if len(r) > 4 else 1, "modelCalls": r[5] if len(r) > 5 else 1,
                              "firstReason": r[6] if len(r) > 6 else None, "rewriteStatus": r[7] if len(r) > 7 else None}
                             for i, r in records.items()]}
    counts = {k: sum(v == k for v in outcomes.values()) for k in
              ("answered", "false_refusal", "false_answer", "correct_abstention", "error")}
    judged = {"dataset_sha256_when_generated": sha, "dataset_sha256_when_judged": sha,
              "summary": {"abstention": {"counts": counts}, "fact_recall_answered": 0.9, "fact_recall_all": 0.7,
                          "faithfulness_mean": None},
              "questions": [{"id": i, "outcome": o} for i, o in outcomes.items()]}
    (tmp_path / f"{name}.json").write_text(json.dumps(answers))
    path = tmp_path / f"{name}.judged.json"
    path.write_text(json.dumps(judged))
    return path


def straight(tmp_path, sha=SHA):
    return write_run(tmp_path, "answers-A", {"q1": ("answered", "Lena [1]", None, 30000), "q2": ("insufficient_context", None, "model_abstained", 20000),
                                              "q3": ("insufficient_context", None, "identifier_not_found", 400, 1, 0)},
                     {"q1": "answered", "q2": "false_refusal", "q3": "correct_abstention"}, sha=sha)


def retry(tmp_path, flow="retry", name="answers-B", q2=("answered", "Cara [1]", None, 70000, 2, 3, "model_abstained", "accepted"),
          q1_answer="Lena [1]", sha=SHA, outcome_q2="answered"):
    return write_run(tmp_path, name, {"q1": ("answered", q1_answer, None, 31000), "q2": q2,
                                       "q3": ("insufficient_context", None, "identifier_not_found", 500, 2, 2, "identifier_not_found", "accepted")},
                     {"q1": "answered", "q2": outcome_q2, "q3": "correct_abstention"}, flow=flow, sha=sha)


def test_compare_reports_abstention_fact_recall_latency_and_model_calls_per_run(tmp_path):
    result = compare([load_run(straight(tmp_path)), load_run(retry(tmp_path))])
    a, b = result.stats
    assert (a.flow, b.flow) == ("straight", "retry")
    assert (a.counts["false_refusal"], b.counts["false_refusal"]) == (1, 0)
    assert (b.fact_recall_answered, b.fact_recall_all) == (0.9, 0.7)
    assert a.median_ms == 20000 and b.median_ms == 31000
    assert a.mean_model_calls == pytest.approx(2 / 3) and b.mean_model_calls == pytest.approx((1 + 3 + 2) / 3)
    assert b.retried_median_ms == pytest.approx(35250) and a.retried_median_ms is None


def test_latency_median_and_p90_from_client_ms(tmp_path):
    path = write_run(tmp_path, "answers-L", {f"q{i}": ("answered", "x", None, i * 1000) for i in range(1, 11)},
                     {f"q{i}": "answered" for i in range(1, 11)})
    other = write_run(tmp_path, "answers-M", {f"q{i}": ("answered", "x", None, 1000) for i in range(1, 11)},
                      {f"q{i}": "answered" for i in range(1, 11)})
    stats = compare([load_run(path), load_run(other)]).stats[0]
    assert (stats.median_ms, stats.p90_ms) == (5500, 9000)


def test_compare_lists_questions_whose_outcome_changed(tmp_path):
    result = compare([load_run(straight(tmp_path)), load_run(retry(tmp_path))])
    assert [row["id"] for row in result.changed] == ["q2"]
    assert result.changed[0]["runs"][1]["rewrite_status"] == "accepted"


def test_compare_refuses_runs_with_different_dataset_sha(tmp_path):
    with pytest.raises(CompareError, match="different datasets"):
        compare([load_run(straight(tmp_path)), load_run(retry(tmp_path, sha="b" * 64))])


def test_compare_refuses_a_file_mixing_flows(tmp_path):
    path = retry(tmp_path)
    answers_path = tmp_path / "answers-B.json"
    payload = json.loads(answers_path.read_text())
    payload["questions"][0]["flow"] = "graph"
    answers_path.write_text(json.dumps(payload))
    with pytest.raises(CompareError, match="mixes flows"):
        load_run(path)


def test_compare_flags_first_attempt_parity_breaks(tmp_path):
    result = compare([load_run(straight(tmp_path)), load_run(retry(tmp_path, q1_answer="Someone else [1]"))])
    assert result.parity_breaks == {"answers-B": ["q1"]}


def test_a_third_run_is_compared_to_the_second_on_status_and_reason(tmp_path):
    third = retry(tmp_path, flow="graph", name="answers-C", q2=("insufficient_context", None, "model_abstained", 60000, 2, 3, "model_abstained", "accepted"),
                  outcome_q2="false_refusal")
    result = compare([load_run(straight(tmp_path)), load_run(retry(tmp_path)), load_run(third)])
    assert result.flow_differences == {"answers-C": ["q2"]}


def test_compare_cli_prints_the_decision_table_and_exits_1_on_a_false_answer(tmp_path, capsys):
    a, b = straight(tmp_path), retry(tmp_path)
    assert runner.main(["compare", str(a), str(b)]) == 0
    out = capsys.readouterr().out
    assert "false refusals" in out and "first-attempt parity" in out and "q2:" in out
    bad = write_run(tmp_path, "answers-X", {"q1": ("answered", "x", None, 1), "q2": ("answered", "y", None, 1), "q3": ("answered", "z", None, 1)},
                    {"q1": "answered", "q2": "answered", "q3": "false_answer"})
    assert runner.main(["compare", str(a), str(bad)]) == 1


def test_report_text_is_plain_text(tmp_path):
    assert "median client_ms" in report_text(compare([load_run(straight(tmp_path)), load_run(retry(tmp_path))]))
