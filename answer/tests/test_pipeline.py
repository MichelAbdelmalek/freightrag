import pytest

from freightrag_answer.llm import ContextWindowExceeded
from freightrag_answer.pipeline import answer_question
from tests.fakes import FakeLlm, FakeSearch, reply_json, source

KEY = "test-key-not-a-secret-0123456789abcdefghij"

RATE_48213 = source(1, "rate-confirmation-48213", "RATE", chunk_index=2,
                    content="Document: rate-confirmation | Load: 48213 | Section: RATE\nDetention: $50.00 per hour")
RATE_48227 = source(2, "rate-confirmation-48227", "RATE", chunk_index=2,
                    content="Document: rate-confirmation | Load: 48227 | Section: RATE\nDetention: $75.00 per hour")


def run(question="What is the detention rate for load 48213?", sources=None, llm=None, search=None):
    search = search or FakeSearch([RATE_48213, RATE_48227] if sources is None else sources)
    llm = llm or FakeLlm(reply_json(answer="Detention is $50.00 per hour [1].", citations=[1]))
    return answer_question(question, KEY, search, llm), search, llm


def test_an_answered_question_cites_and_maps_its_sources():
    result, search, llm = run()

    assert result.status == "answered" and result.reason is None
    assert result.answer == "Detention is $50.00 per hour [1]."
    [citation] = result.citations
    assert (citation.source, citation.document_id, citation.title, citation.section, citation.chunk_index) == (
        1, "doc-1", "rate-confirmation-48213", "RATE", 2)
    assert result.chunk_count == 2
    assert len(llm.calls) == 1
    assert "Detention: $50.00 per hour" in llm.calls[0][1]["content"]           # the sources reached the prompt
    assert result.search_ms >= 0 and result.llm_ms >= 0 and result.total_ms >= result.search_ms


def test_the_key_is_forwarded_to_search_unchanged():
    _, search, _ = run()
    assert search.calls == [(KEY, "What is the detention rate for load 48213?")]


def test_q39_the_guard_abstains_without_calling_the_model():
    result, _, llm = run("What is the detention rate for load 48999?")

    assert (result.status, result.reason, result.answer, result.citations) == ("insufficient_context", "identifier_not_found", None, [])
    assert llm.calls == []
    assert result.llm_ms == 0


def test_no_retrieved_chunks_means_no_model_call():
    result, _, llm = run(sources=[])
    assert (result.status, result.reason) == ("insufficient_context", "no_sources")
    assert llm.calls == []


def test_the_model_may_abstain():
    result, _, llm = run("Is there a cargo insurance certificate?", llm=FakeLlm(reply_json("insufficient_context", "", [])))
    assert (result.status, result.reason) == ("insufficient_context", "model_abstained")
    assert len(llm.calls) == 1


@pytest.mark.parametrize(("text", "reason"), [
    (reply_json(citations=[7]), "invented_citation"),
    (reply_json(citations=[]), "no_citations"),
    ("{ not json", "malformed_json"),
])
def test_an_invalid_model_reply_becomes_insufficient_context(text, reason):
    result, _, _ = run(llm=FakeLlm(text))
    assert (result.status, result.reason, result.answer, result.citations) == ("insufficient_context", reason, None, [])


def test_a_truncated_prompt_is_not_trusted():
    result, _, _ = run(llm=FakeLlm(prompt_tokens=8192))
    assert (result.status, result.reason) == ("insufficient_context", "context_truncated")


def test_a_cut_off_answer_is_not_trusted():
    result, _, _ = run(llm=FakeLlm(done_reason="length"))
    assert (result.status, result.reason) == ("insufficient_context", "output_truncated")


def test_a_prompt_that_cannot_fit_is_an_error_not_an_answer():
    result_error = ContextWindowExceeded(9000, 8192)
    with pytest.raises(ContextWindowExceeded):
        run(llm=FakeLlm(error=result_error))


def test_search_errors_propagate():
    with pytest.raises(RuntimeError):
        run(search=FakeSearch(error=RuntimeError("upstream")))
