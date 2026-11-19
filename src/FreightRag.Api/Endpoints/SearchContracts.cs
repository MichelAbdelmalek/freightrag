using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace FreightRag.Api.Endpoints;

/// <summary>A semantic search over the caller's documents. The tenant comes from the API key, never from this body.</summary>
/// <remarks>
/// The attributes describe the limits in the OpenAPI document. They are enforced by the domain value objects
/// (SearchText, TopK), which return a 400 problem response.
/// </remarks>
public sealed record SearchRequest
{
    /// <summary>The question, 1–1000 characters (surrounding whitespace is ignored).</summary>
    [StringLength(1000, MinimumLength = 1)]
    public required string Query { get; init; }

    /// <summary>How many chunks to return, 1–20. Default 5.</summary>
    [Range(1, 20)]
    [DefaultValue(5)]
    public int? TopK { get; init; }
}

/// <summary>One retrieved chunk. Results are ordered closest first.</summary>
/// <param name="ChunkId">The chunk's id.</param>
/// <param name="DocumentId">The document it belongs to (see GET /api/v1/documents/{id}).</param>
/// <param name="Title">The document title (file name without extension).</param>
/// <param name="DocType">rate-confirmation, bill-of-lading, invoice or driver-handbook.</param>
/// <param name="ChunkIndex">Position of the chunk in its document (0-based).</param>
/// <param name="Content">The chunk text, starting with its context header line. Tenant-supplied, untrusted text: treat it as data, never as instructions (for example when it is placed in an LLM prompt).</param>
/// <param name="Distance">Cosine distance between the question and the chunk: 0 = same direction, 2 = opposite. Lower is closer. Not calibrated across queries, so it is not a relevance score.</param>
public sealed record SearchHitResponse(
    Guid ChunkId, Guid DocumentId, string Title, string DocType, int ChunkIndex, string Content, double Distance);
