# Document fields: key/value extraction

Besides storing a document's text for search, the API can read **named fields** out of it: the carrier's name, the load
number, the total due. They come back as plain key/value pairs.

```json
"fields": { "carrierName": "Nile Freight Lines Inc.", "loadNumber": "48213", "totalAgreedRate": "$2,630.00", "temperature": null }
```

- `POST /api/v1/documents` returns `fields` in its `201` result.
- `GET /api/v1/documents/{id}` returns `fields` too.
- Both accept `?fields=carrierName,loadNumber` to ask for specific fields. Leave it out for every field of that document type.
- The list endpoint (`GET /api/v1/documents`) stays a summary: reading fields means reading every chunk, which is too heavy for
  a page of 100 documents, and field names differ by document type.

## Not stored: read from the text

Nothing extra is saved. On upload the fields are read from the same text that is chunked (the decoded `.txt`, or the OCR text
of a PDF). On `GET` they are read again from the stored chunks, which are put back in order with their section headings. A test
checks, for every real document, that reading from the stored chunks gives exactly what reading from the original gives.
Two consequences: a re-upload of identical bytes returns `200 skipped` **without** fields (use `GET` with the id), and a
change to the rules changes what `GET` returns for old documents.

## How a field is read

Plain rules, no model. The documents are regular "Label: value" text, so the rules are deterministic, instant, and never invent
a value.

- **Labels.** A closed list of known labels (`Load number`, `Carrier`, `Total due`, ...), each followed by a colon, at the start of a
  line or after whitespace. Several labels on one line are fine (`Truck number: A-1, Trailer number: B-2`, or the one-line form
  OCR produces). The value runs to the next known label or the end of the line.
- **Sections.** Some labels only count inside a section: `Location:` under `PICKUP` is the pickup, under `DELIVERY` it is the
  delivery. Names and addresses under `SHIPPER`, `CONSIGNEE` and `CARRIER` are read from the lines below the heading.
- **As written.** Values are returned exactly as in the document: `"$2,630.00"`, `"2026-10-15 at 11:40"`, `"None"`. Nothing is
  turned into a number or a date and OCR mistakes are **not** corrected (`482l3` stays `482l3`).
- **Not found is `null`.** A missing label, a blank value, an underscore line (`Received by: ____`), or a value over 300
  characters gives `null`. The first usable match wins when a label appears twice.
- **Table rows.** If no label with a colon gives a money field, a row like `Line haul 1 $2,780.00 $2,780.00` gives its last amount.
- **Limits.** Only the first 200,000 characters are read, lines over 1,000 characters are skipped, and every pattern has a
  one-second time limit.

## The fields

Keys are camelCase and come back in this order. A document with no such label gets `null`. The driver handbook has no fields
(`fields` is `{}`, and asking for one is a `400`).

| Rate confirmation | Read from |
|---|---|
| `brokerName`, `brokerAddress`, `brokerMc` | `Broker:` (name, then the address, then `(MC# n)`) |
| `carrierName`, `carrierMc`, `carrierDot` | `Carrier:` |
| `loadNumber`, `dateIssued` | `Load number:`, `Date issued:` |
| `pickupLocation`, `shipperName`, `pickupWindow` | `Location:`, `Shipper:`, `Date and window:` in PICKUP |
| `deliveryLocation`, `deliveryDate` | `Location:`, `Date:` in DELIVERY |
| `commodity`, `equipment`, `temperature` | the same labels |
| `quantity`, `totalWeight` | `Quantity: 18 pallets, 38,400 lbs` split in two |
| `lineHaul`, `fuelSurcharge`, `totalAgreedRate`, `detention` | the same labels (the amount, or for detention the rate and free time) |
| `paymentTerms` | the text under PAYMENT TERMS |

