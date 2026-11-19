using FreightRag.Core.Application;
using FreightRag.Core.Application.Ports;
using FreightRag.Core.Domain;
using FreightRag.Ingest;
using FreightRag.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreightRag.Tests.Ingest;

public sealed class FolderIngestorTests : IDisposable
{
    private static readonly TenantId Nile = new(Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("freightrag-tests-").FullName;
    private readonly InMemoryDocumentRepository _repository = new();
    private readonly DictionaryTenantDirectory _tenants = new(new() { [TestData.NileFreight] = Nile });

    public void Dispose() => Directory.Delete(_dataDir, recursive: true);

    private FolderIngestor Ingestor(IEmbeddingService? embeddings = null) => new(
        new IngestDocumentHandler(_tenants, _repository, embeddings ?? new FakeEmbeddingService(), new SectionChunker()),
        _tenants,
        NullLogger<FolderIngestor>.Instance);

    private void CopyRealFile(string tenant, string file)
    {
        Directory.CreateDirectory(Path.Combine(_dataDir, tenant));
        File.Copy(TestData.PathOf(tenant, file), Path.Combine(_dataDir, tenant, file));
    }

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_dataDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task Ingests_mapped_tenant_folders_and_reports_a_summary()
    {
        CopyRealFile(TestData.NileFreight, "rate-confirmation-48213.txt");
        CopyRealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt");

        var summary = await Ingestor().RunAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.FilesProcessed);
        Assert.Equal(2, summary.Ingested);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(_repository.Stored.Sum(d => d.Chunks.Count), summary.ChunksCreated);
        Assert.True(summary.ChunksCreated > 0);
        Assert.False(summary.HasFailures);
    }

    [Fact]
    public async Task A_second_run_skips_everything_and_creates_no_chunks()
    {
        CopyRealFile(TestData.NileFreight, "rate-confirmation-48213.txt");
        await Ingestor().RunAsync(_dataDir, TestContext.Current.CancellationToken);

        var summary = await Ingestor().RunAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.FilesProcessed);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.ChunksCreated);
    }

    [Fact]
    public async Task Rejects_folders_without_a_tenant_mapping_and_does_not_read_their_files()
    {
        CopyRealFile(TestData.NileFreight, "rate-confirmation-48213.txt");
        WriteFile("tenant-unknown/invoice-X-1.txt", "INVOICE\nTotal: $1");

        var summary = await Ingestor().RunAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(["tenant-unknown"], summary.RejectedFolders);
        Assert.Equal(1, summary.FilesProcessed);
        Assert.All(_repository.Stored, d => Assert.Equal(Nile, d.TenantId));
    }

    [Fact]
    public async Task Only_txt_files_directly_inside_tenant_folders_are_read()
    {
        CopyRealFile(TestData.NileFreight, "rate-confirmation-48213.txt");
        WriteFile("tenant-nile-freight/notes.md", "NOTES\nnot a txt file");
        WriteFile("tenant-nile-freight/archive/invoice-OLD-1.txt", "INVOICE\nin a sub-folder");
        WriteFile("invoice-ROOT-1.txt", "INVOICE\nnot in a tenant folder");

        var summary = await Ingestor().RunAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.FilesProcessed);
        Assert.Equal("rate-confirmation-48213", Assert.Single(_repository.Stored).Title);
    }

    [Fact]
    public async Task Counts_rejected_files_and_keeps_going()
    {
        WriteFile("tenant-nile-freight/packing-list-1.txt", "PACKING LIST\n3 pallets");
        CopyRealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt");

        var summary = await Ingestor().RunAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Rejected);
        Assert.Equal(1, summary.Ingested);
        Assert.False(summary.HasFailures); // bad input is a rejection, not a failure
    }

    [Fact]
    public async Task An_unexpected_error_on_one_file_is_counted_as_failed_and_the_run_continues()
    {
        CopyRealFile(TestData.NileFreight, "bill-of-lading-SAV-778431.txt");
        CopyRealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt");

        var summary = await Ingestor(new FailOnceEmbeddingService()).RunAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.FilesProcessed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Ingested);
        Assert.True(summary.HasFailures);
    }

    [Fact]
    public async Task Rejects_files_larger_than_the_size_limit_without_reading_them()
    {
        WriteFile("tenant-nile-freight/invoice-BIG-1.txt", "INVOICE\n" + new string('x', FolderIngestor.MaxFileBytes));

        var summary = await Ingestor().RunAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Rejected);
        Assert.Empty(_repository.Stored);
    }

    [Fact]
    public async Task An_http_timeout_is_a_failed_file_not_a_cancelled_run()
    {
        // HttpClient reports a timeout as TaskCanceledException, which must not be mistaken for Ctrl+C.
        CopyRealFile(TestData.NileFreight, "bill-of-lading-SAV-778431.txt");
        CopyRealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt");

        var summary = await Ingestor(new FailOnceEmbeddingService(new TaskCanceledException("HttpClient.Timeout of 100 seconds elapsing")))
            .RunAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Ingested);
    }

    [Fact]
    public async Task A_real_cancellation_stops_the_run()
    {
        CopyRealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ingestor().RunAsync(_dataDir, cts.Token));
    }

    [Fact]
    public async Task Symbolic_links_to_files_are_rejected()
    {
        // A link could smuggle another tenant's document (or a mounted secret) in under this tenant's id.
        CopyRealFile(TestData.DeltaHaul, "invoice-DH-26-0412.txt");
        Directory.CreateDirectory(Path.Combine(_dataDir, TestData.NileFreight));
        File.CreateSymbolicLink(
            Path.Combine(_dataDir, TestData.NileFreight, "invoice-COPY-1.txt"),
            Path.Combine(_dataDir, TestData.DeltaHaul, "invoice-DH-26-0412.txt"));

        var summary = await Ingestor().RunAsync(_dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Rejected);
        Assert.Empty(_repository.Stored);
    }

    [Fact]
    public async Task Symbolic_links_to_folders_are_rejected()
    {
        var elsewhere = Directory.CreateTempSubdirectory("freightrag-elsewhere-").FullName;
        try
        {
            File.Copy(TestData.PathOf(TestData.NileFreight, "invoice-NFL-2026-1187.txt"), Path.Combine(elsewhere, "invoice-NFL-2026-1187.txt"));
            Directory.CreateSymbolicLink(Path.Combine(_dataDir, TestData.NileFreight), elsewhere);

            var summary = await Ingestor().RunAsync(_dataDir, TestContext.Current.CancellationToken);

            Assert.Equal([TestData.NileFreight], summary.RejectedFolders);
            Assert.Empty(_repository.Stored);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_data_directory_is_an_error()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => Ingestor().RunAsync(Path.Combine(_dataDir, "does-not-exist"), TestContext.Current.CancellationToken));
    }

    /// <summary>Throws on the first call (simulating Ollama being briefly unavailable), then works.</summary>
    private sealed class FailOnceEmbeddingService(Exception? error = null) : IEmbeddingService
    {
        private readonly FakeEmbeddingService _inner = new();
        private bool _failed;

        public Task<IReadOnlyList<EmbeddingVector>> EmbedDocumentChunksAsync(
            IReadOnlyList<string> chunkContents, CancellationToken cancellationToken)
        {
            if (_failed)
                return _inner.EmbedDocumentChunksAsync(chunkContents, cancellationToken);
            _failed = true;
            throw error ?? new HttpRequestException("Ollama unavailable");
        }
    }
}
