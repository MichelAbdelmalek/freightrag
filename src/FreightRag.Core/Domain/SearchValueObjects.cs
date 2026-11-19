namespace FreightRag.Core.Domain;

/// <summary>
/// Value object: the name of the embedding model that produced a vector (e.g. "nomic-embed-text").
/// Vectors from different models live in different spaces, so distances between them are meaningless;
/// every stored chunk records its model and every search filters on it.
/// </summary>
public sealed record EmbeddingModelName
{
    public const int MaxLength = 100;

    public EmbeddingModelName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException("An embedding model name is required.");
        if (value.Length > MaxLength)
            throw new DomainException($"An embedding model name can be at most {MaxLength} characters.");
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// Value object: the text of a search query, trimmed, 1–1000 characters.
/// Queries can contain sensitive business details, so <see cref="ToString"/> deliberately reveals only the length.
/// </summary>
public sealed class SearchText
{
    public const int MaxLength = 1000;

    private SearchText(string value) => Value = value;

    public string Value { get; }

    public int Length => Value.Length;

    public static SearchText Create(string? query)
    {
        // Allow some surrounding whitespace, but refuse megabytes of it before paying for Trim().
        if (query is not null && query.Length > 2 * MaxLength)
            throw new DomainException($"query can be at most {MaxLength} characters.");

        var trimmed = query?.Trim() ?? "";
        if (trimmed.Length == 0)
            throw new DomainException("query must not be empty.");
        if (trimmed.Length > MaxLength)
            throw new DomainException($"query can be at most {MaxLength} characters.");
        return new SearchText(trimmed);
    }

    public override string ToString() => $"SearchText(length: {Length})";
}

/// <summary>Value object: how many chunks a search returns (1–20, default 5).</summary>
public sealed record TopK
{
    public const int Default = 5;
    public const int Max = 20;

    private TopK(int value) => Value = value;

    public int Value { get; }

    public static TopK Create(int? value)
    {
        var k = value ?? Default;
        if (k is < 1 or > Max)
            throw new DomainException($"topK must be between 1 and {Max}.");
        return new TopK(k);
    }
}
