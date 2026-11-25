# Plan: return extracted key/value fields from the document APIs

Status: **proposed, not approved, not built.** Drafted by a planning agent on 2026-11-25 from a read-only pass over the repo.
Usable by any coding agent without the earlier conversation. Before relying on a statement about the current code, check it.

## 0. Context (current code)

- `src/FreightRag.Api/Endpoints/DocumentEndpoints.cs` already has the three endpoints:
  - `POST /api/v1/documents` (`IngestAsync`, multipart `file`, .txt or .pdf). It returns `201 IngestedResponse`, `200 SkippedResponse` (same bytes already stored; **no id in the body**) or a problem with a `code`.
  - `GET /api/v1/documents` (`ListAsync`, paged).
  - `GET /api/v1/documents/{id}` (`GetAsync`). It returns `DocumentResponse` with the chunks, or 404 when the document is missing or belongs to another tenant.
- Contracts live in `Endpoints/Contracts.cs`. `IngestedResponse(Id, Title, DocType, ChunkCount, SourceFormat="txt", Extraction=null)`.
- `IngestDocumentHandler` (`Core/Application/IngestDocument.cs`) runs these steps: tenant, then `DocType.TryFromFileName`, then format and the `%PDF-` check, then the hash, then `ExistsAsync`, then the text (strict UTF-8, or `IPdfTextExtractor` for PDF), then the NUL check, then `FreightDocument.Create` (`DocumentIdentifiers.Extract` + `SectionChunker.Split`), then embed, then `AddAsync`, then `IngestResult.Ingested(DocumentId, ChunkCount, Format, Extraction)`.
- `SectionChunker` reads sections in `private static ReadSections`. A heading is a line of 2-80 characters with no lower-case letters and no final `.`, made mostly of letters. Only the first line in a run of heading-shaped lines counts as a heading.
  - Each chunk is `"Document: <type>[ | Load: ...][ | BOL: ...][ | Invoice: ...] | Section: <NAME>\n<body>"`. The heading line itself appears **only** in the header.
  - A heading with no body uses its own name as the body.
  - Bodies over 1,000 characters are split at paragraph or line boundaries. A line is never cut.
- `DocumentIdentifiers` shows the house regex style to copy: `[GeneratedRegex(..., RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]`, `[\t\p{Zs}]` and anchored lines.
- `IDocumentQueries.GetAsync` returns `DocumentDetails(Id, Title, DocType string, CreatedAt, Chunks[ChunkText(Index, Content)])`, ordered by index.
- Tests:
  - `src/FreightRag.Tests`: Domain, Application, Api (`ApiFactory` with `Repository`, `Pdf` fake, `Logs` = `CapturingLoggerProvider`) and Support (`TestData`; `data/**/*.txt` is copied to the output).
  - `ArchitectureTests` stops Domain and Application from depending on infrastructure or third-party code.
  - `OpenApiTests.Matches_the_committed_snapshot_in_docs` compares the API description against `docs/openapi/v1.json`.
- **Branch state (verified 2026-11-25):** PR #19 (PDF upload with OCR) was merged into `main` as `e661eed`. Commit `9633fab` ("Use Tesseract page mode 3 ... add sample document generators") was pushed to `origin/feature/pdf-ocr-ingestion` after that merge and is **not on `main`**. It holds the Tesseract page-mode fix (main still has mode 6, which loses table cells) and `extract/tools/make_realistic_samples.py` / `check_realistic.py`, which the accuracy check in section 8 needs.
  - Recommended: open a small PR for `9633fab` against `main` first, then branch `feature/document-fields` from `main`.
  - Alternative: branch from `origin/feature/pdf-ocr-ingestion` and let the new PR carry `9633fab` (say so in its description).

## 1. Interpretation (ASSUMPTIONS to confirm with the user)

User's words: two APIs, one to POST a document whose result adds the extracted fields as key and value (for example "Carrier Name: ..."), one to GET the document details by id, maybe a third for all documents, "just that"; the APIs should "add an option for fetching specific fields"; fields are "POST result only" (not stored).

1. `POST /api/v1/documents?fields=carrierName,loadNumber` takes an optional `fields` query parameter (comma-separated names). If it is omitted, the response contains every field defined for the document type. The `201` body gains `fields`, a JSON object of name to string, with `null` when a field is not found.
2. **Fields are not stored.** `GET /api/v1/documents/{id}?fields=...` computes them on demand from the stored chunk text: reassemble the chunks in order, drop each header line, and put the section heading back. **Recommended: include this.**
   - It costs no storage and no migration.
   - It is the only way to get fields after a `200 skipped` re-upload, because that response has no id and no fields.
   - It lets a caller ask for specific fields later.
   - A property test (section 7, step 5) proves that GET returns exactly what POST returned for every real document.
   - GET without `fields` also returns all fields, so the parameter means the same thing on both endpoints. Adding a property is a non-breaking change.
3. `GET /api/v1/documents` (list) is **not** extended:
   - Fields would require loading every chunk of up to 100 documents per page, which turns a cheap summary query into a heavy one.
   - Field names differ by document type, so `fields` validation on a mixed list would be ambiguous.
   - A client can call GET by id for the documents it cares about.
