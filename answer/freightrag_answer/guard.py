"""The guard: a deterministic check that runs BEFORE any model call.

A small model shown five RATE sections for loads 48213 and 48227 will happily "answer" a question about load 48999
by borrowing a neighbour's numbers. A string comparison can't be talked into that: if the question names an
identifier and no retrieved chunk contains it, the sources cannot answer the question.
"""

import re

from freightrag_answer.models import Source

# Codes such as SAV-778431, NFL-2026-1187, DH-26-0431: 2-5 letters, then dash-separated digit groups. Needing four
# or more digits in total keeps "Class-8" and "COVID-19" out.
_CODE = re.compile(r"(?<![A-Za-z0-9-])([A-Za-z]{2,5}(?:-\d+)+)(?![A-Za-z0-9])")
_MIN_CODE_DIGITS = 4

# Bare numbers of five or more digits: load 48213, BOL 778431. Prices ($2,705.00), years (2026) and durations
# (10-hour) are shorter or have other characters around them. A long number that is not an identifier (e.g.
# "20000 pounds") makes the guard abstain, which is the safe direction.
_NUMBER = re.compile(r"(?<![A-Za-z0-9-])\d{5,}(?![A-Za-z0-9])")


def extract_identifiers(question: str) -> list[str]:
    """Identifiers in the order they appear, upper-cased, without duplicates."""
    found: list[tuple[int, str]] = []
    for match in _CODE.finditer(question):
        if sum(c.isdigit() for c in match.group(1)) >= _MIN_CODE_DIGITS:
            found.append((match.start(), match.group(1).upper()))
    found.extend((m.start(), m.group(0)) for m in _NUMBER.finditer(question))

    identifiers: list[str] = []
    for _, value in sorted(found):
        if value not in identifiers:
            identifiers.append(value)
    return identifiers


def _contains(content: str, identifier: str) -> bool:
    """Whole-token match, case-insensitive: 48213 is not inside 148213, but 778431 is inside SAV-778431."""
    return re.search(rf"(?<![A-Za-z0-9]){re.escape(identifier)}(?![A-Za-z0-9])", content, re.IGNORECASE) is not None


def missing_identifiers(question: str, sources: list[Source]) -> list[str]:
    """Identifiers named in the question that no retrieved chunk contains."""
    return [i for i in extract_identifiers(question) if not any(_contains(s.content, i) for s in sources)]


def uncited_identifiers(question: str, cited: list[Source]) -> list[str]:
    """Identifiers named in the question that no CITED chunk contains. Runs after the model has answered.

    missing_identifiers only asks whether the identifier is anywhere in the retrieved set. That is not enough: load
    77118 can be in one retrieved chunk while the model cites a chunk about load 77139 and names its receiver. An
    answer is only as good as the chunks it points at, so each identifier in the question must appear in at least one
    chunk the answer cites ("at least one", not "every": a correct answer may also cite a chunk without identifiers,
    such as the driver handbook).
    """
    return [i for i in extract_identifiers(question) if not any(_contains(s.content, i) for s in cited)]
