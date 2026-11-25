using System.Text;

namespace FreightRag.Core.Domain;

/// <summary>
/// Rebuilds a document's text from its stored chunks: each chunk is "header line, then body", and the section's heading
/// lives only in the header. Fields are not stored, so reading them later means reading this text again.
/// </summary>
public static class StoredDocumentText
{
    public static string Reassemble(IEnumerable<string> chunkContents)
    {
        ArgumentNullException.ThrowIfNull(chunkContents);

        var text = new StringBuilder();
        string? previous = null;
        foreach (var content in chunkContents)
        {
            var newline = content.IndexOf('\n');
            var header = newline < 0 ? content : content[..newline];
            var body = newline < 0 ? "" : content[(newline + 1)..];

            // A heading cannot contain lower-case letters, so the LAST marker is always ours.
            var at = header.LastIndexOf(SectionChunker.SectionMarker, StringComparison.Ordinal);
            var section = at < 0 ? SectionChunker.UntitledSection : header[(at + SectionChunker.SectionMarker.Length)..];

            if (section != previous)
            {
                if (text.Length > 0)
                    text.Append("\n\n");
                // A heading with no body was stored with its own text as the body, so it is already in the body.
                if (section != SectionChunker.UntitledSection && body != section)
                    text.Append(section).Append('\n');
                previous = section;
            }
            else
            {
                text.Append('\n');   // another piece of the same section, cut at a line or paragraph boundary
            }
            text.Append(body);
        }
        return text.ToString();
    }
}
