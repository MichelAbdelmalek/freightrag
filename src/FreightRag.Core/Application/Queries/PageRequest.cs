using FreightRag.Core.Domain;

namespace FreightRag.Core.Application.Queries;

/// <summary>A validated page of a list: 1-based page number and a bounded page size.</summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxPage = 100_000;

    private PageRequest(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    public int Page { get; }
    public int PageSize { get; }

    /// <summary>Rows to skip (SQL OFFSET).</summary>
    public int Offset => (Page - 1) * PageSize;

    /// <summary>Missing values fall back to page 1 and <see cref="DefaultPageSize"/>.</summary>
    public static PageRequest Create(int? page, int? pageSize)
    {
        var p = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (p is < 1 or > MaxPage)
            throw new DomainException($"page must be between 1 and {MaxPage}.");
        if (size is < 1 or > MaxPageSize)
            throw new DomainException($"pageSize must be between 1 and {MaxPageSize}.");
        return new PageRequest(p, size);
    }
}

/// <summary>One page of results plus the total, so clients can show "page 2 of 5".</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, PageRequest Request, int TotalCount)
{
    public int TotalPages => (TotalCount + Request.PageSize - 1) / Request.PageSize;
}
