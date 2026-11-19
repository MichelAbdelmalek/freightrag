using FreightRag.Core.Domain;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Domain;

public class SectionChunkerTests
{
    private readonly SectionChunker _chunker = new();

    // ---------- Heading detection ----------

    [Theory]
    [InlineData("RATE")]
    [InlineData("PICKUP")]
    [InlineData("3. DETENTION REPORTING")]
    [InlineData("DELIVERY RECEIPT (PROOF OF DELIVERY)")]
    [InlineData("STRAIGHT BILL OF LADING - NOT NEGOTIABLE")]
    [InlineData("NILE FREIGHT LINES - DRIVER HANDBOOK (EXCERPT, 2026 EDITION)")]
    [InlineData("  SPECIAL INSTRUCTIONS  ")]
    public void Recognises_all_caps_and_numbered_headings(string line)
    {
        Assert.True(SectionChunker.IsHeading(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]                                  // too short
    [InlineData("Load number: 48213")]                 // has lower case
    [InlineData("Detention: $50.00 per hour after 2 free hours")]
    [InlineData("2026-10-14")]                         // no letters
    [InlineData("1. Pick up at gate 3")]               // numbered, but not a caps heading
    [InlineData("SAV-778431")]                         // an identifier code, mostly digits
    [InlineData("NFL-T5530")]                          // an identifier code, mostly digits
    [InlineData("DOUBLE BROKERING OF THIS LOAD IS STRICTLY PROHIBITED.")] // a shouted sentence
    public void Rejects_lines_that_are_not_headings(string line)
    {
        Assert.False(SectionChunker.IsHeading(line));
    }

    [Fact]
    public void Rejects_very_long_caps_lines()
    {
        Assert.False(SectionChunker.IsHeading(new string('A', 81)));
    }

    // ---------- Real documents ----------

    [Fact]
    public void Chunk_containing_Detention_in_rate_confirmation_48213_has_the_load_number_in_its_header()
    {
        // The test the project brief asked for: a retrieved chunk must carry its load number.
        var chunks = ChunkRealFile(TestData.NileFreight, "rate-confirmation-48213.txt");

        var detention = Assert.Single(chunks, c => c.Content.Contains("Detention"));
        var header = detention.Content.Split('\n')[0];
        Assert.Contains("Load: 48213", header);
        Assert.Equal("Document: rate-confirmation | Load: 48213 | Section: RATE", header);
    }

    [Fact]
    public void Rate_confirmation_is_split_into_its_sections_in_order()
    {
        var chunks = ChunkRealFile(TestData.NileFreight, "rate-confirmation-48213.txt");

        Assert.Equal(
            ["RATE CONFIRMATION", "PICKUP", "DELIVERY", "FREIGHT", "RATE", "PAYMENT TERMS", "SPECIAL INSTRUCTIONS"],
            chunks.Select(c => c.SectionName));
    }

    [Fact]
    public void Invoice_header_carries_all_three_identifiers()
    {
        var chunks = ChunkRealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt");

        var charges = Assert.Single(chunks, c => c.SectionName == "CHARGES");
        Assert.StartsWith(
            "Document: invoice | Load: 48213 | BOL: SAV-778431 | Invoice: NFL-2026-1187 | Section: CHARGES\n",
            charges.Content);
    }

    [Fact]
    public void Handbook_uses_numbered_headings_and_has_no_identifier_segment()
    {
        var chunks = ChunkRealFile(TestData.NileFreight, "driver-handbook-excerpt.txt");

        // The title line has no body of its own, so it becomes its own chunk (its text must not be lost).
        Assert.Equal(
            ["NILE FREIGHT LINES - DRIVER HANDBOOK (EXCERPT, 2026 EDITION)", "1. SAFETY EQUIPMENT", "2. HOURS OF SERVICE",
             "3. DETENTION REPORTING", "4. ACCIDENTS AND INCIDENTS", "5. FUEL CARDS"],
            chunks.Select(c => c.SectionName));
        Assert.StartsWith("Document: driver-handbook | Section: 3. DETENTION REPORTING\nDetention is billable", chunks[3].Content);
    }

    public static TheoryData<string, string> AllRealFiles() => new()
    {
        { TestData.NileFreight, "rate-confirmation-48213.txt" },
        { TestData.NileFreight, "bill-of-lading-SAV-778431.txt" },
        { TestData.NileFreight, "invoice-NFL-2026-1187.txt" },
        { TestData.NileFreight, "driver-handbook-excerpt.txt" },
        { TestData.DeltaHaul, "rate-confirmation-77104.txt" },
        { TestData.DeltaHaul, "bill-of-lading-HOU-220957.txt" },
        { TestData.DeltaHaul, "invoice-DH-26-0412.txt" },
    };

    [Theory]
    [MemberData(nameof(AllRealFiles))]
    public void Every_chunk_of_every_real_file_satisfies_the_chunking_rules(string tenant, string file)
    {
        var text = TestData.ReadText(tenant, file);
        Assert.True(DocType.TryFromFileName(file, out var docType));
        var sourceLines = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).ToHashSet();

        var chunks = _chunker.Split(text, docType, DocumentIdentifiers.Extract(text));

        Assert.NotEmpty(chunks);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index)); // contiguous, 0-based
        foreach (var chunk in chunks)
        {
            var lines = chunk.Content.Split('\n');
            Assert.StartsWith($"Document: {docType.Name} | ", lines[0]);
            Assert.EndsWith($"| Section: {chunk.SectionName}", lines[0]);

            var body = lines.Skip(1).ToArray();
            Assert.Contains(body, l => l.Length > 0);
            Assert.True(string.Join('\n', body).Length <= SectionChunker.DefaultMaxBodyChars);
            // Never split mid-line: every body line is a complete line of the source file.
            Assert.All(body.Where(l => l.Length > 0), l => Assert.Contains(l, sourceLines));
        }

