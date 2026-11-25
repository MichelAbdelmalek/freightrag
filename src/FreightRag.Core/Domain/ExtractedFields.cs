namespace FreightRag.Core.Domain;

/// <summary>
/// The key/value fields read from a document: name to value, in catalog order, null when a field was not found.
/// Values are document content. <see cref="ToString"/> shows only counts, so logging this object can never leak them.
/// </summary>
public sealed class ExtractedFields
{
    private readonly Dictionary<string, string?> _byName;

    internal ExtractedFields(IReadOnlyList<KeyValuePair<string, string?>> items)
    {
        Items = items;
        _byName = items.ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);
    }

    public IReadOnlyList<KeyValuePair<string, string?>> Items { get; }

    public int Count => Items.Count;

    public string? this[string name] => _byName[name];

    public override string ToString() => $"ExtractedFields({Count} requested, {Items.Count(i => i.Value is not null)} found)";
}
