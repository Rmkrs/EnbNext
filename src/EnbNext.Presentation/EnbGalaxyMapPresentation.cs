using EnbNext.Assets;
using EnbNext.Net7.Protocol;

namespace EnbNext.Presentation;

/// <summary>Retail 007A8B70/007AB6D0 admission and 007ABA70 colors.</summary>
public static class EnbGalaxyMapPresentation
{
    public static Net7GalaxySectorRecord[] VisibleSectors(Net7GalaxyMapData map)
    {
        var parents = map.Systems.Select(s => s.SystemHashId).ToHashSet();
        // 007AAF20 has no model/label for a subtype outside these four cases.
        return map.Sectors.Where(s => s.Subtype is 6 or 7 or 8 or 11 && parents.Contains(s.Field2)).ToArray();
    }

    public static EnbRgb SectorColor(Net7GalaxyMapData map, Net7GalaxySectorRecord sector, bool highlighted = false)
    {
        var parent = map.Systems.FirstOrDefault(s => s.SystemHashId == sector.Field2);
        var color = new EnbRgb(.35f, .35f, .35f);
        if (map.IsKnown(sector) && parent is not null)
            color = new(Lighten(parent.VectorA[0]), Lighten(parent.VectorA[1]), Lighten(parent.VectorA[2]));
        return highlighted ? new(1 - color.R, 1 - color.G, 1 - color.B) : color;
    }

    // 007ACEF0: directed edge, destination color; invert for an intra-system link.
    public static EnbRgb LinkColor(Net7GalaxyMapData map, Net7GalaxySectorRecord from, Net7GalaxySectorRecord to)
        => SectorColor(map, to, from.Field2 == to.Field2);

    private static float Lighten(float value) => value + (1 - value) * .2f;
}
