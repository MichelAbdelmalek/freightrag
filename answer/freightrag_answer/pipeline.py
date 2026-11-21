"""The answer pipeline: search -> guard -> build_prompt -> generate -> validate.

Each step is a small pure function in its own module (guard.missing_identifiers, prompt.build_messages,
llm.OllamaChat.generate, validate.validate); this function only wires them. That is deliberate: when the flow grows
branches (retry with a rewritten question, ask for clarification), each step becomes a LangGraph node and this
function becomes the graph.
"""

import time
from dataclasses import dataclass, field
from typing import Protocol

from freightrag_answer.guard import missing_identifiers
from freightrag_answer.llm import LlmReply, truncation_reason
from freightrag_answer.models import Source
from freightrag_answer.prompt import build_messages
from freightrag_answer.validate import validate


class SearchPort(Protocol):
    def search(self, key: str, question: str) -> list[Source]: ...


class LlmPort(Protocol):
    num_ctx: int

    def generate(self, messages: list[dict[str, str]]) -> LlmReply: ...


@dataclass(frozen=True)
class Citation:
    source: int
    document_id: str
    title: str
    section: str | None
    chunk_index: int


@dataclass(frozen=True)
class AnswerResult:
    status: str                       # "answered" | "insufficient_context"
    answer: str | None
    citations: list[Citation] = field(default_factory=list)
    reason: str | None = None         # internal category (logged, not returned); None when answered
    chunk_count: int = 0
    search_ms: int = 0
    llm_ms: int = 0                   # 0 when the model was not called
    total_ms: int = 0


def _ms(start: float, end: float) -> int:
    return int((end - start) * 1000)


def answer_question(question: str, key: str, search: SearchPort, llm: LlmPort) -> AnswerResult:
    started = time.perf_counter()

    sources = search.search(key, question)
    searched = time.perf_counter()
    timing = {"chunk_count": len(sources), "search_ms": _ms(started, searched)}

    def abstain(reason: str, llm_ms: int = 0) -> AnswerResult:
        return AnswerResult("insufficient_context", None, [], reason, llm_ms=llm_ms,
                            total_ms=_ms(started, time.perf_counter()), **timing)

    if not sources:
        return abstain("no_sources")
    if missing_identifiers(question, sources):
        return abstain("identifier_not_found")

    reply = llm.generate(build_messages(question, sources))
    llm_ms = _ms(searched, time.perf_counter())

    truncated = truncation_reason(reply, llm.num_ctx)
    if truncated:
        return abstain(truncated, llm_ms)

    verdict = validate(reply.text, len(sources))
    if verdict.status != "answered":
        return abstain(verdict.reason or "invalid", llm_ms)

    by_number = {s.number: s for s in sources}
    citations = [Citation(n, by_number[n].document_id, by_number[n].title, by_number[n].section, by_number[n].chunk_index)
                 for n in verdict.citations]
    return AnswerResult("answered", verdict.answer, citations, None, llm_ms=llm_ms,
                        total_ms=_ms(started, time.perf_counter()), **timing)
