using System.Diagnostics.CodeAnalysis;

namespace FreightRag.Core.Domain;

/// <summary>Value object: how an uploaded file stores its text. A closed "smart enum": plain text or PDF.</summary>
public sealed record SourceFormat
{
    public static readonly SourceFormat Text = new("txt");
    public static readonly SourceFormat Pdf = new("pdf");

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    private SourceFormat(string name) => Name = name;

    public string Name { get; }

    /// <summary>The format named by the file extension (.txt or .pdf, any case); anything else is unsupported.</summary>
    public static bool TryFromFileName(string fileName, [NotNullWhen(true)] out SourceFormat? format)
    {
        var extension = Path.GetExtension(fileName ?? "");
        format = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ? Text
            : extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? Pdf
            : null;
        return format is not null;
    }

    /// <summary>
    /// True when the bytes start with "%PDF-". Deliberately strict: the PDF spec tolerates junk before the header,
    /// but a file that does not begin with it is far more likely to be a renamed text file than a real PDF.
    /// </summary>
    public static bool HasPdfSignature(ReadOnlySpan<byte> content) => content.StartsWith(PdfMagic);

    public override string ToString() => Name;
}
