"""The HTTP layer. Run with: uvicorn --factory freightrag_answer.app:create_app

Privacy rules, enforced by what this module chooses to log: never the question, the answer, chunk content or the
API key. One summary line per request carries only categories, counts and timings.
"""

import http
import logging
import os
from contextlib import asynccontextmanager
from typing import Annotated

from fastapi import FastAPI, Header, HTTPException, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel
from starlette.exceptions import HTTPException as StarletteHTTPException

from freightrag_answer.config import Settings
from freightrag_answer.llm import ContextWindowExceeded, LlmError, OllamaChat
from freightrag_answer.pipeline import AnswerResult, answer_question
from freightrag_answer.search import SearchClient, SearchError

log = logging.getLogger("freightrag_answer")

MAX_BODY_BYTES = 16 * 1024
MAX_QUESTION_LENGTH = 1000


class AnswerRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)   # a caller cannot send a tenant or a topK

    question: str = Field(min_length=1, max_length=MAX_QUESTION_LENGTH)


class CitationResponse(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)

    source: int
    document_id: str
    title: str
    section: str | None
    chunk_index: int


class AnswerResponse(BaseModel):
    status: str
    answer: str | None
    citations: list[CitationResponse]


def problem(status: int, detail: str, headers: dict[str, str] | None = None) -> JSONResponse:
    """RFC 9457 problem details, like the .NET API."""
    return JSONResponse({"type": "about:blank", "title": http.HTTPStatus(status).phrase, "status": status, "detail": detail},
                        status_code=status, media_type="application/problem+json", headers=headers)


class BodyLimit:
    """Rejects bodies over max_bytes with 413: by Content-Length up front, and by counting bytes for chunked uploads."""

    def __init__(self, app, max_bytes: int):
        self.app = app
        self.max_bytes = max_bytes

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http":
            return await self.app(scope, receive, send)

        declared = dict(scope["headers"]).get(b"content-length", b"")
        if declared.isdigit() and int(declared) > self.max_bytes:
            return await self._too_large()(scope, receive, send)

        received = 0

        async def limited_receive():
            nonlocal received
            message = await receive()
            if message["type"] == "http.request":
                received += len(message.get("body", b""))
                if received > self.max_bytes:
                    raise HTTPException(413)   # FastAPI's own class: it re-raises this instead of turning it into a 400
            return message

        await self.app(scope, limited_receive, send)

    def _too_large(self) -> JSONResponse:
        return problem(413, f"The request body is limited to {self.max_bytes // 1024} KB.")


def _summary(result: AnswerResult) -> str:
    return (f"answer status={result.status} reason={result.reason or 'none'} chunks={result.chunk_count} "
            f"citations={len(result.citations)} search_ms={result.search_ms} llm_ms={result.llm_ms} total_ms={result.total_ms}")


def _configure_logging() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    for noisy in ("httpx", "httpcore"):
        logging.getLogger(noisy).setLevel(logging.WARNING)


def create_app(settings: Settings | None = None, search=None, llm=None) -> FastAPI:
    """Tests pass fakes for search and llm; production builds the real clients from the environment."""
    _configure_logging()
    settings = settings or Settings.from_environ(os.environ)
    owned = []
    if search is None:
        search = SearchClient(settings.search_api_url, settings.top_k, settings.search_timeout_s, settings.connect_timeout_s)
        owned.append(search)
    if llm is None:
        llm = OllamaChat(settings.ollama_url, settings.chat_model, settings.num_ctx, settings.num_predict,
                         settings.llm_timeout_s, settings.connect_timeout_s)
        owned.append(llm)

    @asynccontextmanager
    async def lifespan(_app):
        yield
        for client in owned:
            client.close()

    app = FastAPI(title="FreightRAG answer service", lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None)
    app.add_middleware(BodyLimit, max_bytes=MAX_BODY_BYTES)

    @app.exception_handler(RequestValidationError)
    async def invalid_request(_request: Request, _error: RequestValidationError):
        # Fixed text on purpose: pydantic's own messages echo the input, and the input is the user's question.
        return problem(400, f"Send a JSON object with only a 'question' of 1-{MAX_QUESTION_LENGTH} characters.")

    @app.exception_handler(StarletteHTTPException)
    async def http_error(_request: Request, error: StarletteHTTPException):
        return problem(error.status_code, str(error.detail) if error.status_code != 413
                       else f"The request body is limited to {MAX_BODY_BYTES // 1024} KB.", headers=error.headers)

    @app.post("/api/v1/answer", response_model=AnswerResponse, response_model_by_alias=True)
    def answer(request: AnswerRequest, x_api_key: Annotated[str | None, Header()] = None):
        if x_api_key is None or not x_api_key.strip():
            return problem(401, "Send your API key in the X-Api-Key header.")
        try:
            result = answer_question(request.question, x_api_key, search, llm)
        except SearchError as error:
            log.warning("answer failed category=search status=%d", error.status_code)
            return problem(error.status_code, str(error), headers={"Retry-After": error.retry_after} if error.retry_after else None)
        except LlmError as error:
            log.warning("answer failed category=llm status=%d", error.status_code)
            return problem(error.status_code, str(error))
        except ContextWindowExceeded as error:
            log.warning("answer failed category=context_window needed=%d window=%d", error.needed_tokens, error.num_ctx)
            return problem(500, str(error))
        except Exception as error:   # noqa: BLE001 - last resort. Only the class name is logged: messages can hold content.
            log.error("answer failed category=unexpected error=%s", type(error).__name__)
            return problem(500, "The answer service failed unexpectedly.")

        log.info(_summary(result))
        return AnswerResponse(
            status=result.status, answer=result.answer,
            citations=[CitationResponse(source=c.source, document_id=c.document_id, title=c.title, section=c.section,
                                        chunk_index=c.chunk_index) for c in result.citations])

    @app.get("/health/live")
    def live():
        return {"status": "ok"}

    @app.get("/health/ready")
    def ready():
        search_ok, llm_ok = search.ready(), llm.ready()
        if search_ok and llm_ok:
            return {"status": "ok"}
        return problem(503, f"Not ready: search API {'ok' if search_ok else 'unavailable'}, "
                            f"model {'ok' if llm_ok else 'unavailable'}.")

    return app
