using System.Text;
using FreightRag.Core.Domain;

namespace FreightRag.Tests.Domain;

public class FreightDocumentTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097"));
    private const string Text = "RATE CONFIRMATION\nLoad number: 48213\n\nRATE\nLine haul: $2,450.00\n";

    [Fact]
    public void Create_chunks_the_text_and_captures_the_document_facts()
    {
        var doc = Create(Text);

        Assert.NotEqual(Guid.Empty, doc.Id);
        Assert.Equal(Tenant, doc.TenantId);
        Assert.Equal("rate-confirmation-48213", doc.Title);
        Assert.Equal(DocType.RateConfirmation, doc.DocType);
        Assert.Equal(SourceHash.FromContent(Encoding.UTF8.GetBytes(Text)), doc.SourceHash);
        Assert.Equal("48213", doc.Identifiers.LoadNumber);
        Assert.Equal(["RATE CONFIRMATION", "RATE"], doc.Chunks.Select(c => c.SectionName));
    }

    [Fact]
    public void A_document_without_any_content_is_rejected()
    {
        var ex = Assert.Throws<DomainException>(() => Create("\n  \n"));
        Assert.Contains("no content", ex.Message);
    }

    [Fact]
    public void Is_not_ready_to_store_until_every_chunk_has_an_embedding()
    {
        var doc = Create(Text);
        Assert.False(doc.IsReadyToStore);
        Assert.All(doc.Chunks, c => Assert.Null(c.Embedding));

        var vectors = doc.Chunks.Select((_, i) => Vector(i)).ToList();
        doc.AttachEmbeddings(vectors);

        Assert.True(doc.IsReadyToStore);
        // Embeddings are matched to chunks by position.
        Assert.Equal(vectors, doc.Chunks.Select(c => c.Embedding));
    }

    [Fact]
    public void Embedding_count_must_match_chunk_count()
    {
        var doc = Create(Text);

        var ex = Assert.Throws<DomainException>(() => doc.AttachEmbeddings([Vector(0)]));
        Assert.Contains("2 chunks", ex.Message);
        Assert.False(doc.IsReadyToStore);
    }

    [Fact]
    public void Embeddings_can_only_be_attached_once()
    {
        var doc = Create(Text);
        doc.AttachEmbeddings([Vector(0), Vector(1)]);

        Assert.Throws<DomainException>(() => doc.AttachEmbeddings([Vector(2), Vector(3)]));
    }

    private static FreightDocument Create(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return FreightDocument.Create(
            Tenant, "rate-confirmation-48213.txt", DocType.RateConfirmation, SourceHash.FromContent(bytes), text, new SectionChunker());
    }

    private static EmbeddingVector Vector(int seed)
    {
        var values = new float[EmbeddingVector.Dimensions];
        values[0] = seed;
        return new EmbeddingVector(values);
    }
}
