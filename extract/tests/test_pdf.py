import pytest

from freightrag_extract import extractor as ex
from freightrag_extract.config import Settings
from freightrag_extract.ocr import Line, OcrPage
from freightrag_extract.pdf import ExtractError

SETTINGS = Settings()


def forbid_ocr(*_args):
    raise AssertionError("Tesseract must not run for a page that has a text layer")


def fake_ocr(_image, _dpi, _settings):
    return OcrPage([Line("RATE", 0), Line("Detention: $5O.OO per hour", 0)], mean_confidence=92.0, low_confidence_numeric_tokens=1)


def code_of(data, **kwargs):
    with pytest.raises(ExtractError) as info:
        ex.extract(data, SETTINGS, **kwargs)
    return info.value.code


def test_a_text_layer_pdf_is_read_without_ocr(pdfs):
    result = ex.extract(pdfs["rate-confirmation-48213-text.pdf"], SETTINGS, ocr=forbid_ocr)

    assert "Load number: 48213" in result.text and "Detention: $50.00 per hour" in result.text
    report = result.report
    assert (report["ocrPageCount"], report["textLayerPageCount"], report["meanOcrConfidence"]) == (0, report["pageCount"], None)
    assert {p["method"] for p in report["pages"]} == {"text_layer"}
    assert report["headingCount"] >= 5 and "no_headings" not in report["warnings"]


def test_a_scanned_pdf_goes_through_ocr_and_its_numbers_are_corrected(pdfs):
    result = ex.extract(pdfs["rate-confirmation-48213-scan.pdf"], SETTINGS, ocr=fake_ocr)

    assert result.text == "RATE\nDetention: $50.00 per hour"
    report = result.report
    assert (report["ocrPageCount"], report["meanOcrConfidence"], report["numericCorrections"], report["lowConfidenceNumericTokens"]) == (1, 92.0, 1, 1)
    assert report["pages"][0] == {"page": 1, "method": "ocr", "characterCount": len(result.text), "meanConfidence": 92.0, "dpi": 300}
    assert report["warnings"] == ["numeric_tokens_low_confidence"]


@pytest.mark.parametrize(("name", "code"), [
    ("corrupt.pdf", "corrupt_pdf"), ("fake.pdf", "not_pdf"), ("empty.pdf", "not_pdf"), ("encrypted.pdf", "encrypted_pdf"),
    ("too-many-pages.pdf", "too_many_pages"), ("huge-page.pdf", "page_too_large")])
def test_bad_pdfs_are_rejected_with_a_fixed_code_and_never_reach_ocr(pdfs, name, code):
    assert code_of(pdfs[name], ocr=forbid_ocr) == code


def test_a_blank_scan_has_no_text(pdfs):
    assert code_of(pdfs["blank-scan.pdf"], ocr=lambda *_: OcrPage([], None, 0)) == "no_text"


def test_a_scan_that_cannot_be_read_is_unreadable(pdfs):
    assert code_of(pdfs["rate-confirmation-48213-scan.pdf"],
                   ocr=lambda *_: OcrPage([Line("xq", 0)], mean_confidence=12.0)) == "unreadable"


def test_low_confidence_is_a_warning_not_a_rejection(pdfs):
    result = ex.extract(pdfs["rate-confirmation-48213-scan.pdf"], SETTINGS,
                        ocr=lambda *_: OcrPage([Line("RATE", 0), Line("Total 123", 0)], mean_confidence=65.0))
    assert "low_confidence" in result.report["warnings"]


def test_a_failed_tesseract_becomes_a_limit_error(pdfs, monkeypatch):
    from freightrag_extract import ocr
    monkeypatch.setattr(ocr, "run_tesseract", lambda *a, **k: (_ for _ in ()).throw(ocr.OcrError("timeout")))
    assert code_of(pdfs["rate-confirmation-48213-scan.pdf"]) == "extraction_limit_exceeded"
