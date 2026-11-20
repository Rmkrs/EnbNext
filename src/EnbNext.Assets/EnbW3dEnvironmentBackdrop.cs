using System.Numerics;

namespace EnbNext.Assets;

/// <summary>
/// Bounded authored environment-backdrop view over the current W3D model.
/// Intentionally preserves the selected sphere's triangles, both runtime
/// texture-coordinate streams and per-vertex diffuse gradient instead of
/// reconstructing those values analytically from direction.
/// </summary>
public sealed class EnbW3dEnvironmentBackdrop
{
    private EnbW3dEnvironmentBackdrop(
        EnbW3dMesh mesh,
        EnbW3dMaterialPass pass,
        EnbW3dTextureStage stage0,
        EnbW3dTextureStage stage1,
        string stage0TextureName,
        string stage1TextureName,
        IReadOnlyList<float> vertexIntensity)
    {
        Mesh = mesh;
        Pass = pass;
        Stage0 = stage0;
        Stage1 = stage1;
        Stage0TextureName = stage0TextureName;
        Stage1TextureName = stage1TextureName;
        VertexIntensity = vertexIntensity;
    }

    public EnbW3dMesh Mesh { get; }
    public EnbW3dMaterialPass Pass { get; }
    public EnbW3dTextureStage Stage0 { get; }
    public EnbW3dTextureStage Stage1 { get; }
    public string Stage0TextureName { get; }
    public string Stage1TextureName { get; }
    public IReadOnlyList<float> VertexIntensity { get; }
    // Bounded ordinary HLOD: opaque sphere, then same-radius additive members.
    // The original single-sphere and specialized influence paths remain distinct.
    public IReadOnlyList<EnbW3dEnvironmentBackdrop> AdditiveLayers { get; private init; } = [];
    public bool IsLayered => AdditiveLayers.Count != 0;

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out EnbW3dEnvironmentBackdrop backdrop)
    {
        backdrop = default!;
        if (!EnbW3dModel.TryDecode(bytes, out EnbW3dModel model))
            return false;

        EnbW3dEnvironmentBackdrop? candidate = null;
        foreach (EnbW3dMesh mesh in model.Meshes)
        {
            if (!TryBuildCandidate(mesh, out EnbW3dEnvironmentBackdrop current))
                continue;

            // The selected current-retail environment HLOD resolves to one
            // authored backdrop mesh. Ambiguity is fail-closed rather than
            // inventing a mesh-selection rule outside the evidence.
            if (candidate is not null)
                return false;
            candidate = current;
        }

        if (candidate is null)
            return TryBuildLayered(model, out backdrop);

        backdrop = candidate;
        return true;
    }

    private static bool TryBuildLayered(EnbW3dModel model, out EnbW3dEnvironmentBackdrop backdrop)
    {
        backdrop = default!;
        if (model.HierarchicalLods.Count != 1 || model.Hierarchies.Count != 1 ||
            model.Meshes.Count != 3 || model.HierarchyModels.Count != 0 || model.Animations.Count != 0)
            return false;
        var hlod = model.HierarchicalLods[0];
        var hierarchy = model.Hierarchies[0];
        if (hlod.Levels.Count != 1 || hlod.Aggregate is not null || hlod.ProxyArray is not null ||
            !hlod.HierarchyName.Equals(hierarchy.Name, StringComparison.OrdinalIgnoreCase) ||
            hierarchy.Version < 0x30000 || hierarchy.Center != Vector3.Zero ||
            hlod.Levels[0].Members.Count != 3) return false;
        var selected = new List<EnbW3dMesh>();
        foreach (var member in hlod.Levels[0].Members)
        {
            var matches = model.Meshes.Where(m => $"{m.ContainerName}.{m.Name}".Equals(member.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || selected.Contains(matches[0]) || member.ReferenceIndex >= hierarchy.Pivots.Count)
                return false;
            // No animation or inferred attachment transforms in this bounded form.
            uint pivot = member.ReferenceIndex;
            int count = 0;
            while (pivot != uint.MaxValue)
            {
                if (pivot >= hierarchy.Pivots.Count || ++count > hierarchy.Pivots.Count) return false;
                var p = hierarchy.Pivots[(int)pivot];
                if (p.Translation != Vector3.Zero || p.Rotation != new EnbW3dRawQuaternion(0, 0, 0, 1)) return false;
                pivot = p.ParentIndex;
            }
            selected.Add(matches[0]);
        }
        var spheres = selected.Where(m => m.Name.Equals("SPHERE01", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (spheres.Length != 1 || !TryBuildCandidate(spheres[0], out var sphere, layered: true)) return false;
        var additions = new List<EnbW3dEnvironmentBackdrop>();
        foreach (var mesh in selected.Where(m => !ReferenceEquals(m, spheres[0])))
        {
            if (!TryBuildCandidate(mesh, out var layer, layered: true, sphereRadius: sphere.Mesh.BoundingRadius) ||
                !layer.Stage0TextureName.Equals(sphere.Stage0TextureName, StringComparison.OrdinalIgnoreCase) ||
                !layer.Stage1TextureName.Equals(sphere.Stage1TextureName, StringComparison.OrdinalIgnoreCase)) return false;
            additions.Add(layer);
        }
        // Shader classifier/: sphere is immediate; additive members are bucket
        // 10 FIFO. Member order is preserved within that bucket.
        backdrop = new(sphere.Mesh, sphere.Pass, sphere.Stage0, sphere.Stage1,
            sphere.Stage0TextureName, sphere.Stage1TextureName, sphere.VertexIntensity)
            { AdditiveLayers = additions.ToArray() };
        return true;
    }

    private static bool TryBuildCandidate(EnbW3dMesh mesh, out EnbW3dEnvironmentBackdrop backdrop,
        bool layered = false, float? sphereRadius = null)
    {
        backdrop = default!;
        if ((!sphereRadius.HasValue && !string.Equals(mesh.Name, "SPHERE01", StringComparison.OrdinalIgnoreCase)) ||
            mesh.MaterialInfo is not EnbW3dMaterialInfo materialInfo ||
            materialInfo.PassCount != 1 ||
            materialInfo.VertexMaterialCount != 1 ||
            materialInfo.ShaderCount != 1 ||
            materialInfo.TextureCount != 2 ||
            mesh.Vertices.Count == 0 ||
            mesh.Triangles.Count == 0 ||
            mesh.MaterialPasses.Count != 1 ||
            mesh.VertexMaterials.Count != 1 ||
            mesh.Shaders.Count != 1 ||
            mesh.TextureNames.Count != 2 ||
            mesh.TextureInfos.Count != 2 ||
            !ValidateCameraCenteredSphere(mesh, sphereRadius) ||
            (layered && mesh.AuthoredSortLevel != 0))
        {
            return false;
        }

        EnbW3dMaterialPass pass = mesh.MaterialPasses[0];
        if (!TryResolveUniformZero(pass.VertexMaterialIds) ||
            !TryResolveUniformZero(pass.ShaderIds) ||
            pass.DiffuseColors.Count != mesh.Vertices.Count ||
            pass.DiffuseIllumination.Count != 0 ||
            pass.SpecularColors.Count != 0 ||
            pass.TextureStages.Count != 2)
        {
            return false;
        }

        EnbW3dTextureStage? stage0 = pass.TextureStages.FirstOrDefault(static stage => stage.Ordinal == 0);
        EnbW3dTextureStage? stage1 = pass.TextureStages.FirstOrDefault(static stage => stage.Ordinal == 1);
        if (stage0 is null || stage1 is null ||
            !stage0.TryResolveSingleTextureName(mesh.TextureNames, out string stage0TextureName) ||
            !stage1.TryResolveSingleTextureName(mesh.TextureNames, out string stage1TextureName) ||
            stage0.RuntimeTexCoords.Count != mesh.Vertices.Count ||
            stage1.RuntimeTexCoords.Count != mesh.Vertices.Count ||
            !ValidateTextureInfo(mesh.TextureInfos[0]) ||
            !ValidateTextureInfo(mesh.TextureInfos[1]) ||
            !ValidateMaterial(mesh.VertexMaterials[0], layered) ||
            !ValidateShaderRecord(mesh.Shaders[0], layered, sphereRadius.HasValue))
        {
            return false;
        }

        float[] intensity = new float[pass.DiffuseColors.Count];
        for (int index = 0; index < pass.DiffuseColors.Count; index++)
        {
            EnbColor32 color = pass.DiffuseColors[index];
            if (color.R != color.G || color.R != color.B || color.A != byte.MaxValue)
                return false;
            intensity[index] = color.R / 255.0f;
        }

        backdrop = new EnbW3dEnvironmentBackdrop(
            mesh,
            pass,
            stage0,
            stage1,
            stage0TextureName,
            stage1TextureName,
            intensity);
        return true;
    }

    private static bool ValidateCameraCenteredSphere(EnbW3dMesh mesh, float? sphereRadius = null)
    {
        float expectedRadius = sphereRadius ?? mesh.BoundingRadius;
        Vector3 center = sphereRadius.HasValue ? Vector3.Zero : mesh.BoundingSphereCenter;
        if (!float.IsFinite(expectedRadius) || expectedRadius <= 0f ||
            !IsFinite(mesh.BoundingSphereCenter) ||
            center.Length() > MathF.Max(expectedRadius * 1e-4f, 1e-3f))
        {
            return false;
        }

        float radiusTolerance = MathF.Max(expectedRadius * 2e-5f, 1e-3f);
        foreach (Vector3 vertex in mesh.Vertices)
        {
            if (!IsFinite(vertex))
                return false;
            float radius = Vector3.Distance(vertex, center);
            if (!float.IsFinite(radius) || MathF.Abs(radius - expectedRadius) > radiusTolerance)
                return false;
        }

        foreach (EnbW3dTriangle triangle in mesh.Triangles)
        {
            int i0 = checked((int)triangle.Vertex0);
            int i1 = checked((int)triangle.Vertex1);
            int i2 = checked((int)triangle.Vertex2);
            Vector3 a = mesh.Vertices[i0];
            Vector3 b = mesh.Vertices[i1];
            Vector3 c = mesh.Vertices[i2];
            Vector3 face = Vector3.Cross(b - a, c - a);
            float faceLengthSquared = face.LengthSquared();
            if (!float.IsFinite(faceLengthSquared) || faceLengthSquared <= 1e-8f)
                return false;

            Vector3 centroid = ((a + b + c) / 3f) - center;
            // BACK_blue is authored for an observer inside the sphere. The
            // triangle winding therefore points toward the sphere centre.
            if (Vector3.Dot(face, centroid) >= 0f)
                return false;
        }

        return true;
    }

    private static bool ValidateTextureInfo(EnbW3dTextureInfo? info)
        => info is EnbW3dTextureInfo value &&
           value.Attributes == 0x0000_0004u &&
           value.Word1 == 1u &&
           value.Word2 == 0x4170_0000u;

    private static bool ValidateMaterial(EnbW3dVertexMaterial material, bool layered)
    {
        EnbW3dVertexMaterialInfo info = material.Info;
        return info.Attributes == (layered ? 0x600u : 0u) &&
               IsBlack(info.Ambient) &&
               IsBlack(info.Diffuse) &&
               IsBlack(info.Specular) &&
               IsWhite(info.Emissive) &&
               info.Power == 1.0f &&
               info.Opacity == 1.0f &&
               info.UnknownFloat == 0.0f &&
               info.Mapper0TypeSelector == 0 &&
               info.Mapper1TypeSelector == (layered ? 6 : 0) &&
               material.Mapper0 is null &&
               (layered ? material.Mapper1 is {} mapper && float.IsFinite(mapper.UScale) && float.IsFinite(mapper.VScale)
                   : material.Mapper1 is null);
    }

    private static bool ValidateShaderRecord(EnbW3dShader shader, bool layered, bool additive)
    {
        ReadOnlySpan<byte> raw = shader.Raw.Span;
        ReadOnlySpan<byte> selectedCurrentRetail =
        [
            0x07, 0x00, 0x00, 0x00,
            0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02,
            0x00, 0x02, 0x00, 0x02,
        ];
        if (!layered) return raw.SequenceEqual(selectedCurrentRetail);
        Span<byte> expected = stackalloc byte[16];
        selectedCurrentRetail.CopyTo(expected);
        expected[additive ? 3 : 1] = 1; // additive destination ONE, or sphere Z-write
        return raw.SequenceEqual(expected);
    }

    private static bool TryResolveUniformZero(IReadOnlyList<uint> ids)
        => ids.Count != 0 && ids.All(static id => id == 0u);

    private static bool IsBlack(EnbW3dPackedRgb color)
        => color.R == 0 && color.G == 0 && color.B == 0;

    private static bool IsWhite(EnbW3dPackedRgb color)
        => color.R == byte.MaxValue && color.G == byte.MaxValue && color.B == byte.MaxValue;

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
