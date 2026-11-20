namespace EnbNext.Rendering;

/// <summary>SHADOW samples and coverage for 0046E120.</summary>
public static class EnbRetailTerrainShadowHeightBuilder
{
    public static (ushort[] Heights, byte[] Coverage) Build(int width, ReadOnlySpan<ushort> heights,
        float xScale, float yScale, float heightScale, bool wrapping, IReadOnlyList<EnbRetailShadowObject> objects,
        CancellationToken cancellation = default)
    {
        int count = checked(width * width);
        if (width <= 0 || heights.Length != count) throw new ArgumentException("Shadow samples differ from the height field.");
        var result = heights.ToArray();
        var coverage = new byte[count];
        cancellation.ThrowIfCancellationRequested();
        EnbRetailTerrainShadowEffects.Apply(new(result, width, xScale, yScale, heightScale, wrapping), objects, coverage, cancellation);
        return (result, coverage);
    }
}
