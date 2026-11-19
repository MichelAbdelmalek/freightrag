using System.Text;

namespace FreightRag.Core.Domain;

/// <summary>
/// Domain service that cuts a document into chunks, the unit we embed and later retrieve.
///
/// Why chunk by section? Retrieval returns individual chunks, so each chunk should be about
/// one thing ("RATE", "PICKUP", "3. DETENTION REPORTING"). Smaller, focused chunks give
/// sharper embeddings than one vector for a whole document.
///
/// Why a context header? A chunk such as "Detention: $50.00 per hour" is useless on its own:
/// which load? So every chunk starts with e.g.
///   "Document: rate-confirmation | Load: 48213 | Section: RATE"
/// which is embedded together with the text and shown to the LLM later.
/// </summary>
public sealed class SectionChunker(int maxBodyChars = SectionChunker.DefaultMaxBodyChars)
{
    /// <summary>Sections whose body (excluding the header line) is longer than this are split further.</summary>
    public const int DefaultMaxBodyChars = 1000;

    public const string UntitledSection = "(untitled)";

    private const int MaxHeadingLength = 80;

    public int MaxBodyChars { get; } = maxBodyChars > 0
        ? maxBodyChars
        : throw new ArgumentOutOfRangeException(nameof(maxBodyChars), "Must be positive.");

    /// <summary>
    /// A heading-shaped line is short, has no lower-case letters, is mostly letters and does not end
    /// with a full stop, e.g. "RATE", "DELIVERY RECEIPT (PROOF OF DELIVERY)" or "3. DETENTION REPORTING".
    /// "Mostly letters" stops codes such as "SAV-778431" from counting; the full-stop rule stops
    /// shouted sentences ("LOADS MUST BE SEALED."). Whether it actually starts a section also depends
    /// on the line before it — see <see cref="ReadSections"/>.
    /// </summary>
    public static bool IsHeading(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length is < 2 or > MaxHeadingLength)
            return false;
        if (trimmed.Any(char.IsLower) || trimmed.EndsWith('.'))
            return false;

        var letters = trimmed.Count(char.IsLetter);
        var nonSpace = trimmed.Count(c => !char.IsWhiteSpace(c));
        return letters >= 2 && letters * 2 >= nonSpace;
    }

    public IReadOnlyList<DocumentChunk> Split(string text, DocType docType, DocumentIdentifiers identifiers)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(docType);
        ArgumentNullException.ThrowIfNull(identifiers);

        var headerStart = identifiers.IsEmpty
            ? $"Document: {docType.Name}"
            : $"Document: {docType.Name} | {identifiers.ToHeaderSegment()}";

        var chunks = new List<DocumentChunk>();
        foreach (var section in ReadSections(text))
        {
            foreach (var body in SplitBody(section.Lines))
            {
                var content = $"{headerStart} | Section: {section.Name}\n{body}";
                chunks.Add(new DocumentChunk(chunks.Count, section.Name, content));
            }
        }
        return chunks;
    }

    private sealed record Section(string Name, List<string> Lines);

    /// <summary>
    /// Groups lines under the heading above them. No text is ever dropped:
    /// - In a run of consecutive heading-shaped lines only the first is a heading; the rest are body
    ///   ("SHIPPER" / "BAYOU FROZEN FOODS" / "1500 CLINTON DR" keeps the name and address in SHIPPER).
    /// - A heading with no body (e.g. a document title) uses its own text as the body.
    /// </summary>
    private static IEnumerable<Section> ReadSections(string text)
    {
        var lines = text.TrimStart('\uFEFF').ReplaceLineEndings("\n").Split('\n');

        var sections = new List<Section>();
        Section? current = null;
        var previousWasHeadingShaped = false;
        foreach (var rawLine in lines)
        {
            // Trailing whitespace carries no meaning; whitespace-only lines become blank lines.
            var line = rawLine.TrimEnd();
            var headingShaped = IsHeading(line);
            var startsSection = headingShaped && !previousWasHeadingShaped;
            previousWasHeadingShaped = headingShaped;

            if (startsSection)
            {
                current = new Section(line.Trim(), []);
                sections.Add(current);
                continue;
            }

            if (current is null)
            {
                if (line.Length == 0)
                    continue; // blank lines before any content
                current = new Section(UntitledSection, []);
                sections.Add(current);
            }
            current.Lines.Add(line);
        }

        foreach (var section in sections)
        {
            var body = TrimBlankLines(section.Lines);
            yield return section with { Lines = body.Count > 0 ? body : [section.Name] };
        }
    }

    /// <summary>
    /// Returns the section body as one piece, or — if it is too long — several pieces cut at
    /// paragraph boundaries (blank lines), falling back to line boundaries for a huge paragraph.
    /// A line is never cut, so a single line longer than the limit becomes its own piece.
    /// </summary>
    private IEnumerable<string> SplitBody(List<string> lines)
    {
        var whole = string.Join('\n', lines);
        if (whole.Length <= MaxBodyChars)
            return [whole];

        var pieces = new List<string>();
        var paragraphs = SplitIntoParagraphs(lines);
        var pending = new List<string>(); // small paragraphs waiting to be packed together

        foreach (var paragraph in paragraphs)
        {
            var paragraphText = string.Join('\n', paragraph);
            if (paragraphText.Length <= MaxBodyChars)
            {
                pending.Add(paragraphText);
                continue;
            }

            pieces.AddRange(PackGreedily(pending, separator: "\n\n"));
            pending.Clear();
            pieces.AddRange(PackGreedily(paragraph, separator: "\n"));
        }
        pieces.AddRange(PackGreedily(pending, separator: "\n\n"));
        return pieces;
    }

    /// <summary>Joins consecutive units while the result stays within the limit.</summary>
    private List<string> PackGreedily(IReadOnlyList<string> units, string separator)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        foreach (var unit in units)
        {
            if (current.Length > 0 && current.Length + separator.Length + unit.Length > MaxBodyChars)
            {
                pieces.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0)
                current.Append(separator);
            current.Append(unit);
        }
        if (current.Length > 0)
            pieces.Add(current.ToString());
        return pieces;
    }

    private static List<List<string>> SplitIntoParagraphs(List<string> lines)
    {
        var paragraphs = new List<List<string>>();
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (current.Count > 0)
                    paragraphs.Add(current);
                current = [];
            }
            else
            {
                current.Add(line);
            }
        }
        if (current.Count > 0)
            paragraphs.Add(current);
        return paragraphs;
    }

    private static List<string> TrimBlankLines(List<string> lines)
    {
        var start = lines.FindIndex(l => l.Length > 0);
        if (start < 0)
            return [];
        var end = lines.FindLastIndex(l => l.Length > 0);
        return lines.GetRange(start, end - start + 1);
    }
}
