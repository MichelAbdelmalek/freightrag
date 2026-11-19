using FreightRag.Core.Domain;
using Pgvector;

namespace FreightRag.IntegrationTests;

/// <summary>
/// Hand-made 768-dimension vectors. With cosine distance only the direction matters, so vectors are built from
/// unit axes: the query points along axis 0; "near" chunks are mostly axis 0 with a little of another axis;
/// "far" chunks are mostly another axis.
/// </summary>
internal static class Vectors
{
    public const int Dimensions = EmbeddingVector.Dimensions;

    /// <summary>The query direction.</summary>
    public static float[] Query() => Axis0(1f);

    /// <summary>Close to the query: axis 0 plus a small tilt towards <paramref name="axis"/>.</summary>
    public static float[] Near(int axis, float tilt)
    {
        var v = Axis0(1f);
        v[1 + axis % (Dimensions - 1)] = tilt;
        return v;
    }

    /// <summary>Far from the query: mostly <paramref name="axis"/>, a little of axis 0.</summary>
    public static float[] Far(int axis)
    {
        var v = Axis0(0.2f);
        v[1 + axis % (Dimensions - 1)] = 1f;
        return v;
    }

    /// <summary>
    /// Realistic, dense vectors for HNSW tests: the query direction plus a random unit "noise" direction, scaled.
    /// Seeded, so every run builds the same data. (Sparse single-axis vectors give HNSW a degenerate graph in
    /// which some points are unreachable — real embeddings are dense, so tests use dense vectors too.)
    /// </summary>
    public static float[] AroundQuery(Random random, float queryWeight, float noiseWeight)
    {
        var noise = new double[Dimensions];
        double norm = 0;
        for (var i = 0; i < Dimensions; i++)
        {
            // Box–Muller: a standard normal sample from two uniform ones.
            var gaussian = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            noise[i] = gaussian;
            norm += gaussian * gaussian;
        }
        norm = Math.Sqrt(norm);

        var v = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
            v[i] = (float)(noiseWeight * noise[i] / norm);
        v[0] += queryWeight;
        return v;
    }

    /// <summary>Very close to the query (cosine distance ≈ 0.001).</summary>
    public static IEnumerable<float[]> Crowd(int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => AroundQuery(random, 1f, 0.05f)).ToList();
    }

    /// <summary>Clearly further away (cosine distance ≈ 0.7), but still "the best this tenant has".</summary>
    public static IEnumerable<float[]> Distant(int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => AroundQuery(random, 0.3f, 1f)).ToList();
    }

    public static Vector ToVector(float[] values) => new(values);

    private static float[] Axis0(float value)
    {
        var v = new float[Dimensions];
        v[0] = value;
        return v;
    }
}
