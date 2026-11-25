"""PDF access through PDFium (pypdfium2). Untrusted input: nothing here executes anything in the file.

`init_forms()` is never called, so PDF forms, XFA and JavaScript stay inactive, and attachments are never read.
"""

import io

import pypdfium2 as pdfium
import pypdfium2.raw as pdfium_raw
from PIL import Image

from freightrag_extract.signature import is_pdf


class ExtractError(Exception):
    """A rejection with a fixed code (not_pdf, corrupt_pdf, encrypted_pdf, ...). Never carries document content."""

    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


def open_document(data: bytes) -> pdfium.PdfDocument:
    if not is_pdf(data):
        raise ExtractError("not_pdf")
    try:
        document = pdfium.PdfDocument(data)
    except pdfium.PdfiumError as error:
        raise ExtractError("encrypted_pdf" if getattr(error, "err_code", None) == pdfium_raw.FPDF_ERR_PASSWORD else "corrupt_pdf") from None
    except Exception:       # noqa: BLE001 - any parser failure on untrusted input is just "corrupt"
        raise ExtractError("corrupt_pdf") from None
    # -1 means "not encrypted". Anything else is encryption, including files that open without a password
    # because only an owner password is set: we refuse all of them.
    if pdfium_raw.FPDF_GetSecurityHandlerRevision(document.raw) != -1:
        document.close()
        raise ExtractError("encrypted_pdf")
    return document


def page_size(page: pdfium.PdfPage) -> tuple[float, float]:
    return page.get_size()


def page_text(page: pdfium.PdfPage) -> str:
    textpage = page.get_textpage()
    try:
        return textpage.get_text_range()
    finally:
        textpage.close()


def render_gray(page: pdfium.PdfPage, dpi: int, max_pixels: int) -> Image.Image:
    Image.MAX_IMAGE_PIXELS = max_pixels       # Pillow refuses (and warns about) decompression bombs above this
    bitmap = page.render(scale=dpi / 72, may_draw_forms=False)
    return bitmap.to_pil().convert("L")


def to_png(image: Image.Image) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()
