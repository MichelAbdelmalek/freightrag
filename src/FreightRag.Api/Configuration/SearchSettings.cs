using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Persistence;

namespace FreightRag.Api.Configuration;

/// <summary>Search tuning from the "Search" section, validated at startup.</summary>
/// <param name="Candidates">Candidates per leg before fusion (Search:CandidateCount, default 20).</param>
/// <param name="Fusion">Reciprocal Rank Fusion with Search:RrfK (default 60).</param>
/// <param name="ChunkSearch">Vector-leg tuning (Search:MaxScanTuples, default 20,000).</param>
public sealed record SearchSettings(CandidateCount Candidates, ReciprocalRankFusion Fusion, ChunkSearchOptions ChunkSearch)
{
    public static SearchSettings Load(IConfiguration configuration) => new(
        Read("Search:CandidateCount", configuration, v => CandidateCount.Create(v)),
        Read("Search:RrfK", configuration, v => v is null ? new ReciprocalRankFusion() : new ReciprocalRankFusion(v.Value)),
        Read("Search:MaxScanTuples", configuration, v => new ChunkSearchOptions(v ?? 20_000)));

    private static T Read<T>(string key, IConfiguration configuration, Func<int?, T> create)
    {
        var raw = configuration[key];
        int? value = null;
        if (!string.IsNullOrWhiteSpace(raw))
            value = int.TryParse(raw, out var parsed) ? parsed : throw new InvalidOperationException($"{key} must be a whole number.");
        try
        {
            return create(value);
        }
        catch (Exception ex) when (ex is DomainException or ArgumentException)
        {
            throw new InvalidOperationException($"{key}: {ex.Message}", ex);
        }
    }
}
