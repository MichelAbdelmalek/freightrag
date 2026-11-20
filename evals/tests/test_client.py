import json

import httpx
import pytest

from freightrag_eval.client import ApiError, MissingKeyError, SearchClient, api_key, env_var_for

KEY = "test-key-not-a-secret-0123456789abcdefghij"


def test_env_var_name_is_derived_from_the_tenant_slug():
    assert env_var_for("tenant-nile-freight") == "FREIGHTRAG_API_KEY_TENANT_NILE_FREIGHT"


def test_reads_the_key_from_the_environment():
    assert api_key("tenant-nile-freight", {"FREIGHTRAG_API_KEY_TENANT_NILE_FREIGHT": KEY}) == KEY


def test_missing_key_names_the_variable():
    with pytest.raises(MissingKeyError, match="FREIGHTRAG_API_KEY_TENANT_DELTA_HAUL"):
        api_key("tenant-delta-haul", {})


def test_search_sends_the_key_header_and_body():
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["key"] = request.headers["X-Api-Key"]
        seen["body"] = json.loads(request.content)
        return httpx.Response(200, json=[{"chunkId": "c", "documentId": "d", "title": "t", "docType": "invoice",
                                          "chunkIndex": 0, "content": "Document: invoice | Section: S\nx", "distance": 0.1}])

    client = SearchClient("http://api:8080", transport=httpx.MockTransport(handler))
    hits = client.search(KEY, "What is the rate?", top_k=5)

    assert seen == {"key": KEY, "body": {"query": "What is the rate?", "topK": 5}}
    assert hits[0].rank == 1 and hits[0].section == "S" and hits[0].document_id == "d"


def test_lists_all_document_ids_across_pages():
    def handler(request: httpx.Request) -> httpx.Response:
        page = int(request.url.params["page"])
        items = [{"id": f"doc-{page}"}]
        return httpx.Response(200, json={"items": items, "page": page, "pageSize": 100, "totalCount": 2, "totalPages": 2})

    client = SearchClient("http://api:8080", transport=httpx.MockTransport(handler))
    assert client.document_ids(KEY) == {"doc-1", "doc-2"}


def test_api_errors_never_contain_the_key():
    client = SearchClient("http://api:8080", transport=httpx.MockTransport(lambda r: httpx.Response(401)))
    with pytest.raises(ApiError) as error:
        client.search(KEY, "q", top_k=5)
    assert "401" in str(error.value)
    assert KEY not in str(error.value)
