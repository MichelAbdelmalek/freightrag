"""The extraction report: numbers and codes only, never document content."""

MAX_HEADING_LENGTH = 80


def looks_like_heading(line: str) -> bool:
    """The same rule as SectionChunker.IsHeading in the .NET API: 2-80 characters, no lowercase letters, no final period,
    and at least half of the non-space characters are letters."""
    t = line.strip()
    if not 2 <= len(t) <= MAX_HEADING_LENGTH:
        return False
    if any(c.islower() for c in t) or t.endswith("."):
        return False
    letters = sum(c.isalpha() for c in t)
    non_space = sum(not c.isspace() for c in t)
    return letters >= 2 and letters * 2 >= non_space


def count_headings(text: str) -> int:
    """Counts sections the chunker would start: only the first line of a run of heading-shaped lines is a heading."""
    count, previous = 0, False
    for line in text.split("\n"):
        shaped = looks_like_heading(line)
        if shaped and not previous:
            count += 1
        previous = shaped
    return count


def warnings_for(mean_confidence: float | None, headings: int, low_numeric: int, dpi_reduced: bool, low_confidence: int) -> list[str]:
    warnings = []
    if mean_confidence is not None and mean_confidence < low_confidence:
        warnings.append("low_confidence")
    if headings == 0:
        warnings.append("no_headings")
    if low_numeric > 0:
        warnings.append("numeric_tokens_low_confidence")
    if dpi_reduced:
        warnings.append("dpi_reduced")
    return warnings
