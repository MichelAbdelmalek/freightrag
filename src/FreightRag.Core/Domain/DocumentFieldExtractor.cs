using System.Text.RegularExpressions;

namespace FreightRag.Core.Domain;

/// <summary>
/// Domain service: reads the key/value fields of a document by plain rules. No model, no storage, nothing guessed:
/// a value is the text exactly as written after a known label, and a field that is not there is null.
/// </summary>
public static class DocumentFieldExtractor
{
    public static ExtractedFields Extract(string text, FieldSelection selection)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(selection);

        var source = FieldSource.Read(text);
        var rules = DocumentFields.RulesFor(selection.DocType).ToDictionary(r => r.Field.Name);
        var items = new List<KeyValuePair<string, string?>>(selection.Names.Count);
        foreach (var name in selection.Names)
        {
            string? value;
            try
            {
                value = rules[name].Read(source);
            }
            catch (RegexMatchTimeoutException)
            {
                value = null;   // never let the exception (its message holds the input) travel
            }
            items.Add(new(name, value));
        }
        return new ExtractedFields(items);
    }
}

/// <summary>A document read once into sections, lines and label hits, so every field rule is a cheap lookup.</summary>
internal sealed partial class FieldSource
{
    internal const int MaxTextLength = 200_000;
    internal const int MaxLineLength = 1000;
    internal const int MaxValueLength = 300;

    // Every label a rule can ask for: key (what rules say) and pattern (how it is written). Words are joined by [\t ]+.
    private static readonly (string Key, string Pattern)[] Labels =
    [
        ("broker", "Broker"), ("carrier", "Carrier"), ("load number", @"Load[\t ]+number"), ("date issued", @"Date[\t ]+issued"),
        ("location", "Location"), ("shipper", "Shipper"), ("date and window", @"Date[\t ]+and[\t ]+window"), ("date", "Date"),
        ("commodity", "Commodity"), ("equipment", "Equipment"), ("temperature", "Temperature"), ("quantity", "Quantity"),
        ("line haul", @"Line[\t ]+haul"), ("fuel surcharge", @"Fuel[\t ]+surcharge"), ("total agreed rate", @"Total[\t ]+agreed[\t ]+rate"),
        ("detention", @"Detention(?:[\t ]+at[\t ]+[A-Za-z]{1,20})?(?:[\t ]*\([^():\n]{1,80}\))?"), ("payment terms", @"Payment(?:[\t ]+terms)?"),
        ("bol number", @"BOL[\t ]+number"), ("ship date", @"Ship[\t ]+date"), ("consignee", "Consignee"), ("driver", "Driver"),
        ("truck number", @"Truck(?:[\t ]+number)?"), ("trailer number", @"Trailer(?:[\t ]+number)?"),
        ("seal number", @"Seal(?:[\t ]+number)?"), ("seal intact on arrival", @"Seal[\t ]+intact[\t ]+on[\t ]+arrival"),
        ("freight class", @"Freight[\t ]+class"), ("temperature setting", @"Temperature[\t ]+setting"),
        ("hazardous materials", @"Hazardous[\t ]+materials"), ("temperature control", @"Temperature[\t ]+control"), ("delivered", "Delivered"), ("received by", @"Received[\t ]+by"),
        ("condition", "Condition"), ("product temperature at delivery", @"Product[\t ]+temperature[\t ]+at[\t ]+delivery"),
        ("total weight", @"Total[\t ]+weight"), ("from", "From"), ("bill to", @"Bill[\t ]+to"),
        ("invoice number", @"Invoice[\t ]+number"), ("invoice date", @"Invoice[\t ]+date"), ("total due", @"Total[\t ]+due"),
        ("payment due date", @"Payment[\t ]+due[\t ]+date"), ("attachments", "Attachments"), ("questions", "Questions"),
    ];

    // A label counts at the start of a line or after whitespace: OCR and fax text put several labels on one line, and
    // OCR output collapses the wide gaps to single spaces ("BOL number: X-1 Load number: 52010 Ship date: ..."). The set
    // of labels is closed and each must be followed by a colon, which keeps ordinary words from counting.
    private static readonly Regex LabelRegex = new(
        @"(?:^|(?<=[\t ]))(?:" + string.Join("|", Labels.Select((l, i) => $"(?<k{i}>{l.Pattern})")) + @")[\t ]*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private sealed record Hit(string Key, int LabelStart, string Value);

    private sealed record Line(string Text, List<Hit> Hits);

    private sealed record Section(string Name, List<Line> Lines);

    private readonly List<Section> _sections;

    private FieldSource(List<Section> sections) => _sections = sections;

    internal static FieldSource Read(string text)
    {
        if (text.Length > MaxTextLength)
            text = text[..MaxTextLength];

        var sections = new List<Section>();
        foreach (var section in SectionChunker.ReadSections(text))
        {
            var lines = new List<Line>();
            foreach (var raw in section.Lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.Length > MaxLineLength)
                    continue;   // blank, or too long to be a field line: skipped
                lines.Add(new Line(line, HitsOf(line)));
            }
            sections.Add(new Section(section.Name, lines));
        }
        return new FieldSource(sections);
    }

    private static List<Hit> HitsOf(string line)
    {
        var starts = new List<(string Key, int Start, int ValueStart)>();
        try
        {
            foreach (Match match in LabelRegex.Matches(line))
            {
                for (var i = 0; i < Labels.Length; i++)
                    if (match.Groups[$"k{i}"].Success)
                    {
                        starts.Add((Labels[i].Key, match.Index, match.Index + match.Length));
                        break;
                    }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }

        var hits = new List<Hit>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Start : line.Length;
            hits.Add(new Hit(starts[i].Key, starts[i].Start, line[starts[i].ValueStart..end]));
        }
        return hits;
    }

