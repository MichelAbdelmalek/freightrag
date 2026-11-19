using FreightRag.Core.Application.Ports;
using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>Read-side queries with EF Core. Every query filters on the caller's tenant.</summary>
public sealed class EfDocumentQueries(IDbContextFactory<FreightRagDbContext> contexts) : IDocumentQueries
{
    public async Task<PagedResult<DocumentSummary>> ListAsync(TenantId tenantId, PageRequest page, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var mine = db.Documents.AsNoTracking().Where(d => d.TenantId == tenantId.Value);

        var total = await mine.CountAsync(cancellationToken);
        var items = await mine
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Skip(page.Offset).Take(page.PageSize)
            .Select(d => new DocumentSummary(d.Id, d.Title, d.DocType, d.CreatedAt,
                d.Chunks.Count(c => c.TenantId == tenantId.Value)))
            .ToListAsync(cancellationToken);

        return new PagedResult<DocumentSummary>(items, page, total);
    }

    public async Task<DocumentDetails?> GetAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var doc = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.TenantId == tenantId.Value)
            .Select(d => new
            {
                d.Id, d.Title, d.DocType, d.CreatedAt,
                Chunks = d.Chunks.Where(c => c.TenantId == tenantId.Value).OrderBy(c => c.ChunkIndex)
                    .Select(c => new ChunkText(c.ChunkIndex, c.Content)).ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return doc is null ? null : new DocumentDetails(doc.Id, doc.Title, doc.DocType, doc.CreatedAt, doc.Chunks);
    }
}
