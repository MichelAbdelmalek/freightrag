using System.Text.RegularExpressions;

namespace FreightRag.Core.Domain;

/// <summary>Why a field request was refused. The message never echoes the input.</summary>
/// <param name="Code">invalid_fields or unknown_field.</param>
/// <param name="ValidNames">The field names that exist for the document type (empty for a handbook).</param>
public sealed record FieldSelectionError(string Code, string Message, IReadOnlyList<string> ValidNames);

/// <summary>Value object: which fields of a document type were asked for, always in catalog order and canonical casing.</summary>
public sealed partial class FieldSelection
{
    public const int MaxRawLength = 1000;
    public const int MaxEntries = 50;

    private FieldSelection(DocType docType, IReadOnlyList<string> names)
    {
        DocType = docType;
        Names = names;
    }

    public DocType DocType { get; }
    public IReadOnlyList<string> Names { get; }

    public static FieldSelection All(DocType docType) => new(docType, DocumentFields.For(docType).Select(f => f.Name).ToList());

    /// <summary>Parses "carrierName, loadNumber": case-insensitive, duplicates ignored, empty means every field.</summary>
    public static bool TryParse(string? raw, DocType docType, out FieldSelection? selection, out FieldSelectionError? error)
    {
        selection = null;
        error = null;
        var valid = DocumentFields.For(docType).Select(f => f.Name).ToList();

        if (string.IsNullOrWhiteSpace(raw))
        {
            selection = All(docType);
            return true;
        }
        if (raw.Length > MaxRawLength)
            return Refuse(out error, valid, "invalid_fields", $"Send at most {MaxRawLength} characters in fields.");

        var entries = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
        {
            selection = All(docType);
            return true;
        }
        if (entries.Length > MaxEntries)
            return Refuse(out error, valid, "invalid_fields", $"Send at most {MaxEntries} field names.");
        if (entries.Any(e => !FieldName().IsMatch(e)))
            return Refuse(out error, valid, "invalid_fields", "Field names are 1 to 40 letters, separated by commas.");

        var requested = entries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Any(r => !valid.Contains(r, StringComparer.OrdinalIgnoreCase)))
            return Refuse(out error, valid, "unknown_field", $"Unknown field name. The valid names for {docType.Name} are listed in validFields.");

        selection = new FieldSelection(docType, valid.Where(v => requested.Contains(v)).ToList());
        return true;
    }

    private static bool Refuse(out FieldSelectionError? error, IReadOnlyList<string> valid, string code, string message)
    {
        error = new FieldSelectionError(code, message, valid);
        return false;
    }

    [GeneratedRegex("^[A-Za-z]{1,40}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FieldName();
}
