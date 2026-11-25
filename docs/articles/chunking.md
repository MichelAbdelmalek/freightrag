# Chunking and context headers

Retrieval returns *chunks*, not documents, so how a document is cut decides what an LLM will later see.

## Why chunk by section

One embedding for a whole rate confirmation blends pickup, rate, detention and payment terms into one
average meaning. One embedding per **section** stays sharp: a question about the detention rate lands
on the `RATE` chunk.

## Section rules (`SectionChunker`)

- A section starts at a heading: a short line with no lower-case letters (`RATE`, `PICKUP`) or a
  numbered heading (`3. DETENTION REPORTING`). It must be mostly letters and not end with `.`, so codes
  like `SAV-778431` and shouted sentences are not headings.
- In a run of consecutive ALL-CAPS lines only the first is the heading (`SHIPPER` / `BAYOU FROZEN FOODS`
  / `1500 CLINTON DR` stays together). A heading with no body keeps its own text. **No text is lost.**
- Sections over ~1,000 characters are split on paragraph boundaries, then on line boundaries. A line is
  never cut.

## The context header

The `RATE` section says `Detention: $50.00 per hour…` but never mentions the load. Stored alone, a
question about "detention on load 48213" has nothing to match. So every chunk starts with a header that
is embedded **together with** the text:

```
Document: rate-confirmation | Load: 48213 | Section: RATE
Line haul: $2,450.00
Detention: $50.00 per hour after 2 free hours at pickup or delivery
```

Identifiers (load, BOL, invoice numbers) are extracted once per document with anchored regular
expressions and repeated in every chunk header.

## Embedding

`nomic-embed-text` is trained with task prefixes: chunks are embedded as `search_document: <content>`
(questions will use `search_query: `). The prefix is sent to the model only — never stored. All chunks of
a document are embedded in one batch call, and any vector that isn't exactly 768-dimensional stops the
document with a clear error.

## Text that came from a PDF or a scan

A PDF is turned into plain text first ([PDF ingestion](pdf-ingestion.md)), and then chunked by exactly the same rules. Two
consequences of those rules matter for OCR text:

- **Lines stay lines.** The extractor never joins lines into paragraphs, because identifiers (`Load number: 48213`) and the
  heading rule both work one line at a time.
- **Pages are joined with a blank line and no marker.** A line like `PAGE 2` is all capitals, so the chunker would take it for
  a section heading and invent a section. Lines such as `Page 2 of 3` are dropped.

If OCR misreads a heading (`RATE` becomes `RATe`), nothing is lost: the lines simply stay in the section before it. If no
line looks like a heading at all, the whole document is one `(untitled)` section, split every 1,000 characters, and the
extraction report says `no_headings`.

