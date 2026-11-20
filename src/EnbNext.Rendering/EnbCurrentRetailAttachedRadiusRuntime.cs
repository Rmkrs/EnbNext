using System.Numerics;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

public readonly record struct EnbCurrentRetailAttachedRadiusNode(
    string Path,
    Vector3 Origin,
    float Radius)
{
    //72CCBF queries the parent's named pivot independently of child placement.
    //Standalone recurrence callers may already supply that point as Origin.
    public Vector3? ParentQueryOrigin { get; init; }
}

/// <summary>
/// Reproduces the radius refresh performed by the current retail Lego
/// attachment controller. Each controller starts with its cbasset Radius + RadiusAdjust
/// and expands to contain every direct child's recursively composed radius,
/// measured from the parent's RenderObject origin to its named-pivot query.
/// </summary>
public static class EnbCurrentRetailAttachedRadiusRuntime
{
    public static bool TryResolve(
        IReadOnlyCollection<EnbCurrentRetailAttachedRadiusNode> nodes,
        out float radius)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        radius = 0f;
        if (nodes.Count == 0)
            return false;

        var byPath = new Dictionary<string, EnbCurrentRetailAttachedRadiusNode>(StringComparer.Ordinal);
        foreach (EnbCurrentRetailAttachedRadiusNode node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Path) ||
                !IsFinite(node.Origin) ||
                node.ParentQueryOrigin is {} query && !IsFinite(query) ||
                !float.IsFinite(node.Radius) ||
                node.Radius < 0f ||
                !byPath.TryAdd(node.Path, node))
            {
                return false;
            }
        }

        if (!byPath.ContainsKey("root"))
            return false;

        var composed = byPath.ToDictionary(static pair => pair.Key, static pair => pair.Value.Radius, StringComparer.Ordinal);
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        foreach (EnbCurrentRetailAttachedRadiusNode child in byPath.Values
                     .Where(static node => !string.Equals(node.Path, "root", StringComparison.Ordinal))
                     .OrderByDescending(static node => PathDepth(node.Path)))
        {
            string? parentPath = ParentPath(child.Path);
            if (parentPath is null || !byPath.TryGetValue(parentPath, out EnbCurrentRetailAttachedRadiusNode parent))
                return false;

            //72CCEF..72CD28: subtract in x87 registers, accumulate Z,Y,X,
            //store squared distance and sqrt before adding the child radius.
            var pivot=child.ParentQueryOrigin??child.Origin;
            R x = (R)parent.Origin.X - pivot.X;
            R y = (R)parent.Origin.Y - pivot.Y;
            R z = (R)parent.Origin.Z - pivot.Z;
            float square = (float)(z * z + y * y + x * x);
            float distance = (float)R.Sqrt((R)square);
            float expanded = (float)((R)composed[child.Path] + distance);
            if (!float.IsFinite(expanded))
                return false;
            composed[parentPath] = MathF.Max(composed[parentPath], expanded);
        }

        radius = composed["root"];
        return float.IsFinite(radius) && radius >= 0f;
    }

    private static string? ParentPath(string path)
    {
        int separator = path.LastIndexOf('/');
        if (separator < 0)
            return null;
        if (separator == path.IndexOf('/'))
            return "root";
        return path[..separator];
    }

    private static int PathDepth(string path)
        => path.Count(static character => character == '/');

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
