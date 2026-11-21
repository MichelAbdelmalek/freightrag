import hashlib
import json

import run_answer_eval as runner
from tests.support import (DELTA_KEY, ENV, NILE_KEY, ROWS, abstained, answered, citation, dataset, services)

REPLIES = {
    "detention rate?": answered("Detention is $50.00 per hour after 2 free hours [1].", citation("doc-n", 0, title="rate-48213", section="RATE")),
    "reefer temperature?": answered("Keep it at -10 degrees F [1].", citation("doc-d", 0, title="rate-77104", section="FREIGHT")),
    "fuel card policy?": abstained(),
}


def generate(tmp_path, transport, extra=(), environ=ENV, rows=ROWS):
    return runner.main(["generate", "--dataset", str(dataset(tmp_path, rows)), "--results-dir", str(tmp_path / "r"), *extra],
                       environ=environ, transport=transport)


def saved(tmp_path):
    [path] = (tmp_path / "r").glob("answers-*.json")
    return path, json.loads(path.read_text())


def test_generate_saves_answers_citations_cited_chunk_text_and_diagnostics(tmp_path, capsys):
    requests = []
    code = generate(tmp_path, services(REPLIES, requests))

    assert code == 0
    path, payload = saved(tmp_path)
    q1, q2, q3 = payload["questions"]
    assert (q1["id"], q1["status"], q1["reason"], q1["llmMs"], q1["chunkCount"]) == ("q1", "answered", None, 900, 5)
    assert q1["citations"][0]["chunk_text"] == "CHUNK-N0 header\nDetention: $50.00 per hour"   # fetched by document + index
    assert q2["citations"][0]["chunk_text"].startswith("CHUNK-D0")
    assert (q3["status"], q3["reason"], q3["citations"]) == ("insufficient_context", "identifier_not_found", [])
    assert payload["dataset_sha256"] == hashlib.sha256((tmp_path / "set.jsonl").read_bytes()).hexdigest()
    assert path.name.startswith("answers-") and path.name.endswith("Z.json")


def test_each_tenants_key_is_used_for_its_questions_and_never_written_anywhere(tmp_path, capsys):
    requests = []
    generate(tmp_path, services(REPLIES, requests))

    sent = {q: k for kind, k, q in requests if kind == "answer"}
    assert sent["detention rate?"] == NILE_KEY and sent["reefer temperature?"] == DELTA_KEY
    out = capsys.readouterr()
    path, _ = saved(tmp_path)
    for text in (out.out, out.err, path.read_text()):
        assert NILE_KEY not in text and DELTA_KEY not in text


def test_a_citation_to_a_document_the_tenant_cannot_see_is_an_isolation_failure(tmp_path, capsys):
    replies = {**REPLIES, "detention rate?": answered("x [1]", citation("doc-d", 0, title="delta-doc"))}   # Delta's doc, asked as Nile
    requests = []
    code = generate(tmp_path, services(replies, requests))

    assert code == 1
    assert "TENANT ISOLATION FAILURE" in capsys.readouterr().out
    _, payload = saved(tmp_path)
    assert payload["questions"][0]["citations"][0]["chunk_text"] is None
    assert ("doc", NILE_KEY, "doc-d") not in requests      # the foreign document is never even fetched
    assert payload["isolation_failures"]


def test_ids_and_limit_select_questions(tmp_path):
    requests = []
    assert generate(tmp_path, services(REPLIES, requests), ["--ids", "q2,q3"]) == 0
    assert [q["id"] for q in saved(tmp_path)[1]["questions"]] == ["q2", "q3"]


def test_limit_takes_the_first_n(tmp_path):
    assert generate(tmp_path, services(REPLIES), ["--limit", "1"]) == 0
    assert [q["id"] for q in saved(tmp_path)[1]["questions"]] == ["q1"]


def test_an_unknown_id_is_a_setup_error(tmp_path, capsys):
    assert generate(tmp_path, services(REPLIES), ["--ids", "q99"]) == 2
    assert "q99" in capsys.readouterr().err


def test_a_failing_question_is_recorded_and_the_run_continues(tmp_path):
    code = generate(tmp_path, services(REPLIES, answer_status={"detention rate?": 500}))

    assert code == 0
    _, payload = saved(tmp_path)
    assert payload["questions"][0]["status"] == "error" and payload["questions"][0]["http_status"] == 500
    assert payload["questions"][1]["status"] == "answered"


def test_an_answer_service_that_is_not_ready_is_a_setup_error(tmp_path, capsys):
    assert generate(tmp_path, services(REPLIES, ready=False)) == 2
    assert "not ready" in capsys.readouterr().err


def test_missing_keys_are_named(tmp_path, capsys):
    assert generate(tmp_path, services(REPLIES), environ={}) == 2
    err = capsys.readouterr().err
    assert "FREIGHTRAG_API_KEY_TENANT_NILE_FREIGHT" in err and "FREIGHTRAG_API_KEY_TENANT_DELTA_HAUL" in err


# --- checkpoints and resume: a long run on flaky infrastructure must not lose what it already has ---

