using FreightRag.Core.Application.Ports;
using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IDocumentRepository"/>. Maps the domain aggregate to persistence
/// records; EF Core parameterizes every value, so no SQL is built from data.
/// </summary>
public sealed class EfDocumentRepository(IDbContextFactory<FreightRagDbContext> contexts) : IDocumentRepository
{
    private const string UniqueViolation = "23505";
    private const string TenantSourceHashKey = "documents_tenant_id_source_hash_key";

    public async Task<bool> ExistsAsync(TenantId tenantId, SourceHash sourceHash, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Documents.AnyAsync(d => d.TenantId == tenantId.Value && d.SourceHash == sourceHash.Value, cancellationToken);
    }

    public async Task<bool> AddAsync(FreightDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsReadyToStore)
            throw new InvalidOperationException($"Document '{document.Title}' cannot be stored before all chunks are embedded.");

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.Documents.Add(new DocumentRecord
        {
            Id = document.Id,
            TenantId = document.TenantId.Value,
            Title = document.Title,
            DocType = document.DocType.Name,
            SourceHash = document.SourceHash.Value,
            Chunks = document.Chunks.Select(c => new ChunkRecord
            {
                TenantId = document.TenantId.Value,
                ChunkIndex = c.Index,
                Content = c.Content,
                Embedding = new Vector(c.Embedding!.Values),
                EmbeddingModel = c.EmbeddingModel!.Value,
            }).ToList(),
        });

        try
        {
            // One SaveChanges = one transaction: the document and all its chunks, or nothing.
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: TenantSourceHashKey })
        {
            // Another run stored the same file for this tenant between ExistsAsync and now: nothing was written.
            return false;
        }
    }
}
