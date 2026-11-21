import json
from pathlib import Path

import pytest

from freightrag_eval.dataset import DatasetError, load

ROOT = Path(__file__).resolve().parent.parent


def write(tmp_path, *rows):
    path = tmp_path / "set.jsonl"
    path.write_text("\n".join(json.dumps(r) for r in rows) + "\n", encoding="utf-8")
    return path


def row(**overrides):
    base = {"id": "q1", "tenant": "tenant-nile-freight", "question": "What is the detention rate?",
            "expected": [{"document_title": "rate-confirmation-48213", "section": "RATE"}],
            "kind": "answerable", "note": "basic lookup", "answer_facts": ["$50"]}
    base.update(overrides)
    return base


def test_the_committed_question_set_is_valid():
    questions = load(ROOT / "retrieval_set.jsonl")
    assert len(questions) == 45
    assert {q.tenant for q in questions} == {"tenant-nile-freight", "tenant-delta-haul"}
    assert sum(q.kind == "unanswerable" for q in questions) == 8
    assert any(len(q.expected) == 2 for q in questions)  # the multi-chunk question
    assert {q.id for q in questions if q.acceptable} == {"q11", "q19"}
    assert all(q.answer_facts for q in questions if q.kind == "answerable")
    assert not any(q.answer_facts for q in questions if q.kind == "unanswerable")


def test_loads_a_valid_row(tmp_path):
    [q] = load(write(tmp_path, row()))
    assert q.id == "q1" and q.expected[0].section == "RATE"


@pytest.mark.parametrize("bad", [
    {"id": ""},
    {"tenant": "Nile Freight"},
    {"question": ""},
    {"question": "x" * 1001},
    {"kind": "maybe"},
    {"expected": []},                                                    # answerable needs at least one
    {"kind": "unanswerable"},                                            # unanswerable must have none (and no facts)
    {"answer_facts": []},                                                # answerable needs facts
    {"answer_facts": ["$50", " "]},                                      # facts must be non-empty strings
    {"answer_facts": "$50"},                                             # must be a list
    {"kind": "unanswerable", "expected": [], "answer_facts": ["$50"]},   # unanswerable must have none
    {"expected": [{"document_title": "rate-confirmation-48213"}]},       # missing section
    {"acceptable": [{"document_title": "x"}]},                           # acceptable item missing section
    {"acceptable": {"document_title": "x", "section": "y"}},             # acceptable must be a list
    {"kind": "unanswerable", "expected": [], "acceptable": [{"document_title": "x", "section": "y"}]},
])
def test_rejects_invalid_rows_with_the_line_number(tmp_path, bad):
    with pytest.raises(DatasetError, match="line 1"):
        load(write(tmp_path, row(**bad)))


def test_rejects_duplicate_ids(tmp_path):
    with pytest.raises(DatasetError, match="duplicate"):
        load(write(tmp_path, row(), row()))


def test_rejects_unknown_fields_to_catch_typos(tmp_path):
    with pytest.raises(DatasetError, match="expect"):
        load(write(tmp_path, {**row(), "expect": []}))


def test_acceptable_is_optional_and_defaults_to_empty(tmp_path):
    [q] = load(write(tmp_path, row()))
    assert q.acceptable == []


def test_loads_acceptable_items(tmp_path):
    [q] = load(write(tmp_path, row(acceptable=[{"document_title": "bill-of-lading-SAV-780377", "section": "SHIPMENT DETAILS"}])))
    assert q.acceptable[0].section == "SHIPMENT DETAILS"
