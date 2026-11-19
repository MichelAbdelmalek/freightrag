// FreightRag.Ingest — composition root.
// Reads settings, wires the layers together (Infrastructure → Application → Domain) and runs the ingest.
//
//   docker compose run --rm ingest          (inside Docker: --data-dir /data)
//   dotnet run --project src/FreightRag.Ingest -- --data-dir data
//
// Exit codes: 0 = success, 1 = some files failed (or unexpected error), 2 = invalid arguments/configuration, 130 = cancelled.

using FreightRag.Core.Application;
using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Configuration;
using FreightRag.Core.Infrastructure.Embedding;
using FreightRag.Core.Infrastructure.Persistence;
using FreightRag.Core.Infrastructure.Tenants;
using FreightRag.Ingest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(logging => logging
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    }));
var logger = loggerFactory.CreateLogger("FreightRag.Ingest");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the current file finish its transaction cleanly
    cancellation.Cancel();
};

try
{
    var dataDir = Path.GetFullPath(CommandLine.ParseDataDir(args));

    // Two separate configuration sources on purpose: the password may ONLY come from User Secrets.
    // (No environment variables or command-line configuration, which would be other ways in.)
    var appSettings = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();
    var userSecrets = new ConfigurationBuilder()
        .AddUserSecrets(typeof(Program).Assembly, optional: true)
        .Build();
    var settings = FreightRagSettings.Load(appSettings, userSecrets, applicationName: "FreightRag.Ingest");

    logger.LogInformation("Data directory: {DataDir}", dataDir);
    logger.LogInformation("Database: {Database}", settings.Database.Describe()); // never the connection string
    logger.LogInformation("Embeddings: {Model} at {Url}", settings.Ollama.EmbeddingModel, settings.Ollama.Url);

    // Infrastructure adapters
    await using var dataSource = FreightRagDbContextOptions.CreateDataSource(settings.Database.ConnectionString);
    var contexts = new FreightRagDbContextFactory(FreightRagDbContextOptions.Create(dataSource));
    using var embeddingGenerator = OllamaClientFactory.CreateEmbeddingGenerator(settings.Ollama.Url, settings.Ollama.EmbeddingModel);
    var tenants = new ConfiguredTenantDirectory(settings.Tenants);

    // Application use case, given its ports
    var handler = new IngestDocumentHandler(
        tenants,
        new EfDocumentRepository(contexts),
        new OllamaEmbeddingService(embeddingGenerator, new EmbeddingModelName(settings.Ollama.EmbeddingModel)),
        new SectionChunker());

    var summary = await new FolderIngestor(handler, tenants, loggerFactory.CreateLogger<FolderIngestor>())
        .RunAsync(dataDir, cancellation.Token);

    logger.LogInformation(
        "Summary: {Processed} files processed ({Ingested} ingested, {Skipped} skipped, {Rejected} rejected, {Failed} failed), "
        + "{Chunks} chunks created, {RejectedFolders} folder(s) rejected",
        summary.FilesProcessed, summary.Ingested, summary.Skipped, summary.Rejected, summary.Failed,
        summary.ChunksCreated, summary.RejectedFolders.Count);

    return summary.HasFailures ? 1 : 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    logger.LogWarning("Cancelled");
    return 130;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DirectoryNotFoundException
                               or FileNotFoundException or InvalidDataException or DomainException)
{
    // Argument/configuration problems (missing or malformed appsettings.json, bad tenant GUIDs,
    // missing password, ...). The message is written to be actionable; no stack trace needed.
    logger.LogError("{Message}", ex.Message);
    return 2;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Unexpected error: {Message}", ex.Message);
    return 1;
}
