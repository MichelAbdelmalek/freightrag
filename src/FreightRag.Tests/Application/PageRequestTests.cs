using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;

namespace FreightRag.Tests.Application;

public class PageRequestTests
{
    [Fact]
    public void Defaults_to_the_first_page_of_twenty()
    {
        var page = PageRequest.Create(null, null);

        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(0, page.Offset);
    }

    [Theory]
    [InlineData(1, 20, 0)]
    [InlineData(3, 10, 20)]
    [InlineData(2, 100, 100)]
    public void Offset_skips_the_previous_pages(int page, int size, int expectedOffset)
    {
        Assert.Equal(expectedOffset, PageRequest.Create(page, size).Offset);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]                 // upper bound keeps one request cheap
    [InlineData(100_001, 20)]            // absurd page numbers are refused instead of overflowing the offset
    public void Rejects_out_of_range_values(int page, int size)
    {
        var ex = Assert.Throws<DomainException>(() => PageRequest.Create(page, size));
        Assert.Contains("page", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_paged_result_knows_how_many_pages_there_are()
    {
        var result = new PagedResult<string>(["a", "b"], PageRequest.Create(1, 2), TotalCount: 5);

        Assert.Equal(3, result.TotalPages);
    }
}
