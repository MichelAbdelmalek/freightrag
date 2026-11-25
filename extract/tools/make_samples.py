"""Sample freight documents (invented, all fictional): three loads, each with a rate confirmation, a bill of lading and an
invoice, in the same shape as data/*.txt. Written as .txt, as text-layer PDFs and as scanned PDFs.

    python -m tools.make_samples --out /out
"""

import argparse
from decimal import Decimal
from pathlib import Path

from tools.make_fixtures import scanned_pdf, text_pdf

CARRIER = "Blue Ridge Freight Co."
CARRIER_LINE = "Blue Ridge Freight Co. (MC# 655120, DOT# 2894417)"


def money(value: Decimal) -> str:
    return f"${value:,.2f}"


LOADS = [
    {
        "load": "51001", "bol": "MEM-420118", "invoice": "BRF-2026-0101", "issued": "2026-11-03", "ship": "2026-11-05", "delivered": "2026-11-06",
        "broker": "Magnolia Logistics Group, 410 Union Ave, Memphis, TN 38103 (MC# 481907)",
        "pickup": ("Delta Cold Storage Hub, 2200 Frontage Rd, Memphis, TN 38109", "Delta Cold Storage", "2026-11-05, 06:00 to 10:00"),
        "drop": ("Carolina Grocery Distribution Center, Dock 12, 880 Commerce Dr, Charlotte, NC 28206", "2026-11-06, deliver by 15:00"),
        "freight": ["Commodity: Frozen seafood", "Quantity: 24 pallets, 36,000 lbs", "Equipment: 53 ft refrigerated trailer",
                    "Temperature: Keep at 0 F or below. Loads arriving above 10 F may be rejected."],
        "line_haul": Decimal("3100.00"), "fuel": Decimal("265.00"), "detention_rate": Decimal("60.00"), "free_hours": 2,
        "special": ["Driver must call dispatch at 901-555-0142 one hour before arrival at pickup.",
                    "Reefer must be pre-cooled to 0 F before loading.", "Seal number must be recorded on the bill of lading."],
        "driver": "Marcus Webb", "truck": "BRF-305", "trailer": "BRF-R2210", "seal": "DC-770412",
        "consignee_name": "Carolina Grocery Distribution Center", "consignee_addr": "880 Commerce Dr, Charlotte, NC 28206",
        "shipper_addr": "2200 Frontage Rd, Memphis, TN 38109", "shipper": "Delta Cold Storage",
        "cargo": "24 pallets of frozen seafood, total weight 36,000 lbs", "freight_class": "65", "hazmat": "None",
        "delivered_at": "11:20", "received_by": "Priya Raman, Receiving Manager",
        "condition": "24 pallets received at 0 F. No exceptions noted.", "seal_intact": "Yes",
        "detention_hours": Decimal("2.0"), "extra": [], "to": "Magnolia Logistics Group, 410 Union Ave, Memphis, TN 38103",
    },
    {
        "load": "51014", "bol": "DAL-310045", "invoice": "BRF-2026-0108", "issued": "2026-11-07", "ship": "2026-11-09", "delivered": "2026-11-09",
        "broker": "Lone Star Freight Brokers LLC, 1500 Elm St, Dallas, TX 75201 (MC# 530264)",
        "pickup": ("Texas Building Supply, 4100 Irving Blvd, Dallas, TX 75247", "Texas Building Supply", "2026-11-09, 07:00 to 11:00"),
        "drop": ("Sooner Home Centers, Receiving Door 3, 920 NW 10th St, Oklahoma City, OK 73106", "2026-11-09, deliver by 18:00"),
        "freight": ["Commodity: Building materials (drywall and trim)", "Quantity: 20 pallets, 41,200 lbs", "Equipment: 53 ft dry van"],
        "line_haul": Decimal("1150.00"), "fuel": Decimal("98.00"), "detention_rate": Decimal("45.00"), "free_hours": 2,
        "special": ["Driver must call dispatch at 214-555-0176 one hour before arrival at pickup.",
                    "Load must be secured with load bars; the receiver may refuse shifted freight."],
        "driver": "Dana Ortiz", "truck": "BRF-118", "trailer": "BRF-T4402", "seal": "TB-338907",
        "consignee_name": "Sooner Home Centers", "consignee_addr": "920 NW 10th St, Oklahoma City, OK 73106",
        "shipper_addr": "4100 Irving Blvd, Dallas, TX 75247", "shipper": "Texas Building Supply",
        "cargo": "20 pallets of drywall and trim, total weight 41,200 lbs", "freight_class": "70", "hazmat": "None",
        "delivered_at": "16:05", "received_by": "Glenn Ashby, Dock Supervisor",
        "condition": "20 pallets received. No exceptions noted.", "seal_intact": "Yes",
        "detention_hours": Decimal("0"), "extra": [], "to": "Lone Star Freight Brokers LLC, 1500 Elm St, Dallas, TX 75201",
    },
    {
        "load": "51027", "bol": "ATL-420377", "invoice": "BRF-2026-0115", "issued": "2026-11-12", "ship": "2026-11-14", "delivered": "2026-11-15",
        "broker": "Peachtree Carrier Network Inc., 77 Marietta St NW, Atlanta, GA 30303 (MC# 612345)",
        "pickup": ("Southern Steel Service Center, 1900 Fulton Industrial Blvd, Atlanta, GA 30336", "Southern Steel Service Center", "2026-11-14, 08:00 to 12:00"),
        "drop": ("Music City Fabrication, Gate B, 3300 Murfreesboro Pike, Nashville, TN 37217", "2026-11-15, deliver by 14:00"),
        "freight": ["Commodity: Steel coils", "Quantity: 3 coils, 44,800 lbs", "Equipment: 48 ft flatbed with tarps"],
        "line_haul": Decimal("1875.00"), "fuel": Decimal("160.00"), "detention_rate": Decimal("55.00"), "free_hours": 2,
        "special": ["Driver must call dispatch at 404-555-0129 one hour before arrival at pickup.",
                    "Tarping is required and is paid at $75.00 per load as an accessorial.", "Coils must be chained and blocked per the shipper's diagram."],
        "driver": "Terrence Hall", "truck": "BRF-421", "trailer": "BRF-F6618", "seal": "SS-905533",
        "consignee_name": "Music City Fabrication", "consignee_addr": "3300 Murfreesboro Pike, Nashville, TN 37217",
        "shipper_addr": "1900 Fulton Industrial Blvd, Atlanta, GA 30336", "shipper": "Southern Steel Service Center",
        "cargo": "3 steel coils, total weight 44,800 lbs", "freight_class": "50", "hazmat": "None",
        "delivered_at": "10:45", "received_by": "Hector Valdez, Yard Foreman",
        "condition": "3 coils received. Coil 2 had a dented outer wrap; inspected, no damage to the steel. Exception noted.", "seal_intact": "Yes",
        "detention_hours": Decimal("0"), "extra": [("Tarping (accessorial per rate confirmation)", Decimal("75.00"))],
        "to": "Peachtree Carrier Network Inc., 77 Marietta St NW, Atlanta, GA 30303",
    },
]


