"""Validates the model's reply. In code, never trusting the model: JSON schema constraints on the model's output
make a malformed reply unlikely, not impossible, and say nothing about whether a citation is real."""

import json
import re
from dataclasses import dataclass
from typing import Literal

from pydantic import BaseModel, ConfigDict, ValidationError

_MARKER = re.compile(r"\[(\d+)\]")


class _Reply(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)   # strict: "1" and true are not the integer 1

    status: Literal["answered", "insufficient_context"]
    answer: str
    citations: list[int]


@dataclass(frozen=True)
class Validated:
    status: str                  # "answered" | "insufficient_context"
    answer: str | None
    citations: tuple[int, ...]
    reason: str | None           # a category, never content; None when answered


def _abstain(reason: str) -> Validated:
    return Validated("insufficient_context", None, (), reason)


def validate(raw_text: str, source_count: int) -> Validated:
    try:
        data = json.loads(raw_text)
    except ValueError:
        return _abstain("malformed_json")
    try:
        reply = _Reply.model_validate(data)
    except ValidationError:
        return _abstain("schema_mismatch")

    if reply.status == "insufficient_context":
        return _abstain("model_abstained")
    if not reply.answer.strip():
        return _abstain("empty_answer")
    if not reply.citations:
        return _abstain("no_citations")

    markers = {int(n) for n in _MARKER.findall(reply.answer)}
    cited = set(reply.citations) | markers
    if any(n < 1 or n > source_count for n in cited):
        return _abstain("invented_citation")
    return Validated("answered", reply.answer, tuple(sorted(cited)), None)
