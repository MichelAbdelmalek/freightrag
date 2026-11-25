import logging

import pytest
from fastapi.testclient import TestClient

from freightrag_answer.app import create_app
from freightrag_answer.config import Settings
from freightrag_answer.llm import ContextWindowExceeded, LlmError
from freightrag_answer.search import SearchError
from tests.fakes import FakeLlm, FakeSearch, reply_json, source

KEY = "k-secret-0123456789-not-real"
QUESTION = "What is the detention rate for load 48213?"
SECRET_CONTENT = "Document: rate-confirmation | Load: 48213 | Section: RATE\nDetention: $50.00 per hour, ZEBRA-CONTENT"
SECRET_ANSWER = "Detention is $50.00 per hour, PELICAN-ANSWER [1]."

SOURCES = [source(1, "rate-confirmation-48213", "RATE", chunk_index=2, content=SECRET_CONTENT, document_id="doc-a")]


def make(search=None, llm=None):
    search = search or FakeSearch(SOURCES)
    llm = llm or FakeLlm(reply_json(answer=SECRET_ANSWER, citations=[1]))
    app = create_app(Settings.from_environ({}), search=search, llm=llm)
    return TestClient(app, raise_server_exceptions=False), search, llm


def ask(client, question=QUESTION, key=KEY, **kwargs):
    headers = {"X-Api-Key": key} if key is not None else {}
    return client.post("/api/v1/answer", json={"question": question}, headers=headers, **kwargs)


def test_an_answered_question_returns_status_answer_and_mapped_citations():
    client, _, _ = make()
    response = ask(client)

    assert response.status_code == 200
    body = response.json()
    diagnostics = body.pop("diagnostics")
    assert body == {
        "status": "answered",
        "answer": SECRET_ANSWER,
        "citations": [{"source": 1, "documentId": "doc-a", "title": "rate-confirmation-48213", "section": "RATE", "chunkIndex": 2}],
    }
    assert diagnostics["reason"] is None and diagnostics["chunkCount"] == 1
    assert all(isinstance(diagnostics[k], int) and diagnostics[k] >= 0 for k in ("searchMs", "llmMs", "totalMs"))


def test_q39_returns_insufficient_context_and_never_calls_the_model():
    client, _, llm = make()
    response = ask(client, "What is the detention rate for load 48999?")

    assert response.status_code == 200
    body = response.json()
    assert (body["status"], body["answer"], body["citations"]) == ("insufficient_context", None, [])
    # diagnostics carry a category and numbers, never content; llmMs 0 shows the model was not called
    assert body["diagnostics"]["reason"] == "identifier_not_found" and body["diagnostics"]["llmMs"] == 0
    assert "48999" not in response.text
    assert llm.calls == []


def test_the_key_is_forwarded_and_the_question_is_stripped():
    client, search, _ = make()
    ask(client, "  " + QUESTION + "  ")
    assert search.calls == [(KEY, QUESTION)]


def test_a_missing_key_is_401_and_nothing_is_called():
    client, search, llm = make()
    for response in (ask(client, key=None), ask(client, key="   ")):
        assert response.status_code == 401
        assert response.headers["content-type"].startswith("application/problem+json")
    assert search.calls == [] and llm.calls == []


@pytest.mark.parametrize("body", [
    {"question": ""}, {"question": "   "}, {"question": "x" * 1001}, {}, {"question": 5},
    {"question": "ok", "tenant": "tenant-nile-freight"},       # a caller cannot pick a tenant
    {"question": "ok", "topK": 50},
])
def test_invalid_bodies_are_400_without_echoing_the_input(body):
    client, search, _ = make()
    response = client.post("/api/v1/answer", json=body, headers={"X-Api-Key": KEY})
    assert response.status_code == 400
    assert response.headers["content-type"].startswith("application/problem+json")
    assert "xxxx" not in response.text and "tenant-nile" not in response.text
    assert search.calls == []


def test_a_malformed_json_body_is_400():
    client, _, _ = make()
    response = client.post("/api/v1/answer", content=b"{nope", headers={"X-Api-Key": KEY, "Content-Type": "application/json"})
    assert response.status_code == 400


def test_a_body_over_the_limit_is_413():
    client, search, _ = make()
    assert ask(client).status_code == 200                                    # a normal request is fine
    response = client.post("/api/v1/answer", json={"question": "x" * 20_000}, headers={"X-Api-Key": KEY})
    assert response.status_code == 413
    assert response.headers["content-type"].startswith("application/problem+json")
    assert len(search.calls) == 1                                            # the oversized one never reached search


