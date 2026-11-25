"""One PDF in, plain text and a report out. Text layer first; OCR only for pages that have no usable text.

`ocr` is injectable so the logic is testable without Tesseract; the default runs the real thing.
"""

import time
from collections.abc import Callable
from dataclasses import dataclass

from PIL import Image

from freightrag_extract import limits, normalise, ocr as ocr_module
from freightrag_extract.config import Settings
from freightrag_extract.pdf import ExtractError, open_document, page_size, page_text, render_gray, to_png
from freightrag_extract.report import count_headings, warnings_for

OcrFunction = Callable[[Image.Image, int, Settings], ocr_module.OcrPage]


def tesseract_ocr(image: Image.Image, dpi: int, settings: Settings) -> ocr_module.OcrPage:
    try:
        tsv = ocr_module.run_tesseract(to_png(image), dpi, settings.tesseract_psm, settings.page_timeout_s)
    except ocr_module.OcrError:
        raise ExtractError("extraction_limit_exceeded") from None
    return ocr_module.parse_tsv(tsv)


@dataclass(frozen=True)
class Extraction:
    text: str
    report: dict


def _usable_text_layer(text: str, minimum: int) -> bool:
    visible = [c for c in text if not c.isspace()]
    return len(visible) >= minimum and text.count("�") / max(len(text), 1) < 0.05


def extract(data: bytes, settings: Settings, ocr: OcrFunction = tesseract_ocr) -> Extraction:
    started = time.perf_counter()
    document = open_document(data)
    try:
        pages = len(document)
        if (code := limits.check_page_count(pages, settings.max_pages)) is not None:
            raise ExtractError(code)

        # Check every page's size before doing any work: a huge page must not cost us the earlier pages' OCR time.
        dpis = []
        for index in range(pages):
            width, height = page_size(document[index])
            dpi = limits.choose_dpi(width, height, settings.ocr_dpi, settings.max_page_pixels, settings.min_dpi, settings.max_page_points)
            if dpi is None:
                raise ExtractError("page_too_large")
            dpis.append(dpi)

        texts, page_reports = [], []
        weighted, weight = 0.0, 0
        corrections = low_numeric = 0
        for index in range(pages):
            page = document[index]
            layer = page_text(page)
            if _usable_text_layer(layer, settings.min_text_layer_chars):
                text, fixed = normalise.normalise_page(layer, numeric_fix=False)     # a text layer has no OCR look-alikes
                method, confidence, used_dpi = "text_layer", None, 0
            else:
                result = ocr(render_gray(page, dpis[index], settings.max_page_pixels), dpis[index], settings)
                raw = "\n".join(
                    ("\n" if i and line.paragraph != result.lines[i - 1].paragraph else "") + line.text
                    for i, line in enumerate(result.lines))
                text, fixed = normalise.normalise_page(raw)
                method, confidence, used_dpi = "ocr", result.mean_confidence, dpis[index]
                low_numeric += result.low_confidence_numeric_tokens
                if confidence is not None:
                    weighted += confidence * max(len(text), 1)
                    weight += max(len(text), 1)
            corrections += fixed
            texts.append(text)
            page_reports.append({"page": index + 1, "method": method, "characterCount": len(text),
                                 "meanConfidence": None if confidence is None else round(confidence, 1), "dpi": used_dpi})
    finally:
        document.close()

    joined = normalise.join_pages(texts)
    if not joined.strip():
        raise ExtractError("no_text")
    mean = round(weighted / weight, 1) if weight else None
    if mean is not None and mean < settings.min_confidence:
        raise ExtractError("unreadable")

    headings = count_headings(joined)
    ocr_pages = sum(1 for p in page_reports if p["method"] == "ocr")
    reduced = any(d < settings.ocr_dpi for d, p in zip(dpis, page_reports) if p["method"] == "ocr")
    report = {
        "pageCount": pages, "ocrPageCount": ocr_pages, "textLayerPageCount": pages - ocr_pages,
        "characterCount": len(joined), "meanOcrConfidence": mean, "headingCount": headings,
        "numericCorrections": corrections, "lowConfidenceNumericTokens": low_numeric,
        "durationMs": int((time.perf_counter() - started) * 1000), "pages": page_reports,
        "warnings": warnings_for(mean, headings, low_numeric, reduced, settings.low_confidence),
    }
    return Extraction(joined, report)
