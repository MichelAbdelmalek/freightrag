import json

import run_answer_eval as runner
from tests.support import FakeJudge, ROWS, dataset, unfaithful


def record(qid, status, answer=None, citations=(), reason=None):
    tenant = {"q1": "tenant-nile-freight", "q2": "tenant-delta-haul", "q3": "tenant-nile-freight"}[qid]
    kind = "unanswerable" if qid == "q3" else "answerable"
    return {"id": qid, "tenant": tenant, "kind": kind, "question": {"q1": "detention rate?", "q2": "reefer temperature?",
            "q3": "fuel card policy?"}[qid], "status": status, "answer": answer, "citations": list(citations), "reason": reason}


def cit(text, source=1):
    return {"source": source, "documentId": "d", "title": "t", "section": "S", "chunkIndex": 0, "chunk_text": text}


def answers_file(tmp_path, records, dataset_path):
    import hashlib
    path = tmp_path / "answers-20261121T000000Z.json"
    path.write_text(json.dumps({"run_at": "x", "dataset_sha256": hashlib.sha256(dataset_path.read_bytes()).hexdigest(),
                                "questions": records}), encoding="utf-8")
    return path


def judge(tmp_path, records, fake=None, extra=(), rows=ROWS):
    ds = dataset(tmp_path, rows)
    path = answers_file(tmp_path, records, ds)
    fake = fake or FakeJudge()
    code = runner.main(["judge", str(path), "--dataset", str(ds), *extra], environ={},
                       judge_factory=lambda environ: (fake, {"provider": "fake", "model": "m"}))
    return code, fake, path


def test_only_the_cited_chunks_reach_the_judge(tmp_path):
    rec = record("q1", "answered", "Detention is $50.00 per hour after 2 free hours [1].",
                 [cit("CITED chunk text", 1), {**cit(None, 2), "chunk_text": None}], None)
    _, fake, _ = judge(tmp_path, [rec])

    assert fake.calls == [("detention rate?", "Detention is $50.00 per hour after 2 free hours [1].", ["CITED chunk text"])]


def test_abstained_and_failed_questions_are_not_judged(tmp_path):
    records = [record("q1", "insufficient_context", reason="model_abstained"), record("q2", "error"),
               record("q3", "insufficient_context", reason="identifier_not_found")]
    _, fake, _ = judge(tmp_path, records)
    assert fake.calls == []


def test_fact_recall_is_per_question_and_overall(tmp_path, capsys):
    records = [record("q1", "answered", "Detention is $50.00 per hour [1].", [cit("c")]),    # 1 of 2 facts
               record("q2", "insufficient_context", reason="model_abstained")]                # false refusal: 0 of 1
    code, _, path = judge(tmp_path, records, extra=["--skip-faithfulness"])
    out = capsys.readouterr().out
    judged = json.loads(path.with_suffix(".judged.json").read_text())

    assert code == 0
    q1, q2 = judged["questions"][:2]
    assert q1["facts"] == {"found": 1, "total": 2, "missing": ["2 free hours"]}
    assert q2["facts"] == {"found": 0, "total": 1, "missing": ["-10"]} and q2["outcome"] == "false_refusal"
    assert judged["summary"]["fact_recall_answered"] == 0.5          # 1 of 2 facts over the answered question
    assert judged["summary"]["fact_recall_all"] == 1 / 3            # 1 of 3 facts when the refusal counts as zero
    assert "FALSE REFUSAL: 1" in out and "missing facts: 2 free hours" in out


def test_a_false_answer_is_shown_first_and_makes_the_exit_code_1(tmp_path, capsys):
    records = [record("q1", "answered", "Detention is $50.00 per hour after 2 free hours [1].", [cit("c")]),
               record("q3", "answered", "The fuel card limit is $500 [1].", [cit("c")])]
    code, _, _ = judge(tmp_path, records)
    out = capsys.readouterr().out

    assert code == 1
    assert out.startswith("!! FALSE ANSWERS")                         # before the slow faithfulness pass and the table
    assert out.count("FALSE ANSWERS") == 2 and "q3" in out.splitlines()[0]   # and repeated in the summary


def test_unsupported_claims_are_reported_and_averaged(tmp_path, capsys):
    text = "Detention is $50.00 per hour after 2 free hours, capped at 8 hours [1]."
    code, _, path = judge(tmp_path, [record("q1", "answered", text, [cit("c")])], FakeJudge({text: unfaithful("capped at 8 hours")}))
    out = capsys.readouterr().out
    judged = json.loads(path.with_suffix(".judged.json").read_text())

    assert code == 0
    assert "UNSUPPORTED claim: capped at 8 hours (not in the chunk)" in out
    assert judged["summary"]["faithfulness_mean"] == 0.5 and judged["summary"]["unsupported_claims"] == 1
    assert judged["judge"] == {"provider": "fake", "model": "m"}


def test_skip_faithfulness_never_builds_the_judge(tmp_path):
    ds = dataset(tmp_path)
    path = answers_file(tmp_path, [record("q1", "answered", "$50 2 free hours", [cit("c")])], ds)

    def factory(environ):
        raise AssertionError("must not build a judge")

    assert runner.main(["judge", str(path), "--dataset", str(ds), "--skip-faithfulness"], environ={}, judge_factory=factory) == 0
    assert json.loads(path.with_suffix(".judged.json").read_text())["judge"] is None


def test_judged_results_are_saved_next_to_the_answers_file(tmp_path):
    _, _, path = judge(tmp_path, [record("q3", "insufficient_context", reason="identifier_not_found")])
    assert path.with_suffix(".judged.json").exists()
    assert path.with_suffix(".judged.json").name == "answers-20261121T000000Z.judged.json"


def test_a_changed_dataset_is_noted_and_an_unknown_id_is_an_error(tmp_path, capsys):
    ds = dataset(tmp_path)
    path = answers_file(tmp_path, [record("q3", "insufficient_context")], ds)
    data = json.loads(path.read_text()); data["dataset_sha256"] = "old"; path.write_text(json.dumps(data))
    assert runner.main(["judge", str(path), "--dataset", str(ds), "--skip-faithfulness"], environ={}) == 0
    assert "dataset changed" in capsys.readouterr().out

    data["questions"][0]["id"] = "q99"; path.write_text(json.dumps(data))
    assert runner.main(["judge", str(path), "--dataset", str(ds), "--skip-faithfulness"], environ={}) == 2


def test_ids_and_limit_apply_to_judging(tmp_path):
    records = [record("q1", "answered", "$50 2 free hours", [cit("c")]), record("q3", "insufficient_context")]
    _, fake, path = judge(tmp_path, records, extra=["--ids", "q3"])
    assert fake.calls == []
    assert [q["id"] for q in json.loads(path.with_suffix(".judged.json").read_text())["questions"]] == ["q3"]
