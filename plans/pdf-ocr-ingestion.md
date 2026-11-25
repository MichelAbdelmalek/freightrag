# Plan: PDF upload with OCR (searchable text) for FreightRAG

Drafted by a planning agent on 2026-11-25 from a read of the repo. Status: **proposed, not approved, nothing built yet.**
Usable by any coding agent without the earlier conversation. Verify each "based on the current code" statement before relying on it.

User decisions: extract **searchable text** (not structured JSON), and the PDFs are **scanned** (OCR needed). Text-layer PDFs must also work and must not be OCR'd when they already contain text.

## 0. Context (based on the current code)

- `POST /api/v1/documents` (`src/FreightRag.Api/Endpoints/DocumentEndpoints.cs`, `IngestAsync`) checks the file name (bare name, at most 200 characters, no control or format characters), accepts only `.txt` (anything else is `400 "Unsupported file"`), and caps the size at `MaxUploadBytes = 10 MB` (`413`). It reads the bytes and calls `IngestDocumentHandler.HandleAsync(new IngestDocumentCommand(tenantSlug, fileName, content))`. It maps `Ingested→201`, `Skipped→200` and `Rejected→422`. The upload is rate limited per tenant by the `ingest` policy (30 per minute).
- `IngestDocumentHandler` (`src/FreightRag.Core/Application/IngestDocument.cs`) runs these steps in order: resolve the tenant, then `DocType.TryFromFileName` (file-name prefix), then `SourceHash.FromContent(raw bytes)`, then `ExistsAsync` (the idempotency check, done before any expensive step), then a strict UTF-8 decode, then a NUL check, then `FreightDocument.Create` (title = `Path.GetFileNameWithoutExtension`, `DocumentIdentifiers.Extract`, `SectionChunker.Split`), then embed, then `AddAsync`.
- `SectionChunker.IsHeading` treats a line as a heading when it is 2–80 characters, has no lower-case letters, does not end with `.`, and at least half of its non-space characters are letters. Only the first line in a run of heading-shaped lines is a heading. Chunk header: `Document: <type> | Load: … | Section: <name>`. Text before the first heading goes into `(untitled)`. Bodies over 1,000 characters are split at paragraph or line boundaries.
- `DocumentIdentifiers` uses strict anchored regular expressions (`Load number: [0-9]+`, …). An OCR error such as `482l3` would silently drop the identifier from every chunk header.
- `FolderIngestor` (console) only enumerates `*.txt`.
- The `answer/` service is FastAPI + uvicorn, built with uv (`exclude-newer`, exact pins), on `python:3.14.10-slim-bookworm@sha256:…`, runs as non-root uid 1654, and in compose it has `read_only`, `cap_drop: ALL`, `no-new-privileges` and tmpfs `/tmp`. It is tested in CI by the `answer-tests` job.
- The `evals/tests/conftest.py` fixture `network_attempts` blocks sockets.
- Schema changes are made as EF Core migrations, plus a generated `db/NNN_*.sql` checked by `MigrationScriptTests` (regenerate with `UPDATE_DB_SCRIPTS=1`).
- `AppSettingsConsistencyTests` requires the `Tenants`, `Ollama` and `Database` sections to be identical in the Ingest, Api and Migrator `appsettings.json` files.

## 1. Goal and non-goals

**Goal:** a tenant uploads a `.pdf` (scanned, or with a text layer). Its text is extracted (OCR only for pages without usable text), normalised, and then chunked, embedded, searched and cited exactly like `.txt`. The response carries an extraction report that contains no document content.

**Non-goals:**
- Structured field or JSON extraction.
- Layout or table reconstruction.
- Handwriting.
- Languages other than English.
- Big vision or LLM OCR.
- A GPU.
- Keeping the PDF after processing.
- PDF support in the console `FolderIngestor` (it stays `.txt`; it gets a stub extractor, see §6).
- Re-OCR of stored documents.
- A delete endpoint.
- Form or XFA rendering, and running PDF JavaScript.

## 2. Architecture decision

**Recommendation: a new small Python service `extract/`, modelled on `answer/`.**
- It uses **pypdfium2** for both the text layer and rasterising.
- It uses **Tesseract 5 from Debian apt**, called through `subprocess` with an argument list and a TSV output.
- The .NET API calls it over an internal-only Docker network.
- Inside the container, each PDF is processed in a short-lived worker subprocess that has `setrlimit` limits, so a parser crash or a decompression bomb kills only that worker.

### Where the extraction runs

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Separate `extract` container** | Untrusted native parsers (PDFium, Leptonica, Tesseract) run away from the DB password and API-key hashes. Hard memory and CPU caps; an OOM kills only this container. No network egress. Same pattern as `answer/`. | One more image (about 250–350 MB, mostly shared layers with `answer`). One HTTP hop. | **Chosen** |
| In-process in the .NET API | No extra service. | Native PDF parsing in the process that holds secrets. A crash or OOM takes the API down. No per-request memory cap. | Rejected (security) |
| .NET in a sidecar | Same language. | Same image-size cost as Python, with weaker libraries (below). | Rejected |

### Which libraries