4. `200 skipped` stays unchanged, with no fields. Recomputing them would mean decoding the file again, or re-running OCR for a PDF.
5. Extraction is rules-based (deterministic code), not the model. The user did not choose between rules and the model; confirm.

## 2. Non-goals

- No storage: no table, no column, no migration, no `db/*.sql`.
- No LLM, Ollama or model calls. Extraction is deterministic code.
- No reading of handwriting, signatures, stamps or check boxes.
- No changes to search, answer, the chunker's output or the embeddings.
- No normalisation. Values are returned **exactly as written**: dates such as `"2026-10-12"`, amounts such as `"$2,630.00"`, `"none"`. Nothing is parsed into numbers or dates, and OCR errors are not corrected.
- No list-endpoint fields, and no `special instructions` field (free text over several lines; the chunks already hold it).

## 3. Field catalog (derived only from the 24 files in `data/`)

Keys are camelCase. When a field is listed under several types it is extracted per type, and its meaning is documented per type.

**Common across types:**
- `loadNumber`: all three types.
- `carrierName`: all three types.
- `bolNumber`: BOL and invoice.
- `lineHaul`, `fuelSurcharge`, `detention`, `paymentTerms`: rate confirmation and invoice.
- `shipperName`, `commodity`, `quantity`, `totalWeight`: rate confirmation and BOL.

**rate-confirmation (23 fields):**

| Key | Rule (S = must be in that section; first non-empty match wins) |
|---|---|
| brokerName, brokerAddress, brokerMc | `Broker:` -> name/address split + `(MC# n)` |
| carrierName, carrierMc, carrierDot | `Carrier:` -> name + `(MC# n, DOT# n)` |
| loadNumber | `Load number:` |
| dateIssued | `Date issued:` |
| pickupLocation, shipperName, pickupWindow | S=PICKUP: `Location:`, `Shipper:`, `Date and window:` |
| deliveryLocation, deliveryDate | S=DELIVERY: `Location:`, `Date:` |
| commodity, equipment, temperature | `Commodity:`, `Equipment:`, `Temperature:` |
| quantity, totalWeight | `Quantity:` split `^(?<q>[^,]{1,40}),[\t ]*(?<w>[0-9][0-9,.]{0,12}[\t ]*lbs)$`; when there is no match, quantity = the whole value and totalWeight = null |
| lineHaul, fuelSurcharge, totalAgreedRate, detention | `Line haul:`, `Fuel surcharge:`, `Total agreed rate:`, `Detention:` (+ money table fallback, section 4) |
| paymentTerms | first body line of section PAYMENT TERMS; alias label `Payment terms:` / `Payment:` |

**bill-of-lading (22 fields):** bolNumber, loadNumber, shipDate (`Ship date:`), shipperName, shipperAddress, consigneeName, consigneeAddress, carrierName, driverName (`Driver:`), truckNumber (`Truck number:`/`Truck:`), trailerNumber (`Trailer number:`/`Trailer:`), sealNumber (`Seal number:`/`Seal:`), quantity, commodity, totalWeight, freightClass (`Freight class:`), temperatureSetting (`Temperature setting:`), hazardousMaterials (`Hazardous materials:`), deliveredAt (`Delivered:`), receivedBy (`Received by:`), deliveryCondition (`Condition:`), productTemperatureAtDelivery (`Product temperature at delivery:`), sealIntact (`Seal intact on arrival:`).
- Shipper, consignee and carrier:
  - First choose the inline label `Shipper:`/`Consignee:`/`Carrier:`. These appear in fax-style OCR text.
  - Otherwise use the SHIPPER/CONSIGNEE/CARRIER section. The first non-label body line is the name and the next non-label line is the address. If the first line contains `, <digit>`, split it into name and address instead.
  - The carrier's first line is cut at the first embedded label (`Blue Ridge Freight Co.  Driver: ...`).
- Shipment sentence, in section SHIPMENT DETAILS, first line: `^(?<quantity>[0-9][0-9,]{0,6}[\t ]+[A-Za-z]{1,20})[\t ]+of[\t ]+(?<commodity>[^\n]{1,200}?),[\t ]+total weight[\t ]+(?<w>[0-9][0-9,.]{0,12}[\t ]*lbs)[\t ]*$`. The label `Total weight:` is an alias for totalWeight.

**invoice (16 fields):** carrierName, carrierAddress (`From:`), billToName, billToAddress (`Bill to:`), invoiceNumber, invoiceDate, loadNumber, bolNumber, lineHaul, fuelSurcharge, detention, totalDue (`Total due:`), paymentDueDate, paymentTerms, attachments (`Attachments:`), billingContact (`Questions:`).
- detention, in S=CHARGES: label `Detention` with an optional qualifier `(?:[\t ]+[^:\n]{1,80}?)?` before the colon, so `Detention (2 billable hours at $75.00/hour): $150.00` gives `"$150.00"`. `Detention: none` gives `"none"`.
- `Payment due date: 2026-11-15 (Net 30)` is split by `^(?<d>[^()]{1,40}?)[\t ]*\((?<t>[^()]{1,40})\)$` into paymentDueDate `"2026-11-15"` and paymentTerms `"Net 30"`. When the pattern does not match, the whole value is paymentDueDate and paymentTerms is null.

