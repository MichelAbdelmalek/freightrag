"""One pass of search -> guard -> build_prompt -> generate -> validate -> cited-chunk check, as plain values.

`run_attempt` takes the original question and a separate search query. Only the search sees the query: the guard, the
prompt and the cited-chunk check all use the original question, so a rewritten query can change what is retrieved but
never what is asked or which identifiers must be present. The caller's API key is never an argument: it is already
inside `search_once`.
"""

import time
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import Protocol

from freightrag_answer.guard import missing_identifiers, uncited_identifiers
from freightrag_answer.llm import RESPONSE_SCHEMA, LlmReply, truncation_reason
from freightrag_answer.models import Source
from freightrag_answer.prompt import build_messages
from freightrag_answer.validate import validate


class SearchPort(Protocol):
    def search(self, key: str, question: str) -> list[Source]: ...


class LlmPort(Protocol):
    num_ctx: int

    def generate(self, messages: list[dict[str, str]], schema: dict | None = RESPONSE_SCHEMA) -> LlmReply: ...


@dataclass(frozen=True)
class Citation:
    source: int
    document_id: str
    title: str
    section: str | None
    chunk_index: int


@dataclass(frozen=True)
class Attempt:
    status: str                       # "answered" | "insufficient_context"
    answer: str | None
    citations: list[Citation] = field(default_factory=list)
    reason: str | None = None
    chunk_count: int = 0
    search_ms: int = 0
    llm_ms: int = 0
    model_called: bool = False


def _ms(start: float, end: float) -> int:
    return int((end - start) * 1000)


def run_attempt(question: str, query: str, search_once: Callable[[str], list[Source]], llm: LlmPort) -> Attempt:
    started = time.perf_counter()
    sources = search_once(query)
    searched = time.perf_counter()
    base = {"chunk_count": len(sources), "search_ms": _ms(started, searched)}

    def abstain(reason: str, called: bool = False) -> Attempt:
        return Attempt("insufficient_context", None, [], reason, llm_ms=_ms(searched, time.perf_counter()) if called else 0,
                       model_called=called, **base)

    if not sources:
        return abstain("no_sources")
    if missing_identifiers(question, sources):
        return abstain("identifier_not_found")

    reply = llm.generate(build_messages(question, sources))

    truncated = truncation_reason(reply, llm.num_ctx)
    if truncated:
        return abstain(truncated, True)

    verdict = validate(reply.text, len(sources))
    if verdict.status != "answered":
        return abstain(verdict.reason or "invalid", True)

    by_number = {s.number: s for s in sources}
    if uncited_identifiers(question, [by_number[n] for n in verdict.citations]):
        return abstain("cited_chunk_lacks_identifier", True)    # e.g. it cited load 77139's chunk for a load 77118 question
    citations = [Citation(n, by_number[n].document_id, by_number[n].title, by_number[n].section, by_number[n].chunk_index)
                 for n in verdict.citations]
    return Attempt("answered", verdict.answer, citations, None, llm_ms=_ms(searched, time.perf_counter()), model_called=True, **base)
