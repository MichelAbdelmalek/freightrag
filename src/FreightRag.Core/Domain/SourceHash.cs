using System.Security.Cryptography;

namespace FreightRag.Core.Domain;

/// <summary>
/// Value object: SHA-256 of a document's raw bytes, as 64 lowercase hex characters.
/// Together with the tenant it identifies a document, which makes ingestion idempotent:
/// the same file ingested twice for the same tenant is detected and skipped.
/// </summary>
public sealed record SourceHash
{
    private SourceHash(string value) => Value = value;

    public string Value { get; }

    /// <summary>Hashes the exact bytes of the file (no newline or encoding normalisation).</summary>
    public static SourceHash FromContent(ReadOnlySpan<byte> content) =>
        new(Convert.ToHexStringLower(SHA256.HashData(content)));

    /// <summary>Rehydrates a hash that was stored earlier, validating its format.</summary>
    public static SourceHash Parse(string value)
    {
        var isValid = value.Length == 64 && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
        if (!isValid)
            throw new DomainException("A source hash must be 64 lowercase hexadecimal characters.");
        return new SourceHash(value);
    }

    public override string ToString() => Value;
}
