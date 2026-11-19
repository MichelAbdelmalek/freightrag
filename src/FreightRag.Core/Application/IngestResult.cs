namespace FreightRag.Core.Application;

/// <summary>Outcome of ingesting one file. Unexpected failures (DB down, Ollama error) are exceptions instead.</summary>
public abstract record IngestResult
{
    private IngestResult() { } // closed hierarchy: only the three cases below

    public sealed record Ingested(Guid DocumentId, int ChunkCount) : IngestResult;

    /// <summary>Already stored for this tenant — nothing to do.</summary>
    public sealed record Skipped(string Reason) : IngestResult;

    /// <summary>The input itself is not acceptable (unknown tenant/type, empty, not UTF-8).</summary>
    public sealed record Rejected(string Reason) : IngestResult;
}