**driver-handbook:** no fields. `fields` is `{}`, and requesting any name gives a 400 with `validFields: []`.

## 4. Extraction rules (pure, deterministic)

1. **Normalise and bound the input.** Apply `ReplaceLineEndings("\n")`, strip a BOM, and only read the first **200,000 characters**. Lines longer than **1,000 characters** are skipped.
2. **Sections.** Use the chunker's own reader (`SectionChunker.ReadSections`, made `internal static` and returning `TextSection(Name, Lines)`), so POST, GET and the chunks agree on section boundaries.
3. **Label tokenizer.** Use one `[GeneratedRegex]` whose pattern is a closed alternation of every known label (case-insensitive; words joined by `[\t ]+`; optional spaces before `:`).
   - A label counts only at the **start of a line**, or after **two or more spaces/tabs**, or after **`,` plus optional spaces**. OCR'd forms put several labels on one line (`BOL number: CLT-330915          Load number: 52010`), and this rule handles that.
   - A value runs from after the colon up to the next label occurrence, or to the end of the line. It is then trimmed, and a trailing `,` is dropped.
   - A label inside a sentence (`...good condition. Product temperature at delivery: -8 degrees F.`) is **not** at an allowed anchor. It stays part of `deliveryCondition`, and `productTemperatureAtDelivery` is null for that file. This is the documented behaviour.
4. **Validity.**
   - A value must contain at least one letter or digit. Blank lines and underscores (`Received by: ____`) mean the field was not found.
   - A value longer than **300 characters** gives `null`. It is never truncated.
   - When a label appears twice, the first valid match in scope wins, as in `DocumentIdentifiers`.
5. **Name/address/IDs.** Strip a trailing `\((?<ids>[^()]{1,80})\)$`, then read `MC#[\t ]*(?<mc>[0-9]{1,10})` and `DOT#[\t ]*(?<dot>[0-9]{1,10})`. The name is the text before the first `,[\t ]+(?=[0-9])` and the address is the rest. If there is no such comma, the whole text is the name and the address is null.
6. **Money table fallback.** This applies only to lineHaul, fuelSurcharge, totalAgreedRate, totalDue and the invoice detention, and only when the colon rule found nothing. Pattern: `^[\t ]*(?i:<label>)\b[^:\n]{0,200}?(?<a>\$[0-9][0-9,]{0,12}\.[0-9]{2})[\t ]*$`, which takes the **last** amount on a table row. For example, `Fuel surcharge (per mile) 412 $0.62 $255.44` gives `"$255.44"`.
7. **OCR noise.** `Load number: 482l3` gives `"482l3"`, as written; it is not corrected. Extra spaces in labels and before the colon are tolerated. A misspelled label (`Load nurnber`) gives null. Nothing is ever guessed.
8. **Regex safety.**
   - Use only `[GeneratedRegex]` with `matchTimeoutMilliseconds: 1000`, and only bounded quantifiers (`{0,n}`). No nested quantifiers, no `.*` across lines.
   - Catch `RegexMatchTimeoutException` inside the extractor and turn it into null. Its message includes the input, so it must never reach the logs.

## 5. API contract

- **Parameter:** `fields`, a query string, optional, on `POST /api/v1/documents` and `GET /api/v1/documents/{id}`.
  - Split it on `,`, trim each entry and drop empty entries. If nothing remains, all fields are returned.
  - Matching is case-insensitive (`LOADNUMBER` = `loadNumber`). Duplicates are allowed and ignored.
  - The response always uses the **catalog order and the canonical casing**.
  - For a repeated parameter (`?fields=a&fields=b`), write a test and document what minimal-API `string?` binding does; it is expected to join the values with a comma.
- **Validation (400 problem details, `code` extension):**
  - `invalid_fields`: the raw value is longer than 1,000 characters, more than 50 entries are given, or an entry does not match `^[A-Za-z]{1,40}$`.
  - `unknown_field`: a name is not defined for this document type. The problem includes `validFields: [...]` for that type and does **not** echo the input. Detail: "Unknown field name. The valid names for rate-confirmation are listed in validFields."
- **POST validation order:** file-name checks, then format, then size, then **fields**. The fields are validated against the type taken from the file name, before the handler runs, so nothing is stored and the PDF extractor is not called. If the file-name type is unknown, the field check is skipped and the handler's existing `422 unknown_doc_type` applies.
- **GET validation order:** load the document, return 404 if it is missing or foreign, and only then validate the fields. Validating after the 404 check means a bad `fields` value cannot reveal whether an id exists in another tenant.

Example 1: POST 201 for `rate-confirmation-48213.txt` (Nile), all fields.

