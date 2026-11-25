# PDF ingestion: reading text from PDFs and scans

You can upload a **PDF** to the same endpoint as a `.txt` file. The text is pulled out of it, and from there it is chunked,
embedded, searched and cited exactly like any other document. This page explains how the text comes out, in plain words,
and what the limits are.

## Two kinds of PDF

| | Text-layer PDF | Scanned PDF |
|---|---|---|
| What it is | Exported from software (Word, an accounting system). The letters are stored as text. | A photo or scan of paper. Each page is just a picture. |
| Can you select the text in a viewer? | Yes | No |
| How we read it | Copy the text out. Exact, instant. | **OCR**: look at the picture and recognise the letters. |

**OCR** (optical character recognition) is software that reads a picture of text. It is very good, not perfect. It can
confuse look-alike characters: a letter `O` for a zero, a lowercase `l` for a one. In a freight document that matters,
because `$5O.OO` is not `$50.00`. So the extractor does three things:

1. It reads the **text layer first**. A page that already has text is never sent through OCR.
2. For scanned pages it measures how **confident** the OCR is on each word, and reports the average.
3. It fixes the commonest slip: inside something shaped like a number (`$5O.OO`, `482l3`), look-alike letters are read as
   digits. Words and identifiers such as `SAV-778431` are never touched.

## The pipeline

```
upload .pdf ──► API ──► extractor (separate container) ──► text ──► chunker ──► embeddings ──► search
                 │            │
                 │            └─ text layer, or render the page to an image (300 DPI) and run Tesseract
                 └─ checks the file name, the %PDF- header, the size; the tenant never leaves the API
```

The extractor is a **separate container** (`extract/`, Python) on purpose. A PDF is untrusted input, and parsing it is
complicated native code. Keeping that away from the API, which holds the database password, means a malicious or broken
PDF can at worst hurt the extractor. It has no internet access, a memory and CPU cap, a read-only file system, and each PDF is
processed in a short-lived worker process with its own limits. Nothing is written to disk and nothing is kept.

## Naming your files

The document type still comes from the **file name prefix**: `rate-confirmation-48213.pdf`, `invoice-NFL-2026-1187.pdf`,
`bill-of-lading-SAV-778431.pdf`, `driver-handbook-excerpt.pdf`. The title is the name without `.pdf`.
A `.txt` and a `.pdf` with the same name can both exist (they are different files); search will return both.

## What you get back

A `201` response has the usual fields plus `sourceFormat: "pdf"` and an `extraction` report. It holds **numbers and codes,
never text**:

```json
{ "id": "…", "title": "rate-confirmation-48213", "docType": "rate-confirmation", "chunkCount": 7, "sourceFormat": "pdf",
  "extraction": { "pageCount": 1, "ocrPageCount": 1, "textLayerPageCount": 0, "characterCount": 1342,
    "meanOcrConfidence": 95.9, "lowConfidence": false, "headingCount": 7, "numericCorrections": 0,
    "lowConfidenceNumericTokens": 0, "durationMs": 1100,
    "pages": [{ "page": 1, "method": "ocr", "characterCount": 1342, "meanConfidence": 95.9, "dpi": 300 }],
    "warnings": [] } }
```

| Warning | Meaning | What to do |
|---|---|---|
| `low_confidence` | Mean OCR confidence is below 80 | Read the document's chunks (`GET /api/v1/documents/{id}`) before trusting it; rescan if you can |
| `numeric_tokens_low_confidence` | Some numbers were read with low confidence | Check amounts and identifiers by eye |
| `no_headings` | No line looked like a section heading (`RATE`, `DELIVERY`) | Everything goes into one `(untitled)` section: search works, but sections are coarser |
| `dpi_reduced` | A large page was rendered at a lower resolution | Usually harmless |

## Limits and errors

| Limit | Value |
|---|---|
| File size | 10 MB |
| Pages | 20 |
| Page size | at most 22 inches per side, and about 12 megapixels when rendered |
| Languages | English |
| Time | OCR takes about 1 to 1.5 seconds a page on a laptop CPU; one PDF is processed at a time |

A rejection is `422` with a fixed `code` (never any text from your file): `not_pdf` (a `.pdf` that does not start with
`%PDF-`), `corrupt_pdf`, `encrypted_pdf`, `too_many_pages`, `page_too_large`, `no_text`, `unreadable` (OCR confidence below 40),
`extraction_limit_exceeded`. `503` means the extractor is busy (`extractor_busy`, retry after the `Retry-After` seconds) or
not running (`extractor_unavailable`). Plain `.txt` uploads keep working while it is down.

## What it measured

On synthetic scans of the project's own documents (drawn as page images, tilted 0 to 2 degrees, with noise), the tests require
a character error rate of at most 3%, a word error rate of at most 8%, the key figures present (`Detention: $50.00 per hour`,
`Total agreed rate: $2,630.00`) and at least 90% of the headings recovered. They pass. Real scans are messier than these,
so treat this as a floor for clean documents, not a guarantee.

Two measured details worth knowing:

- Tesseract's default page mode for "one column of text" (mode 4) **scrambled line order on slightly tilted pages** (error rate
  7% at 1 degree, 28% at 2 degrees). Mode 6 (one uniform block) stayed at about 1%, so it is the default. It assumes
  single-column pages; multi-column layouts and tables are best effort.
- Resources on the real image with 1 CPU and 768 MB: about 1.0 s for a one-page scan and 1.4 s a page over several pages; peak
  memory about 140 MB; image 366 MB.

## Security notes

- The extractor never receives the tenant, the API key or the file name; the tenant is decided by the API key inside the API.
- Checks: `%PDF-` at byte 0, 10 MB, 20 pages, page size, encrypted files refused, PDF forms/JavaScript/attachments never read
  or run, no shell (Tesseract is started with a fixed argument list), logs hold codes and counts only.
- Extracted text becomes chunk text, which is **untrusted** exactly like a `.txt` upload: the answer prompt already treats it as
  data. A scan can contain hidden or tiny text, the same risk as any document.
- **Residual risk:** PDFium and Tesseract are native parsers of attacker-controlled input. Isolation, resource limits and
  dependency updates reduce it; they do not remove it. For production, also authenticate the API-to-extractor call.
