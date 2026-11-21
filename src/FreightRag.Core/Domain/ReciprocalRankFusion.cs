namespace FreightRag.Core.Domain;

/// <summary>One ranked list to fuse, e.g. the vector leg's or the keyword leg's candidates, best first.</summary>
/// <param name="Name">The list's name ("vector", "keyword"); used to report per-list ranks.</param>
/// <param name="ChunkIds">Chunk ids in rank order (index 0 = rank 1).</param>
public sealed record RankedList(string Name, IReadOnlyList<Guid> ChunkIds);

/// <summary>A fused result: the chunk, its RRF score and its rank in each list it appeared in.</summary>
public sealed record FusedResult(Guid ChunkId, double Score, IReadOnlyDictionary<string, int> Ranks)
{
    /// <summary>1-based rank in <paramref name="list"/>, or null if the chunk was not in that list.</summary>
    public int? RankIn(string list) => Ranks.TryGetValue(list, out var rank) ? rank : null;
}

/// <summary>
/// Domain service: Reciprocal Rank Fusion (Cormack, Clarke &amp; Büttcher, 2009).
///
///     score(chunk) = Σ over lists  1 / (k + rank of chunk in that list)        rank starts at 1
///
/// Why RRF for hybrid search: the vector leg ranks by cosine distance and the keyword leg by ts_rank_cd — two scores
/// on unrelated scales that can't be added. RRF only uses ranks, so it needs no normalization, and it rewards chunks
/// that rank well in BOTH lists. k (60 in the original paper) damps the gap between neighbouring ranks: with k = 60,
/// rank 1 is worth 1/61 and rank 2 1/62 — close — so agreement between lists matters more than one list's top spot.
/// </summary>
public sealed class ReciprocalRankFusion
{
    public const int DefaultK = 60;

    public ReciprocalRankFusion(int k = DefaultK)
    {
        if (k < 1)
            throw new DomainException("The RRF constant k must be at least 1.");
        K = k;
    }

    public int K { get; }

    /// <summary>
    /// Fuses the lists. Order: score descending, ties broken by chunk id ascending (deterministic). A duplicate id
    /// within one list counts once, at its first position; ranks stay positional.
    /// </summary>
    public IReadOnlyList<FusedResult> Fuse(IReadOnlyList<RankedList> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        if (lists.Select(l => l.Name).Distinct(StringComparer.Ordinal).Count() != lists.Count)
            throw new DomainException("Ranked lists must have unique names.");

        var scores = new Dictionary<Guid, double>();
        var ranks = new Dictionary<Guid, Dictionary<string, int>>();
        foreach (var list in lists)
        {
            for (var i = 0; i < list.ChunkIds.Count; i++)
            {
                var id = list.ChunkIds[i];
                var perList = ranks.TryGetValue(id, out var existing) ? existing : ranks[id] = new Dictionary<string, int>();
                if (perList.ContainsKey(list.Name))
                    continue; // duplicate within this list: first occurrence counts

                var rank = i + 1;
                perList[list.Name] = rank;
                scores[id] = scores.GetValueOrDefault(id) + 1.0 / (K + rank);
            }
        }

        return scores
            .OrderByDescending(s => s.Value)
            .ThenBy(s => s.Key)
            .Select(s => new FusedResult(s.Key, s.Value, ranks[s.Key]))
            .ToList();
    }
}

/// <summary>Which retrieval legs a search runs.</summary>
public sealed record SearchMode
{
    public static readonly SearchMode Vector = new("vector", usesVector: true, usesKeyword: false);
    public static readonly SearchMode Keyword = new("keyword", usesVector: false, usesKeyword: true);
    public static readonly SearchMode Hybrid = new("hybrid", usesVector: true, usesKeyword: true);

    private static readonly SearchMode[] All = [Vector, Keyword, Hybrid];

    private SearchMode(string name, bool usesVector, bool usesKeyword)
    {
        Name = name;
        UsesVector = usesVector;
        UsesKeyword = usesKeyword;
    }

    public string Name { get; }
    public bool UsesVector { get; }
    public bool UsesKeyword { get; }

    /// <summary>"vector", "keyword" or "hybrid" (case-insensitive); null → hybrid.</summary>
    public static SearchMode Parse(string? value)
    {
        if (value is null)
            return Hybrid;
        return All.FirstOrDefault(m => string.Equals(m.Name, value, StringComparison.OrdinalIgnoreCase))
               ?? throw new DomainException($"mode must be one of: {string.Join(", ", All.Select(m => m.Name))}.");
    }

    public override string ToString() => Name;
}

/// <summary>
/// How many candidates each leg retrieves before fusion (default 20). At least the largest topK, so a full page is
/// always possible; at most 200, which bounds the work per search.
/// </summary>
public sealed record CandidateCount
{
    public const int Default = 20;
    public const int Max = 200;

    private CandidateCount(int value) => Value = value;

    public int Value { get; }

    public static CandidateCount Create(int? value)
    {
        var n = value ?? Default;
        if (n < TopK.Max || n > Max)
            throw new DomainException($"The candidate count must be between {TopK.Max} and {Max}.");
        return new CandidateCount(n);
    }
}
