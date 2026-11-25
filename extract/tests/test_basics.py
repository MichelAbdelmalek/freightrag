import pytest

from freightrag_extract.config import ConfigError, Settings
from freightrag_extract.limits import check_page_count, choose_dpi
from freightrag_extract.signature import is_pdf

LETTER = (612, 792)


@pytest.mark.parametrize(("data", "expected"), [
    (b"%PDF-1.7\n", True), (b"%PDF-", True), (b" %PDF-1.7", False), (b"%PDF", False), (b"INVOICE\nTotal", False), (b"", False)])
def test_signature_is_percent_pdf_dash_at_offset_zero(data, expected):
    assert is_pdf(data) is expected


def test_letter_keeps_300_dpi():
    assert choose_dpi(*LETTER, dpi=300, max_pixels=12_000_000, min_dpi=150, max_points=1584) == 300


def test_a_big_page_gets_a_lower_dpi_to_stay_under_the_pixel_cap():
    a3 = (842, 1191)
    chosen = choose_dpi(*a3, dpi=300, max_pixels=12_000_000, min_dpi=150, max_points=1584)
    assert 150 <= chosen < 300
    assert (842 / 72 * chosen) * (1191 / 72 * chosen) <= 12_000_000


def test_a_huge_page_is_refused():
    assert choose_dpi(14400, 14400, dpi=300, max_pixels=12_000_000, min_dpi=150, max_points=1584) is None
    assert choose_dpi(1584, 1584, dpi=300, max_pixels=5_000_000, min_dpi=150, max_points=1584) is None   # fits the points, not the pixels


def test_page_counts():
    assert check_page_count(1, 20) is None and check_page_count(20, 20) is None
    assert check_page_count(21, 20) == "too_many_pages"
    assert check_page_count(0, 20) == "no_text"


def test_settings_defaults_and_validation():
    s = Settings.from_environ({})
    assert (s.max_pages, s.ocr_dpi, s.tesseract_psm, s.request_deadline_s) == (20, 300, 6, 180)
    for name, value in [("MAX_PAGES", "21"), ("OCR_DPI", "abc"), ("MIN_DPI", "400"), ("PAGE_TIMEOUT_S", "0")]:
        with pytest.raises(ConfigError, match=name):
            Settings.from_environ({name: value})


def test_settings_round_trip_through_the_environment_the_worker_reads():
    original = Settings(worker_memory_mb=300, tesseract_psm=3, max_pages=5)
    assert Settings.from_environ(original.to_environ()).worker_memory_mb == 300
    assert Settings.from_environ(original.to_environ()) == Settings(**{**original.__dict__, "queue_limit": 1, "max_body_bytes": original.max_body_bytes})
