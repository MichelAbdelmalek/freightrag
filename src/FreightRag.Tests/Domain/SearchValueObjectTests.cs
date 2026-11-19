using FreightRag.Core.Domain;

namespace FreightRag.Tests.Domain;

public class EmbeddingModelNameTests
{
    [Fact]
    public void Keeps_a_valid_model_name()
    {
        Assert.Equal("nomic-embed-text", new EmbeddingModelName("nomic-embed-text").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_blank_names(string name)
    {
        Assert.Throws<DomainException>(() => new EmbeddingModelName(name));
    }

    [Fact]
    public void Rejects_names_longer_than_100_characters()
    {
        Assert.Throws<DomainException>(() => new EmbeddingModelName(new string('m', 101)));
    }

    [Fact]
    public void Equal_names_are_equal()
    {
        Assert.Equal(new EmbeddingModelName("nomic-embed-text"), new EmbeddingModelName("nomic-embed-text"));
    }
}

public class SearchTextTests
{
    [Fact]
    public void Trims_surrounding_whitespace()
    {
        Assert.Equal("detention for load 48213", SearchText.Create("  detention for load 48213\n").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\n")]
    public void Rejects_missing_or_blank_queries(string? query)
    {
        var ex = Assert.Throws<DomainException>(() => SearchText.Create(query));
        Assert.Contains("query", ex.Message);
    }

    [Fact]
    public void Accepts_exactly_1000_characters_and_rejects_1001()
    {
        Assert.Equal(1000, SearchText.Create(new string('q', 1000)).Value.Length);
        Assert.Throws<DomainException>(() => SearchText.Create(new string('q', 1001)));
    }

    [Fact]
    public void Rejects_huge_raw_input_even_if_it_trims_down_to_something_short()
    {
        // "a" followed by megabytes of whitespace must not be accepted (or trimmed at great cost) as a 1-char query.
        var ex = Assert.Throws<DomainException>(() => SearchText.Create("a" + new string(' ', 2 * SearchText.MaxLength)));
        Assert.Contains("1000", ex.Message);
    }

    [Fact]
    public void Never_reveals_the_query_through_ToString()
    {
        // Query text can contain sensitive business details; it must never end up in logs by accident.
        var text = SearchText.Create("confidential customer pricing");

        Assert.DoesNotContain("confidential", text.ToString());
        Assert.Contains("29", text.ToString()); // length only
    }
}

public class TopKTests
{
    [Fact]
    public void Defaults_to_5()
    {
        Assert.Equal(5, TopK.Create(null).Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void Accepts_1_to_20(int k)
    {
        Assert.Equal(k, TopK.Create(k).Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void Rejects_values_outside_1_to_20(int k)
    {
        var ex = Assert.Throws<DomainException>(() => TopK.Create(k));
        Assert.Contains("topK", ex.Message);
    }
}
