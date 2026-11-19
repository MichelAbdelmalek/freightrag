namespace FreightRag.Api.Endpoints;

// HTTP contracts: what clients see. Kept separate from the Application read models so the public API
// can stay stable while internals change. They are documented in the OpenAPI document.

/// <summary>A document was stored.</summary>
/// <param name="Id">The new document's id; also in the Location header.</param>
/// <param name="Title">The file name without extension.</param>
/// <param name="DocType">rate-confirmation, bill-of-lading, invoice or driver-handbook.</param>
/// <param name="ChunkCount">How many chunks were embedded and stored.</param>
public sealed record IngestedResponse(Guid Id, string Title, string DocType, int ChunkCount);

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
public sealed record DocumentResponse(
    Guid Id, string Title, string DocType, DateTimeOffset CreatedAt, IReadOnlyList<ChunkResponse> Chunks);

/// <summary>A stored chunk: its position and its content, starting with the context header line.</summary>
public sealed record ChunkResponse(int Index, string Content);
