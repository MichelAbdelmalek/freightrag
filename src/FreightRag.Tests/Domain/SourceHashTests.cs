using System.Text;
using FreightRag.Core.Domain;

namespace FreightRag.Tests.Domain;

public class SourceHashTests
{
    [Fact]
    public void Is_lowercase_hex_sha256_of_the_raw_bytes()
    {
        // Well-known SHA-256 test vector for the ASCII string "abc".
        var hash = SourceHash.FromContent(Encoding.ASCII.GetBytes("abc"));

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash.Value);
    }

    [Fact]
    public void Same_content_gives_equal_hashes_and_different_content_does_not()
    {
        var a = SourceHash.FromContent("RATE"u8);
        var b = SourceHash.FromContent("RATE"u8);
        var c = SourceHash.FromContent("RATE "u8);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")] // upper case
    [InlineData("zz7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")] // not hex
    public void Parse_rejects_anything_but_64_lowercase_hex_chars(string value)
    {
        Assert.Throws<DomainException>(() => SourceHash.Parse(value));
    }
}
