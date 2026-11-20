using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>0098EA70 per-level fields and0098EE30 table arithmetic. Raw
/// signed cost, threshold and bias values retain native predicates; authored
/// admission and concrete child cost dispatch remain separate consumers.</summary>
public static class EnbCurrentRetailHlodDetailRuntime
{
    public readonly record struct Level(float Threshold, float Cost, float DetailCoefficient, float Quality);
    public sealed record Tables(int MinimumLevel, float[] Costs, float[] Priorities);

    public static Level FromCost(float threshold, int cost)
    {
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float storedCost = cost == 0 ? BitConverter.UInt32BitsToSingle(0x358637bd) : (float)cost;
        float quality = cost == 0 ? 0 : (float)((R)1 - (R).5 / unchecked(cost * cost));
        return new(threshold, storedCost, 0, quality);
    }

    public static Tables CreateTables(IReadOnlyList<Level> levels, float detail, float bias)
    {
        // Native count-zero indexes the entry before the output buffer. There
        // is no defined table within this managed buffer for that access.
        if (levels.Count == 0) throw new ArgumentException("HLOD detail tables require a retained level.", nameof(levels));
        float[] costs = new float[levels.Count], priorities = new float[levels.Count + 1];
        int minimum=UpdateTables(levels,detail,bias,costs,priorities);
        return new(minimum,costs,priorities);
    }

    public static int UpdateTables(IReadOnlyList<Level> levels,float detail,float bias,
        Span<float> costs,Span<float> priorities)
    {
        if(levels.Count==0 || costs.Length!=levels.Count || priorities.Length!=levels.Count+1)
            throw new ArgumentException("HLOD owner tables must match the retained levels.");
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        for (int index = 0; index < levels.Count; index++)
            costs[index] = (float)((R)detail * levels[index].DetailCoefficient + levels[index].Cost);
        int minimum = 0;
        while (minimum < levels.Count && (levels[minimum].Threshold < detail ||
            float.IsNaN(levels[minimum].Threshold) || float.IsNaN(detail)))
            priorities[minimum++] = float.MaxValue;
        if (minimum >= levels.Count) minimum = levels.Count - 1;
        priorities[minimum] = float.MaxValue;
        for (int index = minimum + 1; index < levels.Count; index++)
            priorities[index] = (float)((R)levels[index].Quality * bias * detail / costs[index]);
        priorities[levels.Count] = -1;
        return minimum;
    }
}