```json
{ "id": "...", "title": "rate-confirmation-48213", "docType": "rate-confirmation", "chunkCount": 8,
  "sourceFormat": "txt", "extraction": null,
  "fields": { "brokerName": "Crescent Logistics LLC", "brokerAddress": "1200 Peachtree St NE, Atlanta, GA 30309",
    "brokerMc": "784512", "carrierName": "Nile Freight Lines Inc.", "carrierMc": "902114", "carrierDot": "3318820",
    "loadNumber": "48213", "dateIssued": "2026-10-12",
    "pickupLocation": "Savannah Port Terminal, Gate 3, 400 Port Rd, Savannah, GA 31401",
    "shipperName": "Coastal Electronics Import Co.", "pickupWindow": "2026-10-14, 08:00 to 12:00",
    "deliveryLocation": "Peachtree Distribution Center, Dock 4, 2250 Fulton Industrial Blvd SW, Atlanta, GA 30336",
    "deliveryDate": "2026-10-15, deliver by 14:00", "commodity": "Packaged consumer electronics",
    "quantity": "18 pallets", "totalWeight": "38,400 lbs", "equipment": "53 ft dry van", "temperature": null,
    "lineHaul": "$2,450.00", "fuelSurcharge": "$180.00", "totalAgreedRate": "$2,630.00",
    "detention": "$50.00 per hour after 2 free hours at pickup or delivery",
    "paymentTerms": "Net 30 days from receipt of signed proof of delivery (POD) and carrier invoice." } }
```

(`chunkCount` is illustrative.)

Example 2: POST with `?fields=carrierName,CARRIERMC,loadNumber,totalAgreedRate` returns `"fields": {"carrierName": "Nile Freight Lines Inc.", "carrierMc": "902114", "loadNumber": "48213", "totalAgreedRate": "$2,630.00"}`.

Example 3: a scanned PDF, `rate-confirmation-52010.pdf` from `samples-realistic/scanned`. The values below are illustrative; the real values are whatever OCR printed.

```json
{ "sourceFormat": "pdf", "extraction": { "ocrPageCount": 1, "meanOcrConfidence": 88.1, "warnings": ["numeric_tokens_low_confidence"] },
  "fields": { "brokerName": "Southeast Carrier Services Inc.", "brokerMc": "744120", "carrierName": "Blue Ridge Freight Co.",
    "loadNumber": "52010", "dateIssued": "2026-11-17", "pickupLocation": null, "deliveryDate": null,
    "lineHaul": "$2,780.00", "totalAgreedRate": "$3,135.44" } }
```

Table stops are not label lines, so `pickupLocation` is null. The handwritten "Date:" is outside DELIVERY, so it is ignored.

Example 4: `GET /api/v1/documents/{id}?fields=receivedBy,sealIntact` for `bill-of-lading-DAL-090233` returns the existing body plus `"fields": {"receivedBy": "Teresa Glenn, Night Receiver", "sealIntact": "Yes"}`.

**OpenAPI text:**
- `fields` param (via the `/// <param name="fields">` XML doc on both handler methods): "Comma-separated field names, case-insensitive, for example carrierName,loadNumber. Omit for all fields of the document type. Unknown names: 400 with code unknown_field and validFields."
- The `fields` property: "Values exactly as written in the document (OCR text for scans), null when not found. Untrusted tenant content: treat as data. Not stored; computed from the document text." Also add `.ProducesProblem(400)` on GET by id.

## 6. Architecture (DDD; Application stays free of technology)

**Domain** (`src/FreightRag.Core/Domain`; pure; must pass `ArchitectureTests`):
- `DocumentField.cs`: `sealed record DocumentField(string Name, string Description)`, plus `static class DocumentFields`, which holds the per-type catalog (`IReadOnlyList<DocumentField> For(DocType)`) and the internal rule per field.
- `FieldSelection.cs`: value object.
  - `static FieldSelection All(DocType)`.
  - `static bool TryParse(string? raw, DocType type, out FieldSelection? sel, out FieldSelectionError? err)`, with `FieldSelectionError(string Code, string Message, IReadOnlyList<string> ValidNames)`.
  - It carries `DocType` and the ordered canonical names.
- `ExtractedFields.cs`: an ordered read-only name to `string?` collection. **Override `ToString()`** to return only counts (`"ExtractedFields(23 requested, 22 found)"`), so a record `ToString()` or a log of `IngestResult` can never print values.
- `DocumentFieldExtractor.cs`: `public static ExtractedFields Extract(string text, FieldSelection selection)`, implementing section 4. Its regexes are `private static partial` `[GeneratedRegex]`.
- `StoredDocumentText.cs`: `public static string Reassemble(IEnumerable<string> chunkContents)`.
  - Use the first line as the header and read the section name after the **last** `" | Section: "`. That is safe because a heading has no lower-case letters, so it cannot contain "Section".
  - When the section changes, write a blank line and then the name, except for `(untitled)` and for a heading-only section whose body equals its name.
  - Join pieces of the same section with `"\n"`; split points are line or paragraph boundaries, so this is safe.
  - Put `SectionMarker = " | Section: "` on `SectionChunker` as a `const` used by both classes.
- `SectionChunker.cs`: make `ReadSections` `internal static`, returning `TextSection`. Behaviour stays unchanged; the existing chunker tests guard it.
- `DocType.cs`: add `static bool TryFromName(string name, out DocType?)`. This is needed because the read model holds the type as a string.

