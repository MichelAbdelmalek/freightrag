"""Tesseract, called as a subprocess with an argument list (never a shell, never a user-derived argument).

The image goes in on stdin and the TSV comes out on stdout, so no file is written. The TSV has one row per word with a
confidence; we group the words into lines and paragraphs and keep the confidences for the report.
"""

import re
import subprocess
from dataclasses import dataclass, field

_HAS_DIGIT = re.compile(r"[0-9]")
LOW_NUMERIC_CONFIDENCE = 70


class OcrError(Exception):
    """Tesseract failed or timed out. The message never contains document text."""


@dataclass(frozen=True)
class Line:
    text: str
    paragraph: int            # lines of one (block, paragraph) share a number; a change means a blank line in between


@dataclass
class OcrPage:
    lines: list[Line] = field(default_factory=list)
    mean_confidence: float | None = None
    low_confidence_numeric_tokens: int = 0


def parse_tsv(tsv: str) -> OcrPage:
    words: dict[tuple[int, int, int], list[str]] = {}
    order: list[tuple[int, int, int]] = []
    confidences: list[float] = []
    low_numeric = 0
    for raw in tsv.splitlines()[1:]:
        cols = raw.split("\t")
        if len(cols) < 12:
            continue
        try:
            level, block, par, line, conf = int(cols[0]), int(cols[2]), int(cols[3]), int(cols[4]), float(cols[10])
        except ValueError:
            continue
        text = cols[11].strip()
        if level != 5 or conf < 0 or not text:        # only real words carry a confidence
            continue
        key = (block, par, line)
        if key not in words:
            words[key] = []
            order.append(key)
        words[key].append(text)
        confidences.append(conf)
        if _HAS_DIGIT.search(text) and conf < LOW_NUMERIC_CONFIDENCE:
            low_numeric += 1

    paragraphs: dict[tuple[int, int], int] = {}
    lines = []
    for key in order:
        paragraphs.setdefault((key[0], key[1]), len(paragraphs))
        lines.append(Line(" ".join(words[key]), paragraphs[(key[0], key[1])]))
    mean = sum(confidences) / len(confidences) if confidences else None
    return OcrPage(lines, mean, low_numeric)


def run_tesseract(png: bytes, dpi: int, psm: int, timeout: int) -> str:
    command = ["tesseract", "stdin", "stdout", "-l", "eng", "--oem", "1", "--psm", str(psm), "--dpi", str(dpi),
               "-c", "preserve_interword_spaces=1", "tsv"]
    try:
        done = subprocess.run(command, input=png, capture_output=True, timeout=timeout, check=False,
                              env={"PATH": "/usr/bin:/bin", "OMP_THREAD_LIMIT": "1", "LC_ALL": "C"})
    except subprocess.TimeoutExpired:
        raise OcrError("timeout") from None
    if done.returncode != 0:
        raise OcrError("failed")
    return done.stdout.decode("utf-8", errors="replace")
