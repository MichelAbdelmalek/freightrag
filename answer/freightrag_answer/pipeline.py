"""The straight-line answer pipeline: search -> guard -> build_prompt -> generate -> validate -> cited-chunk check.

Each step is a small pure function in its own module (guard.missing_identifiers, prompt.build_messages,
llm.OllamaChat.generate, validate.validate); `steps.run_attempt` wires one pass and this module turns it into the
result the HTTP layer returns. `retry.py` (plain Python) and `graph.py` (LangGraph) run the same pass a second time
after a rewrite; they are selected by ANSWER_FLOW.
"""

import time
from dataclasses import dataclass, field

from freightrag_answer.steps import Attempt, Citation, LlmPort, SearchPort, run_attempt

__all__ = ["AnswerResult", "Citation", "LlmPort", "SearchPort", "answer_question", "to_result"]


@dataclass(frozen=True)
class AnswerResult:
    status: str                       # "answered" | "insufficient_context"
    answer: str | None
    citations: list[Citation] = field(default_factory=list)
    reason: str | None = None         # internal category (logged, not returned); None when answered
    chunk_count: int = 0
    search_ms: int = 0
    llm_ms: int = 0                   # time in answer-generating model calls; 0 when the model was not called
    total_ms: int = 0
    flow: str = "straight"            # straight | retry | graph
    attempts: int = 1
    first_reason: str | None = None   # why the first attempt abstained, when a retry was considered
    rewrite_status: str | None = None # accepted or the category the rewrite was rejected for; None when no rewrite was tried
    rewrite_ms: int = 0
    model_calls: int = 0              # answer calls plus the rewrite call


def to_result(attempt: Attempt, total_ms: int) -> AnswerResult:
    return AnswerResult(attempt.status, attempt.answer, attempt.citations, attempt.reason, attempt.chunk_count,
                        attempt.search_ms, attempt.llm_ms, total_ms, model_calls=int(attempt.model_called))


def answer_question(question: str, key: str, search: SearchPort, llm: LlmPort) -> AnswerResult:
    started = time.perf_counter()
    attempt = run_attempt(question, question, lambda q: search.search(key, q), llm)
    return to_result(attempt, int((time.perf_counter() - started) * 1000))
