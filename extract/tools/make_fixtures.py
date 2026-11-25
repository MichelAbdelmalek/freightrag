"""Synthetic PDFs for tests, generated from the project's own .txt documents. Nothing is committed: run in Docker.

(a) a text-layer PDF, (b) a scanned-style PDF (pages drawn as images, slightly rotated, with noise, embedded image-only),
and the negative cases: corrupt, fake (text named .pdf), encrypted, empty, too many pages, a huge page, a blank scan.
Run:  python tools/make_fixtures.py --out /out
"""

import argparse
import io
import random
import textwrap
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"
LETTER_PT = (612, 792)


def text_pdf(lines: list[str], pagesize=LETTER_PT, per_page: int = 60, encrypt: str | None = None) -> bytes:
    from reportlab.lib.pdfencrypt import StandardEncryption
    from reportlab.pdfgen import canvas

    buffer = io.BytesIO()
    kwargs = {"encrypt": StandardEncryption(encrypt, canPrint=1)} if encrypt else {}
    c = canvas.Canvas(buffer, pagesize=pagesize, invariant=1, **kwargs)
    top = pagesize[1] - 54
    for start in range(0, max(len(lines), 1), per_page):
        c.setFont("Helvetica", 10)
        for i, line in enumerate(lines[start:start + per_page]):
            c.drawString(54, top - i * 12, line)
        c.showPage()
    c.save()
    return buffer.getvalue()


def blank_pages_pdf(pages: int, pagesize=LETTER_PT) -> bytes:
    from reportlab.pdfgen import canvas

    buffer = io.BytesIO()
    c = canvas.Canvas(buffer, pagesize=pagesize, invariant=1)
    for n in range(pages):
        c.drawString(54, 700, f"page {n + 1} has a line of text")
        c.showPage()
    c.save()
    return buffer.getvalue()


def _page_image(lines: list[str], rotate: float, rng: random.Random, dpi: int = 300) -> Image.Image:
    width, height = int(8.5 * dpi), int(11 * dpi)
    image = Image.new("L", (width, height), 255)
    font = ImageFont.truetype(FONT, int(10 * dpi / 72 * 1.05))
    draw = ImageDraw.Draw(image)
    y = int(0.75 * dpi)
    for line in lines:
        draw.text((int(0.75 * dpi), y), line, font=font, fill=0)
        y += int(font.size * 1.4)
    if rotate:
        image = image.rotate(rotate, resample=Image.BICUBIC, fillcolor=255)
    if rng is not None:
        pixels = image.load()
        for _ in range(width * height // 400):                 # light salt-and-pepper style noise
            x, yy = rng.randrange(width), rng.randrange(height)
            pixels[x, yy] = max(0, min(255, pixels[x, yy] + rng.randint(-60, 60)))
    return image


def scanned_pdf(lines: list[str], rotate: float = 1.0, seed: int = 42, per_page: int = 50, dpi: int = 300) -> bytes:
    rng = random.Random(seed)
    # A real page wraps long lines; drawing them whole would run off the paper and clip the text (some documents have
    # lines of 150 characters). Wrapping changes only where lines break, which the accuracy tests ignore (they compare
    # whitespace-squashed text); headings are short and are never wrapped.
    lines = [wrapped for line in lines for wrapped in (textwrap.wrap(line, 95, break_long_words=False) or [""])]
    chunks = [lines[i:i + per_page] for i in range(0, max(len(lines), 1), per_page)] or [[]]
    images = [_page_image(c, rotate, rng, dpi) for c in chunks]
    buffer = io.BytesIO()
    images[0].save(buffer, "PDF", resolution=dpi, save_all=True, append_images=images[1:])
    return buffer.getvalue()


def blank_scan_pdf() -> bytes:
    return scanned_pdf([], rotate=0.0)


def build_all(data_dir: Path) -> dict[str, bytes]:
    source = (data_dir / "tenant-nile-freight" / "rate-confirmation-48213.txt").read_text(encoding="utf-8").splitlines()
    text = text_pdf(source)
    return {
        "rate-confirmation-48213-text.pdf": text,
        "rate-confirmation-48213-scan.pdf": scanned_pdf(source),
        "corrupt.pdf": text[:400] + b"\x00garbage" * 40,
        "fake.pdf": b"INVOICE\nTotal: $1\n",
        "encrypted.pdf": text_pdf(source, encrypt="secret"),
        "empty.pdf": b"",
        "too-many-pages.pdf": blank_pages_pdf(21),
        "huge-page.pdf": blank_pages_pdf(1, pagesize=(14400, 14400)),
        "blank-scan.pdf": blank_scan_pdf(),
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--data", type=Path, default=Path("/data"))
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    for name, content in build_all(args.data).items():
        (args.out / name).write_bytes(content)
        print(f"{name}: {len(content)} bytes")
