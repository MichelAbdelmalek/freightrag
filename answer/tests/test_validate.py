import json

import pytest

from freightrag_answer.validate import validate
from tests.fakes import reply_json


def test_a_valid_answer_passes():
    v = validate(reply_json(answer="Detention is $50.00 per hour [1].", citations=[1]), source_count=3)
    assert (v.status, v.answer, v.citations, v.reason) == ("answered", "Detention is $50.00 per hour [1].", (1,), None)


def test_citations_are_deduplicated_and_sorted():
    assert validate(reply_json(answer="a [2] b [1]", citations=[2, 1, 2]), 3).citations == (1, 2)


def test_markers_in_the_text_are_added_to_the_citations():
    v = validate(reply_json(answer="a [1] and b [2]", citations=[1]), 3)
    assert v.citations == (1, 2)


def test_the_model_may_abstain():
    v = validate(reply_json(status="insufficient_context", answer="", citations=[]), 3)
    assert (v.status, v.answer, v.citations, v.reason) == ("insufficient_context", None, (), "model_abstained")


@pytest.mark.parametrize(("raw", "reason"), [
    (reply_json(citations=[7]), "invented_citation"),                                   # 7 of 5 sources
    (reply_json(citations=[0]), "invented_citation"),                                   # numbering starts at 1
    (reply_json(answer="Fee is $9 [9].", citations=[1]), "invented_citation"),          # invented marker in the text
    (reply_json(citations=[]), "no_citations"),                                         # answered but nothing cited
    (reply_json(answer="   ", citations=[1]), "empty_answer"),
    ("not json at all", "malformed_json"),
    ("", "malformed_json"),
    ('{"status": "answered", "answer": "x"', "malformed_json"),                         # truncated
    ('["answered"]', "schema_mismatch"),                                                # JSON, wrong shape
    (json.dumps({"status": "maybe", "answer": "x", "citations": [1]}), "schema_mismatch"),
    (json.dumps({"status": "answered", "answer": "x"}), "schema_mismatch"),             # missing field
    (json.dumps({"status": "answered", "answer": "x", "citations": ["1"]}), "schema_mismatch"),   # no coercion
    (json.dumps({"status": "answered", "answer": "x", "citations": [True]}), "schema_mismatch"),
    (json.dumps({"status": "answered", "answer": "x", "citations": [1], "extra": 1}), "schema_mismatch"),
])
def test_anything_wrong_becomes_insufficient_context_with_a_reason(raw, reason):
    v = validate(raw, source_count=5)
    assert (v.status, v.answer, v.citations, v.reason) == ("insufficient_context", None, (), reason)