        // Nothing is lost: every non-blank source line is in some chunk body or is a section heading.
        var bodyLines = chunks.SelectMany(c => c.Content.Split('\n').Skip(1)).ToHashSet();
        var headings = chunks.Select(c => c.SectionName).ToHashSet();
        var lost = sourceLines.Where(l => l.Trim().Length > 0 && !bodyLines.Contains(l) && !headings.Contains(l.Trim()));
        Assert.Empty(lost);
    }

    // ---------- Size-based splitting (synthetic text) ----------

    [Fact]
    public void Long_section_is_split_on_paragraph_boundaries()
    {
        var p1 = Paragraph('a', 400);
        var p2 = Paragraph('b', 400);
        var p3 = Paragraph('c', 400);
        var text = $"NOTES\n{p1}\n\n{p2}\n\n{p3}\n";

        var chunks = _chunker.Split(text, DocType.DriverHandbook, DocumentIdentifiers.None);

        // p1 + blank line + p2 = 802 chars fits; adding p3 would exceed 1,000.
        Assert.Equal(2, chunks.Count);
        Assert.Equal($"Document: driver-handbook | Section: NOTES\n{p1}\n\n{p2}", chunks[0].Content);
        Assert.Equal($"Document: driver-handbook | Section: NOTES\n{p3}", chunks[1].Content);
    }

    [Fact]
    public void Paragraph_longer_than_the_limit_is_split_on_line_boundaries()
    {
        var lines = Enumerable.Range(0, 5).Select(i => Paragraph((char)('a' + i), 300)).ToArray();
        var text = "NOTES\n" + string.Join('\n', lines);

        var chunks = _chunker.Split(text, DocType.DriverHandbook, DocumentIdentifiers.None);

        // 3 lines = 902 chars fits; a 4th would make 1,203.
        Assert.Equal(2, chunks.Count);
        Assert.EndsWith("\n" + string.Join('\n', lines[..3]), chunks[0].Content);
        Assert.EndsWith("\n" + string.Join('\n', lines[3..]), chunks[1].Content);
    }

    [Fact]
    public void A_single_line_longer_than_the_limit_is_kept_whole()
    {
        var longLine = Paragraph('x', 1500);

        var chunks = _chunker.Split($"NOTES\n{longLine}", DocType.DriverHandbook, DocumentIdentifiers.None);

        var chunk = Assert.Single(chunks);
        Assert.EndsWith("\n" + longLine, chunk.Content);
    }

    [Fact]
    public void Text_before_the_first_heading_goes_into_an_untitled_section()
    {
        var chunks = _chunker.Split("Some preamble.\nRATE\nLine haul: $1", DocType.Invoice, DocumentIdentifiers.None);

        Assert.Equal(["(untitled)", "RATE"], chunks.Select(c => c.SectionName));
    }

    [Fact]
    public void Surrounding_blank_lines_are_dropped()
    {
        var chunks = _chunker.Split("\n\nRATE\n\nLine haul: $1\n\n\n", DocType.Invoice, DocumentIdentifiers.None);

        var chunk = Assert.Single(chunks);
        Assert.Equal("Document: invoice | Section: RATE\nLine haul: $1", chunk.Content);
    }

    [Fact]
    public void A_heading_without_a_body_keeps_its_text_as_the_body()
    {
        var chunks = _chunker.Split("TITLE PAGE\n\nRATE\nLine haul: $1", DocType.Invoice, DocumentIdentifiers.None);

        Assert.Equal("Document: invoice | Section: TITLE PAGE\nTITLE PAGE", chunks[0].Content);
        Assert.Equal("Document: invoice | Section: RATE\nLine haul: $1", chunks[1].Content);
    }

    [Fact]
    public void In_a_run_of_all_caps_lines_only_the_first_is_a_heading()
    {
        // Typical upper-case TMS output: the shipper name and address must stay in the SHIPPER chunk.
        var text = "SHIPPER\nBAYOU FROZEN FOODS\n1500 CLINTON DR\nHOUSTON, TX 77020\n\nCONSIGNEE\nPeachtree DC";

        var chunks = _chunker.Split(text, DocType.BillOfLading, DocumentIdentifiers.None);

        Assert.Equal(["SHIPPER", "CONSIGNEE"], chunks.Select(c => c.SectionName));
        Assert.EndsWith("Section: SHIPPER\nBAYOU FROZEN FOODS\n1500 CLINTON DR\nHOUSTON, TX 77020", chunks[0].Content);
    }

    [Fact]
    public void A_heading_directly_after_a_normal_line_still_starts_a_section()
    {
        var chunks = _chunker.Split("RATE\nLine haul: $1\nPICKUP\nLocation: Savannah", DocType.RateConfirmation, DocumentIdentifiers.None);

        Assert.Equal(["RATE", "PICKUP"], chunks.Select(c => c.SectionName));
    }

    [Fact]
    public void Windows_line_endings_are_normalised()
    {
        var chunks = _chunker.Split("RATE\r\nLine haul: $1\r\nFuel: $2\r\n", DocType.Invoice, DocumentIdentifiers.None);

        Assert.Equal("Document: invoice | Section: RATE\nLine haul: $1\nFuel: $2", Assert.Single(chunks).Content);
    }

    [Fact]
    public void Empty_document_produces_no_chunks()
    {
        Assert.Empty(_chunker.Split("  \n\n", DocType.Invoice, DocumentIdentifiers.None));
    }

    private IReadOnlyList<DocumentChunk> ChunkRealFile(string tenant, string file)
    {
        var text = TestData.ReadText(tenant, file);
        Assert.True(DocType.TryFromFileName(file, out var docType));
        return _chunker.Split(text, docType, DocumentIdentifiers.Extract(text));
    }

    /// <summary>Lower-case filler so the line can never look like a heading.</summary>
    private static string Paragraph(char c, int length) => new(c, length);
}
