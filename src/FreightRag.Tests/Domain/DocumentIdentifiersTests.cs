using FreightRag.Core.Domain;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Domain;

public class DocumentIdentifiersTests
{
    [Theory]
    [InlineData(TestData.NileFreight, "rate-confirmation-48213.txt", "48213", null, null)]
    [InlineData(TestData.NileFreight, "bill-of-lading-SAV-778431.txt", "48213", "SAV-778431", null)]
    [InlineData(TestData.NileFreight, "invoice-NFL-2026-1187.txt", "48213", "SAV-778431", "NFL-2026-1187")]
    [InlineData(TestData.NileFreight, "driver-handbook-excerpt.txt", null, null, null)]
    [InlineData(TestData.DeltaHaul, "rate-confirmation-77104.txt", "77104", null, null)]
    [InlineData(TestData.DeltaHaul, "bill-of-lading-HOU-220957.txt", "77104", "HOU-220957", null)]
    [InlineData(TestData.DeltaHaul, "invoice-DH-26-0412.txt", "77104", "HOU-220957", "DH-26-0412")]
    public void Extracts_identifiers_from_the_real_documents(
        string tenant, string file, string? load, string? bol, string? invoice)
    {
        var ids = DocumentIdentifiers.Extract(TestData.ReadText(tenant, file));

        Assert.Equal(load, ids.LoadNumber);
        Assert.Equal(bol, ids.BolNumber);
        Assert.Equal(invoice, ids.InvoiceNumber);
    }

    [Fact]
    public void Header_segment_lists_identifiers_in_fixed_order_load_bol_invoice()
    {
        var ids = DocumentIdentifiers.Extract(
            "Invoice number: NFL-2026-1187\nBOL number: SAV-778431\nLoad number: 48213\n");

        Assert.Equal("Load: 48213 | BOL: SAV-778431 | Invoice: NFL-2026-1187", ids.ToHeaderSegment());
    }

    [Fact]
    public void Header_segment_is_empty_when_no_identifiers_are_found()
    {
        var ids = DocumentIdentifiers.Extract("1. SAFETY EQUIPMENT\nWear a vest.");

        Assert.True(ids.IsEmpty);
        Assert.Equal("", ids.ToHeaderSegment());
    }

    [Theory]
    [InlineData("Load number: 48213\r\n", "48213")]   // Windows line endings
    [InlineData("  Load number:   48213  \n", "48213")] // surrounding whitespace
    [InlineData("LOAD NUMBER: 48213", "48213")]          // label is case-insensitive
    [InlineData("RATE\rLoad number: 48213\rDate: x", "48213")] // classic-Mac CR-only line endings
    [InlineData("Load number: 48213\u00A0\n", "48213")] // trailing non-breaking space
    public void Load_number_tolerates_formatting_differences(string text, string expected)
    {
        Assert.Equal(expected, DocumentIdentifiers.Extract(text).LoadNumber);
    }

    [Theory]
    [InlineData("Load number: 48213 (see note)")]  // trailing text: not a clean identifier line
    [InlineData("Previous load number: 48213")]    // label must start the line
    [InlineData("Load number: ABC")]                // load numbers are digits
    [InlineData("Load number:\n48213")]             // value must be on the same line
    [InlineData("Load number: \u0664\u0668")]       // non-ASCII (Arabic-Indic) digits are not load numbers
    public void Load_number_ignores_lines_that_are_not_identifier_lines(string text)
    {
        Assert.Null(DocumentIdentifiers.Extract(text).LoadNumber);
    }

    [Fact]
    public void First_occurrence_wins()
    {
        var ids = DocumentIdentifiers.Extract("Load number: 111\nLoad number: 222\n");

        Assert.Equal("111", ids.LoadNumber);
    }

    [Theory]
    [InlineData("BOL number: sav-778431")]   // BOL prefix is upper case letters
    [InlineData("BOL number: SAV778431")]    // must contain a dash
    [InlineData("Invoice number: NFL")]       // needs at least one digit
    public void Malformed_bol_and_invoice_values_are_ignored(string text)
    {
        var ids = DocumentIdentifiers.Extract(text);

        Assert.Null(ids.BolNumber);
        Assert.Null(ids.InvoiceNumber);
    }

    [Theory]
    [InlineData("BOL number: 00123456789012345", "00123456789012345", null)] // purely numeric BOL
    [InlineData("Invoice number: 118734", null, "118734")]                   // purely numeric invoice
    public void Numeric_bol_and_invoice_numbers_are_recognised(string text, string? bol, string? invoice)
    {
        var ids = DocumentIdentifiers.Extract(text);

        Assert.Equal(bol, ids.BolNumber);
        Assert.Equal(invoice, ids.InvoiceNumber);
    }
}
