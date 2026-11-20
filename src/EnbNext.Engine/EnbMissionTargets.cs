using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>Finds the object a mission step names in the current sector (missions carry no coordinates).</summary>
public static class EnbMissionTargets
{
    public readonly record struct Target(string Name, Vector3 Position);

    /// <summary>The longest non-player object name (3+ characters) the step text contains.</summary>
    public static Target? Find(string step, IEnumerable<IEnbRenderObject> objects)
    {
        Target? best = null;
        foreach (var obj in objects)
        {
            if (obj.IsLocalShip || obj.ObjectType == 1 || obj.Spatial?.Transform is not { } pose) continue;
            string? name = EnbCurrentRetailRadarCompatibilityAdapter.ResolveContactName(obj);
            if (name is not { Length: >= 3 } || !step.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || name.Length > best.Value.Name.Length) best = new(name, pose.Position);
        }
        return best;
    }
}
