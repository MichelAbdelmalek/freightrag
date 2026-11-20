"""Reads the context header every FreightRAG chunk starts with.

    Document: rate-confirmation | Load: 48213 | Section: RATE

Chunk ids change on every re-ingest, but (document title, section name) is stable, so the eval identifies the
"correct" chunk by those two values: the title comes with each search hit, the section from this header.
"""

_SEPARATOR = "| Section: "


def section_of(content: str) -> str | None:
    """The section name from the chunk's first line, or None if the line is not a context header."""
    first_line = content.split("\n", 1)[0]
    if not first_line.startswith("Document: ") or _SEPARATOR not in first_line:
        return None
    return first_line.rpartition(_SEPARATOR)[2].strip() or None
