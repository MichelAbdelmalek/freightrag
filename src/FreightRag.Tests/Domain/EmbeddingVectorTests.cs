using FreightRag.Core.Domain;

namespace FreightRag.Tests.Domain;

public class EmbeddingVectorTests
{
    [Theory]
    [InlineData(767)]
    [InlineData(769)]
    [InlineData(0)]
    public void Rejects_any_dimension_other_than_768(int dimensions)
    {
        var ex = Assert.Throws<DomainException>(() => new EmbeddingVector(new float[dimensions]));
        Assert.Contains("768", ex.Message);
        Assert.Contains(dimensions.ToString(), ex.Message);
    }

    [Fact]
    public void Accepts_exactly_768_dimensions_and_copies_the_values()
    {
        var values = new float[768];
        values[0] = 0.5f;

        var vector = new EmbeddingVector(values);
        values[0] = 99f; // mutating the source must not change the value object

        Assert.Equal(768, vector.Values.Length);
        Assert.Equal(0.5f, vector.Values.Span[0]);
    }

    [Fact]
    public void Rejects_non_finite_values()
    {
        var values = new float[768];
        values[10] = float.NaN;

        Assert.Throws<DomainException>(() => new EmbeddingVector(values));
    }
}