def test_a_chunked_body_over_the_limit_is_413_too():
    client, _, _ = make()

    def chunks():
        for _ in range(5):
            yield b'{"question": "' + b"x" * 5000 + b'"}'

    response = client.post("/api/v1/answer", content=chunks(), headers={"X-Api-Key": KEY, "Content-Type": "application/json"})
    assert response.status_code == 413


def test_upstream_search_errors_are_passed_on():
    client, _, _ = make(search=FakeSearch(error=SearchError(401, "The search API rejected the API key.")))
    assert ask(client).status_code == 401

    client, _, _ = make(search=FakeSearch(error=SearchError(429, "Rate limited.", retry_after="7")))
    response = ask(client)
    assert response.status_code == 429 and response.headers["Retry-After"] == "7"

    client, _, _ = make(search=FakeSearch(error=SearchError(502, "Search API unavailable.")))
    assert ask(client).status_code == 502


def test_llm_errors_are_gateway_errors():
    client, _, _ = make(llm=FakeLlm(error=LlmError(504, "The model timed out.")))
    assert ask(client).status_code == 504


def test_a_prompt_that_does_not_fit_is_a_clear_500():
    client, _, _ = make(llm=FakeLlm(error=ContextWindowExceeded(9000, 8192)))
    response = ask(client)
    assert response.status_code == 500
    assert "context window" in response.json()["detail"]
    assert "ZEBRA" not in response.text


def test_an_unexpected_error_is_a_generic_500():
    client, _, _ = make(search=FakeSearch(error=RuntimeError(f"boom {KEY} {QUESTION}")))
    response = ask(client)
    assert response.status_code == 500
    assert KEY not in response.text and QUESTION not in response.text


def test_q38_the_cited_chunk_check_is_reported_as_a_category_and_never_leaks_content(caplog):
    wrong_load = source(1, "bill-of-lading-HOU-221988", "DELIVERY RECEIPT (PROOF OF DELIVERY)",
                        content="Document: bill-of-lading | Load: 77139 | Section: DELIVERY RECEIPT\nReceived by: Marcus Bell, ZEBRA-CONTENT")
    other = source(2, "rate-confirmation-77118", "RATE", content="Document: rate-confirmation | Load: 77118 | Section: RATE\nx")
    client, _, _ = make(search=FakeSearch([wrong_load, other]), llm=FakeLlm(reply_json(answer="Marcus Bell, PELICAN-ANSWER [1].", citations=[1])))
    caplog.set_level(logging.DEBUG)

    response = ask(client, "Who received load 77118?")

    body = response.json()
    assert (body["status"], body["answer"], body["citations"]) == ("insufficient_context", None, [])
    assert body["diagnostics"]["reason"] == "cited_chunk_lacks_identifier"
    for secret in (KEY, "Who received load 77118", "77118", "ZEBRA-CONTENT", "PELICAN-ANSWER", "Marcus Bell"):
        assert secret not in caplog.text and secret not in response.text.replace('"cited_chunk_lacks_identifier"', ""), secret
    assert any("reason=cited_chunk_lacks_identifier" in r.getMessage() for r in caplog.records)


def test_health_endpoints():
    client, search, llm = make()
    assert client.get("/health/live").status_code == 200
    assert client.get("/health/ready").status_code == 200
    llm.ready_result = False
    assert client.get("/health/ready").status_code == 503
    llm.ready_result, search.ready_result = True, False
    assert client.get("/health/ready").status_code == 503


def test_nothing_sensitive_is_logged_but_the_summary_line_is(caplog):
    caplog.set_level(logging.DEBUG)
    client, _, _ = make()

    ask(client)
    ask(client, "What is the detention rate for load 48999?")                      # the guard path too
    make(search=FakeSearch(error=SearchError(401, "rejected")))[0].post(
        "/api/v1/answer", json={"question": QUESTION}, headers={"X-Api-Key": KEY})

    for secret in (KEY, QUESTION, "48999", "ZEBRA-CONTENT", "PELICAN-ANSWER", SECRET_CONTENT):
        assert secret not in caplog.text, secret
    lines = [r.getMessage() for r in caplog.records if r.getMessage().startswith("answer ")]
    assert any("status=answered" in l and "reason=none" in l and "chunks=1" in l and "citations=1" in l
               and "search_ms=" in l and "llm_ms=" in l and "total_ms=" in l for l in lines), lines
    assert any("status=insufficient_context" in l and "reason=identifier_not_found" in l for l in lines)