| Candidate | Licence | Notes | Verdict |
|---|---|---|---|
| Tesseract (apt `tesseract-ocr`, `tesseract-ocr-eng`) | Apache-2.0 | CPU only, about 100–250 MB RSS per page at 300 DPI. Gives word confidences (TSV). Mature. | **Chosen** OCR engine |
| pytesseract | Apache-2.0 | A thin wrapper that itself shells out to the binary. | Not needed. Calling `subprocess.run([...], input=png, timeout=…)` ourselves gives explicit timeouts, TSV parsing and one fewer dependency. |
| pypdfium2 (PDFium) | Apache-2.0 / BSD-3 | Chrome's heavily fuzzed engine. Text layer plus rendering in one parser. Self-contained wheel. JavaScript, forms and XFA are only active if `init_forms()` is called (we never call it). | **Chosen** |
| PyMuPDF | **AGPL-3.0** (or commercial) | AGPL in a network service creates source-disclosure obligations, which is unacceptable for an org codebase. | **Avoid** |
| pypdf / pdfminer.six | BSD / MIT | Pure Python, text layer only, no rasterising. pypdf has had DoS CVEs. Adding it would mean a second parser and a larger attack surface. | Not needed |
| OCRmyPDF | MPL-2.0 | Pulls Ghostscript (**AGPL**), qpdf and pikepdf. Output is a PDF rather than text. Heavy. | Rejected |
| Pure .NET (Tesseract NuGet wrapper + Docnet/PdfPig) | Apache/MIT | The wrapper is stale and needs native Linux libraries. PdfPig cannot rasterise. It would run inside the API. | Rejected |
| Vision LLM via Ollama | — | Several GB of RAM, competes with qwen3:8b, risks killing the Docker VM. | Rejected (hard constraint) |

### Flow

