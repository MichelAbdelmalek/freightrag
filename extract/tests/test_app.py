import asyncio
import logging

from fastapi.testclient import TestClient

from freightrag_extract.app import create_app
from freightrag_extract.config import Settings

SENTINEL = "ZEBRA-OCR-CONTENT"
OK = {"text": f"RATE\nDetention {SENTINEL}", "report": {"pageCount": 1, "ocrPageCount": 1, "textLayerPageCount": 0, "characterCount": 30,
      "meanOcrConfidence": 91.0, "headingCount": 1, "numericCorrections": 0, "lowConfidenceNumericTokens": 0, "durationMs": 10,
      "pages": [], "warnings": []}}
PDF = {"content-type": "application/pdf"}


def client(runner, **settings):
    return TestClient(create_app(Settings(**settings), runner=runner), raise_server_exceptions=False)


async def ok_runner(_data):
    return OK


def test_a_pdf_returns_text_and_report():
    response = client(ok_runner).post("/v1/extract", content=b"%PDF-1.7 x", headers=PDF)
    assert response.status_code == 200 and response.json() == OK


def test_the_runner_receives_exactly_the_body():
    seen = []

    async def runner(data):
        seen.append(data)
        return OK
    client(runner).post("/v1/extract", content=b"%PDF-1.7 body", headers=PDF)
    assert seen == [b"%PDF-1.7 body"]


def test_wrong_content_type_is_415_and_nothing_runs():
    async def runner(_):
        raise AssertionError("must not run")
    response = client(runner).post("/v1/extract", content=b"x", headers={"content-type": "text/plain"})
    assert response.status_code == 415 and response.json()["code"] == "unsupported_media_type"


def test_a_body_over_the_limit_is_413():
    response = client(ok_runner, max_body_bytes=100).post("/v1/extract", content=b"%PDF-" + b"x" * 200, headers=PDF)
    assert response.status_code == 413 and response.json()["code"] == "too_large"


def test_rejections_are_422_problem_details_with_the_code():
    async def runner(_):
        return {"error": "encrypted_pdf"}
    response = client(runner).post("/v1/extract", content=b"%PDF-1.7", headers=PDF)
    assert response.status_code == 422 and response.headers["content-type"].startswith("application/problem+json")
    assert response.json()["code"] == "encrypted_pdf"


def test_an_unknown_error_code_is_reported_as_corrupt_pdf():
    async def runner(_):
        return {"error": "something <odd>"}
    assert client(runner).post("/v1/extract", content=b"%PDF-", headers=PDF).json()["code"] == "corrupt_pdf"


def test_a_second_request_beyond_the_queue_gets_503_with_retry_after():
    started, release = asyncio.Event(), asyncio.Event()

    async def slow(_):
        started.set()
        await release.wait()
        return OK

    async def scenario():
        import httpx
        app = create_app(Settings(queue_limit=0), runner=slow)
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(transport=transport, base_url="http://x") as c:
            first = asyncio.create_task(c.post("/v1/extract", content=b"%PDF-1", headers=PDF))
            await started.wait()
            second = await c.post("/v1/extract", content=b"%PDF-2", headers=PDF)
            release.set()
            return second, await first
    second, first = asyncio.run(scenario())
    assert second.status_code == 503 and second.headers["retry-after"] == "30" and second.json()["code"] == "extractor_busy"
    assert first.status_code == 200


def test_problem_details_and_logs_never_contain_the_text(caplog, network_attempts):
    caplog.set_level(logging.DEBUG)
    c = client(ok_runner)
    c.post("/v1/extract", content=b"%PDF-1.7", headers=PDF)

    async def rejected(_):
        return {"error": "no_text"}
    bad = client(rejected).post("/v1/extract", content=b"%PDF-1.7", headers=PDF)

    assert SENTINEL not in bad.text and SENTINEL not in caplog.text
    assert any("extract status=ok pages=1" in r.getMessage() for r in caplog.records)
    assert network_attempts == []


def test_health():
    assert client(ok_runner).get("/health").json() == {"status": "ok"}