**Application:**
- `IngestDocument.cs`:
  - `IngestDocumentCommand` gains `FieldSelection? Fields = null`, with a default so `FolderIngestor` and the existing tests compile.
  - On the success path, the handler builds `DocumentFieldExtractor.Extract(text, command.Fields ?? FieldSelection.All(docType))` from the **same `text`** that went to `FreightDocument.Create`. If the selection's type differs from the document's type, it throws `ArgumentException`.
  - `IngestResult.Ingested` gains `ExtractedFields? Fields = null`.
- `Queries/GetDocument.cs` (new): `GetDocumentHandler(IDocumentQueries)` with `HandleAsync(TenantId, Guid, string? fields, ct)`. It returns a closed result: `Found(DocumentDetails, ExtractedFields)`, `NotFound`, or `InvalidFields(FieldSelectionError)`. It does load, then 404, then TryParse, then reassemble, then extract.

**API:**
- `Contracts.cs`: `IngestedResponse` gains `IReadOnlyDictionary<string, string?>? Fields = null` and `DocumentResponse` gains `IReadOnlyDictionary<string, string?> Fields`. Build these as `OrderedDictionary<string,string?>` (in .NET 10) so the catalog order is guaranteed.
- `DocumentEndpoints.cs`: add `string? fields` to `IngestAsync` (parse after the size check) and to `GetAsync` (now `Results<Ok<DocumentResponse>, NotFound, ProblemHttpResult>`, using `GetDocumentHandler`). Add `Problem(..., code, extra)` with a `validFields` extension.
- `Program.cs`: `AddSingleton<GetDocumentHandler>()`.
- The console ingest is unchanged. It ignores `Fields`, and its logs only print `ChunkCount`, so keep them that way.

## 7. TDD steps (write each test first, see it fail, then implement)

Commands for every step: `docker compose run --build --rm tests` (`--build` matters: the image bakes the tests in). There are no local installs.

1. **Catalog** (`Tests/Domain/DocumentFieldsTests.cs`):
   - Names are unique per type and match `^[a-z][A-Za-z]{1,39}$`.
   - The three exact name lists from section 3 hold, in order.
   - The handbook list is empty.
   - The common fields appear in the types listed in section 3.
2. **FieldSelection** (`FieldSelectionTests.cs`):
   - Null, empty and `",,"` give All.
   - `"LOADNUMBER, carrierName,loadNumber"` gives `[carrierName, loadNumber]` in catalog order, with duplicates ignored.
   - `"foo"` gives `unknown_field` with ValidNames equal to the catalog.
   - `"load-number"`, 51 entries, and more than 1,000 characters each give `invalid_fields`.
   - Any name for the handbook gives `unknown_field` with `[]`.
