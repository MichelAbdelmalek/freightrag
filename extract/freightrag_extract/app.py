"""The extractor's HTTP layer (internal only). Run with: uvicorn --factory freightrag_extract.app:create_app

POST /v1/extract with Content-Type: application/pdf and the raw bytes. It takes no tenant and no file name.
Privacy: one summary line per request with a code, counts and timings. Never document text, never the bytes.
"""

import asyncio
import http
import json
import logging
import os
import sys
from collections.abc import Awaitable, Callable

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

from freightrag_extract.config import Settings

log = logging.getLogger("freightrag_extract")

Runner = Callable[[bytes], Awaitable[dict]]

STATUS_FOR_CODE = {"not_pdf": 422, "corrupt_pdf": 422, "encrypted_pdf": 422, "too_many_pages": 422, "page_too_large": 422,
                   "no_text": 422, "unreadable": 422, "extraction_limit_exceeded": 422}


def problem(status: int, code: str, detail: str, headers: dict[str, str] | None = None) -> JSONResponse:
    """RFC 9457 problem details with a machine-readable code. The detail is fixed text, never document content."""
    return JSONResponse({"type": "about:blank", "title": http.HTTPStatus(status).phrase, "status": status, "detail": detail, "code": code},
                        status_code=status, media_type="application/problem+json", headers=headers)


class BodyLimit:
    """Rejects bodies over max_bytes with 413: by Content-Length up front, and by counting bytes for chunked uploads."""

    def __init__(self, app, max_bytes: int):
        self.app, self.max_bytes = app, max_bytes

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http":
            return await self.app(scope, receive, send)
        declared = dict(scope["headers"]).get(b"content-length", b"")
        if declared.isdigit() and int(declared) > self.max_bytes:
            return await problem(413, "too_large", "The PDF is too large.")(scope, receive, send)
        received = 0

        async def limited_receive():
            nonlocal received
            message = await receive()
            if message["type"] == "http.request":
                received += len(message.get("body", b""))
                if received > self.max_bytes:
                    raise HTTPException(413)
            return message

        await self.app(scope, limited_receive, send)


async def run_worker(data: bytes, settings: Settings, command: list[str] | None = None, deadline: float | None = None) -> dict:
    """Runs the worker process. A timeout, a crash or a limit kill all become extraction_limit_exceeded."""
    command = command or [sys.executable, "-E", "-s", "-m", "freightrag_extract.worker"]
    env = {"PATH": "/usr/local/bin:/usr/bin:/bin", "OMP_THREAD_LIMIT": "1", "LC_ALL": "C",
           "TMPDIR": os.environ.get("TMPDIR", "/tmp"), **settings.to_environ()}
    process = await asyncio.create_subprocess_exec(*command, stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
                                                   stderr=asyncio.subprocess.DEVNULL, env=env)
    try:
        out, _ = await asyncio.wait_for(process.communicate(data), timeout=deadline or settings.request_deadline_s)
    except TimeoutError:
        process.kill()
        await process.wait()
        return {"error": "extraction_limit_exceeded"}
    if process.returncode != 0:
        return {"error": "extraction_limit_exceeded"}
    try:
        payload = json.loads(out)
    except ValueError:
        return {"error": "extraction_limit_exceeded"}
    return payload if isinstance(payload, dict) else {"error": "extraction_limit_exceeded"}


def create_app(settings: Settings | None = None, runner: Runner | None = None) -> FastAPI:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    settings = settings or Settings.from_environ(os.environ)
    runner = runner or (lambda data: run_worker(data, settings))
    gate = asyncio.Semaphore(1)       # OCR is slow and memory-hungry: one PDF at a time
    waiting = 0

    app = FastAPI(title="FreightRAG PDF extractor", docs_url=None, redoc_url=None, openapi_url=None)
    app.add_middleware(BodyLimit, max_bytes=settings.max_body_bytes)

    @app.exception_handler(StarletteHTTPException)
    async def http_error(_request: Request, error: StarletteHTTPException):
        if error.status_code == 413:
            return problem(413, "too_large", "The PDF is too large.")
        return problem(error.status_code, "http_error", "The request could not be processed.")

    @app.post("/v1/extract")
    async def extract(request: Request):
        nonlocal waiting
        if request.headers.get("content-type", "").split(";")[0].strip().lower() != "application/pdf":
            return problem(415, "unsupported_media_type", "Send the PDF bytes with Content-Type: application/pdf.")
        data = await request.body()

        if gate.locked():
            if waiting >= settings.queue_limit:
                log.info("extract status=busy")
                return problem(503, "extractor_busy", "Another PDF is being read.", headers={"Retry-After": "30"})
            waiting += 1
            try:
                await gate.acquire()
            finally:
                waiting -= 1
        else:
            await gate.acquire()
        try:
            payload = await runner(data)
        finally:
            gate.release()

        if "error" in payload:
            code = str(payload["error"])
            log.info("extract status=rejected code=%s", code)
            return problem(STATUS_FOR_CODE.get(code, 422), code if code in STATUS_FOR_CODE else "corrupt_pdf", "The PDF could not be read.")
        report = payload["report"]
        log.info("extract status=ok pages=%d ocr_pages=%d confidence=%s chars=%d ms=%d", report["pageCount"], report["ocrPageCount"],
                 report["meanOcrConfidence"], report["characterCount"], report["durationMs"])
        return payload

    @app.get("/health")
    def health():
        return {"status": "ok"}

    return app
