namespace FreightRag.Core.Infrastructure.Persistence.Records;

/// <summary>
/// Persistence shape of a row in <c>documents</c>. Kept separate from the domain aggregate
/// (<see cref="Domain.FreightDocument"/>) so the domain never depends on EF Core; repositories map between them.
/// </summary>
public sealed class DocumentRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Title { get; set; } = "";
    public string DocType { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<ChunkRecord> Chunks { get; set; } = [];
}
