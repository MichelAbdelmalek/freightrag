using FreightRag.Core.Domain;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Domain;

public class StoredDocumentTextTests
{
    public static TheoryData<string, string, int> RealFiles()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var tenant in Directory.GetDirectories(Path.Combine(AppContext.BaseDirectory, "data")).Order())
            foreach (var file in Directory.GetFiles(tenant, "*.txt").Order())
                foreach (var max in new[] { SectionChunker.DefaultMaxBodyChars, 60 })   // 60 forces many split bodies
                    data.Add(Path.GetFileName(tenant), Path.GetFileName(file), max);
        return data;
    }

    [Theory]
    [MemberData(nameof(RealFiles))]
    public void Fields_read_from_the_stored_chunks_equal_fields_read_from_the_original(string tenant, string file, int maxBody)
    {
        var text = TestData.ReadText(tenant, file);
        Assert.True(DocType.TryFromFileName(file, out var type));
        var chunks = new SectionChunker(maxBody).Split(text, type!, DocumentIdentifiers.Extract(text));

        var fromChunks = StoredDocumentText.Reassemble(chunks.Select(c => c.Content));

        FieldSelection.TryParse(null, type!, out var all, out _);
        Assert.Equal(DocumentFieldExtractor.Extract(text, all!).Items, DocumentFieldExtractor.Extract(fromChunks, all!).Items);
    }

    [Fact]
    public void A_heading_only_title_section_is_not_duplicated()
    {
        var text = "INVOICE\n\nFrom: Acme Co., 1 Main St\nLoad number: 5";
        var chunks = new SectionChunker().Split(text, DocType.Invoice, DocumentIdentifiers.Extract(text));

        var back = StoredDocumentText.Reassemble(chunks.Select(c => c.Content));

        Assert.Equal(1, back.Split('\n').Count(l => l == "INVOICE"));
    }

    [Fact]
    public void Untitled_text_comes_back_without_a_heading() =>
        Assert.Equal("first line", StoredDocumentText.Reassemble(["Document: invoice | Section: (untitled)\nfirst line"]));
}
