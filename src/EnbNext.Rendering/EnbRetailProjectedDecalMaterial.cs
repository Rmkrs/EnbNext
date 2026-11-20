using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>007633E0 material construction; 009B6DB0/009B8470 stream format.</summary>
public static class EnbRetailProjectedDecalMaterial
{
    public static EnbW3dRenderMesh Build(EnbRetailDecalGeometry geometry,
        EnbProjectedDecalDefinition definition, string name)
    {
        var white = new EnbRenderPackedRgb8(255,255,255,0); var black = new EnbRenderPackedRgb8(0,0,0,0);
        var material = new EnbW3dRenderVertexMaterial(0, name, 0, white, white, black, black, 0, 1, 0, null, null);
        var lighting = new EnbW3dVertexLightingSemantics(definition.Lighting, EnbW3dMaterialColorSource.Material,
            EnbW3dMaterialColorSource.Material, EnbW3dMaterialColorSource.Material, EnbW3dMaterialColorSource.Material);
        uint shader = definition.Lighting ? 0x984b3u : 0x984b3u & ~0x1c00u;
        if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(shader, material, 1, lighting, out var semantics, out var error))
            throw new InvalidDataException(error);
        var texture = new EnbW3dRenderTextureBinding(0, definition.Texture, null)
        {
            ResolveLogicalNameAtRuntime = true,
            Sampling = EnbW3dTextureSamplingSemantics.RetailDefault with
                { AddressU = EnbRenderTextureAddressMode.Clamp, AddressV = EnbRenderTextureAddressMode.Clamp },
        };
        var stage = new EnbW3dRenderTextureStage(0, [texture], geometry.Uvs, EnbW3dRenderBindingCardinality.Uniform)
            { EffectiveTextureCoordinateSetIndex = 0, EffectiveRuntimeTexCoords = geometry.Uvs };
        var triangleStates = new EnbW3dTriangleBindingPool();
        var bindings = Enumerable.Range(0, geometry.Indices.Length / 3).Select(i => triangleStates.Create(i,
            (int)geometry.Indices[i * 3], null, null, [texture], semantics) with
            { UsesImplicitVertexMaterial = true, UsesImplicitShader = true }).ToArray();
        var pass = new EnbW3dRenderMaterialPass(0, [], EnbW3dRenderBindingCardinality.None, [],
            EnbW3dRenderBindingCardinality.None, [], [], [], [stage], bindings, semantics);
        var min = geometry.Positions.Length == 0 ? Vector3.Zero : geometry.Positions.Aggregate(Vector3.Min);
        var max = geometry.Positions.Length == 0 ? Vector3.Zero : geometry.Positions.Aggregate(Vector3.Max);
        return new(0, name, "damage decal", Vector3.Distance(min, max) * .5f, geometry.Positions,
            geometry.Normals, [], geometry.Indices,
            Enumerable.Range(0, geometry.Indices.Length / 3)
                .Select(_ => new EnbW3dRenderTriangleMetadata(0, Vector3.Zero, 0)).ToArray(), [material], [pass])
        {
            BoundingBoxMin = min, BoundingBoxMax = max, BoundingSphereCenter = (min + max) * .5f,
            EffectiveColor1 = Enumerable.Repeat(new EnbRenderColor32(255,255,255,255), geometry.Positions.Length).ToArray(),
            CurrentRetailDepthBias = 8,
        };
    }
}
