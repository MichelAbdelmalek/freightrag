using System.Text.RegularExpressions;

namespace FreightRag.Core.Domain;

/// <summary>
/// Value object: the business identifiers found in a document (load, bill of lading, invoice).
/// They are repeated in every chunk header so that a chunk retrieved on its own still says
/// which load/BOL/invoice it belongs to — this is what makes "detention for load 48213" findable.
/// </summary>
public sealed partial record DocumentIdentifiers(string? LoadNumber, string? BolNumber, string? InvoiceNumber)
{
    public static DocumentIdentifiers None { get; } = new(null, null, null);

    public bool IsEmpty => LoadNumber is null && BolNumber is null && InvoiceNumber is null;

    /// <summary>Scans the whole document; for each identifier the first matching line wins.</summary>
    public static DocumentIdentifiers Extract(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Same normalisation as the chunker: \r\n, lone \r and Unicode line separators all become \n,
        // because the regexes' ^ and $ only understand \n.
        var normalised = text.ReplaceLineEndings("\n");
        return new DocumentIdentifiers(
            FirstValue(LoadNumberLine(), normalised),
            FirstValue(BolNumberLine(), normalised),
            FirstValue(InvoiceNumberLine(), normalised));
    }

    /// <summary>"Load: 48213 | BOL: SAV-778431 | Invoice: NFL-2026-1187" (missing ones omitted, fixed order).</summary>
    public string ToHeaderSegment()
    {
        var parts = new List<string>(3);
        if (LoadNumber is not null) parts.Add($"Load: {LoadNumber}");
        if (BolNumber is not null) parts.Add($"BOL: {BolNumber}");
        if (InvoiceNumber is not null) parts.Add($"Invoice: {InvoiceNumber}");
        return string.Join(" | ", parts);
    }

    private static string? FirstValue(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups["value"].Value : null;
    }

    // Each pattern matches one whole line: optional indentation, a case-insensitive label,
    // the value, optional trailing spaces, end of line.
    // [\t\p{Zs}] = tab or any Unicode space (incl. non-breaking space) but NOT a newline, so a
    // match never spans lines. [0-9] (not \d) accepts ASCII digits only.
    // [GeneratedRegex] compiles these at build time; the timeout is a guard against pathological input.

    /// <summary>"Load number: 48213" — digits only.</summary>
    [GeneratedRegex(@"^[\t\p{Zs}]*(?i:load number):[\t\p{Zs}]*(?<value>[0-9]+)[\t\p{Zs}]*$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LoadNumberLine();

    /// <summary>"BOL number: SAV-778431" or "BOL number: 00123456789" — optional 2–5 capital letters and a dash, then digits.</summary>
    [GeneratedRegex(@"^[\t\p{Zs}]*(?i:bol number):[\t\p{Zs}]*(?<value>(?:[A-Z]{2,5}-)?[0-9]+)[\t\p{Zs}]*$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BolNumberLine();

    /// <summary>"Invoice number: NFL-2026-1187" or "118734" — capitals/digits, optional dash-separated parts, at least one digit.</summary>
    [GeneratedRegex(@"^[\t\p{Zs}]*(?i:invoice number):[\t\p{Zs}]*(?<value>(?=[A-Z-]*[0-9])[A-Z0-9]+(?:-[A-Z0-9]+)*)[\t\p{Zs}]*$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvoiceNumberLine();
}
