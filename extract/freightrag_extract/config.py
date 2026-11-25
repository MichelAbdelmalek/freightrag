"""Settings from environment variables, validated once at startup so a typo fails fast and names the variable."""

from collections.abc import Mapping
from dataclasses import dataclass


class ConfigError(Exception):
    pass


def _int(environ: Mapping[str, str], name: str, default: int, low: int, high: int) -> int:
    raw = environ.get(name, str(default)).strip()
    try:
        value = int(raw)
    except ValueError:
        raise ConfigError(f"{name} must be a whole number, got {raw!r}.") from None
    if not low <= value <= high:
        raise ConfigError(f"{name} must be between {low} and {high}, got {value}.")
    return value


@dataclass(frozen=True)
class Settings:
    max_pages: int = 20
    max_page_points: int = 1584          # 22 inches; Letter is 612 x 792
    ocr_dpi: int = 300
    min_dpi: int = 150
    max_page_pixels: int = 12_000_000    # about 12 MP: Letter at 300 DPI is 8.4 MP
    min_text_layer_chars: int = 20       # fewer characters than this and the page is treated as a scan
    low_confidence: int = 80             # mean OCR confidence below this raises the low_confidence warning
    min_confidence: int = 40             # below this the document is rejected as unreadable
    page_timeout_s: int = 60
    request_deadline_s: int = 180
    worker_memory_mb: int = 640
    queue_limit: int = 1
    tesseract_psm: int = 6               # one uniform block of text: robust to small skews, unlike mode 4 (measured)
    max_body_bytes: int = 10 * 1024 * 1024

    def to_environ(self) -> dict[str, str]:
        """The settings as environment variables, for the worker process (which reads them with from_environ)."""
        return {"MAX_PAGES": str(self.max_pages), "MAX_PAGE_POINTS": str(self.max_page_points), "OCR_DPI": str(self.ocr_dpi),
                "MIN_DPI": str(self.min_dpi), "MAX_PAGE_PIXELS": str(self.max_page_pixels),
                "MIN_TEXT_LAYER_CHARS": str(self.min_text_layer_chars), "LOW_CONFIDENCE": str(self.low_confidence),
                "MIN_CONFIDENCE": str(self.min_confidence), "PAGE_TIMEOUT_S": str(self.page_timeout_s),
                "REQUEST_DEADLINE_S": str(self.request_deadline_s), "WORKER_MEMORY_MB": str(self.worker_memory_mb),
                "TESSERACT_PSM": str(self.tesseract_psm)}

    @classmethod
    def from_environ(cls, environ: Mapping[str, str]) -> "Settings":
        dpi = _int(environ, "OCR_DPI", 300, 100, 600)
        min_dpi = _int(environ, "MIN_DPI", 150, 72, 600)
        if min_dpi > dpi:
            raise ConfigError(f"MIN_DPI ({min_dpi}) must not be above OCR_DPI ({dpi}).")
        return cls(
            max_pages=_int(environ, "MAX_PAGES", 20, 1, 20),
            max_page_points=_int(environ, "MAX_PAGE_POINTS", 1584, 100, 14400),
            ocr_dpi=dpi, min_dpi=min_dpi,
            max_page_pixels=_int(environ, "MAX_PAGE_PIXELS", 12_000_000, 100_000, 50_000_000),
            min_text_layer_chars=_int(environ, "MIN_TEXT_LAYER_CHARS", 20, 1, 10_000),
            low_confidence=_int(environ, "LOW_CONFIDENCE", 80, 1, 100),
            min_confidence=_int(environ, "MIN_CONFIDENCE", 40, 0, 99),
            page_timeout_s=_int(environ, "PAGE_TIMEOUT_S", 60, 1, 600),
            request_deadline_s=_int(environ, "REQUEST_DEADLINE_S", 180, 5, 900),
            worker_memory_mb=_int(environ, "WORKER_MEMORY_MB", 640, 128, 4096),
            queue_limit=_int(environ, "QUEUE_LIMIT", 1, 0, 10),
            tesseract_psm=_int(environ, "TESSERACT_PSM", 6, 0, 13),
        )
