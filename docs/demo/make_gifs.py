"""Record the README demo GIFs from the Scalar API page of a running FreightRAG API.

Run it in Docker, with the API already up (`docker compose up -d --build extract api`), from the repository root:

  docker run --rm -e ONLY=upload,get,search --add-host host.docker.internal:host-gateway \\
    -v "$PWD/docs/demo:/demo:ro" -v "$PWD/docs/images:/out" -v "<folder with the PDF>:/work" \\
    mcr.microsoft.com/playwright/python:v1.49.1-noble sh -c \\
    "pip install -q --target /tmp/pw playwright==1.49.1 Pillow==11.0.0 && \\
     PYTHONPATH=/tmp/pw python /demo/make_gifs.py http://host.docker.internal:8080 /work/<new-file>.pdf"

The upload step needs a PDF the tenant has not ingested yet (a repeat returns "skipped" and the script stops), and its
name must start with a document type such as `rate-confirmation`. Set DOC_ID=<id> to run only the `get` step. The Scalar page already holds the public test key
of tenant Nile Freight, so no secret is read or typed here, and the key shows masked.
Each GIF is a series of screenshots joined by Pillow.
"""
import io
import os
import re
import sys
from pathlib import Path

from PIL import Image
from playwright.sync_api import sync_playwright

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://host.docker.internal:8080"
SAMPLE = sys.argv[2] if len(sys.argv) > 2 else "/work/rate-confirmation-51001.pdf"
OUT = Path(sys.argv[3] if len(sys.argv) > 3 else "/out")
WIDTH = 960
ONLY = os.environ.get("ONLY", "upload,get,search").split(",")


class Recorder:
    def __init__(self, page):
        self.page, self.frames = page, []

    def snap(self, ms=1200):
        img = Image.open(io.BytesIO(self.page.screenshot())).convert("RGB")
        img = img.resize((WIDTH, int(img.height * WIDTH / img.width)), Image.LANCZOS)
        self.frames.append((img, ms))

    def save(self, name):
        imgs = [f for f, _ in self.frames]
        imgs[0].save(OUT / name, save_all=True, append_images=imgs[1:], duration=[d for _, d in self.frames],
                     loop=0, optimize=True)
        print(name, len(imgs), "frames", (OUT / name).stat().st_size // 1024, "KB")


def open_client(pg, nav, button):
    pg.goto(BASE + "/scalar/v1", wait_until="networkidle")
    pg.get_by_role("button", name="Open Group - Search").click() if nav.startswith("Find") else None
    pg.get_by_text(re.compile(nav)).first.click()
    pg.wait_for_timeout(800)
    pg.get_by_role("button", name=button).first.click()
    pg.wait_for_timeout(1200)


SCROLL_JS = """(dy) => {
  const els = [...document.querySelectorAll('*')].filter(e => {
    const r = e.getBoundingClientRect(), o = getComputedStyle(e).overflowY;
    return r.left > 640 && r.width > 300 && e.scrollHeight > e.clientHeight + 20 && (o === 'auto' || o === 'scroll');
  });
  const el = els[els.length - 1];
  if (el) el.scrollBy(0, dy);
  return !!el;
}"""


def scroll_response(pg, rec, steps, dy=330):
    for _ in range(steps):
        if not pg.evaluate(SCROLL_JS, dy):
            print("warning: nothing to scroll")
        pg.wait_for_timeout(500)
        rec.snap(1500)


def upload(pg):
    """Upload a scanned PDF: the response carries the extraction report and the fields."""
    open_client(pg, "Ingest one", "Test Request (post /api/v1/documents)")
    rec = Recorder(pg)
    rec.snap(1500)
    with pg.expect_file_chooser() as fc:
        pg.get_by_text("Select File").first.click()
    fc.value.set_files(SAMPLE)
    pg.wait_for_timeout(400)
    rec.snap(1500)
    pg.get_by_role("button", name="Send").first.click()
    status = pg.get_by_text(re.compile(r"^\d{3} (Created|OK|Unprocessable Content)")).first.inner_text(timeout=60000)
    if not status.startswith("201"):
        sys.exit(f"Upload returned {status!r} (already ingested, or rejected): pick a new file")
    pg.wait_for_timeout(500)
    rec.snap(2000)
    doc_id = pg.get_by_text(re.compile(r'^"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"$')).first.inner_text().strip('"')
    scroll_response(pg, rec, 4, 330)
    rec.save("scalar-upload-pdf.gif")
    return doc_id


def get_fields(pg, doc_id):
    """Get one document, asking only for some fields."""
    open_client(pg, "Get one document", "Test Request (get /api/v1/documents/{id})")
    rec = Recorder(pg)
    rec.snap(1200)
    pg.mouse.click(480, 336)
    pg.keyboard.press("End")
    pg.keyboard.press("Shift+Home")
    pg.keyboard.press("Delete")
    pg.keyboard.type(doc_id, delay=15)
    pg.mouse.click(480, 567)
    pg.keyboard.type("loadNumber,lineHaul,totalAgreedRate", delay=70)
    rec.snap(1800)
    pg.get_by_role("button", name="Send").first.click()
    pg.wait_for_selector("text=200 OK", timeout=30000)
    pg.wait_for_timeout(500)
    rec.snap(2500)
    scroll_response(pg, rec, 2)
    rec.save("scalar-get-fields.gif")


def search(pg):
    open_client(pg, "Find the caller", "Test Request (post /api/v1/search)")
    rec = Recorder(pg)
    rec.snap(1200)
    pg.locator(".cm-content").first.click()
    pg.keyboard.press("ControlOrMeta+a")
    pg.keyboard.type('{"query": "detention rate for load 48213", "topK": 3}', delay=40)
    rec.snap(1800)
    pg.get_by_role("button", name="Send").first.click()
    pg.wait_for_selector("text=200 OK", timeout=60000)
    pg.wait_for_timeout(500)
    rec.snap(2500)
    scroll_response(pg, rec, 2)
    rec.save("scalar-search.gif")


with sync_playwright() as p:
    browser = p.chromium.launch()
    pg = browser.new_page(viewport={"width": 1280, "height": 800})
    doc_id = os.environ.get("DOC_ID", "")
    if "upload" in ONLY:
        doc_id = upload(pg)
    if "get" in ONLY:
        get_fields(pg, doc_id)
    if "search" in ONLY:
        search(pg)
    browser.close()
