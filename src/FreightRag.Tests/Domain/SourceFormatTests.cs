using FreightRag.Core.Domain;

namespace FreightRag.Tests.Domain;

public class SourceFormatTests
{
    [Theory]
    [InlineData("rate-confirmation-48213.txt", "txt")]
    [InlineData("rate-confirmation-48213.TXT", "txt")]
    [InlineData("rate-confirmation-48213.pdf", "pdf")]
    [InlineData("Invoice-DH-26-0412.PDF", "pdf")]
    public void Txt_and_pdf_extensions_are_recognised_case_insensitively(string fileName, string expected)
    {
        Assert.True(SourceFormat.TryFromFileName(fileName, out var format));
        Assert.Equal(expected, format!.Name);
    }

    [Theory]
    [InlineData("invoice-X-1.docx")]
    [InlineData("invoice-X-1.pdf.exe")]
    [InlineData("invoice-X-1")]
    [InlineData("")]
    public void Other_extensions_are_not_recognised(string fileName)
    {
        Assert.False(SourceFormat.TryFromFileName(fileName, out var format));
        Assert.Null(format);
    }

    [Theory]
    [InlineData("%PDF-1.7\n", true)]
    [InlineData("%PDF-", true)]
    [InlineData(" %PDF-1.7", false)]   // junk before the header
    [InlineData("%PDF", false)]        // no dash
    [InlineData("INVOICE\nTotal: $1", false)]
    [InlineData("", false)]
    public void A_pdf_signature_is_percent_PDF_dash_at_offset_zero(string content, bool expected) =>
        Assert.Equal(expected, SourceFormat.HasPdfSignature(System.Text.Encoding.ASCII.GetBytes(content)));
}
