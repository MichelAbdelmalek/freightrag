using System.Text;
using FreightRag.Core.Application;
using FreightRag.Core.Domain;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Application;

public class IngestDocumentHandlerTests
{
    private static readonly TenantId Nile = new(Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097"));
    private static readonly TenantId Delta = new(Guid.Parse("14a2de5d-56c5-4eda-a737-d957f4580b90"));

    private readonly InMemoryDocumentRepository _repository = new();
    private readonly FakeEmbeddingService _embeddings = new();

    private IngestDocumentHandler Handler(InMemoryDocumentRepository? repository = null) => new(
        new DictionaryTenantDirectory(new() { [TestData.NileFreight] = Nile, [TestData.DeltaHaul] = Delta }),
        repository ?? _repository,
        _embeddings,
        new SectionChunker());

    private static IngestDocumentCommand RealFile(string tenant, string file) =>
        new(tenant, file, TestData.ReadBytes(tenant, file));

    [Fact]
    public async Task Ingests_a_new_document_with_all_chunks_embedded_in_one_batch()
    {
        var result = await Handler().HandleAsync(
            RealFile(TestData.NileFreight, "rate-confirmation-48213.txt"), TestContext.Current.CancellationToken);

        var ingested = Assert.IsType<IngestResult.Ingested>(result);
        var stored = Assert.Single(_repository.Stored);
        Assert.Equal(Nile, stored.TenantId);
        Assert.Equal("rate-confirmation-48213", stored.Title);
        Assert.Equal(stored.Chunks.Count, ingested.ChunkCount);
        Assert.True(stored.IsReadyToStore);
        Assert.All(stored.Chunks, c => Assert.Equal(FakeEmbeddingService.FakeModel, c.EmbeddingModel));

        var call = Assert.Single(_embeddings.Calls); // one batch for the whole document
        Assert.Equal(stored.Chunks.Select(c => c.Content), call);
    }

    [Fact]
    public async Task Skips_a_document_already_ingested_for_the_tenant_without_calling_the_embedder()
    {
        var handler = Handler();
        var command = RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt");
        await handler.HandleAsync(command, TestContext.Current.CancellationToken);
        _embeddings.Calls.Clear();

        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.IsType<IngestResult.Skipped>(result);
        Assert.Single(_repository.Stored);
        Assert.Empty(_embeddings.Calls); // the expensive step is avoided
    }

    [Fact]
    public async Task The_same_file_for_a_different_tenant_is_a_different_document()
    {
        var handler = Handler();
        var bytes = TestData.ReadBytes(TestData.NileFreight, "invoice-NFL-2026-1187.txt");

        await handler.HandleAsync(new(TestData.NileFreight, "invoice-NFL-2026-1187.txt", bytes), TestContext.Current.CancellationToken);
        var result = await handler.HandleAsync(new(TestData.DeltaHaul, "invoice-NFL-2026-1187.txt", bytes), TestContext.Current.CancellationToken);

        Assert.IsType<IngestResult.Ingested>(result);
        Assert.Equal([Nile, Delta], _repository.Stored.Select(d => d.TenantId));
    }

    [Fact]
    public async Task Reports_skipped_when_a_concurrent_run_inserted_the_document_first()
    {
        var result = await Handler(new InMemoryDocumentRepository { SimulateConcurrentInsert = true }).HandleAsync(
            RealFile(TestData.DeltaHaul, "rate-confirmation-77104.txt"), TestContext.Current.CancellationToken);

        Assert.IsType<IngestResult.Skipped>(result);
    }

    [Fact]
    public async Task Rejects_an_unmapped_tenant()
    {
        var result = await Handler().HandleAsync(
            new("tenant-unknown", "invoice-X-1.txt", "INVOICE\nTotal: $1"u8.ToArray()), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<IngestResult.Rejected>(result);
        Assert.Contains("tenant-unknown", rejected.Reason);
        Assert.Empty(_repository.Stored);
    }

    [Fact]
    public async Task Rejects_an_unknown_document_type()
    {
        var result = await Handler().HandleAsync(
            new(TestData.NileFreight, "packing-list-1.txt", "PACKING LIST\n3 pallets"u8.ToArray()), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<IngestResult.Rejected>(result);
        Assert.Contains("packing-list-1.txt", rejected.Reason);
        Assert.Empty(_embeddings.Calls);
    }

    [Fact]
    public async Task Rejects_a_document_with_no_content()
    {
        var result = await Handler().HandleAsync(
            new(TestData.NileFreight, "invoice-EMPTY-1.txt", "\n \n"u8.ToArray()), TestContext.Current.CancellationToken);

        Assert.IsType<IngestResult.Rejected>(result);
        Assert.Empty(_embeddings.Calls);
    }

    [Fact]
    public async Task Rejects_bytes_that_are_not_valid_utf8()
    {
        byte[] invalid = [.. "INVOICE\nTotal: "u8, 0xC3, 0x28]; // 0xC3 0x28 is an invalid UTF-8 sequence

        var result = await Handler().HandleAsync(
            new(TestData.NileFreight, "invoice-BAD-1.txt", invalid), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<IngestResult.Rejected>(result);
        Assert.Contains("UTF-8", rejected.Reason);
    }

    [Fact]
    public async Task A_utf8_byte_order_mark_is_not_part_of_the_content()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("RATE\nLine haul: $1")];

        await Handler().HandleAsync(new(TestData.NileFreight, "invoice-BOM-1.txt", withBom), TestContext.Current.CancellationToken);

        Assert.Equal("Document: invoice | Section: RATE\nLine haul: $1", Assert.Single(_repository.Stored).Chunks[0].Content);
    }

    [Fact]
    public async Task Rejects_text_containing_NUL_characters_before_embedding()
    {
        // Postgres TEXT can't store U+0000; catching it here avoids a failed INSERT after paying for embeddings.
        byte[] withNul = [.. "INVOICE\nInvoice number: X1"u8, 0x00, .. "\nTotal: $1"u8];

        var result = await Handler().HandleAsync(
            new(TestData.NileFreight, "invoice-NUL-1.txt", withNul), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<IngestResult.Rejected>(result);
        Assert.Contains("NUL", rejected.Reason);
        Assert.Empty(_embeddings.Calls);
    }
}