| Bill of lading | Read from |
|---|---|
| `bolNumber`, `loadNumber`, `shipDate` | `BOL number:`, `Load number:`, `Ship date:` |
| `shipperName`, `shipperAddress`, `consigneeName`, `consigneeAddress` | the SHIPPER and CONSIGNEE sections (or an inline `Shipper:`) |
| `carrierName` | the CARRIER section (or an inline `Carrier:`) |
| `driverName`, `truckNumber`, `trailerNumber`, `sealNumber` | `Driver:`, `Truck number:`, `Trailer number:`, `Seal number:` |
| `quantity`, `commodity`, `totalWeight` | the sentence in SHIPMENT DETAILS ("18 pallets of ice cream, total weight 34,600 lbs") |
| `freightClass`, `temperatureSetting`, `hazardousMaterials` | the same labels |
| `deliveredAt`, `receivedBy`, `deliveryCondition` | `Delivered:`, `Received by:`, `Condition:` |
| `productTemperatureAtDelivery`, `sealIntact` | `Product temperature at delivery:`, `Seal intact on arrival:` |

| Invoice | Read from |
|---|---|
| `carrierName`, `carrierAddress` | `From:` |
| `billToName`, `billToAddress` | `Bill to:` |
| `invoiceNumber`, `invoiceDate`, `loadNumber`, `bolNumber` | the same labels |
| `lineHaul`, `fuelSurcharge`, `detention`, `totalDue` | the same labels (`Detention (2 billable hours at $75.00/hour): $150.00` gives `$150.00`; `Detention: none` gives `none`) |
| `paymentDueDate`, `paymentTerms` | `Payment due date: 2026-11-15 (Net 30)` split in two |
| `attachments`, `billingContact` | `Attachments:`, `Questions:` |

The same key can mean slightly different things per type: `detention` is a *rate* on a rate confirmation and a *charge* on an invoice.

## Asking for specific fields

`?fields=` takes comma-separated names, matched case-insensitively. Duplicates are ignored and the answer always uses the
canonical spelling and the catalog order.

| Problem | Result |
|---|---|
| A name that does not exist for this document type | `400`, `code: unknown_field`, and `validFields` listing the names that do |
| A name that is not letters only, more than 50 names, or over 1,000 characters | `400`, `code: invalid_fields` |

On upload the check happens **before** anything is stored, embedded or sent to OCR, so a typo costs nothing. On `GET` the
document is looked up first: a bad `fields` value on an id that belongs to another tenant is a `404`, so it can never reveal that
the id exists.

## What it measured

On real documents: a table of expected values (written from the files) is checked for twelve of the 23 non-handbook files in `data/`,
every field of every file must appear verbatim in its document, and the property above (stored chunks give the same fields as the original) holds for all 24 files,
including with tiny chunks that force many splits.

On the five **realistic** sample documents (ruled tables, handwriting, stamps, fax and photocopy damage;
`extract/tools/make_realistic_samples.py`) with `extract/e2e/check_fields.py`, over 52 printed, labelled facts per variant:

| | Correct | Missed (`null`) | Wrong |
|---|---|---|---|
| Clean render | 49 of 52 (94%) | 1 | 2 |
| Scan, fax and photocopy damage | 45 of 52 (87%) | 5 | 2 |

- Every **wrong** value is an OCR misreading that the field then reports as written (`BRF-T4408` read as `BRF-14408`, `lbs` as
  `Ibs`, a comma as a period, a stray `=`), not a rule picking the wrong line.
- **Misses** are layout. For example, OCR put the bold total's amount on its own line above the label `Total agreed rate`, so
  the label has no value on its line. The field is `null`, not guessed. Facts inside unlabeled table cells, and handwriting, are
  not read at all.
- The first version failed this check (58% clean, 48% scanned, with 8 wrong values each) because it only accepted a label after
  two spaces, and OCR collapses wide gaps to single spaces. Accepting any whitespace before a closed set of labels fixed it. A
  side effect: `Condition: ... Product temperature at delivery: -8 degrees F.` now splits into two fields, which is what it says.

## Security

- **Tenant isolation.** Fields come only from the caller's own document: the bytes just uploaded, or a document looked up with
  the caller's tenant. Another tenant's id is `404`.
- **Never logged.** Field values are document content. No log line carries them, the result object's text form shows only
  counts, a regular-expression timeout is caught inside the extractor (its message would hold the input), and problem details
  list only a fixed set of names. Tests check the logs.
- **Untrusted text.** A value is data from an uploaded file. It is returned as a JSON string and never interpreted. **Clients must
  HTML-encode values before showing them**: a page that renders a value as HTML would hand a malicious document a script
  injection.
- **Limits.** Text, line, value and request-option sizes are capped (above) and every pattern has a time limit.
