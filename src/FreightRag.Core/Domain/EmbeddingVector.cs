namespace FreightRag.Core.Domain;

/// <summary>
/// Value object: the embedding of one chunk. The database column is vector(768), so the
/// domain refuses anything else — a model mix-up fails loudly here instead of at INSERT time.
/// </summary>
public sealed class EmbeddingVector
{
    /// <summary>nomic-embed-text produces 768-dimensional vectors.</summary>
    public const int Dimensions = 768;

    private readonly float[] _values;

    public EmbeddingVector(ReadOnlySpan<float> values)
    {
        if (values.Length != Dimensions)
            throw new DomainException(
                $"An embedding must have exactly {Dimensions} dimensions, but this one has {values.Length}.");

        foreach (var v in values)
            if (!float.IsFinite(v))
                throw new DomainException("An embedding cannot contain NaN or infinite values.");

        _values = values.ToArray(); // defensive copy: value objects are immutable
    }

    public ReadOnlyMemory<float> Values => _values;
}
