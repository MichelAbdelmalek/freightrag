using System.Diagnostics.CodeAnalysis;

namespace FreightRag.Core.Domain;

/// <summary>
/// Value object: the kind of freight document. The set is closed (a "smart enum"):
/// only the four instances below can exist.
/// </summary>
public sealed record DocType
{
    public static readonly DocType RateConfirmation = new("rate-confirmation");
    public static readonly DocType BillOfLading = new("bill-of-lading");
    public static readonly DocType Invoice = new("invoice");
    public static readonly DocType DriverHandbook = new("driver-handbook");

    public static IReadOnlyList<DocType> All { get; } = [RateConfirmation, BillOfLading, Invoice, DriverHandbook];

    private DocType(string name) => Name = name;

    /// <summary>Stable name, stored in documents.doc_type and shown in chunk headers.</summary>
    public string Name { get; }

    /// <summary>
    /// Infers the type from the file name prefix, e.g. "invoice-NFL-2026-1187.txt" → invoice.
    /// The prefix must start the name and be followed by '-' or '.', so "invoices-2026.txt" does not match.
    /// </summary>
    public static bool TryFromFileName(string fileName, [NotNullWhen(true)] out DocType? docType)
    {
        var name = Path.GetFileName(fileName);
        docType = All.FirstOrDefault(t =>
            name.StartsWith(t.Name, StringComparison.OrdinalIgnoreCase)
            && name.Length > t.Name.Length
            && name[t.Name.Length] is '-' or '.');
        return docType is not null;
    }

    /// <summary>The type with this exact stored name (as kept in documents.doc_type), or none.</summary>
    public static bool TryFromName(string name, [NotNullWhen(true)] out DocType? docType)
    {
        docType = All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.Ordinal));
        return docType is not null;
    }

    public override string ToString() => Name;
}
