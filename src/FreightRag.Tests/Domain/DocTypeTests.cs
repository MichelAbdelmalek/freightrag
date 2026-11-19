using FreightRag.Core.Domain;

namespace FreightRag.Tests.Domain;

public class DocTypeTests
{
    [Theory]
    [InlineData("rate-confirmation-48213.txt", "rate-confirmation")]
    [InlineData("bill-of-lading-SAV-778431.txt", "bill-of-lading")]
    [InlineData("invoice-NFL-2026-1187.txt", "invoice")]
    [InlineData("driver-handbook-excerpt.txt", "driver-handbook")]
    [InlineData("data/tenant-delta-haul/invoice-DH-26-0412.txt", "invoice")] // directories are ignored
    public void Is_inferred_from_the_file_name_prefix(string fileName, string expected)
    {
        Assert.True(DocType.TryFromFileName(fileName, out var docType));
        Assert.Equal(expected, docType!.Name);
    }

    [Theory]
    [InlineData("packing-list-123.txt")]
    [InlineData("invoices-2026.txt")]          // prefix must be followed by '-' or '.'
    [InlineData("my-rate-confirmation-1.txt")] // prefix must be at the start
    [InlineData("")]
    public void Unknown_prefixes_are_not_recognised(string fileName)
    {
        Assert.False(DocType.TryFromFileName(fileName, out var docType));
        Assert.Null(docType);
    }
}
