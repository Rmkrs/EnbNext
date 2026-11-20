namespace EnbNext.Rendering;

/// <summary>Aggregate RenderObj +80 -> +B0: 00941F60 / 009C58C0 / 0098A000.
/// The first matching pivot wins; a missing name returns index zero.</summary>
public static class EnbCurrentRetailAggregateBoneLookup
{
    public static int Resolve(EnbW3dRenderHierarchy hierarchy, string name)
    {
        for (int index = 0; index < hierarchy.Pivots.Count; index++)
            if (string.Equals(hierarchy.Pivots[index].Name, name, StringComparison.OrdinalIgnoreCase))
                return index;
        return 0;
    }

    /// <summary>The same lookup, refusing only a hierarchy without pivots, whose
    /// bone 0 would be an undefined native read.</summary>
    public static bool TryResolve(EnbW3dRenderHierarchy hierarchy, string name, out int index)
    {
        index = Resolve(hierarchy, name);
        return hierarchy.Pivots.Count != 0;
    }
}