3. **Extractor against the real data** (`DocumentFieldExtractorTests.cs`):
   - **Theory over every `data/tenant-*/*.txt`** (MemberData enumerates `AppContext.BaseDirectory/data`, so 24 cases):
     - (a) The handbook gives `{}`.
     - (b) Every non-null value appears **verbatim** in the source text.
     - (c) For types other than the handbook, loadNumber and carrierName are never null.
     - (d) The values match `ExpectedFields` (a static table in the test file covering **all 23** non-handbook files; filled in by reading each file).
   - At least these expectations must be in the table:
     - RC 48213: the full object in section 5.
     - RC 77104: shipperName **null** (no Shipper line), brokerName "Gulfline Brokerage Inc.", brokerMc "661207", carrierDot "2970415", quantity "22 pallets", totalWeight "41,200 lbs", temperature "Maintain -10 degrees F continuously. Reefer must be pre-cooled before loading.", totalAgreedRate "$4,210.00", detention "$75.00 per hour after 2 free hours", paymentTerms "Net 21 days from receipt of signed POD and temperature download report."
     - RC 77125: brokerName "Lone Star Freight Partners", brokerAddress "2100 Ross Ave, Dallas, TX 75201", brokerMc "590144", shipperName "Prairie Creamery", deliveryLocation "Delta Cold Storage, Door 3, 2950 Democrat Rd, Memphis, TN 38118", deliveryDate "2026-11-06, deliver by 06:00", detention "$70.00 per hour after 2 free hours", totalAgreedRate "$1,840.00".
     - RC 48240: brokerName "Atlantic Bridge Freight Inc.", brokerMc "702388", commodity "Paper towels and tissue products", quantity "26 pallets", totalWeight "21,800 lbs", totalAgreedRate "$2,110.00", temperature null.
     - BOL SAV-778431: bolNumber "SAV-778431", loadNumber "48213", shipDate "2026-10-14", shipperName "Coastal Electronics Import Co.", shipperAddress "400 Port Rd, Savannah, GA 31401", consigneeName "Peachtree Distribution Center", consigneeAddress "2250 Fulton Industrial Blvd SW, Atlanta, GA 30336", carrierName "Nile Freight Lines Inc.", driverName "Omar Haddad", truckNumber "NFL-217", trailerNumber "NFL-T5530", sealNumber "CE-559201", quantity "18 pallets", commodity "packaged consumer electronics", totalWeight "38,400 lbs", freightClass "92.5", temperatureSetting null, hazardousMaterials "None", deliveredAt "2026-10-15 at 11:40", receivedBy "Lena Morris, Receiving Supervisor", sealIntact "Yes".
     - BOL DAL-090233: shipperName "Prairie Creamery", shipperAddress "3300 Irving Blvd, Dallas, TX 75247", consigneeName "Delta Cold Storage", driverName "Tomas Reyes", truckNumber "DH-051", trailerNumber "DH-R1203", sealNumber "PC-774015", commodity "ice cream", temperatureSetting "-20 degrees F", receivedBy "Teresa Glenn, Night Receiver", deliveryCondition "17 pallets accepted. 1 pallet rejected by the receiver because of crushed cartons.", productTemperatureAtDelivery "-19 degrees F.", freightClass null.
     - BOL HOU-220957: deliveryCondition "22 pallets received in good condition. Product temperature at delivery: -8 degrees F.", productTemperatureAtDelivery **null** (label inside a value), receivedBy "James Okafor, Dock Lead", sealNumber "BF-300178".
     - BOL SAV-780377: commodity "fresh produce (bananas and pineapples)", totalWeight "39,000 lbs", trailerNumber "NFL-R2208", temperatureSetting "34 degrees F", productTemperatureAtDelivery "36 degrees F.".
     - Invoice NFL-2026-1187: carrierName "Nile Freight Lines Inc.", carrierAddress "85 Harbor View Rd, Savannah, GA 31405", billToName "Crescent Logistics LLC", billToAddress "1200 Peachtree St NE, Atlanta, GA 30309", invoiceNumber "NFL-2026-1187", invoiceDate "2026-10-16", loadNumber "48213", bolNumber "SAV-778431", lineHaul "$2,450.00", fuelSurcharge "$180.00", detention "$75.00", totalDue "$2,705.00", paymentDueDate "2026-11-15", paymentTerms "Net 30", attachments null, billingContact "billing@nilefreight.example".
     - Invoice DH-26-0412: carrierName "Delta Haul Co.", billToName "Gulfline Brokerage Inc.", detention "none", totalDue "$4,210.00", paymentDueDate "2026-11-15", paymentTerms "Net 21", attachments "signed POD, reefer temperature download report", billingContact "accounts@deltahaul.example".
     - Invoice DH-26-0431: detention "$150.00", totalDue "$4,300.00", paymentDueDate "2026-11-22".
     - Invoice NFL-2026-1219: billToName "Atlantic Bridge Freight Inc.", billToAddress "300 Bay St, Jacksonville, FL 32202", detention "$135.00", totalDue "$2,245.00", paymentDueDate "2026-11-29".
   - These expected values were written by the planning agent from its reading of the files: **re-check each against the real file when writing the table**.
4. **Negative and edge tests** (same file, small inline texts):
   - A missing label gives null.
   - `Load number: 482l3` gives `"482l3"`.
   - `Load  number :   48213` gives `"48213"`.
   - CRLF and lone-CR input give the same result as LF.
   - A duplicate label: the first wins.
   - `Received by: ______` gives null.
   - A value of 301 characters gives null.
   - A 5,000-character line is skipped.
   - A label at a non-anchor (`x Load number: 1`) gives null.
   - `Truck number: A-1, Trailer number: B-2` splits correctly.
   - An OCR one-line form (`BOL number: X-1          Load number: 52010          Ship date: 2026-11-19`) and the inline `SHIPPER: Name, 1 Main St, City, ST 00000` both work.
   - Table rows `Line haul 1 $2,780.00 $2,780.00` and `Fuel surcharge (per mile) 412 $0.62 $255.44` give the last amount.
   - The realistic RC handwritten `Date:` outside DELIVERY is ignored.
   - Adversarial input (10 MB of `Detention ((((...`, `a: a: a: ...`, 200k lines) finishes in under 2 s with no exception.
   - `ExtractedFields.ToString()` contains no value.
5. **Reassembly** (`StoredDocumentTextTests.cs`):
   - For every data file, with `new SectionChunker()` **and** `new SectionChunker(maxBodyChars: 60)` (which forces splits; check the real constructor): `Extract(Reassemble(chunks)) == Extract(original)`.
   - A header whose identifiers are present still parses.
   - A heading-only title section is not duplicated.
6. **Handler** (`Application/IngestDocumentHandlerTests.cs`):
   - `Ingested.Fields` holds all fields for a real RC.
   - A selection gives only the requested fields.
   - A PDF uses the extracted text: the fake gives detention `"$50.00 per hour after 2 free hours"`.
   - A selection for the wrong type throws.
   - `Skipped` and `Rejected` are unchanged.
   - New `Application/GetDocumentHandlerTests.cs`: Found, NotFound (also for another tenant), InvalidFields, and **NotFound before InvalidFields** for a foreign id.
