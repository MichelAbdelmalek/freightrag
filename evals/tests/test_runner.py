import json

import httpx
import pytest

import run_retrieval_eval as runner

NILE_KEY = "nile-key-not-a-secret-0123456789abcdefgh"
DELTA_KEY = "delta-key-not-a-secret-0123456789abcdefg"
ENV = {"FREIGHTRAG_API_KEY_TENANT_NILE_FREIGHT": NILE_KEY, "FREIGHTRAG_API_KEY_TENANT_DELTA_HAUL": DELTA_KEY}

DOCS = {NILE_KEY: ["nile-rate", "nile-handbook"], DELTA_KEY: ["delta-rate"]}
TITLES = {"nile-rate": "rate-confirmation-48213", "nile-handbook": "driver-handbook-excerpt", "delta-rate": "rate-confirmation-77104"}


def chunk(doc, section, distance):
    return {"chunkId": f"{doc}-{section}", "documentId": doc, "title": TITLES[doc], "docType": "x", "chunkIndex": 0,
            "content": f"Document: x | Section: {section}\nbody", "distance": distance}


requests: list[dict] = []


def api(search_results):
    requests.clear()
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.startswith("/health"):  # anonymous, like the real API
            return httpx.Response(200)
        key = request.headers["X-Api-Key"]
        if request.url.path == "/api/v1/documents":
            return httpx.Response(200, json={"items": [{"id": d} for d in DOCS[key]], "page": 1, "pageSize": 100,
                                             "totalCount": len(DOCS[key]), "totalPages": 1})
        body = json.loads(request.content)
        requests.append(body)
        results = search_results[body["query"]]
        return httpx.Response(200, json=results[body["mode"]] if isinstance(results, dict) else results)
    return httpx.MockTransport(handler)


def dataset(tmp_path, rows):
    path = tmp_path / "set.jsonl"
    path.write_text("\n".join(json.dumps(r) for r in rows) + "\n", encoding="utf-8")
    return path


ROWS = [
    {"id": "q1", "tenant": "tenant-nile-freight", "question": "detention rate?", "kind": "answerable", "note": "",
     "expected": [{"document_title": "rate-confirmation-48213", "section": "RATE"}]},
    {"id": "q2", "tenant": "tenant-delta-haul", "question": "what to wear?", "kind": "unanswerable", "note": "", "expected": []},
]


def test_a_full_run_scores_questions_and_writes_results_without_keys(tmp_path, capsys):
    transport = api({
        "detention rate?": [chunk("nile-handbook", "3. DETENTION REPORTING", 0.25), chunk("nile-rate", "RATE", 0.28)],
        "what to wear?": [chunk("delta-rate", "FREIGHT", 0.41)],
    })

    code = runner.main(["--dataset", str(dataset(tmp_path, ROWS)), "--results-dir", str(tmp_path / "results")],
                       environ=ENV, transport=transport)

    assert code == 0
    out = capsys.readouterr().out
    assert "recall@1" in out and "MRR" in out
    [results_file] = (tmp_path / "results").glob("*.json")
    results = json.loads(results_file.read_text())
    assert results["modes"] == ["hybrid"]
    assert results["runs"]["hybrid"]["summary"]["mrr"] == 0.5
    assert results["runs"]["hybrid"]["questions"][1]["top1_distance"] == 0.41
    assert {r["mode"] for r in requests} == {"hybrid"}  # hybrid is the default
    for text in (out, results_file.read_text()):
        assert NILE_KEY not in text and DELTA_KEY not in text
    assert "body" not in results_file.read_text()  # chunk content is not stored


def test_a_chunk_from_another_tenant_fails_loudly(tmp_path, capsys):
    # The API must never do this; the eval is a second line of defence.
    transport = api({
        "detention rate?": [chunk("nile-rate", "RATE", 0.28)],
        "what to wear?": [chunk("nile-handbook", "1. SAFETY EQUIPMENT", 0.2)],  # Nile's handbook returned to Delta!
    })

    code = runner.main(["--dataset", str(dataset(tmp_path, ROWS)), "--results-dir", str(tmp_path / "results")],
                       environ=ENV, transport=transport)

    assert code == 1
    assert "TENANT ISOLATION FAILURE" in capsys.readouterr().out


def test_missing_keys_are_all_named_before_anything_runs(tmp_path, capsys):
    code = runner.main(["--dataset", str(dataset(tmp_path, ROWS)), "--results-dir", str(tmp_path / "r")],
                       environ={}, transport=api({}))

    assert code == 2
    err = capsys.readouterr().err
    assert "FREIGHTRAG_API_KEY_TENANT_NILE_FREIGHT" in err and "FREIGHTRAG_API_KEY_TENANT_DELTA_HAUL" in err


def test_mode_is_sent_to_the_api(tmp_path):
    transport = api({"detention rate?": [chunk("nile-rate", "RATE", 0.28)], "what to wear?": []})

    code = runner.main(["--dataset", str(dataset(tmp_path, ROWS)), "--results-dir", str(tmp_path / "r"), "--mode", "keyword"],
                       environ=ENV, transport=transport)

    assert code == 0
    assert {r["mode"] for r in requests} == {"keyword"}


def test_compare_runs_every_mode_and_reports_them_side_by_side(tmp_path, capsys):
    transport = api({
        "detention rate?": {
            "vector": [chunk("nile-handbook", "3. DETENTION REPORTING", 0.25), chunk("nile-rate", "RATE", 0.28)],
            "keyword": [chunk("nile-rate", "RATE", 0.28)],
            "hybrid": [chunk("nile-rate", "RATE", 0.28)],
        },
        "what to wear?": {"vector": [], "keyword": [], "hybrid": []},
    })

    code = runner.main(["--dataset", str(dataset(tmp_path, ROWS)), "--results-dir", str(tmp_path / "r"), "--compare"],
                       environ=ENV, transport=transport)

    assert code == 0
    out = capsys.readouterr().out
    assert "vector" in out and "keyword" in out and "hybrid" in out
    rank_row = next(line for line in out.splitlines() if line.startswith("q1 "))
    assert rank_row.split()[2:5] == ["2", "1", "1"]  # per-question rank in vector, keyword, hybrid
    mrr_row = next(line for line in out.splitlines() if line.startswith("MRR"))
    assert mrr_row.split()[1:] == ["0.500", "1.000", "1.000"]
    [results_file] = (tmp_path / "r").glob("*.json")
    results = json.loads(results_file.read_text())
    assert results["modes"] == ["vector", "keyword", "hybrid"]
    assert results["runs"]["keyword"]["summary"]["recall_at_1"] == 1.0


def test_mode_and_compare_cannot_be_combined(tmp_path):
    with pytest.raises(SystemExit):
        runner.main(["--dataset", str(dataset(tmp_path, ROWS)), "--mode", "vector", "--compare"], environ=ENV, transport=api({}))
