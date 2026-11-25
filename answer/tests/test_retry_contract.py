"""Behaviour every retry flow must have. Parametrized over the flows so the plain-Python and graph arms share it."""

import importlib

import pytest

from freightrag_answer.llm import LlmError
from freightrag_answer.retry import answer_with_retry
from freightrag_answer.search import SearchError
from tests.fakes import FakeLlm, FakeSearch, reply_json, source

KEY = "test-key-not-a-secret-0123456789abcdefghij"
QUESTION = "Who received load 48255?"
REWRITE = "consignee receiver delivery load 48255"
RATE = source(1, "rate-confirmation-48255", "RATE", content="Document: rate-confirmation | Load: 48255 | Section: RATE\nRate: $60")
BOL = source(1, "bill-of-lading-48255", "DELIVERY", content="Document: bill-of-lading | Load: 48255 | Section: DELIVERY\nReceived by: Lena Morris")
GOOD = reply_json(answer="Lena Morris received it [1].", citations=[1])
ABSTAIN = reply_json("insufficient_context", "", [])
REWRITE_JSON = '{"query": "%s"}' % REWRITE

FLOWS = ["retry"]


def runner(flow):
    if flow == "retry":
        return answer_with_retry
    return importlib.import_module("freightrag_answer.graph").answer_with_graph


@pytest.fixture(params=FLOWS)
def flow(request):
    return runner(request.param)


def run(flow, replies, search=None, llm=None, question=QUESTION):
    search = search or FakeSearch([RATE], by_query={REWRITE: [BOL]})
    llm = llm or FakeLlm(replies=list(replies))
    return flow(question, KEY, search, llm), search, llm


def test_answered_first_time_makes_one_model_call_and_no_rewrite(flow):
    result, search, llm = run(flow, [GOOD], search=FakeSearch([BOL]))
    assert (result.status, result.attempts, result.model_calls, result.rewrite_status) == ("answered", 1, 1, None)
    assert len(llm.calls) == 1 and len(search.calls) == 1


@pytest.mark.parametrize("first", ["model", "guard", "cited"])
def test_each_retry_reason_triggers_exactly_one_rewrite_and_one_retry(flow, first):
    if first == "model":
        search, replies = FakeSearch([RATE], by_query={REWRITE: [BOL]}), [ABSTAIN, REWRITE_JSON, GOOD]
        reason = "model_abstained"
    elif first == "guard":
        search, replies = FakeSearch([source(1, "x", "RATE", content="Load: 11111")], by_query={REWRITE: [BOL]}), [REWRITE_JSON, GOOD]
        reason = "identifier_not_found"
    else:
        other = source(1, "bol-48256", "DELIVERY", content="Document: bill-of-lading | Load: 48256 | Section: DELIVERY\nReceived by: Marcus Bell")
        search = FakeSearch([other, source(2, "rc", "RATE", content="Load: 48255")], by_query={REWRITE: [BOL]})
        replies = [reply_json(answer="Marcus Bell [1].", citations=[1]), REWRITE_JSON, GOOD]
        reason = "cited_chunk_lacks_identifier"
    result, search, llm = run(flow, replies, search=search)
    assert (result.status, result.attempts, result.first_reason, result.rewrite_status) == ("answered", 2, reason, "accepted")
    assert [c[1] for c in search.calls] == [QUESTION, REWRITE]
    assert result.citations[0].title == "bill-of-lading-48255"


@pytest.mark.parametrize("bad", ["context", "output", "invented", "malformed"])
def test_no_retry_for_untrustworthy_replies(flow, bad):
    llm = {"context": FakeLlm(GOOD, prompt_tokens=8000, output_tokens=200),
           "output": FakeLlm(GOOD, done_reason="length"),
           "invented": FakeLlm(reply_json(answer="x [9]", citations=[9])),
           "malformed": FakeLlm("not json")}[bad]
    result, search, _ = run(flow, [], llm=llm, search=FakeSearch([BOL]))
    assert result.status == "insufficient_context" and result.attempts == 1 and len(search.calls) == 1


def test_no_sources_is_not_retried(flow):
    result, search, llm = run(flow, [], search=FakeSearch([]))
    assert (result.reason, result.attempts, len(search.calls), llm.calls) == ("no_sources", 1, 1, [])


def test_always_abstaining_model_stops_after_two_attempts(flow):
    result, search, llm = run(flow, [ABSTAIN, REWRITE_JSON, ABSTAIN], search=FakeSearch([RATE], by_query={REWRITE: [RATE]}))
    assert (result.status, result.attempts, result.model_calls) == ("insufficient_context", 2, 3)
    assert len(search.calls) == 2 and len(llm.calls) == 3


def test_rejected_rewrite_returns_the_first_abstention_without_a_second_search(flow):
    result, search, _ = run(flow, [ABSTAIN, '{"query": "receiver of load 99999"}'])
    assert (result.reason, result.attempts, result.rewrite_status) == ("model_abstained", 1, "identifiers_changed")
    assert len(search.calls) == 1 and result.model_calls == 2


def test_retry_searches_with_the_rewrite_and_the_same_key(flow):
    _, search, _ = run(flow, [ABSTAIN, REWRITE_JSON, GOOD])
    assert search.calls == [(KEY, QUESTION), (KEY, REWRITE)]


def test_retry_guard_still_requires_the_original_identifier(flow):
    no_id = source(1, "x", "DELIVERY", content="Document: bol | Load: 11111 | Section: DELIVERY\nReceived by: Someone")
    result, _, _ = run(flow, [ABSTAIN, REWRITE_JSON], search=FakeSearch([RATE], by_query={REWRITE: [no_id]}))
    assert (result.status, result.reason, result.attempts) == ("insufficient_context", "identifier_not_found", 2)


def test_retry_cited_check_uses_the_original_question(flow):
    other = source(1, "bol-48256", "DELIVERY", content="Document: bill-of-lading | Load: 48256 | Section: DELIVERY\nReceived by: Marcus Bell")
    result, _, _ = run(flow, [ABSTAIN, REWRITE_JSON, reply_json(answer="Marcus Bell [2].", citations=[2])],
                       search=FakeSearch([RATE], by_query={REWRITE: [BOL, other.__class__(2, "c", "d", "t", "S", 0, "Load: 48256")]}))
    assert (result.reason, result.status) == ("cited_chunk_lacks_identifier", "insufficient_context")


def test_retry_answer_prompt_uses_the_original_question(flow):
    _, _, llm = run(flow, [ABSTAIN, REWRITE_JSON, GOOD])
    final = llm.calls[2][1]["content"]
    assert "Who received load 48255?" in final and REWRITE not in final


def test_diagnostics_fields_are_reported(flow):
    result, _, _ = run(flow, [ABSTAIN, REWRITE_JSON, GOOD])
    assert result.first_reason == "model_abstained" and result.rewrite_ms >= 0
    assert result.model_calls == 3 and result.total_ms >= result.search_ms
    assert result.flow in ("retry", "graph")


@pytest.mark.parametrize("error", [SearchError(502, "down"), LlmError(502, "down")])
def test_errors_propagate_unchanged(flow, error):
    search = FakeSearch([RATE], by_query={REWRITE: [BOL]})
    llm = FakeLlm(replies=[ABSTAIN, REWRITE_JSON, GOOD])
    if isinstance(error, SearchError):
        search.error = error
    else:
        llm.error = error
    with pytest.raises(type(error)):
        flow(QUESTION, KEY, search, llm)
