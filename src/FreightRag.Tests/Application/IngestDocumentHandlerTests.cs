using System.Text;
using FreightRag.Core.Application;
using FreightRag.Core.Application.Ports;
using FreightRag.Core.Domain;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Application;

public class IngestDocumentHandlerTests
{
    private static readonly TenantId Nile = new(Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097"));
    private static readonly TenantId Delta = new(Guid.Parse("14a2de5d-56c5-4eda-a737-d957f4580b90"));

    private readonly InMemoryDocumentRepository _repository = new();
    private readonly FakeEmbeddingService _embeddings = new();
    private readonly FakePdfTextExtractor _pdf = new();

    private IngestDocumentHandler Handler(InMemoryDocumentRepository? repository = null) => new(
        new DictionaryTenantDirectory(new() { [TestData.NileFreight] = Nile, [TestData.DeltaHaul] = Delta }),
        repository ?? _repository,
        _embeddings,
        new SectionChunker(),
        _pdf);

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

    // ---------- PDF ----------

    private static readonly byte[] FakePdfBytes = "%PDF-1.7\n%fake scan"u8.ToArray();

    private static IngestDocumentCommand Pdf(string fileName = "rate-confirmation-48213.pdf", byte[]? bytes = null) =>
        new(TestData.NileFreight, fileName, bytes ?? FakePdfBytes);

    [Fact]
    public async Task Pdf_text_from_the_extractor_is_chunked_like_txt()
    {
        var txt = TestData.ReadBytes(TestData.NileFreight, "rate-confirmation-48213.txt");
        _pdf.Result = new PdfExtraction.Extracted(System.Text.Encoding.UTF8.GetString(txt), FakePdfTextExtractor.SampleReport());
        var viaTxt = new InMemoryDocumentRepository();
        await Handler(viaTxt).HandleAsync(RealFile(TestData.NileFreight, "rate-confirmation-48213.txt"), TestContext.Current.CancellationToken);

        var result = await Handler().HandleAsync(Pdf(), TestContext.Current.CancellationToken);

        var ingested = Assert.IsType<IngestResult.Ingested>(result);
        Assert.Equal(SourceFormat.Pdf, ingested.Format);
        Assert.NotNull(ingested.Extraction);
        Assert.Equal(viaTxt.Stored.Single().Chunks.Select(c => c.Content), _repository.Stored.Single().Chunks.Select(c => c.Content));
    }

    [Fact]
    public async Task The_extractor_receives_exactly_the_uploaded_bytes_and_nothing_else()
    {
        await Handler().HandleAsync(Pdf(), TestContext.Current.CancellationToken);

        Assert.Equal([FakePdfBytes], _pdf.Calls); // IPdfTextExtractor has no tenant or file-name parameter at all
    }

    [Theory]
    [InlineData("INVOICE\nTotal: $1")]
    [InlineData(" %PDF-1.7")]   // not at offset 0
    [InlineData("%PDF")]        // no dash
    [InlineData("")]
    public async Task A_file_named_pdf_without_the_pdf_signature_is_rejected_without_calling_the_extractor(string content)
    {
        var result = await Handler().HandleAsync(Pdf(bytes: System.Text.Encoding.UTF8.GetBytes(content)), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<IngestResult.Rejected>(result);
        Assert.Equal("not_pdf", rejected.Code);
        Assert.Empty(_pdf.Calls);
        Assert.Empty(_repository.Stored);
    }

    [Fact]
    public async Task An_already_ingested_pdf_is_skipped_without_calling_the_extractor_or_the_embedder()
    {
        var handler = Handler();
        await handler.HandleAsync(Pdf(), TestContext.Current.CancellationToken);
        _pdf.Calls.Clear();
        _embeddings.Calls.Clear();

        var result = await handler.HandleAsync(Pdf(), TestContext.Current.CancellationToken);

        Assert.IsType<IngestResult.Skipped>(result);
        Assert.Empty(_pdf.Calls);
        Assert.Empty(_embeddings.Calls);
    }

    [Fact]
    public async Task A_txt_never_calls_the_extractor()
    {
        await Handler().HandleAsync(RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt"), TestContext.Current.CancellationToken);

        Assert.Empty(_pdf.Calls);
    }

    [Theory]
    [InlineData("encrypted_pdf")]
    [InlineData("too_many_pages")]
    [InlineData("page_too_large")]
    [InlineData("no_text")]
    [InlineData("unreadable")]
    [InlineData("extraction_limit_exceeded")]
    [InlineData("corrupt_pdf")]
    public async Task Extractor_refusals_become_rejections_with_the_same_code(string code)
    {
        _pdf.Result = new PdfExtraction.Refused(code);

        var result = await Handler().HandleAsync(Pdf(), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<IngestResult.Rejected>(result);
        Assert.Equal(code, rejected.Code);
        Assert.Contains("rate-confirmation-48213.pdf", rejected.Reason);
        Assert.Empty(_repository.Stored);
        Assert.Empty(_embeddings.Calls);
    }

    [Fact]
    public async Task Extracted_text_with_NUL_is_rejected_and_no_message_repeats_the_text()
    {
        _pdf.Result = new PdfExtraction.Extracted("RATE\0ZEBRA-OCR-CONTENT", FakePdfTextExtractor.SampleReport());

        var result = await Handler().HandleAsync(Pdf(), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<IngestResult.Rejected>(result);
        Assert.Equal("invalid_text", rejected.Code);
        Assert.DoesNotContain("ZEBRA-OCR-CONTENT", rejected.Reason);
    }

    [Fact]
    public async Task Extractor_exceptions_propagate_so_the_caller_can_answer_503()
    {
        _pdf.Throws = new PdfExtractorBusyException(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<PdfExtractorBusyException>(() => Handler().HandleAsync(Pdf(), TestContext.Current.CancellationToken));
        Assert.Empty(_repository.Stored);
    }

    // ---------- extracted fields ----------

    private static FieldSelection Select(string? raw, DocType type)
    {
        Assert.True(FieldSelection.TryParse(raw, type, out var selection, out _));
        return selection!;
    }

    [Fact]
    public async Task A_real_rate_confirmation_returns_all_its_fields()
    {
        var result = await Handler().HandleAsync(RealFile(TestData.NileFreight, "rate-confirmation-48213.txt"), TestContext.Current.CancellationToken);

        var fields = Assert.IsType<IngestResult.Ingested>(result).Fields!;
        Assert.Equal(23, fields.Count);
        Assert.Equal(("48213", "Nile Freight Lines Inc.", "$2,630.00"), (fields["loadNumber"], fields["carrierName"], fields["totalAgreedRate"]));
    }

    [Fact]
    public async Task A_selection_returns_only_the_requested_fields()
    {
        var command = RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt") with { Fields = Select("totalDue,invoiceNumber", DocType.Invoice) };

        var result = await Handler().HandleAsync(command, TestContext.Current.CancellationToken);

        var fields = Assert.IsType<IngestResult.Ingested>(result).Fields!;
        Assert.Equal(["invoiceNumber", "totalDue"], fields.Items.Select(i => i.Key));
        Assert.Equal("$2,705.00", fields["totalDue"]);
    }

    [Fact]
    public async Task A_pdf_gets_its_fields_from_the_extracted_text()
    {
        _pdf.Result = new PdfExtraction.Extracted("RATE CONFIRMATION\n\nLoad number: 52010\n\nRATE\nDetention: $60.00 per hour after 2 free hours",
            FakePdfTextExtractor.SampleReport());

        var result = await Handler().HandleAsync(Pdf(), TestContext.Current.CancellationToken);

        var fields = Assert.IsType<IngestResult.Ingested>(result).Fields!;
        Assert.Equal(("52010", "$60.00 per hour after 2 free hours"), (fields["loadNumber"], fields["detention"]));
    }

    [Fact]
    public async Task A_selection_for_another_document_type_is_a_programming_error()
    {
        var command = RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt") with { Fields = Select("loadNumber", DocType.RateConfirmation) };

        await Assert.ThrowsAsync<ArgumentException>(() => Handler().HandleAsync(command, TestContext.Current.CancellationToken));
        Assert.Empty(_repository.Stored);
    }
}