def run_args(tmp_path, *extra, transport, rows=ROWS):
    return runner.main(["generate", "--dataset", str(dataset(tmp_path, rows)), "--results-dir", str(tmp_path / "r"), *extra],
                       environ=ENV, transport=transport)


def test_the_file_is_saved_after_every_question_so_a_crash_keeps_the_partial_run(tmp_path):
    import httpx
    import pytest

    inner = services(REPLIES)

    def crashing(request):
        if request.url.path == "/api/v1/answer" and json.loads(request.content)["question"] == "reefer temperature?":
            raise RuntimeError("the machine fell over")
        return inner.handle_request(request)

    with pytest.raises(RuntimeError):
        run_args(tmp_path, transport=httpx.MockTransport(crashing))

    _, payload = saved(tmp_path)
    assert [q["id"] for q in payload["questions"]] == ["q1"]          # q1 survived
    assert payload["complete"] is False


def test_a_finished_run_is_marked_complete(tmp_path):
    assert run_args(tmp_path, transport=services(REPLIES)) == 0
    assert saved(tmp_path)[1]["complete"] is True


def test_resume_asks_only_the_failed_questions_and_merges_into_the_same_file(tmp_path):
    assert run_args(tmp_path, transport=services(REPLIES, answer_status={"detention rate?": 500})) == 0
    path, first = saved(tmp_path)
    assert first["questions"][0]["status"] == "error"

    requests = []
    assert run_args(tmp_path, "--resume", str(path), transport=services(REPLIES, requests)) == 0

    asked = [q for kind, _, q in requests if kind == "answer"]
    assert asked == ["detention rate?"]                              # q2 and q3 were kept, not re-asked
    _, merged = saved(tmp_path)
    assert [q["id"] for q in merged["questions"]] == ["q1", "q2", "q3"]
    assert [q["status"] for q in merged["questions"]] == ["answered", "answered", "insufficient_context"]
    assert merged["complete"] is True
    assert len(list((tmp_path / "r").glob("answers-*.json"))) == 1   # updated in place, no second file


def test_resume_fills_in_questions_a_crashed_run_never_reached(tmp_path):
    import httpx
    import pytest

    inner = services(REPLIES)
    crash = {"on": True}

    def flaky(request):
        if crash["on"] and request.url.path == "/api/v1/answer" and json.loads(request.content)["question"] == "reefer temperature?":
            raise RuntimeError("down")
        return inner.handle_request(request)

    with pytest.raises(RuntimeError):
        run_args(tmp_path, transport=httpx.MockTransport(flaky))
    path, _ = saved(tmp_path)

    crash["on"] = False
    assert run_args(tmp_path, "--resume", str(path), transport=httpx.MockTransport(flaky)) == 0
    assert [q["id"] for q in saved(tmp_path)[1]["questions"]] == ["q1", "q2", "q3"]


def test_resume_re_asks_a_question_whose_text_changed(tmp_path):
    assert run_args(tmp_path, transport=services(REPLIES)) == 0
    path, _ = saved(tmp_path)
    edited = [{**ROWS[0], "question": "what is the detention rate?"}, *ROWS[1:]]
    replies = {**REPLIES, "what is the detention rate?": REPLIES["detention rate?"]}

    requests = []
    assert run_args(tmp_path, "--resume", str(path), transport=services(replies, requests), rows=edited) == 0
    assert [q for kind, _, q in requests if kind == "answer"] == ["what is the detention rate?"]


def test_resume_of_a_clean_complete_file_asks_nothing(tmp_path):
    assert run_args(tmp_path, transport=services(REPLIES)) == 0
    path, _ = saved(tmp_path)
    requests = []
    assert run_args(tmp_path, "--resume", str(path), transport=services(REPLIES, requests)) == 0
    assert [r for r in requests if r[0] == "answer"] == []


def test_resume_keeps_earlier_isolation_failures_for_kept_questions(tmp_path, capsys):
    replies = {**REPLIES, "detention rate?": answered("x [1]", citation("doc-d", 0, title="delta-doc"))}   # isolation failure on q1
    assert run_args(tmp_path, transport=services(replies)) == 1
    path, first = saved(tmp_path)
    assert first["isolation_failures"]
    capsys.readouterr()

    code = run_args(tmp_path, "--resume", str(path), transport=services(replies))
    assert code == 1 and saved(tmp_path)[1]["isolation_failures"] == first["isolation_failures"]


def test_judging_an_incomplete_file_says_so(tmp_path, capsys):
    import httpx
    import pytest

    inner = services(REPLIES)

    def crashing(request):
        if request.url.path == "/api/v1/answer" and json.loads(request.content)["question"] == "reefer temperature?":
            raise RuntimeError("x")
        return inner.handle_request(request)

    with pytest.raises(RuntimeError):
        run_args(tmp_path, transport=httpx.MockTransport(crashing))
    path, _ = saved(tmp_path)
    capsys.readouterr()

    assert runner.main(["judge", str(path), "--dataset", str(tmp_path / "set.jsonl"), "--skip-faithfulness"], environ={}) == 0
    assert "incomplete" in capsys.readouterr().out
