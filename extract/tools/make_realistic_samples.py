"""Harder, more realistic freight paperwork: ruled tables, handwritten notes, signatures, rubber stamps and scan damage.

All people, companies and numbers are invented. Each document is rendered twice: "clean" (the page as printed) and
"scanned" (tilt, uneven light, noise, JPEG, plus a fax or photocopy look where it fits) so that the effect of the damage can be
separated from the effect of tables and handwriting. A JSON file per document lists the printed facts a reader must get right
and the handwritten facts (which OCR is not expected to read: Tesseract is built for print).

Needs the fonts of fonts-dkg-handwriting and fonts-kristi (handwriting and signatures), used only to draw these samples:
    docker run --rm --user root --entrypoint sh freightrag-extract-test -c \
      'apt-get update -qq && apt-get install -y -qq fonts-dkg-handwriting fonts-kristi >/dev/null && python -m tools.make_realistic_samples --out /out'
"""

import argparse
import glob
import io
import json
import random
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

W, H, DPI = 2550, 3300, 300
INK = (28, 48, 150)          # blue ballpoint
STAMP = (170, 30, 30)        # red rubber stamp
SANS = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
SANS_BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"


def font_path(*patterns: str) -> str:
    for pattern in patterns:
        found = sorted(glob.glob(pattern, recursive=True))
        if found:
            return found[0]
    raise FileNotFoundError(f"font not found: {patterns}")


HAND = font_path("/usr/share/fonts/truetype/fifthhorseman/dkg.ttf")
SIGN = font_path("/usr/share/fonts/truetype/**/Kristi*.ttf")


