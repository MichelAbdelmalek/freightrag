using System.Diagnostics.Metrics;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>
/// Metrics for the search (System.Diagnostics.Metrics: readable with `dotnet-counters monitor --counters FreightRag.Search`
/// today, exportable with OpenTelemetry later). Never carries query text.
/// </summary>
public static class SearchTelemetry
{
    public const string MeterName = "FreightRag.Search";
    public const string ExactFallbacksName = "freightrag.search.exact_fallbacks";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Searches answered by an exact scan because the HNSW iterative scan stopped before finding topK results.
    /// A rising count means hnsw.max_scan_tuples or the data layout (e.g. per-tenant partitioning) needs attention —
    /// or that someone is deliberately triggering expensive searches.
    /// </summary>
    public static readonly Counter<long> ExactFallbacks = Meter.CreateCounter<long>(
        ExactFallbacksName, unit: "{search}",
        description: "Searches answered by an exact scan because the approximate HNSW scan returned fewer than topK results.");
}
