using System.Globalization;
using System.Security.Claims;
using FreightRag.Api.Security;
using FreightRag.Core.Application;
using FreightRag.Core.Application.Ports;
using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FreightRag.Api.Endpoints;

/// <summary>
/// The /api/v1/documents endpoints. They are thin adapters: translate HTTP to the application's use
/// case and queries, and results back to HTTP status codes. No business rules live here.
/// </summary>
public static class DocumentEndpoints
{
    /// <summary>Same limit as the console ingest: these are short text documents.</summary>
    public const long MaxUploadBytes = 10 * 1024 * 1024;

    public const string IngestRateLimitPolicy = "ingest";

    public static RouteGroupBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/documents")
            .WithTags("Documents")
            .RequireAuthorization(); // every route below needs a valid API key

        group.MapPost("/", IngestAsync)
            .WithName("IngestDocument")
            .WithSummary("Ingest one .txt or .pdf document")
            .WithDescription(
                "Chunks the file by section, embeds every chunk with nomic-embed-text and stores it for the caller's tenant. " +
                "Idempotent: uploading the same bytes again returns 200 with status \"skipped\". " +
                "The document type comes from the file-name prefix (rate-confirmation-, bill-of-lading-, invoice-, driver-handbook-). " +
                "A .pdf may be a scan: pages without a text layer are read with OCR (English), up to 20 pages, and it can take " +
                "up to a few minutes. The 201 response then carries an extraction report (pages, OCR confidence, warnings; never content). " +
                "A rejection has a machine-readable \"code\" (for example not_pdf, encrypted_pdf, too_many_pages, no_text). " +
                "The 201 response also carries the fields read from the document as name/value pairs (for example carrierName, loadNumber); ?fields=a,b picks some of them.")
            // Antiforgery protects cookie-authenticated browser forms; this API uses an explicit header key,
            // which a cross-site form cannot send, so CSRF does not apply.
            .DisableAntiforgery()
            .RequireRateLimiting(IngestRateLimitPolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/", ListAsync)
            .WithName("ListDocuments")
            .WithSummary("List the caller's documents")
            .WithDescription("Newest first. page starts at 1; pageSize is 1–100 (default 20).")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetDocument")
            .WithSummary("Get one document with its chunks")
            .WithDescription("Returns 404 both when the document doesn't exist and when it belongs to another tenant. " +
                "The fields are read from the stored text on request (nothing extra is stored); ?fields=a,b picks some of them.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    /// <param name="file">The .txt or .pdf document (multipart/form-data field "file").</param>
    /// <param name="fields">Comma-separated field names to read from the document, case-insensitive, for example carrierName,loadNumber. Omit for every field of its document type. An unknown name is a 400 with code unknown_field and the valid names in validFields.</param>
    internal static async Task<Results<Created<IngestedResponse>, Ok<SkippedResponse>, ProblemHttpResult>> IngestAsync(
        IFormFile file, string? fields, ClaimsPrincipal user, HttpContext httpContext, IngestDocumentHandler handler,
        CancellationToken cancellationToken)
    {
        var fileName = file.FileName;

        // Only a bare file name: no directories, no "..", no backslashes. The tenant comes from the key,
        // so a crafted path could not reach another tenant anyway, but rejecting it keeps titles clean.
        // Control characters (NUL, newlines) and invisible format characters (e.g. right-to-left override,
        // which can disguise "txt.exe" as "exe.txt") are refused as well.
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 200 || fileName.Contains('\\')
            || Path.GetFileName(fileName) != fileName || fileName.StartsWith('.')
            || fileName.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return Problem(StatusCodes.Status400BadRequest, "Invalid file name", "Send a plain file name such as invoice-NFL-2026-1187.txt.", "invalid_file_name");
        if (!SourceFormat.TryFromFileName(fileName, out _))
            return Problem(StatusCodes.Status400BadRequest, "Unsupported file", "Only .txt and .pdf files are accepted.", "unsupported_file");
        if (file.Length > MaxUploadBytes)
            return Problem(StatusCodes.Status413PayloadTooLarge, "File too large", $"The limit is {MaxUploadBytes / (1024 * 1024)} MB.", "too_large");

        // Checked before anything is stored, embedded or OCR'd. If the name has no known type the handler's own 422 applies.
        FieldSelection? selection = null;
        if (DocType.TryFromFileName(fileName, out var typeOfName))
        {
            if (!FieldSelection.TryParse(fields, typeOfName, out selection, out var fieldError))
                return FieldProblem(fieldError!);
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        {
            using var buffer = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        IngestResult result;
        try
        {
            result = await handler.HandleAsync(
                new IngestDocumentCommand(user.GetTenantSlug(), fileName, content, selection), cancellationToken);
        }
        catch (PdfExtractorBusyException busy)
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(busy.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Problem(StatusCodes.Status503ServiceUnavailable, "PDF extractor busy", "Another PDF is being read. Try again shortly.", "extractor_busy");
        }
        catch (PdfExtractorUnavailableException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "PDF extractor unavailable", "PDF files cannot be read right now.", "extractor_unavailable");
        }

        return result switch
        {
            IngestResult.Ingested r => TypedResults.Created(
                $"/api/v1/documents/{r.DocumentId}",
                new IngestedResponse(
                    r.DocumentId,
                    Path.GetFileNameWithoutExtension(fileName),
                    DocType.TryFromFileName(fileName, out var docType) ? docType.Name : "",
                    r.ChunkCount,
                    (r.Format ?? SourceFormat.Text).Name,
                    r.Extraction is null ? null : ToResponse(r.Extraction),
                    r.Fields is null ? null : ToDictionary(r.Fields))),
            IngestResult.Skipped r => TypedResults.Ok(new SkippedResponse("skipped", r.Reason)),
            IngestResult.Rejected r => Problem(StatusCodes.Status422UnprocessableEntity, "Document rejected", r.Reason, r.Code),
            _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}."),
        };
    }

    /// <param name="page">1-based page number (default 1).</param>
    /// <param name="pageSize">Items per page, 1–100 (default 20).</param>
    internal static async Task<Results<Ok<DocumentListResponse>, ProblemHttpResult>> ListAsync(
        int? page, int? pageSize, ClaimsPrincipal user, ITenantDirectory tenants, IDocumentQueries queries,
        CancellationToken cancellationToken)
    {
        PageRequest request;
        try
        {
            request = PageRequest.Create(page, pageSize);
        }
        catch (DomainException ex)
        {
            // Same shape as the 400 the framework returns for unparsable values such as page=abc.
            return Problem(StatusCodes.Status400BadRequest, "Invalid paging", ex.Message);
        }

        var result = await queries.ListAsync(TenantOf(user, tenants), request, cancellationToken);
        return TypedResults.Ok(new DocumentListResponse(
            result.Items.Select(d => new DocumentSummaryResponse(d.Id, d.Title, d.DocType, d.CreatedAt, d.ChunkCount)).ToList(),
            request.Page, request.PageSize, result.TotalCount, result.TotalPages));
    }

    /// <param name="id">The document id returned by the upload.</param>
    /// <param name="fields">Comma-separated field names, case-insensitive, for example carrierName,loadNumber. Omit for every field of the document's type. An unknown name is a 400 with code unknown_field and the valid names in validFields.</param>
    internal static async Task<Results<Ok<DocumentResponse>, NotFound, ProblemHttpResult>> GetAsync(
        Guid id, string? fields, ClaimsPrincipal user, ITenantDirectory tenants, GetDocumentHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(TenantOf(user, tenants), id, fields, cancellationToken);
        return result switch
        {
            GetDocumentResult.Found f => TypedResults.Ok(new DocumentResponse(f.Details.Id, f.Details.Title, f.Details.DocType,
                f.Details.CreatedAt, f.Details.Chunks.Select(c => new ChunkResponse(c.Index, c.Content)).ToList(), ToDictionary(f.Fields))),
            GetDocumentResult.InvalidFields i => FieldProblem(i.Error),
            _ => TypedResults.NotFound(),
        };
    }

    private static TenantId TenantOf(ClaimsPrincipal user, ITenantDirectory tenants) =>
        tenants.TryResolve(user.GetTenantSlug(), out var tenantId)
            ? tenantId
            : throw new InvalidOperationException("Authenticated tenant is not configured."); // ApiKeyStore prevents this at startup

    /// <summary>Name to value in catalog order. A plain dictionary would not promise that order; this one does.</summary>
    private static OrderedDictionary<string, string?> ToDictionary(ExtractedFields fields)
    {
        var dictionary = new OrderedDictionary<string, string?>(fields.Count);
        foreach (var (name, value) in fields.Items)
            dictionary[name] = value;
        return dictionary;
    }

    /// <summary>400 for a bad fields option. Carries the valid names (a fixed list) and never echoes what was sent.</summary>
    private static ProblemHttpResult FieldProblem(FieldSelectionError error) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid fields", detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["validFields"] = error.ValidNames });

    private static ExtractionResponse ToResponse(ExtractionReport r) => new(
        r.PageCount, r.OcrPageCount, r.TextLayerPageCount, r.CharacterCount, r.MeanOcrConfidence, r.LowConfidence,
        r.HeadingCount, r.NumericCorrections, r.LowConfidenceNumericTokens, r.DurationMs,
        r.Pages.Select(p => new PageResponse(p.Page, p.Method, p.CharacterCount, p.MeanConfidence, p.Dpi)).ToList(), r.Warnings);

    /// <summary>RFC 9457 problem details; <paramref name="code"/> is a fixed machine-readable category (never content).</summary>
    private static ProblemHttpResult Problem(int status, string title, string detail, string? code = null) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail,
            extensions: code is null ? null : new Dictionary<string, object?> { ["code"] = code });
}