def rate_confirmation(d: dict) -> str:
    total = d["line_haul"] + d["fuel"]
    lines = [
        "RATE CONFIRMATION", "",
        f"Broker: {d['broker']}", f"Carrier: {CARRIER_LINE}", f"Load number: {d['load']}", f"Date issued: {d['issued']}", "",
        "PICKUP", f"Location: {d['pickup'][0]}", f"Shipper: {d['pickup'][1]}", f"Date and window: {d['pickup'][2]}", "",
        "DELIVERY", f"Location: {d['drop'][0]}", f"Date: {d['drop'][1]}", "",
        "FREIGHT", *d["freight"], "",
        "RATE", f"Line haul: {money(d['line_haul'])}", f"Fuel surcharge: {money(d['fuel'])}", f"Total agreed rate: {money(total)}",
        f"Detention: {money(d['detention_rate'])} per hour after {d['free_hours']} free hours at pickup or delivery", "",
        "PAYMENT TERMS", "Net 30 days from receipt of signed proof of delivery (POD) and carrier invoice.", "",
        "SPECIAL INSTRUCTIONS", *d["special"],
    ]
    return "\n".join(lines) + "\n"


def bill_of_lading(d: dict) -> str:
    lines = [
        "STRAIGHT BILL OF LADING - NOT NEGOTIABLE", "",
        f"BOL number: {d['bol']}", f"Load number: {d['load']}", f"Ship date: {d['ship']}", "",
        "SHIPPER", d["shipper"], d["shipper_addr"], "",
        "CONSIGNEE", d["consignee_name"], d["consignee_addr"], "",
        "CARRIER", CARRIER, f"Driver: {d['driver']}", f"Truck number: {d['truck']}, Trailer number: {d['trailer']}", f"Seal number: {d['seal']}", "",
        "SHIPMENT DETAILS", d["cargo"], f"Freight class: {d['freight_class']}", f"Hazardous materials: {d['hazmat']}", "",
        "DELIVERY RECEIPT (PROOF OF DELIVERY)", f"Delivered: {d['delivered']} at {d['delivered_at']}", f"Received by: {d['received_by']}",
        f"Condition: {d['condition']}", f"Seal intact on arrival: {d['seal_intact']}",
    ]
    return "\n".join(lines) + "\n"


