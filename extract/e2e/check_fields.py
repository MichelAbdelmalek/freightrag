"""Measures how well the document fields are read from the realistic sample documents (needs the stack running).

    docker compose up -d --build extract api
    python3 extract/e2e/check_fields.py samples-realistic

Uploads each scanned PDF with the Nile key and each clean PDF with the Delta key (it leaves them in those tenants: use a
throwaway database), compares the returned `fields` with a table of PRINTED, label-addressable facts, and checks that
GET /documents/{id} returns exactly what the upload returned. Handwriting and unlabeled table cells are not in the table.
Standard library only. Keys come from the project's User Secrets and are never printed.
"""

import json
import sys
import urllib.error
import urllib.request
import uuid
from pathlib import Path

API = "http://127.0.0.1:8080"
SECRETS = Path.home() / ".microsoft/usersecrets/bc38a00e-bd57-4c78-9333-1f52ff1438ed/secrets.json"

TRUTH = {
    "rate-confirmation-52010": {
        "loadNumber": "52010", "dateIssued": "2026-11-17", "brokerName": "Southeast Carrier Services Inc.", "brokerMc": "744120",
        "carrierName": "Blue Ridge Freight Co.", "carrierMc": "655120", "carrierDot": "2894417", "lineHaul": "$2,780.00",
        "fuelSurcharge": "$255.44", "totalAgreedRate": "$3,135.44",
        "detention": "$60.00 per hour after 2 free hours at pickup or delivery.",
        "paymentTerms": "Net 30 days from receipt of signed proof of delivery (POD) and carrier invoice."},
    "bill-of-lading-CLT-330915": {
        "bolNumber": "CLT-330915", "loadNumber": "52010", "shipDate": "2026-11-19", "shipperName": "Carolina Beverage Co.",
        "shipperAddress": "3100 Tryon St, Charlotte, NC 28217", "consigneeName": "Gulf Coast Grocers",
        "consigneeAddress": "410 Marina Blvd, Mobile, AL 36602", "carrierName": "Blue Ridge Freight Co.", "driverName": "Marcus Webb",
        "truckNumber": "BRF-305", "trailerNumber": "BRF-T4408", "sealNumber": "CB-662031", "hazardousMaterials": "None"},
    "invoice-BRF-2026-0127": {
        "invoiceNumber": "BRF-2026-0127", "invoiceDate": "2026-11-21", "loadNumber": "52010", "bolNumber": "CLT-330915",
        "carrierName": "Blue Ridge Freight Co.", "carrierAddress": "410 River Rd, Memphis, TN 38104",
        "billToName": "Southeast Carrier Services Inc.", "billToAddress": "900 Ponce de Leon Ave, Atlanta, GA 30306",
        "lineHaul": "$2,780.00", "fuelSurcharge": "$255.44", "detention": "$90.00", "totalDue": "$3,225.44",
        "paymentDueDate": "2026-12-21", "paymentTerms": "Net 30", "billingContact": "billing@blueridgefreight.example"},
    "bill-of-lading-ATL-501288": {
        "bolNumber": "ATL-501288", "loadNumber": "52022", "shipDate": "2026-11-14", "shipperName": "Southern Steel Service Center",
        "shipperAddress": "1900 Fulton Industrial Blvd, Atlanta, GA 30336", "consigneeName": "Music City Fabrication",
        "consigneeAddress": "3300 Murfreesboro Pike, Nashville, TN 37217", "carrierName": "Blue Ridge Freight Co.",
        "driverName": "Terrence Hall", "truckNumber": "BRF-421", "trailerNumber": "BRF-F6618", "totalWeight": "44,800 lbs"},
}


def key(name: str) -> str:
    return json.loads(SECRETS.read_text(encoding="utf-8-sig"))[f"ApiKeys:{name}"]


def call(method, path, api_key, body=None, content_type=None):
    headers = {"X-Api-Key": api_key, **({"Content-Type": content_type} if content_type else {})}
    request = urllib.request.Request(API + path, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=600) as response:
            return response.status, json.loads(response.read() or b"null")
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read() or b"null")


def upload(pdf: Path, api_key: str):
    boundary = uuid.uuid4().hex
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{pdf.name}"\r\nContent-Type: application/pdf\r\n\r\n'.encode()
            + pdf.read_bytes() + f"\r\n--{boundary}--\r\n".encode())
    return call("POST", "/api/v1/documents", api_key, body, f"multipart/form-data; boundary={boundary}")


def main(root: Path) -> int:
    totals = {"clean": [0, 0, 0, 0], "scanned": [0, 0, 0, 0]}      # correct, missed, wrong, facts
    problems = 0
    for variant, tenant in (("scanned", "tenant-nile-freight"), ("clean", "tenant-delta-haul")):
        api_key = key(tenant)
        for name, truth in TRUTH.items():
            status, body = upload(root / variant / f"{name}.pdf", api_key)
            if status == 200:     # already uploaded earlier: find it through the list
                _, listing = call("GET", "/api/v1/documents?pageSize=100", api_key)
                match = [d for d in listing["items"] if d["title"] == name]
                body = {"id": match[0]["id"], "fields": None} if match else None
            if status not in (200, 201) or body is None:
                print(f"{variant:8} {name}: upload failed ({status}) {body.get('code') if body else ''}")
                problems += 1
                continue
            _, got = call("GET", f"/api/v1/documents/{body['id']}", api_key)
            fields = got["fields"]
            if body.get("fields") is not None and body["fields"] != fields:
                print(f"{variant:8} {name}: POST and GET returned different fields")
                problems += 1
            correct = [k for k, v in truth.items() if fields.get(k) == v]
            missed = [k for k, v in truth.items() if fields.get(k) is None]
            wrong = {k: fields.get(k) for k, v in truth.items() if fields.get(k) not in (None, v)}
            for i, count in enumerate((len(correct), len(missed), len(wrong), len(truth))):
                totals[variant][i] += count
            print(f"{variant:8} {name}: correct {len(correct)}/{len(truth)}, missed {missed}, wrong {wrong}")
    print()
    for variant, (c, m, w, n) in totals.items():
        print(f"{variant:8} {c}/{n} correct ({c / n:.0%}), {m} missed, {w} wrong")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1])))
