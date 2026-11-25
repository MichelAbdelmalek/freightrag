"""The control arm: abstain -> rewrite -> search again -> answer, in plain Python. At most two attempts, one rewrite.

`final_result` builds the response shape and is shared with `graph.py`, so both flows report identically.
"""

import time

from freightrag_answer.rewrite import propose_rewrite
from freightrag_answer.pipeline import AnswerResult, to_result
from freightrag_answer.steps import Attempt, LlmPort, SearchPort, run_attempt

RETRY_REASONS = frozenset({"identifier_not_found", "cited_chunk_lacks_identifier", "model_abstained"})


def should_retry(attempt: Attempt, attempts: int) -> bool:
    """Only an abstention shaped like a retrieval miss, and only once. Never untrustworthy replies (truncated, invented
    citations, malformed JSON) and never an empty search."""
    return attempts == 1 and attempt.reason in RETRY_REASONS


def final_result(flow: str, first: Attempt, last: Attempt, attempts: int, rewrite_status: str | None, rewrite_ms: int,
                 rewrite_called: bool, total_ms: int) -> AnswerResult:
    base = to_result(last, total_ms)
    both = [first, last] if attempts == 2 else [first]
    return AnswerResult(
        base.status, base.answer, base.citations, base.reason, last.chunk_count,
        sum(a.search_ms for a in both), sum(a.llm_ms for a in both), total_ms,
        flow=flow, attempts=attempts, first_reason=first.reason if rewrite_status is not None else None,
        rewrite_status=rewrite_status, rewrite_ms=rewrite_ms,
        model_calls=sum(int(a.model_called) for a in both) + int(rewrite_called))


def answer_with_retry(question: str, key: str, search: SearchPort, llm: LlmPort) -> AnswerResult:
    started = time.perf_counter()

    def search_once(query: str):
        return search.search(key, query)

    def elapsed() -> int:
        return int((time.perf_counter() - started) * 1000)

    first = run_attempt(question, question, search_once, llm)
    if not should_retry(first, 1):
        return final_result("retry", first, first, 1, None, 0, False, elapsed())

    verdict, rewrite_ms = propose_rewrite(question, first.reason or "", llm)
    if verdict.query is None:
        return final_result("retry", first, first, 1, verdict.status, rewrite_ms, True, elapsed())

    second = run_attempt(question, verdict.query, search_once, llm)
    return final_result("retry", first, second, 2, verdict.status, rewrite_ms, True, elapsed())
