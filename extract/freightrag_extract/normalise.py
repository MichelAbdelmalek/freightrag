"""Turns raw OCR or text-layer text into the plain text the .NET chunker expects. Pure functions.

Line breaks are never reflowed: short field lines ("Load number: 48213") must stay one per line, because the chunker's
identifier patterns and its heading rule both work line by line.
"""

import re
import unicodedata

_QUOTES = {"‘": "'", "’": "'", "“": '"', "”": '"', "–": "-", "—": "-"}
_PAGE_MARKER = re.compile(r"^\s*page\s+\d+(\s+of\s+\d+)?\s*$", re.IGNORECASE)
_NUMERIC_SHAPED = re.compile(r"^\$?[0-9OolIS,.]+$")
_CONFUSED = str.maketrans({"O": "0", "o": "0", "l": "1", "I": "1", "S": "5"})
_HYPHEN_END = re.compile(r"[a-z]-$")


def clean_chars(text: str) -> str:
    text = unicodedata.normalize("NFKC", text)
    text = "".join(_QUOTES.get(c, c) for c in text)
    return "".join(c for c in text if c in "\n\t" or (c != "�" and unicodedata.category(c)[0] != "C"))


def dehyphenate(lines: list[str]) -> list[str]:
    """Joins 'deliv-' + 'ered' only when both sides are lowercase letters: 'SAV-' + '778431' must stay apart."""
    out: list[str] = []
    i = 0
    while i < len(lines):
        line = lines[i]
        if _HYPHEN_END.search(line) and i + 1 < len(lines) and lines[i + 1][:1].islower():
            out.append(line[:-1] + lines[i + 1])
            i += 2
        else:
            out.append(line)
            i += 1
    return out


def fix_numeric_tokens(line: str) -> tuple[str, int]:
    """In a token that is only digits, separators and look-alike letters, and has at least one digit, reads the
    look-alikes as digits ($5O.OO -> $50.00). Words and identifiers (SOLO, SAV-778431) have no such shape and are untouched."""
    fixed = 0
    parts = []
    for token in re.split(r"(\s+)", line):
        if _NUMERIC_SHAPED.match(token) and any(c.isdigit() for c in token) and token.translate(_CONFUSED) != token:
            token = token.translate(_CONFUSED)
            fixed += 1
        parts.append(token)
    return "".join(parts), fixed


def drop_page_markers(lines: list[str]) -> list[str]:
    return [line for line in lines if not _PAGE_MARKER.match(line)]


def normalise_page(text: str, numeric_fix: bool = True) -> tuple[str, int]:
    """One page of text -> cleaned text and the number of numeric tokens corrected."""
    lines = [line.rstrip() for line in clean_chars(text).split("\n")]
    lines = dehyphenate(drop_page_markers(lines))
    corrections = 0
    if numeric_fix:
        fixed = []
        for line in lines:
            line, n = fix_numeric_tokens(line)
            corrections += n
            fixed.append(line)
        lines = fixed
    return "\n".join(lines).strip("\n"), corrections


def join_pages(pages: list[str]) -> str:
    """Pages are separated by a blank line and no marker: a line like 'PAGE 2' would look like a heading to the chunker."""
    return "\n\n".join(p for p in pages if p.strip())
