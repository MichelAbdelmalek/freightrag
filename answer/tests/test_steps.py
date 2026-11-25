from freightrag_answer.steps import run_attempt
from tests.fakes import FakeLlm, FakeSearch, reply_json, source

RATE_48213 = source(1, "rate-confirmation-48213", "RATE", content="Document: rate-confirmation | Load: 48213 | Section: RATE\nDetention: $50.00 per hour")
OTHER = source(2, "rate-confirmation-48227", "RATE", content="Document: rate-confirmation | Load: 48227 | Section: RATE\nDetention: $75.00 per hour")
QUESTION = "What is the detention rate for load 48213?"


def attempt(question=QUESTION, query=None, search=None, llm=None):
    search = search or FakeSearch([RATE_48213, OTHER])
    llm = llm or FakeLlm(reply_json(answer="Detention is $50.00 per hour [1].", citations=[1]))
    return run_attempt(question, query or question, lambda q: search.search("k", q), llm), search, llm


def test_search_uses_the_query_but_the_guard_uses_the_question():
    search = FakeSearch([OTHER], by_query={"detention charges": [OTHER]})
    result, search, llm = attempt(query="detention charges", search=search)
    assert search.calls == [("k", "detention charges")]
    assert (result.status, result.reason) == ("insufficient_context", "identifier_not_found")   # 48213 is in no chunk
    assert llm.calls == []


def test_the_answer_prompt_contains_the_question_not_the_query():
    _, _, llm = attempt(query="DELIVERY section detention fee")
    prompt = llm.calls[0][1]["content"]
    assert "load 48213" in prompt and "DELIVERY section" not in prompt


def test_the_cited_check_uses_the_original_questions_identifiers():
    llm = FakeLlm(reply_json(answer="Detention is $75.00 per hour [2].", citations=[2]))
    result, _, _ = attempt(llm=llm, query="detention 48227")
    assert (result.status, result.reason) == ("insufficient_context", "cited_chunk_lacks_identifier")


def test_model_called_is_false_on_guard_exit_and_true_otherwise():
    guarded, _, _ = attempt("What is the detention rate for load 48999?")
    answered, _, _ = attempt()
    assert (guarded.model_called, guarded.llm_ms) == (False, 0)
    assert (answered.model_called, answered.status) == (True, "answered")
