using FreightRag.Core.Infrastructure.Persistence.Records;

namespace FreightRag.IntegrationTests;

/// <summary>Writes documents and chunks with chosen vectors straight through EF Core.</summary>
internal static class Seed
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    public static async Task<DocumentRecord> DocumentAsync(
        TestDatabase database, Guid tenant, string title, IEnumerable<float[]> vectors, string? model = null)
    {
        var document = new DocumentRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            Title = title,
            DocType = "invoice",
            SourceHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Chunks = vectors.Select((v, i) => new ChunkRecord
            {
                TenantId = tenant,
                ChunkIndex = i,
                Content = $"Document: invoice | Section: S{i}\n{title} chunk {i}",
                Embedding = Vectors.ToVector(v),
                EmbeddingModel = model ?? TestDatabase.Model.Value,
            }).ToList(),
        };

        await using var db = database.Contexts.CreateDbContext();
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document;
    }
}
