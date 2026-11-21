import json
import logging

import httpx
import pytest

from freightrag_answer.search import SearchClient, SearchError

KEY = "test-key-not-a-secret-0123456789abcdefghij"


def client(handler, top_k=5):
    return SearchClient("http://api:8080", top_k=top_k, timeout=30.0, connect_timeout=5.0,
                        transport=httpx.MockTransport(handler))


def hit(i, content="Document: rate-confirmation | Load: 48213 | Section: RATE\nbody"):
    return {"chunkId": f"c{i}", "documentId": f"d{i}", "title": f"title-{i}", "docType": "rate-confirmation",
            "chunkIndex": i, "content": content, "distance": 0.2, "vectorRank": i, "keywordRank": None, "fusedScore": 0.01}


def test_the_key_is_forwarded_unchanged_with_hybrid_mode_and_top_k():
    seen = {}

    def handler(request):
        seen["path"], seen["key"], seen["body"] = request.url.path, request.headers["X-Api-Key"], json.loads(request.content)
        return httpx.Response(200, json=[hit(0)])

    client(handler).search(KEY, "What is the rate?")

    assert seen == {"path": "/api/v1/search", "key": KEY, "body": {"query": "What is the rate?", "topK": 5, "mode": "hybrid"}}


def test_results_become_numbered_sources_with_the_section_from_the_header():
    sources = client(lambda r: httpx.Response(200, json=[hit(3), hit(4, content="no header here")])).search(KEY, "q")
    assert [s.number for s in sources] == [1, 2]
    first = sources[0]
    assert (first.chunk_id, first.document_id, first.title, first.section, first.chunk_index) == ("c3", "d3", "title-3", "RATE", 3)
    assert sources[1].section is None


@pytest.mark.parametrize(("upstream", "status"), [(401, 401), (400, 400), (429, 429), (500, 502), (503, 502), (404, 502)])
def test_upstream_statuses_are_mapped(upstream, status):
    with pytest.raises(SearchError) as error:
        client(lambda r: httpx.Response(upstream)).search(KEY, "q")
    assert error.value.status_code == status


def test_retry_after_is_carried_on_a_429():
    with pytest.raises(SearchError) as error:
        client(lambda r: httpx.Response(429, headers={"Retry-After": "7"})).search(KEY, "q")
    assert error.value.retry_after == "7"


@pytest.mark.parametrize(("exc", "status"), [(httpx.ReadTimeout("t"), 504), (httpx.ConnectError("c"), 502)])
def test_transport_failures_are_gateway_errors(exc, status):
    def handler(request):
        raise exc

    with pytest.raises(SearchError) as error:
        client(handler).search(KEY, "q")
    assert error.value.status_code == status


def test_the_key_never_appears_in_errors_or_logs(caplog):
    caplog.set_level(logging.DEBUG)
    with pytest.raises(SearchError) as error:
        client(lambda r: httpx.Response(401)).search(KEY, "q")
    assert KEY not in str(error.value) and KEY not in caplog.text


def test_calls_have_timeouts():
    timeout = client(lambda r: httpx.Response(200, json=[]))._http.timeout
    assert timeout.read == 30.0 and timeout.connect == 5.0


def test_ready_follows_the_search_api_health_endpoint():
    assert client(lambda r: httpx.Response(200 if r.url.path == "/health/ready" else 404)).ready() is True
    assert client(lambda r: httpx.Response(503)).ready() is False
