using FreightRag.Core.Domain;

namespace FreightRag.Core.Application;

/// <summary>Outcome of ingesting one file. Unexpected failures (DB down, Ollama error) are exceptions instead.</summary>
public abstract record IngestResult
{
    private IngestResult() { } // closed hierarchy: only the three cases below

    public sealed record Ingested(Guid DocumentId, int ChunkCount, SourceFormat? Format = null, ExtractionReport? Extraction = null) : IngestResult;

    /// <summary>Already stored for this tenant — nothing to do.</summary>
    public sealed record Skipped(string Reason) : IngestResult;

    /// <summary>The input itself is not acceptable (unknown tenant/type, empty, not UTF-8).</summary>
    /// <param name="Reason">A fixed message for the caller; it never contains document content.</param>
    /// <param name="Code">A fixed category, such as not_pdf or encrypted_pdf.</param>
    public sealed record Rejected(string Reason, string Code = "rejected") : IngestResult;
}
