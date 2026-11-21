using FreightRag.Core.Domain;

namespace FreightRag.Tests.Domain;

public class ReciprocalRankFusionTests
{
    // Fixed ids so the expected order (including tie-breaks) is hand-checkable.
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");

    private static RankedList List(string name, params Guid[] ids) => new(name, ids);

    [Fact]
    public void Scores_are_the_sum_of_one_over_k_plus_rank_with_k_60()
    {
        var fused = new ReciprocalRankFusion().Fuse([List("vector", A, B), List("keyword", C, D, A)]);

        var a = fused.Single(f => f.ChunkId == A);
        Assert.Equal(1.0 / 61 + 1.0 / 63, a.Score, precision: 12); // 0.032266: rank 1 in vector + rank 3 in keyword
        Assert.Equal(0.032266, a.Score, precision: 6);
        Assert.Equal(1.0 / 62, fused.Single(f => f.ChunkId == B).Score, precision: 12); // 0.016129: vector rank 2 only
        Assert.Equal(1.0 / 61, fused.Single(f => f.ChunkId == C).Score, precision: 12); // 0.016393: keyword rank 1 only
        Assert.Equal(1.0 / 62, fused.Single(f => f.ChunkId == D).Score, precision: 12); // keyword rank 2 only
    }

    [Fact]
    public void Order_is_by_score_and_a_chunk_both_lists_like_beats_either_lists_single_best()
    {
        var fused = new ReciprocalRankFusion().Fuse([List("vector", A, B), List("keyword", C, D, A)]);

        // A 0.032266 > C 0.016393 > B 0.016129 = D 0.016129 (tie → lower chunk id first: B before D)
        Assert.Equal([A, C, B, D], fused.Select(f => f.ChunkId));
    }

    [Fact]
    public void Each_result_reports_its_rank_per_list_and_null_where_absent()
    {
        var fused = new ReciprocalRankFusion().Fuse([List("vector", A, B), List("keyword", C, D, A)]);

        Assert.Equal(1, fused.Single(f => f.ChunkId == A).RankIn("vector"));
        Assert.Equal(3, fused.Single(f => f.ChunkId == A).RankIn("keyword"));
        Assert.Null(fused.Single(f => f.ChunkId == B).RankIn("keyword"));
        Assert.Null(fused.Single(f => f.ChunkId == C).RankIn("vector"));
    }

    [Fact]
    public void Equal_scores_are_ordered_by_chunk_id_deterministically()
    {
        // D is rank 1 in one list, A rank 1 in the other: identical scores 1/61.
        var fused = new ReciprocalRankFusion().Fuse([List("vector", D), List("keyword", A)]);

        Assert.Equal([A, D], fused.Select(f => f.ChunkId));
    }

    [Fact]
    public void k_is_configurable()
    {
        var fused = new ReciprocalRankFusion(k: 1).Fuse([List("vector", A, B)]);

        Assert.Equal(0.5, fused[0].Score, precision: 12);       // 1 / (1 + 1)
        Assert.Equal(1.0 / 3, fused[1].Score, precision: 12);   // 1 / (1 + 2)
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void k_must_be_positive(int k)
    {
        Assert.Throws<DomainException>(() => new ReciprocalRankFusion(k));
    }

    [Fact]
    public void A_single_list_keeps_its_order()
    {
        var fused = new ReciprocalRankFusion().Fuse([List("keyword", C, A, B)]);

        Assert.Equal([C, A, B], fused.Select(f => f.ChunkId));
    }

    [Fact]
    public void Duplicates_within_a_list_count_once_at_their_first_position()
    {
        var fused = new ReciprocalRankFusion().Fuse([List("vector", A, A, B)]);

        Assert.Equal(1.0 / 61, fused.Single(f => f.ChunkId == A).Score, precision: 12);
        Assert.Equal(3, fused.Single(f => f.ChunkId == B).RankIn("vector")); // ranks stay positional
    }

    [Fact]
    public void Empty_lists_fuse_to_nothing()
    {
        Assert.Empty(new ReciprocalRankFusion().Fuse([List("vector"), List("keyword")]));
        Assert.Empty(new ReciprocalRankFusion().Fuse([]));
    }

    [Fact]
    public void List_names_must_be_unique()
    {
        Assert.Throws<DomainException>(() => new ReciprocalRankFusion().Fuse([List("vector", A), List("vector", B)]));
    }
}

public class SearchModeTests
{
    [Theory]
    [InlineData(null, "hybrid")]
    [InlineData("hybrid", "hybrid")]
    [InlineData("vector", "vector")]
    [InlineData("keyword", "keyword")]
    [InlineData("KEYWORD", "keyword")]
    public void Parses_the_three_modes_with_hybrid_as_default(string? input, string expected)
    {
        Assert.Equal(expected, SearchMode.Parse(input).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bm25")]
    [InlineData("vector,keyword")]
    public void Rejects_anything_else_and_lists_the_options(string input)
    {
        var ex = Assert.Throws<DomainException>(() => SearchMode.Parse(input));
        Assert.Contains("vector, keyword, hybrid", ex.Message);
    }

    [Fact]
    public void Says_which_legs_run()
    {
        Assert.True(SearchMode.Hybrid.UsesVector && SearchMode.Hybrid.UsesKeyword);
        Assert.True(SearchMode.Vector.UsesVector && !SearchMode.Vector.UsesKeyword);
        Assert.True(!SearchMode.Keyword.UsesVector && SearchMode.Keyword.UsesKeyword);
    }
}

public class CandidateCountTests
{
    [Fact]
    public void Defaults_to_20()
    {
        Assert.Equal(20, CandidateCount.Create(null).Value);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(200)]
    public void Accepts_values_from_the_largest_topK_to_200(int value)
    {
        Assert.Equal(value, CandidateCount.Create(value).Value);
    }

    [Theory]
    [InlineData(19)]   // fewer candidates than the largest topK (20) could never fill a full result page
    [InlineData(0)]
    [InlineData(201)]
    public void Rejects_out_of_range_values(int value)
    {
        Assert.Throws<DomainException>(() => CandidateCount.Create(value));
    }
}
