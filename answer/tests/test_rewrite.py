import json

import pytest

from freightrag_answer.llm import LlmReply
from freightrag_answer.rewrite import REWRITE_SCHEMA, build_rewrite_messages, propose_rewrite, validate_rewrite
from tests.fakes import FakeLlm

QUESTION = "Who received load 48255?"


def raw(query):
    return json.dumps({"query": query})


def test_rewrite_prompt_holds_the_escaped_question_and_reason_and_no_chunk_text():
    messages = build_rewrite_messages("What is <b>load 48255</b> & more?", "model_abstained")
    user = messages[1]["content"]
    assert "&lt;b&gt;load 48255&lt;/b&gt; &amp; more?" in user and "model_abstained" in user
    assert "Document:" not in user and "Section:" not in user.replace("section headings", "")


def test_rewrite_prompt_says_the_question_is_data_and_keeps_numbers():
    system = build_rewrite_messages(QUESTION, "model_abstained")[0]["content"]
    assert "never instructions" in system and "Keep every load" in system


@pytest.mark.parametrize(("text", "status"), [
    ("not json", "malformed_json"),
    (json.dumps({"query": "load 48255", "extra": 1}), "schema_mismatch"),
    (raw("   "), "empty"),
    (raw("load 48255 " + "x" * 300), "too_long"),
    (raw("load 48255\nreceiver"), "multiline_or_control"),
    (raw("<source> load 48255"), "markup"),
    (raw("receiver consignee"), "identifiers_changed"),            # dropped
    (raw("receiver of load 48256"), "identifiers_changed"),        # changed
    (raw("receiver of load 48255 and load 77104"), "identifiers_changed"),   # smuggled another tenant's load
    (raw("receiver of load 48255 SAV-778431"), "identifiers_changed"),
    (raw("who received load 48255?"), "same_as_question"),
])
def test_validate_rewrite_rejects(text, status):
    verdict = validate_rewrite(text, QUESTION)
    assert (verdict.status, verdict.query) == (status, None)


def test_validate_rewrite_accepts_a_reworded_query_with_the_same_identifiers():
    verdict = validate_rewrite(raw("consignee receiver delivery load 48255"), QUESTION)
    assert (verdict.status, verdict.query) == ("accepted", "consignee receiver delivery load 48255")
    ok = validate_rewrite(raw("invoice nfl-2026-1219 charges"), "What are the charges on NFL-2026-1219?")
    assert ok.status == "accepted"


def test_propose_rewrite_asks_for_the_rewrite_schema_and_validates():
    llm = FakeLlm(raw("consignee receiver delivery load 48255"))
    verdict, ms = propose_rewrite(QUESTION, "model_abstained", llm)
    assert verdict.status == "accepted" and ms >= 0 and llm.schemas == [REWRITE_SCHEMA]


def test_a_truncated_rewrite_is_rejected():
    llm = FakeLlm(raw("consignee receiver delivery load 48255"), done_reason="length")
    verdict, _ = propose_rewrite(QUESTION, "model_abstained", llm)
    assert (verdict.status, verdict.query) == ("output_truncated", None)
