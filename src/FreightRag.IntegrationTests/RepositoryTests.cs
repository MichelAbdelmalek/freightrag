using System.Text;
using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FreightRag.IntegrationTests;

/// <summary>The EF Core repository and queries against a real database.</summary>
public sealed class RepositoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TenantId Nile = new(Seed.TenantA);
    private static readonly TenantId Delta = new(Seed.TenantB);

    private static FreightDocument Document(TenantId tenant, string text = "RATE CONFIRMATION\nLoad number: 48213\n\nRATE\nLine haul: $1\n")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var doc = FreightDocument.Create(tenant, "rate-confirmation-48213.txt", DocType.RateConfirmation,
            SourceHash.FromContent(bytes), text, new SectionChunker());
        doc.AttachEmbeddings(doc.Chunks.Select((_, i) => new EmbeddingVector(Vectors.Near(i, 0.1f))).ToList(), TestDatabase.Model);
        return doc;
    }

    [Fact]
    public async Task Stores_a_document_with_its_chunks_and_embedding_model_in_one_go()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new EfDocumentRepository(database.Contexts);
        var doc = Document(Nile);

        Assert.True(await repository.AddAsync(doc, Ct));
        Assert.True(await repository.ExistsAsync(Nile, doc.SourceHash, Ct));
        Assert.False(await repository.ExistsAsync(Delta, doc.SourceHash, Ct));

        await using var db = database.Contexts.CreateDbContext();
        var chunks = await db.Chunks.Where(c => c.DocumentId == doc.Id).OrderBy(c => c.ChunkIndex).ToListAsync(Ct);
        Assert.Equal(doc.Chunks.Select(c => c.Content), chunks.Select(c => c.Content));
        Assert.All(chunks, c => Assert.Equal("nomic-embed-text", c.EmbeddingModel));
        Assert.All(chunks, c => Assert.Equal(Nile.Value, c.TenantId));
    }

    [Fact]
    public async Task A_concurrent_duplicate_is_reported_as_not_added_and_writes_nothing()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new EfDocumentRepository(database.Contexts);
        Assert.True(await repository.AddAsync(Document(Nile), Ct));

        // Same tenant + same bytes, different aggregate id: what a second, racing run would insert.
        Assert.False(await repository.AddAsync(Document(Nile), Ct));

        await using var db = database.Contexts.CreateDbContext();
        Assert.Equal(1, await db.Documents.CountAsync(Ct));
        Assert.Equal(2, await db.Chunks.CountAsync(Ct));
    }

    [Fact]
    public async Task Queries_only_ever_see_the_callers_tenant()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new EfDocumentRepository(database.Contexts);
        var queries = new EfDocumentQueries(database.Contexts);
        var nileDoc = Document(Nile);
        var deltaDoc = Document(Delta);
        await repository.AddAsync(nileDoc, Ct);
        await repository.AddAsync(deltaDoc, Ct);

        var list = await queries.ListAsync(Nile, PageRequest.Create(1, 10), Ct);
        Assert.Equal(nileDoc.Id, Assert.Single(list.Items).Id);
        Assert.Equal(2, list.Items[0].ChunkCount);

        Assert.NotNull(await queries.GetAsync(Nile, nileDoc.Id, Ct));
        Assert.Null(await queries.GetAsync(Nile, deltaDoc.Id, Ct)); // another tenant's document: not found
    }
}
