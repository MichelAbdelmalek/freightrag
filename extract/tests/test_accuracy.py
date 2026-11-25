"""Real Tesseract on synthetic scans of real project documents. Thresholds are the pass/fail criteria of the plan."""

import re

import pytest

from freightrag_extract import extractor as ex
from freightrag_extract.config import Settings
from freightrag_extract.report import looks_like_heading
from tests.conftest import DATA
from tools.make_fixtures import scanned_pdf

DOCS = [("tenant-nile-freight", "rate-confirmation-48213.txt"),
        ("tenant-nile-freight", "invoice-NFL-2026-1187.txt"),
        ("tenant-nile-freight", "bill-of-lading-SAV-778431.txt")]
MUST_CONTAIN = {
    "rate-confirmation-48213.txt": ["Load number: 48213", "Detention: $50.00 per hour", "Total agreed rate: $2,630.00"],
    "invoice-NFL-2026-1187.txt": ["NFL-2026-1187"],
    "bill-of-lading-SAV-778431.txt": ["SAV-778431"],
}


def levenshtein(a, b):
    previous = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        current = [i]
        for j, y in enumerate(b, 1):
            current.append(min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (x != y)))
        previous = current
    return previous[-1]


def squash(text):
    return re.sub(r"\s+", " ", text).strip()


def ocr_of(tenant, name, rotate=1.0):
    source = (DATA / tenant / name).read_text(encoding="utf-8")
    return source, ex.extract(scanned_pdf(source.splitlines(), rotate=rotate), Settings())


@pytest.mark.parametrize("rotate", [0.0, 1.0, 2.0])
@pytest.mark.parametrize(("tenant", "name"), DOCS)
def test_scanned_documents_are_read_accurately(tenant, name, rotate):
    source, result = ocr_of(tenant, name, rotate)

    cer = levenshtein(squash(source), squash(result.text)) / len(squash(source))
    source_words, ocr_words = squash(source).split(" "), squash(result.text).split(" ")
    wer = levenshtein(source_words, ocr_words) / len(source_words)
    assert cer <= 0.03, f"character error rate {cer:.3f}"
    assert wer <= 0.08, f"word error rate {wer:.3f}"
    for expected in MUST_CONTAIN[name]:
        assert expected in result.text, expected

    headings = {line.strip() for line in source.splitlines() if looks_like_heading(line)}
    found = {line.strip() for line in result.text.splitlines()}
    assert len(headings & found) / len(headings) >= 0.9, sorted(headings - found)
    assert result.report["meanOcrConfidence"] >= 80
