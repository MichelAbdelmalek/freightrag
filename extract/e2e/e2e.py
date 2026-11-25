"""End-to-end check against the running stack (api + extract + Ollama + Postgres): upload a scanned PDF, then find it.

    docker compose up -d --build extract api
    docker build -q --target test --build-context data=../data -t freightrag-extract-test extract
    docker run --rm -v "$PWD/extract/.fixtures":/out freightrag-extract-test python tools/make_fixtures.py --out /out --synthetic-load 59001
    python3 extract/e2e/e2e.py extract/.fixtures/rate-confirmation-59001.pdf

Standard library only. The API key comes from the project's User Secrets (or API_KEY) and is never printed.
"""

import json
import os
import sys
import urllib.error
import urllib.request
import uuid
from pathlib import Path

API = os.environ.get("FREIGHTRAG_API_URL", "http://127.0.0.1:8080")
SECRETS = Path.home() / ".microsoft/usersecrets/bc38a00e-bd57-4c78-9333-1f52ff1438ed/secrets.json"


def key() -> str:
    if os.environ.get("API_KEY"):
        return os.environ["API_KEY"]
    return json.loads(SECRETS.read_text(encoding="utf-8-sig"))["ApiKeys:tenant-nile-freight"]


def call(method, path, headers, body=None, content_type=None):
    request = urllib.request.Request(API + path, data=body, method=method, headers={**headers, **({"Content-Type": content_type} if content_type else {})})
    try:
        with urllib.request.urlopen(request, timeout=600) as response:
            return response.status, json.loads(response.read() or b"null")
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read() or b"null")


def upload(pdf: Path, headers):
    boundary = uuid.uuid4().hex
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{pdf.name}\"\r\nContent-Type: application/pdf\r\n\r\n".encode()
            + pdf.read_bytes() + f"\r\n--{boundary}--\r\n".encode())
    return call("POST", "/api/v1/documents", headers, body, f"multipart/form-data; boundary={boundary}")


def main(path: str) -> int:
    pdf = Path(path)
    headers = {"X-Api-Key": key()}
    load = pdf.stem.rsplit("-", 1)[-1]
    status, body = upload(pdf, headers)
    print(f"upload: HTTP {status}")
    if status == 200:
        print("  already ingested (skipped): continuing with the search check")
    elif status != 201:
        print(f"  FAILED: {body.get('code')} {body.get('detail')}")
        return 1
    else:
        extraction = body["extraction"]
        print(f"  pages={extraction['pageCount']} ocr_pages={extraction['ocrPageCount']} confidence={extraction['meanOcrConfidence']} "
              f"headings={extraction['headingCount']} corrections={extraction['numericCorrections']} warnings={extraction['warnings']} "
              f"chunks={body['chunkCount']}")
        if extraction["ocrPageCount"] < 1 or "low_confidence" in extraction["warnings"]:
            print("  FAILED: expected an OCR'd page without a low_confidence warning")
            return 1

    def search(mode):
        status, hits = call("POST", "/api/v1/search", headers, json.dumps({"query": f"detention rate for load {load}", "topK": 3, "mode": mode}).encode(), "application/json")
        return status, hits

    def is_the_scan(hit):
        return hit["title"] == f"rate-confirmation-{load}" and f"Load: {load}" in hit["content"] and "Detention: $50.00 per hour" in hit["content"]

    # Keyword mode must put the scanned RATE chunk first (the load number is an exact word). Hybrid mode must have it in the
    # top 3: the vector leg may rank a look-alike rate chunk first, exactly as it does for the original .txt documents.
    kw_status, kw = search("keyword")
    hy_status, hy = search("hybrid")
    print(f"search keyword: HTTP {kw_status}, first hit {kw[0]['title']}; hybrid: HTTP {hy_status}, top 3 {[h['title'] for h in hy]}")
    ok = kw_status == 200 and hy_status == 200 and is_the_scan(kw[0]) and any(is_the_scan(h) for h in hy)
    print("PASS" if ok else "FAIL: the scanned document was not found with the right chunk")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
