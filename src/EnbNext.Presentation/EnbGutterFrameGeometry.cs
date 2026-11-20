using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>
/// Installed-retail geometry for the four-sided cockpit gutter. The source
/// model is authored on Retail's 640x480 HUD design surface; vertices outside
/// that surface are intentionally retained and clipped by the view.
/// </summary>
public sealed record EnbGutterFrameGeometry(
    IReadOnlyList<EnbGutterFrameTriangle> Triangles,
    float DesignInset)
{
    public const float DesignWidth = 640f;
    public const float DesignHeight = 480f;

    private const string ResourceName = "tsi01_gutter.w3d";

    public static bool TryCreate(
        EnbRetailAssetBundle assets,
        out EnbGutterFrameGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(assets);
        geometry = default!;
        EnbMixResourceCatalog? resources = assets.RetailMixResources;
        if (resources is null ||
            !resources.TryReadResource(ResourceName, out byte[] bytes, out _) ||
            !EnbW3dModel.TryDecode(bytes, out EnbW3dModel model))
        {
            return false;
        }

        EnbW3dHierarchy? hierarchy = model.Hierarchies.FirstOrDefault(static candidate =>
            string.Equals(candidate.Name, "TSI01_GUTTER", StringComparison.OrdinalIgnoreCase));
        if (hierarchy is null)
            return false;

        var triangles = new List<EnbGutterFrameTriangle>(8);
        foreach ((string meshName, EnbGutterSide side) in new[]
        {
            ("GUTTER_T", EnbGutterSide.Top),
            ("GUTTER_B", EnbGutterSide.Bottom),
            ("GUTTER_L", EnbGutterSide.Left),
            ("GUTTER_R", EnbGutterSide.Right),
        })
        {
            EnbW3dMesh? mesh = model.Meshes.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, meshName, StringComparison.OrdinalIgnoreCase));
            EnbW3dPivot? pivot = hierarchy.Pivots.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, meshName, StringComparison.OrdinalIgnoreCase));
            if (mesh is null || pivot is null || !HasIdentityRotation(pivot.Rotation))
                return false;

            foreach (EnbW3dTriangle triangle in mesh.Triangles)
            {
                if (triangle.Vertex0 >= mesh.Vertices.Count ||
                    triangle.Vertex1 >= mesh.Vertices.Count ||
                    triangle.Vertex2 >= mesh.Vertices.Count)
                {
                    return false;
                }

                triangles.Add(new EnbGutterFrameTriangle(
                    ToDesignPoint(mesh.Vertices[(int)triangle.Vertex0], pivot.Translation),
                    ToDesignPoint(mesh.Vertices[(int)triangle.Vertex1], pivot.Translation),
                    ToDesignPoint(mesh.Vertices[(int)triangle.Vertex2], pivot.Translation),
                    side));
            }
        }

        if (triangles.Count != 8)
            return false;

        float[] insets =
        [
            triangles.Where(static triangle => triangle.Side == EnbGutterSide.Left)
                .SelectMany(Points).Max(static point => point.X) + DesignWidth * 0.5f,
            DesignWidth * 0.5f - triangles.Where(static triangle => triangle.Side == EnbGutterSide.Right)
                .SelectMany(Points).Min(static point => point.X),
            DesignHeight * 0.5f - triangles.Where(static triangle => triangle.Side == EnbGutterSide.Top)
                .SelectMany(Points).Min(static point => point.Y),
            triangles.Where(static triangle => triangle.Side == EnbGutterSide.Bottom)
                .SelectMany(Points).Max(static point => point.Y) + DesignHeight * 0.5f,
        ];
        if (insets.Any(static inset => !float.IsFinite(inset) || inset <= 0f) ||
            insets.Max() - insets.Min() > 0.001f)
        {
            return false;
        }

        geometry = new EnbGutterFrameGeometry(triangles, insets.Average());
        return true;

        static IEnumerable<Vector2> Points(EnbGutterFrameTriangle triangle)
        {
            yield return triangle.Point0;
            yield return triangle.Point1;
            yield return triangle.Point2;
        }
    }

    private static Vector2 ToDesignPoint(Vector3 vertex, Vector3 translation)
        => new(vertex.X + translation.X, vertex.Y + translation.Y);

    private static bool HasIdentityRotation(EnbW3dRawQuaternion rotation)
        => MathF.Abs(rotation.Q0) <= 1e-5f &&
           MathF.Abs(rotation.Q1) <= 1e-5f &&
           MathF.Abs(rotation.Q2) <= 1e-5f &&
           MathF.Abs(rotation.Q3 - 1f) <= 1e-5f;
}

public readonly record struct EnbGutterFrameTriangle(
    Vector2 Point0,
    Vector2 Point1,
    Vector2 Point2,
    EnbGutterSide Side);

public enum EnbGutterSide
{
    Top,
    Bottom,
    Left,
    Right,
}
