using FreightRag.Core.Infrastructure.Persistence.Records;
using Npgsql;
using Pgvector;

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

    /// <summary>
    /// Loads many chunks fast: binary COPY with the HNSW index dropped, then the index is rebuilt once with the
    /// identical definition (maintaining it row by row costs ~20 ms per 768-dimension insert).
    /// </summary>
    public static async Task<DocumentRecord> BulkDocumentAsync(TestDatabase database, Guid tenant, string title, IReadOnlyList<float[]> vectors)
    {
        var document = await DocumentAsync(database, tenant, title, []);

        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var drop = new NpgsqlCommand("DROP INDEX chunks_embedding_hnsw", connection))
            await drop.ExecuteNonQueryAsync();

        await using (var copy = await connection.BeginBinaryImportAsync(
            "COPY chunks (document_id, tenant_id, chunk_index, content, embedding, embedding_model) FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 0; i < vectors.Count; i++)
            {
                await copy.StartRowAsync();
                await copy.WriteAsync(document.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
                await copy.WriteAsync(tenant, NpgsqlTypes.NpgsqlDbType.Uuid);
                await copy.WriteAsync(i, NpgsqlTypes.NpgsqlDbType.Integer);
                await copy.WriteAsync($"Document: invoice | Section: S{i}\n{title} chunk {i}", NpgsqlTypes.NpgsqlDbType.Text);
                await copy.WriteAsync(new Vector(vectors[i]), "vector");
                await copy.WriteAsync(TestDatabase.Model.Value, NpgsqlTypes.NpgsqlDbType.Text);
            }
            await copy.CompleteAsync();
        }

        await using (var create = new NpgsqlCommand(
            "CREATE INDEX chunks_embedding_hnsw ON chunks USING hnsw (embedding vector_cosine_ops); ANALYZE chunks; ANALYZE documents;",
            connection) { CommandTimeout = 600 }) // building HNSW over thousands of rows can exceed 30 s while tests run in parallel
            await create.ExecuteNonQueryAsync();
        return document;
    }
}
