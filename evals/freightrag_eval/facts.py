"""Deterministic fact check: does the answer contain the figures and names it must?

Blunt on purpose. It catches a wrong or missing number, name or identifier, and says nothing about wording, so facts
are things that must appear verbatim (figures, names, identifiers), never sentences.

Both texts are normalised first, so formatting differences don't count as errors:
- Unicode NFKC, lower case, collapsed whitespace, every kind of dash/minus becomes "-".
- Numbers are canonical: "$2,450.00" -> "$2450", "$50" and "$50.00" are the same, "0.50" -> "0.5".
"""

import re
import unicodedata
from dataclasses import dataclass

_DASHES = {ord(c): "-" for c in "‐‑‒–—―−﹣－"}
# 2,450.00 | 2450 | 0.50 — thousands separators only in groups of three, so "4,11" is two numbers, not 411.
_NUMBER = re.compile(r"\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?")


def _canonical_number(match: re.Match[str]) -> str:
    text = match.group(0).replace(",", "")
    if "." in text:
        text = text.rstrip("0").rstrip(".")
    return text


def normalize(text: str) -> str:
    text = unicodedata.normalize("NFKC", text).translate(_DASHES).lower()
    text = _NUMBER.sub(_canonical_number, text)
    return " ".join(text.split())


def fact_present(fact: str, answer: str) -> bool:
    """Whole-token match: "50" is not in "500" or "50.5", "48213" is not in "148213".
    A fact ending in a letter may continue ("hard hat" matches "hard hats")."""
    needle = normalize(fact)
    if not needle:
        return False
    tail = r"(?!\d|[.,]\d)" if needle[-1].isdigit() else ""
    return re.search(rf"(?<![a-z0-9]){re.escape(needle)}{tail}", normalize(answer)) is not None


@dataclass(frozen=True)
class FactResult:
    found: int
    total: int
    missing: tuple[str, ...]

    @property
    def recall(self) -> float:
        return self.found / self.total if self.total else 1.0


def check_facts(facts: list[str], answer: str | None) -> FactResult:
    """A missing answer (abstention) finds nothing."""
    missing = tuple(f for f in facts if not (answer and fact_present(f, answer)))
    return FactResult(found=len(facts) - len(missing), total=len(facts), missing=missing)