def f(path: str, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(path, size)


class Page:
    def __init__(self, seed: int):
        self.rng = random.Random(seed)
        self.img = Image.new("RGB", (W, H), "white")
        self.d = ImageDraw.Draw(self.img)

    # ---- print ----
    def text(self, x, y, s, size=40, bold=False, fill=(15, 15, 15)):
        self.d.text((x, y), s, font=f(SANS_BOLD if bold else SANS, size), fill=fill)

    def table(self, x, y, widths, header, rows, size=36, aligns=None, bold_last=False):
        """Ruled table with a shaded header row. Returns the y below it."""
        aligns = aligns or ["l"] * len(widths)
        row_h = int(size * 2.0)
        total_w = sum(widths)
        self.d.rectangle([x, y, x + total_w, y + row_h], fill=(222, 222, 222))
        for r, cells in enumerate([header] + rows):
            top = y + r * row_h
            cx = x
            for c, cell in enumerate(cells):
                font = f(SANS_BOLD if r == 0 or (bold_last and r == len(rows)) else SANS, size)
                tw = self.d.textlength(cell, font=font)
                tx = cx + 14 if aligns[c] == "l" else cx + widths[c] - tw - 14
                self.d.text((tx, top + (row_h - size) // 2 - 4), cell, font=font, fill=(15, 15, 15))
                cx += widths[c]
        bottom = y + (len(rows) + 1) * row_h
        for r in range(len(rows) + 2):                                   # horizontal rules
            self.d.line([x, y + r * row_h, x + total_w, y + r * row_h], fill=(40, 40, 40), width=3 if r in (0, 1, len(rows) + 1) else 2)
        cx = x
        for w in [0] + widths:                                           # vertical rules
            cx += w
            self.d.line([cx, y, cx, bottom], fill=(40, 40, 40), width=2)
        return bottom

    # ---- ink ----
    def handwriting(self, x, y, s, size=64, angle=-1.5):
        """Handwriting font drawn letter by letter with jitter in size, slant, baseline and spacing, in blue ink."""
        font = f(HAND, size)
        cx = x
        for ch in s:
            if ch == " ":
                cx += size * 0.30 + self.rng.randint(-4, 8)
                continue
            glyph = Image.new("RGBA", (size * 2, size * 2), (0, 0, 0, 0))
            ImageDraw.Draw(glyph).text((size // 2, size // 3), ch, font=font, fill=INK + (self.rng.randint(185, 245),))
            glyph = glyph.rotate(self.rng.uniform(-7, 7) + angle, resample=Image.BICUBIC)
            self.img.paste(glyph, (int(cx - size // 2), int(y - size // 3 + self.rng.randint(-5, 5))), glyph)
            cx += font.getlength(ch) * self.rng.uniform(0.85, 1.05)
        return cx

    def signature(self, x, y, name, size=150):
        font = f(SIGN, size)
        layer = Image.new("RGBA", (int(font.getlength(name)) + 120, size * 2), (0, 0, 0, 0))
        ld = ImageDraw.Draw(layer)
        ld.text((40, size // 4), name, font=font, fill=INK + (230,))
        pts, px, py = [], 10, int(size * 1.45)                           # a flourish under the name
        while px < layer.width - 20:
            pts.append((px, py + self.rng.randint(-14, 14)))
            px += self.rng.randint(25, 60)
        ld.line(pts, fill=INK + (220,), width=5, joint="curve")
        layer = layer.rotate(self.rng.uniform(-4, 2), resample=Image.BICUBIC, expand=True)
        self.img.paste(layer, (x, y), layer)

    def initials(self, x, y, s="JL"):
        pts = [(x + i * 22 + self.rng.randint(-9, 9), y + self.rng.randint(-30, 30)) for i in range(7)]
        self.d.line(pts, fill=INK, width=6, joint="curve")
        self.handwriting(x + 18, y - 28, s, size=70, angle=-4)

    def check(self, x, y, size=58):
        """A hand-drawn X in a printed box."""
        self.d.rectangle([x, y, x + size, y + size], outline=(30, 30, 30), width=3)
        self.d.line([x + 8, y + 8, x + size - 6, y + size - 12], fill=INK, width=7)
        self.d.line([x + size - 8, y + 6, x + 10, y + size - 8], fill=INK, width=6)

    def stamp(self, x, y, line1, line2, angle=-9):
        layer = Image.new("RGBA", (900, 330), (0, 0, 0, 0))
        ld = ImageDraw.Draw(layer)
        ld.rounded_rectangle([8, 8, 892, 322], radius=26, outline=STAMP + (190,), width=12)
        ld.text((60, 40), line1, font=f(SANS_BOLD, 118), fill=STAMP + (190,))
        ld.text((70, 200), line2, font=f(SANS_BOLD, 66), fill=STAMP + (185,))
        for _ in range(260):                                             # ink that did not take
            cx, cy = self.rng.randrange(900), self.rng.randrange(330)
            ld.ellipse([cx, cy, cx + self.rng.randint(3, 9), cy + self.rng.randint(3, 9)], fill=(0, 0, 0, 0))
        layer = layer.rotate(angle, resample=Image.BICUBIC, expand=True)
        self.img.paste(layer, (x, y), layer)


def degrade(page: Page, style: str) -> Image.Image:
    """Turns the clean page into a scan, a fax or a photocopy."""
    rng = page.rng
    img = page.img.convert("L")
    if style == "fax":
        img = img.resize((W * 2 // 3, H * 2 // 3), Image.BILINEAR).resize((W, H), Image.NEAREST)   # low resolution
        d = ImageDraw.Draw(img)
        d.text((120, 40), "FAX  11-14-2026  16:42   FROM: 901 555 0142   TO: 404 555 0129   P.1/2", font=f(SANS, 34), fill=0)
        for _ in range(26):                                              # dropped scan lines
            y = rng.randrange(H)
            d.line([0, y, W, y], fill=255, width=rng.randint(1, 3))
        img = img.point(lambda v: 0 if v < 150 else 255).filter(ImageFilter.GaussianBlur(1.1))
    base = Image.linear_gradient("L") if rng.random() < 0.5 else Image.linear_gradient("L").rotate(90)   # square, so no black corners
    gradient = base.resize((W, H)).point(lambda v: 205 + v * 50 // 255)
    img = ImageChops.multiply(img, gradient)                                                      # uneven light
    if style == "photocopy":
        d = ImageDraw.Draw(img)
        for y in (480, 1650, 2820):                                      # three punched holes
            d.ellipse([80, y, 170, y + 90], fill=70)
        shadow = Image.linear_gradient("L").resize((260, H)).rotate(90).point(lambda v: 255 - (255 - v) * 130 // 255)
        img.paste(ImageChops.multiply(img.crop((0, 0, 260, H)), shadow), (0, 0))                  # shadow of the book spine
    img = img.rotate(rng.choice([-1, 1]) * rng.uniform(0.6, 1.6), resample=Image.BICUBIC, fillcolor=228)
    noise = Image.effect_noise((W, H), 14)
    img = ImageChops.add(img, noise, scale=1.0, offset=-128)
    return img.filter(ImageFilter.GaussianBlur(0.7))


def pdf_bytes(image: Image.Image, quality: int) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, "PDF", resolution=DPI, quality=quality)
    return buffer.getvalue()


# ------------------------------------------------------------------ the documents

def rate_confirmation(p: Page):
    p.text(150, 140, "RATE CONFIRMATION", 78, True)
    p.text(150, 260, "Broker: Southeast Carrier Services Inc., 900 Ponce de Leon Ave, Atlanta, GA 30306 (MC# 744120)", 33)
    p.text(150, 312, "Carrier: Blue Ridge Freight Co. (MC# 655120, DOT# 2894417)", 33)
    p.text(150, 364, "Load number: 52010          Date issued: 2026-11-17", 33)
    p.text(150, 470, "STOPS", 46, True)
    y = p.table(150, 540, [150, 260, 900, 540, 400], ["Stop", "Type", "Location", "Date and window", "Ref"], [
        ["1", "Pickup", "Carolina Beverage Co., 3100 Tryon St, Charlotte, NC", "2026-11-19, 06:00-10:00", "PU-88213"],
        ["2", "Delivery", "Gulf Coast Grocers, Dock 7, 410 Marina Blvd, Mobile, AL", "2026-11-20, by 16:00", "PO-440917"]], 30)
    p.text(150, y + 90, "RATE", 46, True)
    y = p.table(150, y + 160, [1000, 300, 500, 450], ["Description", "Qty", "Rate", "Amount"], [
        ["Line haul", "1", "$2,780.00", "$2,780.00"], ["Fuel surcharge (per mile)", "412", "$0.62", "$255.44"],
        ["Stop-off charge", "1", "$100.00", "$100.00"], ["Total agreed rate", "", "", "$3,135.44"]],
        32, ["l", "r", "r", "r"], bold_last=True)
    p.text(150, y + 70, "Detention: $60.00 per hour after 2 free hours at pickup or delivery.", 33)
    p.text(150, y + 125, "Payment: Net 30 days from receipt of signed proof of delivery (POD) and carrier invoice.", 33)
    p.text(150, y + 180, "Double brokering of this load is strictly prohibited.", 33)
    p.text(150, y + 330, "Broker:", 36, True)
    p.signature(330, y + 270, "Elena Ruiz")
    p.d.line([320, y + 450, 1100, y + 450], fill=(30, 30, 30), width=3)
    p.text(1300, y + 330, "Carrier accepted by:", 36, True)
    p.signature(1700, y + 260, "M. Webb")
    p.d.line([1690, y + 450, 2400, y + 450], fill=(30, 30, 30), width=3)
    p.text(1300, y + 480, "Date:", 36, True)
    p.handwriting(1420, y + 470, "11 / 18 / 26", 70)
    p.handwriting(150, y + 590, "pickup moved to 11/19 am - per Elena (JL)", 66, -2.5)
    p.initials(1950, y + 600)


def bill_of_lading(p: Page):
    p.text(150, 140, "STRAIGHT BILL OF LADING - NOT NEGOTIABLE", 62, True)
    p.text(150, 250, "BOL number: CLT-330915          Load number: 52010          Ship date: 2026-11-19", 34)
    p.text(150, 330, "SHIPPER", 40, True)
    p.text(150, 385, "Carolina Beverage Co., 3100 Tryon St, Charlotte, NC 28217", 33)
    p.text(150, 450, "CONSIGNEE", 40, True)
    p.text(150, 505, "Gulf Coast Grocers, 410 Marina Blvd, Mobile, AL 36602", 33)
    p.text(150, 570, "CARRIER", 40, True)
    p.text(150, 625, "Blue Ridge Freight Co.  Driver: Marcus Webb  Truck: BRF-305  Trailer: BRF-T4408  Seal: CB-662031", 33)
    y = p.table(150, 740, [200, 200, 950, 380, 250, 340], ["Pcs", "Pkg", "Description", "Weight (lbs)", "Class", "NMFC"], [
        ["10", "PLT", "Canned beverages, shrink-wrapped", "18,400", "55", "50080-02"],
        ["6", "PLT", "Empty plastic bottles, bagged", "2,100", "70", "156600-01"],
        ["16", "", "TOTAL", "20,500", "", ""]], 32, ["l", "l", "l", "r", "r", "r"], bold_last=True)
    p.text(150, y + 70, "Freight charges:   Prepaid", 34)
    p.check(780, y + 62)
    p.text(900, y + 70, "Collect", 34)
    p.check(1100, y + 62)
    p.text(150, y + 150, "Hazardous materials: None          Temperature control: Not required", 34)
    p.text(150, y + 260, "Shipper signature:", 34, True)
    p.signature(590, y + 190, "R. Whitfield")
    p.d.line([580, y + 360, 1250, y + 360], fill=(30, 30, 30), width=3)
    p.text(1350, y + 260, "Driver signature:", 34, True)
    p.signature(1700, y + 190, "Marcus Webb")
    p.d.line([1690, y + 360, 2400, y + 360], fill=(30, 30, 30), width=3)
    p.text(150, y + 480, "DELIVERY RECEIPT (PROOF OF DELIVERY)", 40, True)
    p.text(150, y + 545, "Delivered: 2026-11-20 at ____    Received by: ____________________    Condition:", 33)
    p.handwriting(1020, y + 520, "15:40", 70)
    p.handwriting(1730, y + 520, "P. Raman", 72)
    p.handwriting(150, y + 640, "OSD - 1 plt short, 1 plt crushed corner.  See driver notes.", 70, -2)
    p.stamp(1500, y + 650, "RECEIVED", "NOV 20 2026 GULF COAST", -10)
    p.text(150, y + 760, "Consignee signature:", 34, True)
    p.signature(590, y + 690, "P. Raman")
    p.d.line([580, y + 860, 1250, y + 860], fill=(30, 30, 30), width=3)


def invoice(p: Page):
    p.text(150, 140, "INVOICE", 84, True)
    p.text(150, 270, "From: Blue Ridge Freight Co., 410 River Rd, Memphis, TN 38104", 33)
    p.text(150, 322, "Bill to: Southeast Carrier Services Inc., 900 Ponce de Leon Ave, Atlanta, GA 30306", 33)
    p.text(150, 400, "Invoice number: BRF-2026-0127     Invoice date: 2026-11-21     Load number: 52010     BOL number: CLT-330915", 31)
    p.text(150, 500, "CHARGES", 46, True)
    y = p.table(150, 570, [850, 330, 260, 480, 460], ["Description", "Reference", "Qty", "Rate", "Amount"], [
        ["Line haul", "Load 52010", "1", "$2,780.00", "$2,780.00"],
        ["Fuel surcharge (per mile)", "412 miles", "412", "$0.62", "$255.44"],
        ["Stop-off charge", "per RC", "1", "$100.00", "$100.00"],
        ["Detention at delivery (hours)", "POD 15:40", "1.5", "$60.00", "$90.00"]], 31, ["l", "l", "r", "r", "r"])
    p.text(1500, y + 60, "Subtotal:", 36, True)
    p.text(2020, y + 60, "$3,225.44", 36)
    p.text(1500, y + 125, "Total due:", 42, True)
    p.text(1990, y + 120, "$3,225.44", 44, True)
    p.text(150, y + 260, "Payment due date: 2026-12-21 (Net 30)", 34)
    p.text(150, y + 320, "Remit payment by ACH. Banking details are provided separately on request; never send payment to", 30)
    p.text(150, y + 365, "details received by email without phone verification.   Questions: billing@blueridgefreight.example", 30)
    p.stamp(250, y + 480, "PAID", "11/28/2026  ACH", -8)
    p.text(1250, y + 520, "Check / ACH no.:", 34, True)
    p.handwriting(1650, y + 500, "ACH 40918", 72)
    p.text(1250, y + 640, "Approved:", 34, True)
    p.initials(1500, y + 660, "TM")
    p.handwriting(150, y + 790, "dispute detention? - no, signed POD attached", 64, -3)


def fax_bill_of_lading(p: Page):
    p.text(150, 190, "BILL OF LADING", 70, True)
    p.text(150, 300, "BOL number: ATL-501288     Load number: 52022     Ship date: 2026-11-14", 36)
    p.text(150, 390, "SHIPPER: Southern Steel Service Center, 1900 Fulton Industrial Blvd, Atlanta, GA 30336", 32)
    p.text(150, 445, "CONSIGNEE: Music City Fabrication, 3300 Murfreesboro Pike, Nashville, TN 37217", 32)
    p.text(150, 500, "CARRIER: Blue Ridge Freight Co.   Driver: Terrence Hall   Truck: BRF-421   Trailer: BRF-F6618", 32)
    y = p.table(150, 610, [180, 200, 1000, 360, 240, 370], ["Pcs", "Pkg", "Description", "Weight (lbs)", "Class", "Seal no."], [
        ["1", "COIL", "Hot rolled steel coil, 48 in", "14,950", "50", "SS-905533"],
        ["1", "COIL", "Hot rolled steel coil, 48 in", "15,100", "50", "SS-905534"],
        ["1", "COIL", "Cold rolled steel coil, 36 in", "14,750", "50", "SS-905535"]], 32, ["l", "l", "l", "r", "r", "l"])
    p.text(150, y + 70, "Total weight: 44,800 lbs", 36, True)
    p.handwriting(980, y + 50, "-> scale ticket 44,750", 66, -2)
    p.text(150, y + 200, "Tarps required: Yes", 34)
    p.check(620, y + 192)
    p.text(150, y + 330, "Shipper:", 34, True)
    p.signature(330, y + 260, "D. Alvarez")
    p.text(1300, y + 330, "Driver:", 34, True)
    p.signature(1480, y + 260, "T. Hall")
    p.stamp(1450, y + 470, "SHIPPED", "NOV 14 2026", 6)


def photocopy_pod(p: Page):
    p.text(150, 140, "PROOF OF DELIVERY", 76, True)
    p.text(150, 260, "Load number: 52035     BOL number: MEM-612044     Delivery date: 2026-11-22", 36)
    p.text(150, 340, "Carrier: Blue Ridge Freight Co.     Consignee: Delta Wholesale Foods, 77 Harbor Dr, Memphis, TN 38103", 31)
    y = p.table(150, 450, [1000, 700, 700], ["Event", "Scheduled", "Actual"], [
        ["Arrived at consignee", "08:00", ""], ["Started unloading", "", ""], ["Finished unloading", "", ""], ["Departed", "", ""]],
        34)
    for i, t in enumerate(["07:52", "08:25", "10:10", "10:25"]):
        p.handwriting(1900, 450 + (i + 1) * 68 + 10, t, 66)
    p.text(150, y + 80, "Pallets delivered: 22      Pallets refused: 0      Damage noted:", 34)
    p.handwriting(1780, y + 55, "none", 70)
    p.text(150, y + 180, "Lumper fee paid by carrier:   Yes", 34)
    p.check(900, y + 172)
    p.text(1050, y + 180, "No", 34)
    p.check(1180, y + 172)
    p.text(1330, y + 180, "Amount: $", 34)
    p.handwriting(1580, y + 162, "145.00", 72)
    p.text(150, y + 330, "Receiver name (print):", 34, True)
    p.handwriting(820, y + 310, "Angela Brooks", 74)
    p.text(150, y + 460, "Receiver signature:", 34, True)
    p.signature(640, y + 390, "A. Brooks", 160)
    p.d.line([630, y + 560, 1500, y + 560], fill=(30, 30, 30), width=3)
    p.stamp(1500, y + 360, "DELIVERED", "11/22/2026 MEMPHIS", -7)


DOCUMENTS = [
    # name, builder, style, printed facts that must be read exactly, handwritten facts (not expected from OCR)
    ("rate-confirmation-52010", rate_confirmation, "scan",
     ["Load number: 52010", "$2,780.00", "$255.44", "$3,135.44", "Carolina Beverage Co.", "Gulf Coast Grocers", "$60.00 per hour"],
     ["11 / 18 / 26", "pickup moved to 11/19 am", "Elena Ruiz", "M. Webb"]),
    ("bill-of-lading-CLT-330915", bill_of_lading, "scan",
     ["BOL number: CLT-330915", "Load number: 52010", "18,400", "20,500", "50080-02", "156600-01", "Seal: CB-662031"],
     ["15:40", "P. Raman", "OSD - 1 plt short", "R. Whitfield"]),
    ("invoice-BRF-2026-0127", invoice, "scan",
     ["Invoice number: BRF-2026-0127", "Load number: 52010", "BOL number: CLT-330915", "$2,780.00", "$255.44", "$90.00", "$3,225.44"],
     ["ACH 40918", "TM", "PAID"]),
    ("bill-of-lading-ATL-501288", fax_bill_of_lading, "fax",
     ["BOL number: ATL-501288", "Load number: 52022", "14,950", "15,100", "14,750", "SS-905533", "44,800"],
     ["scale ticket 44,750", "D. Alvarez", "T. Hall"]),
    ("proof-of-delivery-52035", photocopy_pod, "photocopy",
     ["Load number: 52035", "BOL number: MEM-612044", "Pallets delivered: 22", "Delta Wholesale Foods"],
     ["07:52", "08:25", "10:10", "10:25", "145.00", "Angela Brooks"]),
]


def render(name, builder, style, seed):
    page = Page(seed)
    builder(page)
    return page.img.convert("L"), degrade(page, style)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    for folder in ("clean", "scanned", "preview", "truth"):
        (args.out / folder).mkdir(parents=True, exist_ok=True)
    for seed, (name, builder, style, printed, handwritten) in enumerate(DOCUMENTS, start=11):
        clean, scanned = render(name, builder, style, seed)
        (args.out / "clean" / f"{name}.pdf").write_bytes(pdf_bytes(clean, 85))
        (args.out / "scanned" / f"{name}.pdf").write_bytes(pdf_bytes(scanned, 55))
        scanned.resize((W // 3, H // 3), Image.LANCZOS).save(args.out / "preview" / f"{name}.png")
        (args.out / "truth" / f"{name}.json").write_text(json.dumps({"printed": printed, "handwritten": handwritten}, indent=2))
        print(name, style)
