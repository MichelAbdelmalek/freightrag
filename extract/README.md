# FreightRAG PDF extractor

Turns a PDF into plain text: it copies the **text layer**, and runs **OCR** (Tesseract) only on pages that are scans.
It is called by the .NET API, never by users, over an internal Docker network. Why, how and its limits:
[`docs/articles/pdf-ingestion.md`](../docs/articles/pdf-ingestion.md).

```
POST /v1/extract   Content-Type: application/pdf, body = the raw bytes   (no tenant, no file name)
-> 200 { "text": "…", "report": { pageCount, ocrPageCount, meanOcrConfidence, warnings, … } }
-> 422 problem+json with a "code": not_pdf, corrupt_pdf, encrypted_pdf, too_many_pages, page_too_large, no_text, unreadable,
        extraction_limit_exceeded      503 extractor_busy (Retry-After)      413, 415
```

## Run and test

```bash
docker compose up -d --build extract api                        # no published port: only the API reaches it
docker compose --profile extract run --build --rm extract-tests # unit tests + OCR accuracy on synthetic scans, no network
```

End to end against the running stack (uploads a scan of a made-up load, then searches for it):

```bash
docker build -q --target test --build-context data=./data -t freightrag-extract-test extract
docker run --rm --network none -v "$PWD/extract/.fixtures":/out --entrypoint python freightrag-extract-test \
    tools/make_fixtures.py --out /out --synthetic-load 59001
python3 extract/e2e/e2e.py extract/.fixtures/rate-confirmation-59001.pdf
```

That upload stays in the database of the tenant whose key is used (there is no delete endpoint yet): use a throwaway database.
Timing and memory on your machine: `docker run --rm --network none --cpus 1 --memory 768m --entrypoint python freightrag-extract-test tools/bench.py`.

## Settings (environment variables, validated at start)

`MAX_PAGES` 20, `MAX_PAGE_POINTS` 1584, `OCR_DPI` 300, `MIN_DPI` 150, `MAX_PAGE_PIXELS` 12000000, `MIN_TEXT_LAYER_CHARS` 20,
`LOW_CONFIDENCE` 80, `MIN_CONFIDENCE` 40, `PAGE_TIMEOUT_S` 60, `REQUEST_DEADLINE_S` 180, `WORKER_MEMORY_MB` 640, `QUEUE_LIMIT` 1,
`TESSERACT_PSM` 6 (one uniform block: measured to be robust to tilted scans, unlike mode 4).

## Dependencies

Python 3.14 and every package are pinned in `uv.lock`; `exclude-newer` in `pyproject.toml` applies the two-week rule to
transitive packages too. Tesseract comes from Debian bookworm, pinned to the exact packaged versions in the `Dockerfile`
(`tesseract-ocr` 5.3.0-2, `tesseract-ocr-eng` 1:4.1.0-2; released in 2023). To update the Python packages: move the date, run
`uv lock` in the image (`docker build --target tools -t freightrag-extract-tools extract`, then
`docker run --rm -v "$PWD/extract":/extract -w /extract freightrag-extract-tools uv lock`), and commit `uv.lock`.
