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
            .WithSummary("Ingest one .txt document")
            .WithDescription(
                "Chunks the file by section, embeds every chunk with nomic-embed-text and stores it for the caller's tenant. " +
                "Idempotent: uploading the same bytes again returns 200 with status \"skipped\". " +
                "The document type comes from the file-name prefix (rate-confirmation-, bill-of-lading-, invoice-, driver-handbook-).")
            // Antiforgery protects cookie-authenticated browser forms; this API uses an explicit header key,
            // which a cross-site form cannot send, so CSRF does not apply.
            .DisableAntiforgery()
            .RequireRateLimiting(IngestRateLimitPolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapGet("/", ListAsync)
            .WithName("ListDocuments")
            .WithSummary("List the caller's documents")
            .WithDescription("Newest first. page starts at 1; pageSize is 1–100 (default 20).")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetDocument")
            .WithSummary("Get one document with its chunks")
            .WithDescription("Returns 404 both when the document doesn't exist and when it belongs to another tenant.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    /// <param name="file">The .txt document (multipart/form-data field "file").</param>
    internal static async Task<Results<Created<IngestedResponse>, Ok<SkippedResponse>, ProblemHttpResult>> IngestAsync(
        IFormFile file, ClaimsPrincipal user, IngestDocumentHandler handler, CancellationToken cancellationToken)
    {
        var fileName = file.FileName;

        // Only a bare file name: no directories, no "..", no backslashes. The tenant comes from the key,
        // so a crafted path could not reach another tenant anyway, but rejecting it keeps titles clean.
        // Control characters (NUL, newlines) and invisible format characters (e.g. right-to-left override,
        // which can disguise "txt.exe" as "exe.txt") are refused as well.
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 200 || fileName.Contains('\\')
            || Path.GetFileName(fileName) != fileName || fileName.StartsWith('.')
            || fileName.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return Problem(StatusCodes.Status400BadRequest, "Invalid file name", "Send a plain file name such as invoice-NFL-2026-1187.txt.");
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return Problem(StatusCodes.Status400BadRequest, "Unsupported file", "Only .txt files are accepted.");
        if (file.Length > MaxUploadBytes)
            return Problem(StatusCodes.Status413PayloadTooLarge, "File too large", $"The limit is {MaxUploadBytes / (1024 * 1024)} MB.");

        byte[] content;
        await using (var stream = file.OpenReadStream())
        {
            using var buffer = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        var result = await handler.HandleAsync(
            new IngestDocumentCommand(user.GetTenantSlug(), fileName, content), cancellationToken);

        return result switch
        {
            IngestResult.Ingested r => TypedResults.Created(
                $"/api/v1/documents/{r.DocumentId}",
                new IngestedResponse(
                    r.DocumentId,
                    Path.GetFileNameWithoutExtension(fileName),
                    DocType.TryFromFileName(fileName, out var docType) ? docType.Name : "",
                    r.ChunkCount)),
            IngestResult.Skipped r => TypedResults.Ok(new SkippedResponse("skipped", r.Reason)),
            IngestResult.Rejected r => Problem(StatusCodes.Status422UnprocessableEntity, "Document rejected", r.Reason),
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
    internal static async Task<Results<Ok<DocumentResponse>, NotFound>> GetAsync(
        Guid id, ClaimsPrincipal user, ITenantDirectory tenants, IDocumentQueries queries, CancellationToken cancellationToken)
    {
        var doc = await queries.GetAsync(TenantOf(user, tenants), id, cancellationToken);
        return doc is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new DocumentResponse(doc.Id, doc.Title, doc.DocType, doc.CreatedAt,
                doc.Chunks.Select(c => new ChunkResponse(c.Index, c.Content)).ToList()));
    }

    private static TenantId TenantOf(ClaimsPrincipal user, ITenantDirectory tenants) =>
        tenants.TryResolve(user.GetTenantSlug(), out var tenantId)
            ? tenantId
            : throw new InvalidOperationException("Authenticated tenant is not configured."); // ApiKeyStore prevents this at startup

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
