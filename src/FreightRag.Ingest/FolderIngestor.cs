using FreightRag.Core.Application;
using FreightRag.Core.Application.Ports;
using Microsoft.Extensions.Logging;

namespace FreightRag.Ingest;

/// <summary>Totals for one run, logged at the end.</summary>
internal sealed record IngestSummary(
    int FilesProcessed, int Ingested, int Skipped, int Rejected, int Failed, int ChunksCreated,
    IReadOnlyList<string> RejectedFolders)
{
    public bool HasFailures => Failed > 0;
}

/// <summary>
/// Walks the data directory: one sub-folder per tenant (folder name = tenant slug), and the .txt
/// files directly inside it. Each file is handed to the <see cref="IngestDocumentHandler"/> use case.
/// One bad file never stops the run; it is counted and logged.
/// </summary>
internal sealed class FolderIngestor(IngestDocumentHandler handler, ITenantDirectory tenants, ILogger<FolderIngestor> logger)
{
    /// <summary>Guard against accidentally ingesting a huge file (these are short text documents).</summary>
    public const int MaxFileBytes = 10 * 1024 * 1024;

    public async Task<IngestSummary> RunAsync(string dataDir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dataDir))
            throw new DirectoryNotFoundException($"Data directory '{dataDir}' does not exist.");

        int processed = 0, ingested = 0, skipped = 0, rejected = 0, failed = 0, chunks = 0;
        var rejectedFolders = new List<string>();

        // Sorted for a deterministic, readable log. Files directly in dataDir are ignored: they have no tenant.
        foreach (var folder in Directory.EnumerateDirectories(dataDir).Order(StringComparer.Ordinal))
        {
            var tenantSlug = Path.GetFileName(folder);
            if (new DirectoryInfo(folder).LinkTarget is not null)
            {
                // A linked folder could point anywhere, e.g. at another tenant's documents.
                logger.LogWarning("Rejected folder {Folder}: symbolic links are not followed", tenantSlug);
                rejectedFolders.Add(tenantSlug);
                continue;
            }
            if (!tenants.TryResolve(tenantSlug, out _))
            {
                logger.LogWarning("Rejected folder {Folder}: no tenant mapping in appsettings.json (Tenants section)", tenantSlug);
                rejectedFolders.Add(tenantSlug);
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*.txt", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                var fileName = Path.GetFileName(path);
                var label = $"{tenantSlug}/{fileName}";

                try
                {
                    if (new FileInfo(path).LinkTarget is not null)
                    {
                        // A link could pull in another tenant's file (or a mounted secret) under this tenant's id.
                        rejected++;
                        logger.LogWarning("Rejected {File}: symbolic links are not followed", label);
                        continue;
                    }

                    var bytes = await ReadAtMostAsync(path, MaxFileBytes, cancellationToken);
                    if (bytes is null)
                    {
                        rejected++;
                        logger.LogWarning("Rejected {File}: larger than {MaxBytes} bytes", label, MaxFileBytes);
                        continue;
                    }

                    var result = await handler.HandleAsync(new IngestDocumentCommand(tenantSlug, fileName, bytes), cancellationToken);

                    switch (result)
                    {
                        case IngestResult.Ingested r:
                            ingested++;
                            chunks += r.ChunkCount;
                            logger.LogInformation("Ingested {File}: {Chunks} chunks", label, r.ChunkCount);
                            break;
                        case IngestResult.Skipped r:
                            skipped++;
                            logger.LogInformation("Skipped {File}: {Reason}", label, r.Reason);
                            break;
                        case IngestResult.Rejected r:
                            rejected++;
                            logger.LogWarning("Rejected {File}: {Reason}", label, r.Reason);
                            break;
                    }
                }
                // Only a cancellation WE asked for (Ctrl+C) stops the run. HttpClient reports a timeout as
                // TaskCanceledException too, and that is just a failed file.
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Nothing was stored for this file (the insert is one transaction). Carry on with the next.
                    failed++;
                    logger.LogError(ex, "Failed {File}: {Message}", label, ex.Message);
                }
            }
        }

        return new IngestSummary(processed, ingested, skipped, rejected, failed, chunks, rejectedFolders);
    }

    /// <summary>
    /// Reads the file, but never more than <paramref name="maxBytes"/>: returns null if it is larger.
    /// Reading with a hard limit (instead of trusting FileInfo.Length) also covers files that grow
    /// while being read and special files that report a length of 0.
    /// </summary>
    private static async Task<byte[]?> ReadAtMostAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        var buffer = new byte[Math.Min(maxBytes + 1, Math.Max(stream.CanSeek ? stream.Length + 1 : 81920, 1))];
        using var content = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            content.Write(buffer, 0, read);
            if (content.Length > maxBytes)
                return null;
        }
        return content.ToArray();
    }
}
