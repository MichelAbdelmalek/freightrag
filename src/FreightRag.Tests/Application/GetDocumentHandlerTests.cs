using FreightRag.Core.Application;
using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Application;

public class GetDocumentHandlerTests
{
    private static readonly TenantId Nile = new(Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097"));
    private static readonly TenantId Delta = new(Guid.Parse("14a2de5d-56c5-4eda-a737-d957f4580b90"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryDocumentRepository _repository = new();

    private async Task<(Guid Id, ExtractedFields Posted)> Ingest(string tenant, string file)
    {
        var handler = new IngestDocumentHandler(
            new DictionaryTenantDirectory(new() { [TestData.NileFreight] = Nile, [TestData.DeltaHaul] = Delta }),
            _repository, new FakeEmbeddingService(), new SectionChunker(), new FakePdfTextExtractor());
        var ingested = Assert.IsType<IngestResult.Ingested>(
            await handler.HandleAsync(new(tenant, file, TestData.ReadBytes(tenant, file)), Ct));
        return (ingested.DocumentId, ingested.Fields!);
    }

    private GetDocumentHandler Handler() => new(new InMemoryDocumentQueries(_repository));

    [Fact]
    public async Task Fields_read_later_equal_the_fields_returned_when_the_document_was_posted()
    {
        var (id, posted) = await Ingest(TestData.DeltaHaul, "bill-of-lading-DAL-090233.txt");

        var found = Assert.IsType<GetDocumentResult.Found>(await Handler().HandleAsync(Delta, id, null, Ct));

        Assert.Equal(posted.Items, found.Fields.Items);
        Assert.Equal("bill-of-lading-DAL-090233", found.Details.Title);
    }

    [Fact]
    public async Task A_selection_returns_only_those_fields()
    {
        var (id, _) = await Ingest(TestData.DeltaHaul, "bill-of-lading-DAL-090233.txt");

        var found = Assert.IsType<GetDocumentResult.Found>(await Handler().HandleAsync(Delta, id, "sealIntact, RECEIVEDBY", Ct));

        Assert.Equal([("receivedBy", "Teresa Glenn, Night Receiver"), ("sealIntact", "Yes")],
            found.Fields.Items.Select(i => (i.Key, i.Value)));
    }

    [Fact]
    public async Task Another_tenants_document_and_a_missing_one_are_both_not_found()
    {
        var (id, _) = await Ingest(TestData.NileFreight, "invoice-NFL-2026-1187.txt");

        Assert.IsType<GetDocumentResult.NotFound>(await Handler().HandleAsync(Delta, id, null, Ct));
        Assert.IsType<GetDocumentResult.NotFound>(await Handler().HandleAsync(Nile, Guid.NewGuid(), null, Ct));
    }

    [Fact]
    public async Task A_bad_field_name_is_reported_for_your_own_document()
    {
        var (id, _) = await Ingest(TestData.NileFreight, "invoice-NFL-2026-1187.txt");

        var invalid = Assert.IsType<GetDocumentResult.InvalidFields>(await Handler().HandleAsync(Nile, id, "nope", Ct));

        Assert.Equal("unknown_field", invalid.Error.Code);
    }

    [Fact]
    public async Task A_foreign_id_is_not_found_before_the_fields_are_even_looked_at()
    {
        var (id, _) = await Ingest(TestData.NileFreight, "invoice-NFL-2026-1187.txt");

        // If this said "invalid fields", a caller could learn that the id exists in another tenant.
        Assert.IsType<GetDocumentResult.NotFound>(await Handler().HandleAsync(Delta, id, "nope", Ct));
    }

    [Fact]
    public async Task The_driver_handbook_has_no_fields()
    {
        var (id, _) = await Ingest(TestData.NileFreight, "driver-handbook-excerpt.txt");

        var found = Assert.IsType<GetDocumentResult.Found>(await Handler().HandleAsync(Nile, id, null, Ct));

        Assert.Equal(0, found.Fields.Count);
    }
}