1. The client sends the upload to the API.
2. The API checks the file name (`.txt` or `.pdf`) and the size.
3. The handler resolves the tenant and the DocType (file-name prefix), checks the format and the `%PDF-` magic bytes, computes the hash, and checks whether the hash already exists (so nothing is re-OCR'd).
4. The handler calls `IPdfTextExtractor.ExtractAsync(bytes)`. This sends `POST http://extract:8091/v1/extract` with the PDF bytes only: no tenant and no file name.
5. The extractor returns `{text, report}`.
6. The API runs NUL and decode checks, then `FreightDocument.Create` (the unchanged chunker), embeds, and stores the document together with the report metadata.

Tenant isolation does not change: the tenant comes only from the API key and never leaves the API.

## 3. API contract (`POST /api/v1/documents`)

- **Request:** unchanged (multipart field `file`). The OpenAPI summary becomes "Ingest one .txt or .pdf document". The description adds: PDF, at most 20 pages, scanned pages are OCR'd (English), and it can take up to about 3 minutes.
- **201 response:** `IngestedResponse` gains a nullable `extraction`. It is `null` for `.txt`. For a PDF:

```json
{ "id": "…", "title": "rate-confirmation-48213", "docType": "rate-confirmation", "chunkCount": 7,
  "sourceFormat": "pdf",
  "extraction": { "pageCount": 1, "ocrPageCount": 1, "textLayerPageCount": 0, "characterCount": 1342,
    "meanOcrConfidence": 91.4, "lowConfidence": false, "headingCount": 7, "numericCorrections": 2,
    "lowConfidenceNumericTokens": 0, "durationMs": 8123,
    "pages": [ { "page": 1, "method": "ocr", "characterCount": 1342, "meanConfidence": 91.4, "dpi": 300 } ],
    "warnings": [] } }
```

`warnings` holds codes only: `low_confidence`, `no_headings`, `numeric_tokens_low_confidence`, `dpi_reduced`. `GET /api/v1/documents/{id}` and the list also return `sourceFormat`, and the GET also returns `extraction`.

- **Title:** `Path.GetFileNameWithoutExtension`, as today. A `.txt` and a `.pdf` with the same stem can coexist (different hashes, same title). This is documented, and evals use a separate tenant (§10).
- **DocType:** the file-name prefix, as today (`DocType.TryFromFileName` already accepts `rate-confirmation-48213.pdf`).

**Rejections** (RFC 9457; add a `code` extension member; `detail` is fixed text plus at most the file name, never extracted text):

| Case | Status | `code` |
|---|---|---|
| Bad file name, or an extension other than `.txt`/`.pdf` | 400 | `invalid_file_name` / `unsupported_file` |
| More than 10 MB | 413 | `too_large` |
| `.pdf` whose bytes do not start with `%PDF-` (for example a renamed text file) | 422 | `not_pdf` |
| Corrupt or unparsable PDF | 422 | `corrupt_pdf` |
| Encrypted or password-protected PDF (any security handler) | 422 | `encrypted_pdf` |
| More than 20 pages | 422 | `too_many_pages` |
| A page larger than 1,584 pt (22 in) on a side | 422 | `page_too_large` |
| Zero pages, or no text after OCR | 422 | `no_text` |
| Mean OCR confidence below the hard floor (40) | 422 | `unreadable` |
| Extractor's per-request deadline exceeded or worker killed by limits | 422 | `extraction_limit_exceeded` |
| Unknown type, invalid UTF-8, NUL (existing) | 422 | `unknown_doc_type` / `invalid_text` |
| Extractor busy (its queue is full) | 503 + `Retry-After: 30` | `extractor_busy` |
| Extractor unreachable or HTTP timeout | 503 | `extractor_unavailable` |
| Rate limit | 429 (existing) | — |

## 4. Extractor HTTP contract (internal only)

- `POST /v1/extract`, `Content-Type: application/pdf`, body = raw bytes (at most 10 MB, enforced by a `BodyLimit` middleware copied from `answer/app.py`). No other inputs: no file name, no tenant.
- **200:** `{"text": "<normalised text>", "report": {…same fields as above…}}`.
- **Errors:** `application/problem+json` with `code` (the values in the table above) and status `400`, `413`, `415`, `422`, `503` (busy, with `Retry-After`) or `504`.
- `GET /health` returns `{"status":"ok"}` and does not parse anything.
- **Logging:** one line per request, with the code, page count, OCR page count, mean confidence rounded, character count and milliseconds. Never text. Run uvicorn with `--no-access-log`.

## 5. Data / database impact

- **New migration `AddDocumentSourceFormat`:**
  - `documents.source_format text NOT NULL DEFAULT 'txt'`
  - `documents.extraction_report jsonb NULL` (numbers and codes only)
- **Files:**
  - `DocumentRecord`: add `SourceFormat` and `ExtractionReport` (string mapped to `HasColumnType("jsonb")`).
  - `FreightRagDbContext`: configure both columns.
  - Migration and snapshot via `dotnet ef migrations add AddDocumentSourceFormat` (run in the SDK container as in `docs/articles/database.md`).
  - `db/004_add_document_source_format.sql`, generated by running the tests with `UPDATE_DB_SCRIPTS=1`.
- **Repository and queries:** map them (`EfDocumentRepository`, `EfDocumentQueries`, `ReadModels`).
- **Domain:** `FreightDocument` gains `SourceFormat` and `ExtractionReport?`.
- `chunks` does not change.

## 6. File-by-file change list

**.NET, Domain (`src/FreightRag.Core/Domain/`, pure, no packages):**
- `SourceFormat.cs`: smart enum `Text` / `Pdf`.
  - `TryFromFileName(string, out SourceFormat?)`: `.txt` or `.pdf`, case-insensitive.
  - `static bool HasPdfSignature(ReadOnlySpan<byte>)`: the first 5 bytes are `%PDF-`. This is a deliberately strict policy: the spec tolerates the header anywhere in the first 1 KB, but we accept it at offset 0 only.
- `ExtractionReport.cs`: a record with the report fields, `PageReport`, and `ExtractionMethod` (`TextLayer` / `Ocr`).
  - The factory validates: `pageCount` 1–20; counts at least 0; confidence 0–100 or null for text-layer pages.
  - `bool IsLowConfidence(double threshold)`.
- `FreightDocument.Create(...)`: add optional `SourceFormat sourceFormat` and `ExtractionReport? report` parameters, with defaults that keep the existing call sites compiling.

**.NET, Application:**
- `Ports/IPdfTextExtractor.cs`: `Task<PdfExtraction> ExtractAsync(ReadOnlyMemory<byte> pdf, CancellationToken ct)`. Closed result: `PdfExtraction.Extracted(string Text, ExtractionReport Report)` or `PdfExtraction.Refused(string Code)`.
- Exceptions `PdfExtractorBusyException(TimeSpan retryAfter)` and `PdfExtractorUnavailableException`, defined in Application.
- `IngestResult.Rejected`: add `string Code = "rejected"`; set codes at every existing rejection site.
- `IngestResult.Ingested`: add `SourceFormat` and `ExtractionReport?`.
- `IngestDocument.cs`: after the DocType check, resolve `SourceFormat` from the file name; if it is `Pdf` and `!HasPdfSignature`, reject with `not_pdf`. Keep the hash and exists check before extraction. For `Pdf`, call the extractor, map `Refused` to `Rejected(code)` (fixed message per code, file name only), and use the text. For `.txt`, keep the current path. The NUL check applies to both.

**.NET, Infrastructure:** `Infrastructure/Extraction/HttpPdfTextExtractor.cs`
- Constructor takes `HttpClient`.
- Sends `ByteArrayContent` with `application/pdf`.
- Parses JSON into the domain report: unknown fields are ignored, missing fields are a failure.
- `503` throws `PdfExtractorBusyException` (reads `Retry-After`). `422`/`400`/`415` map to `Refused(code)`, with `code` taken from an allow-list. `HttpRequestException` or a timeout throws `PdfExtractorUnavailableException`.
- Never logs the body.

`Infrastructure/Extraction/UnavailablePdfTextExtractor.cs` is for the console Ingest host. It returns `Refused("pdf_api_only")`, but the console never sends PDFs anyway.

**.NET, API:**
- `Program.cs`: `AddHttpClient<IPdfTextExtractor, HttpPdfTextExtractor>` with `BaseAddress` from `Extractor:Url` (default `http://extract:8091`) and `Timeout` from `Extractor:TimeoutSeconds` (default 240, which must be greater than the extractor's 180 s deadline). Both are validated at startup in a new `Configuration/ExtractorSettings.cs` (pattern: `RateLimitSettings.Load`) and added to the fail-fast block.
- `appsettings.json`: add an `Extractor` section. This is API only; `AppSettingsConsistencyTests` checks only the shared sections.
- `DocumentEndpoints.cs`:
  - accept `.txt` and `.pdf`; error text "Only .txt and .pdf files are accepted.";
  - summary and description as in §3;
  - add `.ProducesProblem(503)`;
  - map `Rejected.Code` into `extensions["code"]`;
  - catch `PdfExtractorBusyException` → 503 with `Retry-After`, and `PdfExtractorUnavailableException` → 503;
  - extend `IngestedResponse` and `DocumentResponse` in `Contracts.cs`.
- `docs/openapi/v1.json`: regenerate through the snapshot test.

**.NET, Ingest:** `Program.cs` registers `UnavailablePdfTextExtractor`. `FolderIngestor` is unchanged (`*.txt`).

**.NET, tests:** see §7. `Support/Fakes.cs` gains `FakePdfTextExtractor` (records calls; scripted result or exception). `ApiFactory` swaps it in.

**Python `extract/`** (copy the layout of `answer/`):
- `pyproject.toml`:
  - `requires-python = "==3.14.*"`;
  - runtime: `fastapi`, `uvicorn`, `pypdfium2`, `pillow`, `pydantic`;
  - dev group: `pytest`;
  - `fixtures` group: `reportlab` (BSD; text-layer and encrypted fixtures);
  - `[tool.uv] package=false`, `exclude-newer` = about 2026-11-11, and the same pytest options.
- `uv.lock` and `.python-version` (3.14.10).
- `Dockerfile`, `.dockerignore`, `README.md`.
- `freightrag_extract/config.py`: env settings with validation, copying `answer/config.py` (`_int`, `_seconds`):
  - `MAX_PAGES=20`, `MAX_PAGE_POINTS=1584`, `OCR_DPI=300`, `MIN_DPI=150`, `MAX_PAGE_PIXELS=12_000_000`;
  - `MIN_TEXT_LAYER_CHARS=20`, `LOW_CONFIDENCE=80`, `MIN_CONFIDENCE=40`;
  - `PAGE_TIMEOUT_S=60`, `REQUEST_DEADLINE_S=180`, `WORKER_MEMORY_MB=640`, `QUEUE_LIMIT=1`, `TESSERACT_PSM=4`.
- `freightrag_extract/signature.py`: `is_pdf(data: bytes) -> bool`.
- `freightrag_extract/limits.py`: pure functions.
  - `choose_dpi(width_pt, height_pt, dpi, max_pixels, min_dpi) -> int | None` (None means reject `page_too_large`).
  - `check_page_count(n, max)`.
- `freightrag_extract/pdf.py`:
  - `open_document(data)`: `pdfium.PdfDocument(data)` with no password. Map a PDFium password error to `encrypted_pdf`, and any other `PdfiumError` to `corrupt_pdf`. Also reject when the raw security handler revision is not -1 (encryption with only an owner password); confirm the function in `pypdfium2.raw` (`FPDF_GetSecurityHandlerRevision`).
  - `page_text(page)`: text-layer text.
  - `render_gray(page, dpi)`: PIL `L` image.
  - Never call `init_forms()`; attachments and JavaScript are never touched.
- `freightrag_extract/ocr.py`: `run_tesseract(png: bytes, dpi, psm, timeout)` calls `subprocess.run(["tesseract","stdin","stdout","-l","eng","--oem","1","--psm",str(psm),"--dpi",str(dpi),"-c","preserve_interword_spaces=1","tsv"], input=png, capture_output=True, timeout=…, check=False, env={…minimal, OMP_THREAD_LIMIT=1})`. No `shell=True` ever, and no user-derived arguments. Pure `parse_tsv(tsv: str) -> list[Line]`, where each line has its words and confidences.
- `freightrag_extract/normalise.py`: pure functions (rules in §8).
- `freightrag_extract/report.py`: builds the report and the warnings.
- `freightrag_extract/worker.py`:
  - `python -I -m freightrag_extract.worker`;
  - first sets `resource.setrlimit` for `RLIMIT_AS` (`WORKER_MEMORY_MB`), `RLIMIT_CPU` (deadline), `RLIMIT_NOFILE` (64) and `RLIMIT_FSIZE` (50 MB), which Tesseract inherits;
  - reads the PDF from stdin and writes JSON to stdout;
  - exits with a code mapped to the error codes;
  - sets `Image.MAX_IMAGE_PIXELS = MAX_PAGE_PIXELS`.
- `freightrag_extract/app.py`:
  - FastAPI with a `BodyLimit`;
  - `asyncio.Semaphore(1)` plus a waiting counter: more than `QUEUE_LIMIT` waiters → 503 `extractor_busy`;
  - runs the worker with `asyncio.create_subprocess_exec` and `asyncio.wait_for(…, REQUEST_DEADLINE_S)`, and kills it on timeout;
  - `problem()` copied from `answer`.
- `tools/make_fixtures.py`: §7.3.
- `tests/`: `conftest.py` (socket blocking copied from `evals/tests/conftest.py`, plus session fixtures that generate the PDFs into `tmp_path_factory`), and the test files in §7.2.
- `e2e/test_pdf_e2e.py`: a manual or local check against the running stack (§10).

**Repo wiring:**
- `compose.yaml`: §9.
- `.github/workflows/ci.yml`: a new `extract-tests` job, a copy of `answer-tests` with `context: ./extract`, `target: test`, `build-contexts: data=./data`, `cache scope=extract`, and `docker run --rm freightrag-extract:ci`. Also a smoke test: `docker image inspect` reports a non-root user, and `tesseract --version` runs.
- `.github/dependabot.yml`: `uv` with `directory: /extract` (group `python-extract`) and `docker` with `directory: /extract`, with the same ignores as `/answer` (including the Python minor-version ignore).

**Documentation:**
- `docs/articles/pdf-ingestion.md` (new):
  - for beginners: a text layer versus a scan, what OCR is, the pipeline, why confidence matters, the warnings, limits and errors;
  - how to name files;
  - privacy: nothing is kept, and logs carry no text.
- `docs/articles/toc.yml`: add `- name: "PDF ingestion: text layer and OCR"` / `href: pdf-ingestion.md` (quoted because of the colon).
- `http-api.md`: table rows, a PDF curl example, and the 503 codes.
- `architecture.md`: the extractor box and the isolation rationale.
- `getting-started.md`: the run command and memory advice.
- `chunking.md`: an "OCR text" section on headings, page joins and `(untitled)`.
- `database.md`: migration 004.
- `README.md`: the command table.

## 7. TDD steps (each test written first and seen to fail)

Work on the branch `feature/pdf-ocr-ingestion` created from `main` (`git switch main && git pull && git switch -c feature/pdf-ocr-ingestion`).

### 7.1 .NET (run with `docker compose run --build --rm tests`)

1. `Domain/SourceFormatTests.cs`:
   - `Txt_and_pdf_extensions_are_recognised_case_insensitively`
   - `Other_extensions_are_not` (`.docx`, `.pdf.exe`, none)
   - `Pdf_signature_requires_percent_PDF_dash_at_offset_0` (`%PDF-1.7` true; `" %PDF-"`, `"INVOICE"`, empty, `%PDF` without the dash all false)
2. `Domain/DocTypeTests.cs`: `Type_is_inferred_from_pdf_file_names_too`. This may already pass; keep it as a guard.
3. `Domain/ExtractionReportTests.cs`:
   - `Rejects_page_count_outside_1_to_20`
   - `Confidence_must_be_0_to_100_or_null_for_text_layer_pages`
   - `IsLowConfidence_uses_the_threshold`
4. `Domain/FreightDocumentTests.cs`: `Pdf_document_keeps_its_format_and_report`; `Title_of_a_pdf_is_the_name_without_extension`.
5. `Application/IngestDocumentHandlerTests.cs`:
   - `Pdf_text_from_the_extractor_is_chunked_like_txt` (the fake returns the text of `rate-confirmation-48213.txt`; the section names equal those of the `.txt` ingest)
   - `Extractor_receives_exactly_the_uploaded_bytes_and_nothing_else`
   - `Pdf_without_signature_is_rejected_as_not_pdf_without_calling_the_extractor`
   - `Already_ingested_pdf_is_skipped_without_calling_the_extractor`
   - `Txt_never_calls_the_extractor`
   - `Extractor_refusal_codes_become_rejections_with_the_same_code` (theory over the codes)
   - `Extracted_text_with_NUL_is_rejected`
   - `Rejection_messages_never_contain_extracted_text` (the fake text contains the sentinel `ZEBRA-OCR-CONTENT`)
6. `Infrastructure/HttpPdfTextExtractorTests.cs` (use a stub `HttpMessageHandler`; no network):
   - `Posts_application_pdf_to_v1_extract`
   - `Maps_200_json_to_Extracted`
   - `Maps_422_code_to_Refused`
   - `Unknown_code_maps_to_corrupt_pdf`
   - `503_throws_busy_with_retry_after`
   - `Connection_failure_and_timeout_throw_unavailable`
7. `Api/DocumentEndpointsTests.cs`:
   - Change the existing `Path_like_or_non_txt_file_names_are_400` inline data: replace `"invoice-X-1.pdf"` with `"invoice-X-1.docx"`.
   - Add `Uploading_a_pdf_returns_201_with_extraction_report`.
   - Add `Text_renamed_to_pdf_is_422_not_pdf`.
   - Add `Extractor_busy_is_503_with_retry_after`.
   - Add `Extractor_down_is_503`.
   - Add `Problem_details_and_logs_never_contain_extracted_text` (the sentinel, checked with `CapturingLoggerProvider`).
   - Add `Pdf_tenant_comes_from_the_key`.
   - Add `Uploaded_scan_text_is_searchable`: the fake extractor returns OCR-like text of 48213; POST `/api/v1/search` `{"query":"detention 48213","mode":"keyword"}` returns title `rate-confirmation-48213`, and its content contains `Detention: $50.00 per hour` and the header `Section: RATE`.
   - Add `GET_document_returns_source_format_and_extraction`.
8. `Api/OpenApiTests` (snapshot): it fails, then you regenerate `docs/openapi/v1.json` and check that the summary reads "Ingest one .txt or .pdf document".
9. `Api/ExtractorSettingsTests.cs`: `Invalid_url_or_timeout_fails_startup`.
10. `MigrationScriptTests` fails until the migration and `db/004_…sql` exist.
11. `IntegrationTests/RepositoryTests.cs`: `Extraction_report_round_trips_as_jsonb_and_txt_defaults_to_txt` (run `docker compose run --build --rm integration-tests`).

### 7.2 Python extractor (`docker compose --profile extract run --build --rm extract-tests`)

Write the tests in this order. Each must fail before you write the module it tests.
- `test_signature.py`
- `test_limits.py`:
  - `choose_dpi` keeps 300 for Letter;
  - lowers the DPI for A3 to stay under the pixel cap;
  - returns None for a 14,400 pt page;
  - rejects 21 pages.
- `test_tsv.py`: parse a small hand-written TSV string; words with `conf=-1` are excluded from the mean; lines are grouped by (block, par, line).
- `test_normalise.py`, one test per rule in §8:
  - `dehyphenates_lowercase_words_only`
  - `keeps_SAV-778431_at_line_end`
  - `fixes_5O.OO_to_50.00_in_numeric_tokens`
  - `does_not_touch_words_like_SOLO`
  - `strips_page_x_of_y_lines`
  - `joins_pages_with_blank_line_not_heading_marker`
  - `removes_NUL_and_control_chars`
  - `nfkc_and_ascii_quotes`
- `test_report.py`:
  - weighted mean confidence;
  - `low_confidence` warning below 80;
  - `unreadable` below 40;
  - `no_headings` when no line satisfies the same rule as `SectionChunker.IsHeading`. Port it as `looks_like_heading` and test it against the same examples as `SectionChunkerTests`.
- `test_pdf.py` (generated fixtures):
  - a text-layer PDF uses `text_layer` for every page and never calls Tesseract (monkeypatch `run_tesseract` to raise);
  - a scanned PDF uses `ocr`;
  - encrypted → `encrypted_pdf`;
  - corrupt or truncated → `corrupt_pdf`;
  - empty bytes → `not_pdf`;
  - text named `.pdf` → `not_pdf`;
  - 21 pages → `too_many_pages`;
  - a huge page → `page_too_large`;
  - a blank scanned page → `no_text`.
- `test_worker.py`:
  - the worker sets the rlimits (inspect with `resource.getrlimit` inside the worker via a debug flag, or unit-test `apply_limits()`);
  - a timeout kills the worker and maps to `extraction_limit_exceeded`;
  - no files are left in `TMPDIR` after a run.
- `test_app.py` (fake worker runner):
  - 200 shape;
  - 413 above 10 MB;
  - 415 for a wrong content type;
  - second concurrent request beyond the queue → 503 with `Retry-After`;
  - problem details never contain the sentinel text;
  - the `caplog` output never contains the sentinel;
  - `network_attempts == []`.
- `test_accuracy.py`: generate scanned PDFs of `rate-confirmation-48213.txt`, `invoice-NFL-2026-1187.txt` and `bill-of-lading-SAV-778431.txt` (1° rotation, light noise). Then:
  - **CER ≤ 0.03** and **WER ≤ 0.08**, computed with a small pure Levenshtein (no new dependency) after whitespace normalisation of both sides;
  - **exact substrings** must be present: `Load number: 48213`, `Detention: $50.00 per hour`, `Total agreed rate: $2,630.00`, `NFL-2026-1187`, `SAV-778431`;
  - **heading recall**: every heading line of the source (`looks_like_heading`) appears as an exact line in the output (at least 90%).
  - Run the test for `TESSERACT_PSM` 3, 4 and 6, and keep the best as the default (4 is expected).

### 7.3 Fixture generation (reproducible, inside Docker, nothing committed)

`extract/tools/make_fixtures.py` is importable by the tests and can also be run with `docker compose --profile extract run --rm extract-tests python tools/make_fixtures.py --out /out`, which writes to a git-ignored `extract/.fixtures/`.
- **(a) Text-layer PDF:** reportlab `Canvas(invariant=1)`, Helvetica 10 pt, one line per source line, a new page when the page is full.
- **(b) Scanned PDF:**
  1. Draw the `.txt` lines with Pillow `ImageDraw`, using DejaVu Sans Mono from apt `fonts-dejavu-core`, in the **test stage only**, at 300 DPI Letter.
  2. Rotate 1.0° (`expand=False`, white fill) and add seeded Gaussian noise (`random.Random(42)`).
  3. Save as an **image-only** PDF with `Image.save(..., "PDF", resolution=300)`.
- **Negative fixtures:**
  - corrupt: the first 400 bytes of (a) plus garbage;
  - fake: `b"INVOICE\n…"` named `.pdf`;
  - encrypted: reportlab `encrypt="pw"`;
  - empty: `b""`;
  - 21 blank pages;
  - huge page: `pagesize=(14400,14400)`;
  - blank scan: a white image.
- **The data files come from `/data`,** copied in through a named build context (see §9).

## 8. OCR normalisation rules (pure functions in `normalise.py`, in this order)

1. **Rendering:** render grayscale at `choose_dpi` (300 by default; Tesseract expects text of roughly 20–30 px). Set `--dpi` to match. Language `eng`. Default `--psm 4`, chosen by the accuracy test. Tesseract's own deskew handles small skews (about 1–2°). **Contingency:** if the 1° fixture fails the CER threshold, add a pure `estimate_skew(image)` (projection profile on a 4× downscale, ±5° in 0.25° steps) and rotate before OCR. This is a separate TDD step.
2. **Lines from TSV:** words joined by single spaces in (block, par, line) order, a blank line between paragraphs or blocks. Runs of spaces are collapsed. Line breaks are never reflowed into paragraphs, because short field lines (`Load number: 48213`) must stay one per line for the identifier regular expressions and the heading rule.
3. **Character clean-up:** Unicode NFKC; smart quotes → `'`/`"`; en and em dashes → `-`. Remove U+0000, U+FFFD and control characters other than `\n` and `\t`.
4. **De-hyphenation:** only when a line ends in `[a-z]-` and the next line starts with `[a-z]`. Never for capitals or digits (`SAV-` + `778431` stays).
5. **Numeric confusion fix:** inside a token that has at least one ASCII digit and matches `^[$]?[0-9OolIS,.]+$`, map `O/o→0`, `l/I→1`, `S→5` only where the neighbours are digits, `,` or `.`. Count each fix in `numericCorrections`. Tokens with digits and word confidence below 70 increment `lowConfidenceNumericTokens` and raise the warning `numeric_tokens_low_confidence`.
6. **Page joins:** pages are joined with `\n\n` and **no marker line**. A marker such as `PAGE 2` passes `IsHeading` and would create false sections. Lines matching `^\s*page\s+\d+(\s+of\s+\d+)?\s*$` (case-insensitive) are dropped.
7. **Text-layer pages:** use the text layer when it has at least `MIN_TEXT_LAYER_CHARS` non-space characters and less than 5% U+FFFD; otherwise OCR that page. Mixed documents are fine, and each page records its own method. Text-layer text goes through steps 3, 4 and 6 only.
8. **No headings:** no invented headings. Content is chunked as `(untitled)` with the existing 1,000-character splits. The report sets `headingCount=0` and the warning `no_headings`. `evals/freightrag_eval/headers.py` already handles any section name.

## 9. Docker / compose

**`extract/Dockerfile`:**
- **`uv` stage:** the same pinned uv image as `answer/`. Verify the tag date: the `answer/Dockerfile` comment says 0.13.7 while the image is 0.13.9, so reconcile this.
- **`base` stage:** **the same Python base digest as `answer/`** (`python:3.14.10-slim-bookworm@sha256:3fe1a0b6…`), so the layers are shared and disk use stays low.
  - `apt-get install -y --no-install-recommends tesseract-ocr=<ver> tesseract-ocr-eng=<ver> && rm -rf /var/lib/apt/lists/*`.
  - To get `<ver>`, run `docker run --rm <base> sh -c 'apt-get update -qq && apt-cache policy tesseract-ocr tesseract-ocr-eng'` (bookworm is expected to ship 5.3.0-2). To check the publish date (at least 14 days old), look at `https://snapshot.debian.org/package/tesseract/` or `tracker.debian.org/pkg/tesseract`. Record the version and date in a comment.
  - A security update that changes the version breaks the build on purpose. Bump the pin deliberately when that happens.
- **`app` stage:**
  - `uv sync --locked --no-dev --no-install-project`;
  - a non-root user with uid 1654;
  - `ENV OMP_THREAD_LIMIT=1 TMPDIR=/tmp`;
  - `CMD uvicorn --factory freightrag_extract.app:create_app --host 0.0.0.0 --port 8091 --no-access-log --workers 1`.
- **`test` stage (FROM app):**
  - as root, install pinned `fonts-dejavu-core`;
  - `uv sync --locked --group dev --group fixtures`;
  - `COPY --from=data . /data/`;
  - switch back to `USER app`;
  - `CMD pytest -q -p no:cacheprovider`.
- **Image size target:** below 350 MB for `app`. Check with `docker image ls freightrag-extract`.

**`compose.yaml`:**

```yaml
  extract:                       # docker compose up -d --build extract api
    build: { context: ./extract, target: app }
    networks: [extract-net]      # internal only: no internet, no host, no published port
    read_only: true
    tmpfs: ["/tmp:size=64m,mode=1777"]
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]
    mem_limit: 768m
    memswap_limit: 768m
    cpus: 1.0
    pids_limit: 64
    healthcheck:
      test: ["CMD", "python", "-c", "import urllib.request;urllib.request.urlopen('http://127.0.0.1:8091/health',timeout=2)"]
      interval: 10s
      timeout: 3s
      retries: 5
  extract-tests:
    profiles: ["extract"]
    build:
      context: ./extract
      target: test
      additional_contexts: { data: ./data }
    network_mode: none
```

- `api` additionally joins `networks: [default, extract-net]` and gets `depends_on: extract: {condition: service_started, required: false}`. Do not add the extractor to `/health/ready`, so the API stays ready without it and PDF uploads return 503.
- Network `extract-net: { internal: true }`.
- The API does not need extra memory.
- Startup advice: start `extract` only when needed. Do not bulk-upload PDFs while `answer` or `answer-eval` is generating with qwen3:8b. Peak is about 6 GB (qwen) + 0.77 GB (cap) + API and Postgres, against the 9.7 GB VM.

## 10. Running and manual testing (secrets never printed)

```bash
git switch -c feature/pdf-ocr-ingestion main
docker compose run --build --rm migrate
docker compose up -d --build extract api
docker compose --profile extract run --build --rm extract-tests python tools/make_fixtures.py --out /out  # mount ./extract/.fixtures:/out
KEY="<tenant key, loaded without echoing it>"
curl -sS -H "X-Api-Key: $KEY" -F "file=@extract/.fixtures/rate-confirmation-59001.pdf;type=application/pdf" http://127.0.0.1:8080/api/v1/documents
curl -sS -H "X-Api-Key: $KEY" -H "Content-Type: application/json" -d '{"query":"detention 59001","topK":3}' http://127.0.0.1:8080/api/v1/search
unset KEY
```

Note for the implementer: the planning agent's draft read the key through a `user-secrets` compose service; verify that service exists before using it. This project's test keys are published in `docs/articles/getting-started.md` and in the Scalar docs, so prefer those. Never put a real key in shell history.

- **Scalar** (`http://localhost:8080/scalar/v1`): use `IngestDocument` and pick the PDF.
- **E2E without polluting eval tenants:** `extract/e2e/test_pdf_e2e.py`, run as a compose `eval`-profile service with keys passed by name like `retrieval-eval`. It uploads a scanned **synthetic** `rate-confirmation-59001` (a template copy of 48213 with load 59001; it does not live in `data/`). It asserts:
  - 201, or 200 skipped;
  - the document has a chunk whose header contains `Load: 59001 | Section: RATE` and whose body contains `Detention: $50.00 per hour`;
  - a hybrid search for "detention rate for load 59001" returns that chunk first;
  - the extraction report has `ocrPageCount == 1` and no `low_confidence`.
- **TXT versus OCR comparison** (optional, a separate commit, ask the user first because it adds a tenant):
  1. Add `tenant-nile-freight-ocr` to the `Tenants` section of all three `appsettings.json` files, and add its key via `user-secrets`.
  2. Upload scanned PDFs of the 13 Nile documents to it (same file stems, so titles match the gold labels).
  3. Copy the Nile `answerable` rows of `evals/retrieval_set.jsonl` into `evals/retrieval_set_ocr.jsonl` with the tenant rewritten.
  4. Run `retrieval-eval --dataset … --compare` and `answer-eval generate --dataset …`, then `judge`, then `compare` against the `.txt` subset run.
  - **Acceptance:** recall@5 and answer correctness drop by at most 1 question.
  - A few PDF-derived questions live in this OCR dataset only. Do not add them to the main set.

## 11. Security checklist

- [ ] The extension allow-list (`.txt`, `.pdf`) and the existing file-name checks are unchanged. The `%PDF-` magic is checked in both the API and the extractor.
- [ ] Size is limited to 10 MB in Kestrel, in the endpoint, and in the extractor `BodyLimit`.
- [ ] Limits: at most 20 pages, at most 1,584 pt per side, at most 12 MP per rendered page with DPI between 150 and 300, `Image.MAX_IMAGE_PIXELS` set.
- [ ] Per-worker limits: `RLIMIT_AS`, `RLIMIT_CPU`, `RLIMIT_NOFILE`, `RLIMIT_FSIZE`. A 60 s per-page Tesseract timeout and a 180 s request deadline. Container limits `mem_limit` 768m, `cpus` 1, `pids` 64.
- [ ] Concurrency: one extraction at a time, a queue of 1, then 503 `extractor_busy`. The per-tenant `ingest` rate limit still applies.
- [ ] Encrypted PDFs are rejected. `init_forms` is never called, and JavaScript, XFA and attachments are never read or run.
- [ ] Nothing is persisted. The PDF goes stdin → worker → Tesseract via stdin (`tesseract stdin stdout`); `TMPDIR` is a size-capped tmpfs; the container is `read_only`. A test asserts that `/tmp` is empty afterwards.
- [ ] Only `subprocess` argument lists; no `shell=True`. The file name never reaches the extractor.
- [ ] Container hardening: non-root, `cap_drop ALL`, `no-new-privileges`, an internal network with no egress and no published port.
- [ ] Errors use fixed messages and codes only. Logs carry categories, counts and timings; tests assert that a sentinel string is absent from both.
- [ ] The tenant is resolved only from the API key; the extractor interface has no tenant parameter.
- [ ] Prompt injection: OCR text becomes chunk content, which is already treated as untrusted in `answer/freightrag_answer/prompt.py` (rule 4). Document that scans can hide text (tiny or white text in a text layer), and that this is the same risk as `.txt`.
- [ ] **HIGH, flagged:** PDFium, Leptonica and Tesseract are native parsers of attacker-controlled input. The mitigations are isolation, rlimits and dependency updates (Dependabot plus apt pins). The residual risk is accepted for a localhost learning project.
- [ ] **Production note:** add mutual auth between the API and the extractor (a shared secret or mTLS). An internal network alone suffices only locally.

## 12. Performance and memory budget

- **Target per page** (Letter, 300 DPI, 1 CPU): OCR at most 15 s; extractor container peak RSS at most 400 MB; the whole container at most 768 MB.
- **Measuring:**
  - The worker reports `resource.getrusage(RUSAGE_SELF/RUSAGE_CHILDREN).ru_maxrss` and milliseconds per page in a debug log line (numbers only).
  - Run `docker stats --no-stream freightrag-extract-1` during a 5-page upload.
  - Run `docker compose --profile extract run --rm extract-tests python tools/bench.py`, which OCRs each fixture 3 times and prints the median ms per page and the peak RSS.
- Record the results in `pdf-ingestion.md`.
- If the peak exceeds 640 MB, lower `MAX_PAGE_PIXELS` or the DPI before raising any limit.

## 13. Rollback and abort criteria

- **Abort or rethink** if any of these holds:
  - CER on the fixtures is above 3% with every PSM and with the deskew contingency;
  - peak memory is above 768 MB at 300 DPI;
  - the `app` image is above 500 MB;
  - the eval comparison loses more than 2 questions.
- **Rollback:** revert the PR. Migration 004 only adds columns (a nullable jsonb, and text with a default), so older code keeps working with it applied. A down-migration exists in EF. PDF documents already stored stay searchable.

## 14. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Digits misread (`$5O.OO`, `482l3`), which breaks the identifier regexes and facts | Normalisation rule 5, `numericCorrections` and `lowConfidenceNumericTokens` warnings, and the exact-substring accuracy test |
| Tables: columns merged or interleaved | `--psm 4`, `preserve_interword_spaces`, line-by-line output; tables are documented as best effort. Structured extraction stays a non-goal |
| Headings misread (for example `RATE` becomes `RATe`), so a section merges into the previous one | No text is lost (the chunker keeps it). The heading-recall test, the `headingCount` report field and the `no_headings` warning |
| Low-quality scans | Warnings at confidence 80 and below, rejection below 40 |
| Slow OCR blocks uploads | Concurrency of 1, 503 with `Retry-After`, and the 240 s API timeout. Async jobs are a future option |
| Docker VM memory pressure from OCR plus qwen3:8b | Hard `mem_limit`, the extractor started only when needed, documented advice |
| Disk | Shared base image, `--no-install-recommends`, fixtures generated rather than committed. Never `docker system prune`; delete images only after the user confirms |
| Duplicate title when the `.txt` and the `.pdf` share a stem | Documented. Evals use a separate tenant or the synthetic 59001 |

## 15. Process rules

- Work on one branch and open one PR.
- Commit and PR messages contain **no AI-tool mentions and no Co-Authored-By or "Generated with" lines**.
- Ask the user before merging.
- Never read, print or echo secrets unnecessarily; load keys by command substitution and `unset` them afterwards. (The two published test keys are an exception the user made on purpose.)
- Pin versions exactly, and confirm each is at least 14 days old:
  - .NET: none are expected (`AddHttpClient` is in the ASP.NET shared framework);
  - Python: `uv lock` with `exclude-newer`;
  - images: tag plus digest;
  - apt: pinned version plus snapshot date.

## 16. Definition of done

- Every test in §7 was written first, seen to fail, and now passes:
  - `docker compose run --build --rm tests`;
  - `integration-tests`;
  - `--profile extract run --rm extract-tests`;
  - `answer-tests` and `retrieval-eval-tests` are unchanged and green.
- The CI jobs `test`, `integration`, `extract-tests` and `docs` (DocFX with warnings as errors) are green. Dependabot entries for `/extract` are added.
- The manual e2e (59001) passes locally. The bench numbers meet §12 and are recorded.
- The OpenAPI snapshot, `db/004_…sql` and all the documentation in §6 are updated; `toc.yml` has the quoted title.
- The security checklist in §11 is fully ticked.
- The PR is opened, and the user is asked before merging.

## Critical files for implementation

- `src/FreightRag.Core/Application/IngestDocument.cs`
- `src/FreightRag.Api/Endpoints/DocumentEndpoints.cs`
- `src/FreightRag.Core/Domain/SectionChunker.cs`
- `compose.yaml`
- `answer/Dockerfile` (template for `extract/Dockerfile`)
