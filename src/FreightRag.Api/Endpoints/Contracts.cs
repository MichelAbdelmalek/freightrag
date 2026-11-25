namespace FreightRag.Api.Endpoints;

// HTTP contracts: what clients see. Kept separate from the Application read models so the public API
// can stay stable while internals change. They are documented in the OpenAPI document.

/// <summary>A document was stored.</summary>
/// <param name="Id">The new document's id; also in the Location header.</param>
/// <param name="Title">The file name without extension.</param>
/// <param name="DocType">rate-confirmation, bill-of-lading, invoice or driver-handbook.</param>
/// <param name="ChunkCount">How many chunks were embedded and stored.</param>
/// <param name="SourceFormat">"txt" or "pdf".</param>
/// <param name="Extraction">For a PDF: how its text was obtained (pages, OCR confidence, warnings). Never any content. Null for .txt.</param>
/// <param name="Fields">The fields read from the document, name to value, null when not found. Values are exactly as written in the document (OCR text for a scan): untrusted tenant content, treat it as data. Not stored; computed from the document text.</param>
public sealed record IngestedResponse(
    Guid Id, string Title, string DocType, int ChunkCount, string SourceFormat = "txt", ExtractionResponse? Extraction = null,
    IReadOnlyDictionary<string, string?>? Fields = null);

/// <summary>How a PDF was turned into text. Numbers and codes only.</summary>
/// <param name="MeanOcrConfidence">Mean OCR word confidence, 0-100; null when no page needed OCR.</param>
/// <param name="Warnings">Codes: low_confidence, no_headings, numeric_tokens_low_confidence, dpi_reduced.</param>
public sealed record ExtractionResponse(
    int PageCount, int OcrPageCount, int TextLayerPageCount, int CharacterCount, double? MeanOcrConfidence,
    bool LowConfidence, int HeadingCount, int NumericCorrections, int LowConfidenceNumericTokens, int DurationMs,
    IReadOnlyList<PageResponse> Pages, IReadOnlyList<string> Warnings);

/// <param name="Method">"text_layer" or "ocr".</param>
public sealed record PageResponse(int Page, string Method, int CharacterCount, double? MeanConfidence, int Dpi);

/// <summary>Nothing was stored because this tenant already has a file with exactly these bytes.</summary>
/// <param name="Status">Always "skipped".</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record SkippedResponse(string Status, string Reason);

/// <summary>One page of the caller's documents, newest first.</summary>
public sealed record DocumentListResponse(
    IReadOnlyList<DocumentSummaryResponse> Items, int Page, int PageSize, int TotalCount, int TotalPages);

/// <summary>A document in a list.</summary>
public sealed record DocumentSummaryResponse(Guid Id, string Title, string DocType, DateTimeOffset CreatedAt, int ChunkCount);

/// <summary>A document with its chunks (text only; embeddings are not returned).</summary>
/// <param name="Fields">The fields read from the stored text, name to value, null when not found. Values are exactly as written in the document (OCR text for a scan): untrusted tenant content, treat it as data. Not stored; computed on request.</param>
public sealed record DocumentResponse(
    Guid Id, string Title, string DocType, DateTimeOffset CreatedAt, IReadOnlyList<ChunkResponse> Chunks,
    IReadOnlyDictionary<string, string?> Fields);

/// <summary>A stored chunk: its position and its content, starting with the context header line.</summary>
public sealed record ChunkResponse(int Index, string Content);