def invoice(d: dict) -> str:
    charges = [("Line haul", d["line_haul"]), ("Fuel surcharge", d["fuel"])]
    if d["detention_hours"] > 0:
        charges.append((f"Detention at delivery ({d['detention_hours']} billable hours at {money(d['detention_rate'])}/hour)",
                        d["detention_rate"] * d["detention_hours"]))
    charges += d["extra"]
    total = sum(amount for _, amount in charges)
    issued_day = int(d["issued"][-2:]) + 4
    month = int(d["issued"][5:7])
    invoice_date = f"2026-{month:02d}-{issued_day:02d}"
    due = f"2026-{month + 1:02d}-{issued_day:02d}"
    lines = [
        "INVOICE", "",
        f"From: {CARRIER}, 410 River Rd, Memphis, TN 38104", f"Bill to: {d['to']}", "",
        f"Invoice number: {d['invoice']}", f"Invoice date: {invoice_date}", f"Load number: {d['load']}", f"BOL number: {d['bol']}", "",
        "CHARGES", *[f"{name}: {money(amount)}" for name, amount in charges], f"Total due: {money(total)}", "",
        f"Payment due date: {due} (Net 30)",
        "Remit payment by ACH. Banking details are provided separately on request; never send payment to details received by email without phone verification.",
        "Questions: billing@blueridgefreight.example",
    ]
    return "\n".join(lines) + "\n"


def build() -> dict[str, str]:
    documents = {}
    for d in LOADS:
        documents[f"rate-confirmation-{d['load']}"] = rate_confirmation(d)
        documents[f"bill-of-lading-{d['bol']}"] = bill_of_lading(d)
        documents[f"invoice-{d['invoice']}"] = invoice(d)
    return documents


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    for folder in ("txt", "pdf-text", "pdf-scan"):
        (args.out / folder).mkdir(parents=True, exist_ok=True)
    for name, text in build().items():
        lines = text.splitlines()
        (args.out / "txt" / f"{name}.txt").write_text(text, encoding="utf-8")
        (args.out / "pdf-text" / f"{name}.pdf").write_bytes(text_pdf(lines))
        (args.out / "pdf-scan" / f"{name}.pdf").write_bytes(scanned_pdf(lines, rotate=0.7, seed=7))
        print(name)
