namespace EnbNext.Rendering;

/// <summary>
/// HLOD render 0098DDB1: when the rendering parent has flag 0x10000
/// (ForceSubObjectLod), each aggregate child receives vt+0x120 (Set_LOD_Level)
/// with the parent's vt+0x124 (Get_LOD_Level), clamped by 0098ED30.
/// </summary>
public static class EnbCurrentRetailAggregateLevelRuntime
{
    public static int? ResolvePushedLevel(bool forceSubObjectLod, bool parentRenders, int parentLevel, int childLevelCount)
    {
        if (!forceSubObjectLod || !parentRenders || childLevelCount <= 0) return null;
        return Math.Min(Math.Max(parentLevel, 0), childLevelCount - 1);
    }
}
