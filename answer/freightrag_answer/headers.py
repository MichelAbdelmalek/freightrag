"""Reads the context header every FreightRAG chunk starts with.

    Document: rate-confirmation | Load: 48213 | Section: RATE

Same logic as evals/freightrag_eval/headers.py: the search API returns the chunk text, and the section name is only
in that first line.
"""

_SEPARATOR = "| Section: "


def section_of(content: str) -> str | None:
    """The section name from the chunk's first line, or None if the line is not a context header."""
    first_line = content.split("\n", 1)[0]
    if not first_line.startswith("Document: ") or _SEPARATOR not in first_line:
        return None
    return first_line.rpartition(_SEPARATOR)[2].strip() or None