7. **API** (`Api/DocumentEndpointsTests.cs`):
   - The 201 body has all `fields` in catalog order.
   - `?fields=carrierName,CARRIERMC` gives exactly 2 keys.
   - An unknown name gives 400 `unknown_field` with `validFields`, `Repository.Stored` is empty, and `Pdf.Calls` is empty.
   - `invalid_fields` gives 400.
   - The handbook upload gives `{}`.
   - A PDF upload gives `fields.detention` from the fake text.
   - A 200 skipped body has no `fields`.
   - GET by id returns the same fields as the POST, and `?fields=` selects.
   - A foreign id with a bad `fields` value gives **404**.
   - Repeated `fields` parameter behaviour is pinned.
   - **Log test:** after POST and GET of RC 48213, `_factory.Logs.AllText()` contains none of `"Crescent Logistics LLC"`, `"$2,630.00"` or `"902114"`.
   - Tenant-isolation tests stay unchanged and green.
8. **OpenAPI** (`OpenApiTests.cs`):
   - The `fields` query parameter exists on POST and on GET `{id}`.
   - The `IngestedResponse` and `DocumentResponse` schemas have `fields`, whose description contains "untrusted".
   - GET `{id}` documents 400.
   - Regenerate the snapshot: `docker compose run --build --rm -e UPDATE_OPENAPI_SNAPSHOT=1 -v "$PWD/docs":/repo/docs tests`, then commit `docs/openapi/v1.json`.
9. Run everything:
   - `docker compose run --build --rm tests`
   - `docker compose run --build --rm integration-tests` (unchanged; proves nothing regressed)
   - `docker compose build docs` (0 warnings)

## 8. Accuracy check on the realistic scans (honest measurement, not a unit test)

- **Generate the samples.** The `samples-realistic/` folder is git-ignored. Generate it with the command in the docstring of `extract/tools/make_realistic_samples.py` (needs `9633fab`).
- **Start the stack:** `docker compose up -d --build extract api`.
- **Check script.** Add `extract/e2e/check_fields.py` (standard library only, like `e2e.py`). It holds a field truth table: only printed, label-addressable facts, never handwriting.
  - RC 52010 (12): loadNumber 52010, dateIssued 2026-11-17, brokerName Southeast Carrier Services Inc., brokerMc 744120, carrierName Blue Ridge Freight Co., carrierMc 655120, carrierDot 2894417, lineHaul $2,780.00, fuelSurcharge $255.44, totalAgreedRate $3,135.44, detention "$60.00 per hour after 2 free hours at pickup or delivery.", paymentTerms "Net 30 days from receipt of signed proof of delivery (POD) and carrier invoice."
  - BOL CLT-330915 (13): bolNumber, loadNumber 52010, shipDate 2026-11-19, shipper Carolina Beverage Co. / "3100 Tryon St, Charlotte, NC 28217", consignee Gulf Coast Grocers / "410 Marina Blvd, Mobile, AL 36602", carrierName, driverName Marcus Webb, truckNumber BRF-305, trailerNumber BRF-T4408, sealNumber CB-662031, hazardousMaterials None.
  - Invoice BRF-2026-0127 (15): invoiceNumber, invoiceDate 2026-11-21, loadNumber, bolNumber CLT-330915, carrier and billTo names and addresses, lineHaul, fuelSurcharge, detention $90.00, totalDue $3,225.44, paymentDueDate 2026-12-21, paymentTerms Net 30, billingContact billing@blueridgefreight.example.
  - BOL ATL-501288 (12): bolNumber, loadNumber 52022, shipDate 2026-11-14, shipper and consignee names and addresses (inline labels), carrierName, driverName Terrence Hall, truckNumber BRF-421, trailerNumber BRF-F6618, totalWeight "44,800 lbs".
  - `proof-of-delivery-52035` is rejected `422 unknown_doc_type` (its name has no known prefix). Report it and skip it.
- **How the script runs.**
  - It uploads each `scanned/*.pdf` with one tenant key and each `clean/*.pdf` with the other. On 201 it takes `fields`, then **also** GETs the id and asserts the GET fields equal the POST fields. On 200 skipped it finds the id via the list by title and GETs it.
  - It prints `correct / missed (null) / wrong (non-null, different)` per document. It never prints keys.
- **Success criteria:**
  - Clean renders: at least 90% of the 52 facts correct.
  - Scans: at least 75% correct.
  - **Zero "wrong" values caused by a rule picking the wrong line or label.** A wrong value is acceptable only if the OCR text itself contains that misread, which you can confirm through GET `{id}` chunks.
  - Facts inside ruled tables without a row label, such as seal numbers in the fax table and pickup/delivery in the STOPS table, are expected to be missed. They are not in the truth table, and the docs say so.
  - Never add a document-specific hack. Every rule change must keep step 3 of section 7 green.
  - Record the measured numbers in the docs.

## 9. Security checklist (org policy: no high or critical issues expected; flag any found)

- **Tenant isolation.** Fields come only from the caller's own document. POST uses the bytes the caller just sent. GET goes through `IDocumentQueries.GetAsync(tenantId, ...)`, and a foreign id is a 404 before any `fields` validation. There is a test for this.
- **Never logged.** No new log statements carry values.
  - `ExtractedFields.ToString()` hides values, which protects any `{Result}` logging of the record.
  - Regex timeouts are caught inside the extractor; `RegexMatchTimeoutException` carries the input.
  - Problem details never echo the requested names or any content; they return only `code` and `validFields`, the latter being the static catalog.
  - There is a log test for this.