    /// <summary>A usable value: trimmed, not a trailing comma, has a letter or digit, at most 300 characters. Else null.</summary>
    internal static string? Clean(string? value)
    {
        var v = value?.Trim().TrimEnd(',', ' ', '\t');
        if (string.IsNullOrEmpty(v) || v.Length > MaxValueLength || !v.Any(char.IsLetterOrDigit))
            return null;
        return v;
    }

    /// <summary>The first usable value after this label, optionally only inside the named section.</summary>
    internal string? Value(string key, string? section = null)
    {
        foreach (var s in _sections)
        {
            if (section is not null && !s.Name.Equals(section, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var line in s.Lines)
                foreach (var hit in line.Hits)
                    if (hit.Key == key && Clean(hit.Value) is { } value)
                        return value;
        }
        return null;
    }

    /// <summary>Lines of a section that are not "Label: value" lines (names, addresses, free text), as written.</summary>
    internal IEnumerable<string> PlainLines(string section)
    {
        foreach (var s in _sections.Where(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)))
            foreach (var line in s.Lines)
            {
                if (line.Hits.Count > 0 && line.Hits[0].LabelStart == 0)
                    continue;
                var text = line.Hits.Count > 0 ? line.Text[..line.Hits[0].LabelStart] : line.Text;
                if (Clean(text) is { } plain)
                    yield return plain;
            }
    }

    /// <summary>For a table row without a colon ("Line haul  1  $2,780.00  $2,780.00"): the last amount on the row.</summary>
    internal string? LastAmountOnRowLabelled(string labelPattern)
    {
        var row = new Regex(@"^[\t ]*" + labelPattern + @"\b[^\n]{0,200}?(?<a>\$[0-9][0-9,]{0,12}\.[0-9]{2})[\t ]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (var s in _sections)
            foreach (var line in s.Lines)
            {
                var match = row.Match(line.Text);
                if (match.Success)
                    return match.Groups["a"].Value;
            }
        return null;
    }

    // ---- shapes of values ----

    [GeneratedRegex(@"\([^()]{1,80}\)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingIds();

    [GeneratedRegex(@",[\t ]+(?=[0-9])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommaBeforeDigit();

    [GeneratedRegex(@"MC#[\t ]*(?<n>[0-9]{1,10})", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex McNumber();

    [GeneratedRegex(@"DOT#[\t ]*(?<n>[0-9]{1,10})", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DotNumber();

    [GeneratedRegex(@"^(?<q>[^,]{1,40}),[\t ]*(?<w>[0-9][0-9,.]{0,12}[\t ]*lbs)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuantityAndWeight();

    [GeneratedRegex(@"^(?<q>[0-9][0-9,]{0,6}[\t ]+[A-Za-z]{1,20})[\t ]+of[\t ]+(?<c>[^\n]{1,200}?),[\t ]+total weight[\t ]+(?<w>[0-9][0-9,.]{0,12}[\t ]*lbs)[\t ]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ShipmentSentence();

    [GeneratedRegex(@"^(?<d>[^()]{1,40}?)[\t ]*\((?<t>[^()]{1,40})\)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DateAndTerms();

    /// <summary>"Acme Co., 12 Main St, City, ST 00000 (MC# 1)": the name is the text before the first ", digit".</summary>
    internal static string? NameOf(string? raw)
    {
        if (raw is null) return null;
        var withoutIds = TrailingIds().Replace(raw, "").Trim();
        var split = CommaBeforeDigit().Match(withoutIds);
        return Clean(split.Success ? withoutIds[..split.Index] : withoutIds);
    }

    internal static string? AddressOf(string? raw)
    {
        if (raw is null) return null;
        var withoutIds = TrailingIds().Replace(raw, "").Trim();
        var split = CommaBeforeDigit().Match(withoutIds);
        return split.Success ? Clean(withoutIds[(split.Index + split.Length)..]) : null;
    }

    internal static string? McOf(string? raw) => raw is null ? null : McNumber().Match(raw) is { Success: true } m ? m.Groups["n"].Value : null;

    internal static string? DotOf(string? raw) => raw is null ? null : DotNumber().Match(raw) is { Success: true } m ? m.Groups["n"].Value : null;

    internal static (string? Quantity, string? Weight) SplitQuantity(string? raw)
    {
        if (raw is null) return (null, null);
        var m = QuantityAndWeight().Match(raw);
        return m.Success ? (Clean(m.Groups["q"].Value), Clean(m.Groups["w"].Value)) : (raw, null);
    }

    internal static (string? Quantity, string? Commodity, string? Weight) SplitShipment(string? line)
    {
        if (line is null) return (null, null, null);
        var m = ShipmentSentence().Match(line);
        return m.Success ? (Clean(m.Groups["q"].Value), Clean(m.Groups["c"].Value), Clean(m.Groups["w"].Value)) : (null, null, null);
    }

    internal static (string? Date, string? Terms) SplitDateAndTerms(string? raw)
    {
        if (raw is null) return (null, null);
        var m = DateAndTerms().Match(raw);
        return m.Success ? (Clean(m.Groups["d"].Value), Clean(m.Groups["t"].Value)) : (raw, null);
    }
}
