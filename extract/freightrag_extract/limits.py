"""Pure limits on untrusted PDFs: how many pages, how big a page, and at what DPI it may be rendered."""

import math


def check_page_count(pages: int, maximum: int) -> str | None:
    """None when fine, otherwise the rejection code."""
    if pages < 1:
        return "no_text"
    if pages > maximum:
        return "too_many_pages"
    return None


def choose_dpi(width_pt: float, height_pt: float, dpi: int, max_pixels: int, min_dpi: int, max_points: int) -> int | None:
    """The DPI to render a page at: the wanted one, lowered (never below min_dpi) so the image stays under max_pixels.
    None means the page is too large to read safely (reject it as page_too_large)."""
    if width_pt <= 0 or height_pt <= 0 or max(width_pt, height_pt) > max_points:
        return None
    chosen = dpi
    while chosen > min_dpi and (width_pt / 72 * chosen) * (height_pt / 72 * chosen) > max_pixels:
        chosen = max(min_dpi, chosen - 25)
    if (width_pt / 72 * chosen) * (height_pt / 72 * chosen) > max_pixels:
        return None
    return chosen


def pixels(width_pt: float, height_pt: float, dpi: int) -> int:
    return math.ceil(width_pt / 72 * dpi) * math.ceil(height_pt / 72 * dpi)