- **Untrusted text.** Values are returned as JSON strings and never interpreted, executed or used in SQL, paths, regex patterns or prompts. The OpenAPI description and the docs say clients must HTML-encode them before display.
  - **Medium risk, flag it:** a UI that renders values as HTML would get stored XSS from a malicious document.
- **Resource bounds.**
  - Text: 200,000 characters. Lines: 1,000 characters. Values: 300 characters.
  - `fields`: at most 1,000 characters and 50 entries.
  - Regexes are bounded and generated, with a 1 s timeout.
  - POST is already rate limited. GET is not; extraction on GET is bounded at well under a millisecond for typical documents, so that is acceptable. Note it as low risk.
- **Validation before cost.** Field errors are rejected before storage, embedding or OCR.
- No new packages, no new network paths, no secrets. Manual tests use the published test keys only through `$KEY`, and the key is never printed.

## 10. Docs to update

- `docs/articles/http-api.md`: add `?fields=` to the POST and GET `{id}` rows, the 400 codes `invalid_fields` and `unknown_field`, and curl examples.
- New `docs/articles/document-fields.md`: what it does, the catalog tables (section 3), the rules (as written, null when not found, first match wins, label anchors), OCR limits and the measured accuracy (section 8), the "not stored, computed on demand" note, and security notes.
  - Add a `toc.yml` entry: `- name: "Document fields: key/value extraction"` (**quote it: the title contains a colon**, and DocFX warnings fail the build).
- `docs/articles/architecture.md`: add a short "Document fields" section (pure domain service, called from the ingest handler and `GetDocumentHandler`, no storage).
- `README.md`: one line in the API feature list.
- Regenerate the OpenAPI snapshot (section 7 step 8). Check with `docker compose build docs`: 0 warnings.

## 11. Manual test

```bash
docker compose up -d --build api            # plus extract for PDFs
KEY='<Nile Freight test key from docs/articles/getting-started.md>'   # never echo it
curl -s -H "X-Api-Key: $KEY" -F "file=@data/tenant-nile-freight/rate-confirmation-48213.txt;type=text/plain" \
     "http://localhost:8080/api/v1/documents?fields=carrierName,carrierMc,loadNumber,totalAgreedRate"
curl -s -H "X-Api-Key: $KEY" "http://localhost:8080/api/v1/documents/<id>?fields=detention,paymentTerms"
curl -s -H "X-Api-Key: $KEY" "http://localhost:8080/api/v1/documents/<id>?fields=nope"     # 400 unknown_field + validFields
curl -s -H "X-Api-Key: $KEY" -F "file=@samples-realistic/scanned/rate-confirmation-52010.pdf;type=application/pdf" \
     http://localhost:8080/api/v1/documents                                                 # fields from OCR text
```

## 12. Hard constraints (project rules)

- Everything runs in Docker; nothing is installed locally.
- TDD: write the tests first, run them and see them fail, then implement.
- DDD: rules in the Domain, orchestration in the Application layer, thin endpoints.
- No new packages.
- One branch (`feature/document-fields`), one PR. **Ask before merging.**
- Commit and PR messages must **not mention AI tools**: no `Co-Authored-By` or "generated with" lines.
- Test commands: `docker compose run --build --rm tests`; integration tests with `docker compose run --build --rm integration-tests`; docs with `docker compose build docs`.

## 13. Definition of done

- All new and existing unit tests and integration tests pass in Docker.
- The OpenAPI snapshot is updated.
- The docs build has 0 warnings.
- The section 8 accuracy run meets its thresholds and its numbers are recorded in `document-fields.md`.
- The log-leak and tenant tests are green.
- There is no migration or schema change: `git diff --stat` shows nothing under `db/` or `Migrations/`.
- The PR is open against `main`, and the user has been asked before merging.

## 14. Risks

- **OCR layouts vary.** Tables and multi-column text cause nulls. These are reported honestly and never guessed.
- **POST and GET disagree** if the chunker format changes. The reassembly property test catches this; keep `SectionMarker` shared.
- **Same key, different meaning.** `detention` is a rate on a rate confirmation and a charge on an invoice. This is documented per type.
- **Label-inside-sentence facts** (HOU-220957) return null by design.
- **Stacking.** `9633fab` is not yet in `main`; resolve this first (section 0).

## Critical files for implementation

- `src/FreightRag.Api/Endpoints/DocumentEndpoints.cs`
- `src/FreightRag.Api/Endpoints/Contracts.cs`
- `src/FreightRag.Core/Application/IngestDocument.cs` (and `IngestResult.cs`)
- `src/FreightRag.Core/Domain/SectionChunker.cs` (plus new `DocumentFieldExtractor.cs`, `DocumentField.cs`, `FieldSelection.cs`, `ExtractedFields.cs`, `StoredDocumentText.cs`)
- `src/FreightRag.Tests/Api/DocumentEndpointsTests.cs`
