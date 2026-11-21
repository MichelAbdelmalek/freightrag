"""Loads and validates the question set (one JSON object per line)."""

import json
import re
from dataclasses import dataclass, field
from pathlib import Path

from freightrag_eval.metrics import Expected

_FIELDS = {"id", "tenant", "question", "expected", "kind", "note"}
_OPTIONAL = {"acceptable", "answer_facts"}
_KINDS = {"answerable", "unanswerable"}
_TENANT = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
_MAX_QUESTION = 1000  # the API's limit


class DatasetError(Exception):
    pass


@dataclass(frozen=True)
class Question:
    id: str
    tenant: str
    question: str
    expected: list[Expected]
    kind: str
    note: str
    acceptable: list[Expected] = field(default_factory=list)
    answer_facts: list[str] = field(default_factory=list)   # strings a correct ANSWER must contain (answer evaluation)


def load(path: str | Path) -> list[Question]:
    questions: list[Question] = []
    seen: set[str] = set()
    for number, line in enumerate(Path(path).read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
            question = _parse(row)
        except (json.JSONDecodeError, ValueError, TypeError, KeyError) as error:
            raise DatasetError(f"{path}, line {number}: {error}") from error
        if question.id in seen:
            raise DatasetError(f"{path}, line {number}: duplicate id {question.id!r}")
        seen.add(question.id)
        questions.append(question)
    return questions


def _parse(row: dict) -> Question:
    if not isinstance(row, dict):
        raise ValueError("each line must be a JSON object")
    unknown = set(row) - _FIELDS - _OPTIONAL
    if unknown:
        raise ValueError(f"unknown field(s) {sorted(unknown)} (typo?)")
    missing = _FIELDS - set(row)
    if missing:
        raise ValueError(f"missing field(s) {sorted(missing)}")

    qid, tenant, text, kind, note = (row[k] for k in ("id", "tenant", "question", "kind", "note"))
    if not isinstance(qid, str) or not qid.strip():
        raise ValueError("id must be a non-empty string")
    if not isinstance(tenant, str) or not _TENANT.match(tenant):
        raise ValueError(f"tenant must be a slug like tenant-nile-freight, got {tenant!r}")
    if not isinstance(text, str) or not text.strip() or len(text.strip()) > _MAX_QUESTION:
        raise ValueError(f"question must be 1-{_MAX_QUESTION} characters")
    if kind not in _KINDS:
        raise ValueError(f"kind must be one of {sorted(_KINDS)}, got {kind!r}")
    if not isinstance(note, str):
        raise ValueError("note must be a string")

    expected = _chunks(row["expected"], "expected")
    acceptable = _chunks(row.get("acceptable", []), "acceptable")
    if kind == "answerable" and not expected:
        raise ValueError("an answerable question needs at least one expected item")
    if kind == "unanswerable" and expected:
        raise ValueError("an unanswerable question must have no expected items")
    if kind == "unanswerable" and acceptable:
        raise ValueError("an unanswerable question must have no acceptable items")

    facts = row.get("answer_facts", [])
    if not isinstance(facts, list) or any(not isinstance(f, str) or not f.strip() for f in facts):
        raise ValueError("answer_facts must be a list of non-empty strings")
    if kind == "answerable" and not facts:
        raise ValueError("an answerable question needs answer_facts")
    if kind == "unanswerable" and facts:
        raise ValueError("an unanswerable question must have no answer_facts")

    return Question(id=qid, tenant=tenant, question=text, expected=expected, kind=kind, note=note,
                    acceptable=acceptable, answer_facts=facts)


def _chunks(items, name: str) -> list[Expected]:
    if not isinstance(items, list):
        raise ValueError(f"{name} must be a list")
    chunks = [Expected(document_title=e["document_title"], section=e["section"]) for e in items]
    for c in chunks:
        if not isinstance(c.document_title, str) or not isinstance(c.section, str) \
                or not c.document_title.strip() or not c.section.strip():
            raise ValueError(f"{name} items need a non-empty document_title and section")
    return chunks
