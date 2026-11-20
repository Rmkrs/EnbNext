using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>GPU mesh input for the retail height field (00474790, 00478240, 00474B30).</summary>
public static class EnbRetailTerrainMesh
{
    public static IReadOnlyList<EnbW3dRenderMesh> Build(EnbRetailTerrain terrain,
        EnbW3dRenderTextureLocation color, EnbW3dRenderTextureLocation detail)
        => Build(terrain,color,detail,false,out _);

    internal static IReadOnlyList<EnbW3dRenderMesh> Build(EnbRetailTerrain terrain,
        EnbW3dRenderTextureLocation color,EnbW3dRenderTextureLocation detail,bool localCoordinates,out float[] midpoints)
    {
        var white = new EnbRenderPackedRgb8(255, 255, 255, 0);
        var material = new EnbW3dRenderVertexMaterial(0, "terrain", 0,
            white, white, white, new(0, 0, 0, 0), 1, 1, 0, null, null);
        var lighting = new EnbW3dVertexLightingSemantics(false,
            EnbW3dMaterialColorSource.Material, EnbW3dMaterialColorSource.Material,
            EnbW3dMaterialColorSource.Material, EnbW3dMaterialColorSource.Material);
        // Retail's ordinary two-texture terrain state. Terrain is prelit, with scene fog.
        if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(0x29411b, material, 2, lighting,
                out var semantics, out var diagnostic))
            throw new InvalidDataException(diagnostic);
        var textures = new[] {
            new EnbW3dRenderTextureBinding(0, terrain.ColorMap, color),
            new EnbW3dRenderTextureBinding(1, terrain.DetailMap, detail) };
        var meshes = new List<EnbW3dRenderMesh>();
        int cells = terrain.Width / terrain.PatchGridWidth, side = cells + 1;
        midpoints=new float[terrain.PatchGridWidth*terrain.PatchGridWidth];
        uint[] indices=EnbRetailTerrainIndices.Build(side)[0].Triangles(EnbTerrainLodNeighborMask.None);
        for (int py = 0; py < terrain.PatchGridWidth; py++)
        for (int px = 0; px < terrain.PatchGridWidth; px++)
        {
            var vertices=EnbRetailTerrainVertices.Build(side,px*cells,py*cells,terrain.Width,
                terrain.Heights,terrain.VerticalScale,terrain.DetailScale);
            midpoints[py*terrain.PatchGridWidth+px]=vertices.Midpoint;
            var positions = vertices.Positions;
            var normals = new Vector3[positions.Length];
            var uv0 = vertices.ColorCoordinates;
            var uv1 = vertices.DetailCoordinates;
            float scale=terrain.HorizontalScale/terrain.Width;
            var world=Matrix4x4.CreateScale(scale,scale,terrain.VerticalScale)*
                Matrix4x4.CreateTranslation((px-terrain.PatchGridWidth/2)*cells*scale,
                    (py-terrain.PatchGridWidth/2)*cells*scale,0);
            Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int i = y * side + x;
                if(!localCoordinates)positions[i]=Vector3.Transform(positions[i],world);
                normals[i] = Vector3.UnitZ;
                min = Vector3.Min(min, positions[i]); max = Vector3.Max(max, positions[i]);
                if(terrain.FlipColorMap)uv0[i]=new(uv0[i].X,1-uv0[i].Y);
            }
            var stages = new[] {
                new EnbW3dRenderTextureStage(0, [textures[0]], uv0, EnbW3dRenderBindingCardinality.Uniform)
                    { EffectiveTextureCoordinateSetIndex = 0, EffectiveRuntimeTexCoords = uv0 },
                new EnbW3dRenderTextureStage(1, [textures[1]], uv1, EnbW3dRenderBindingCardinality.Uniform)
                    { EffectiveTextureCoordinateSetIndex = 1, EffectiveRuntimeTexCoords = uv1 } };
            var triangleStates = new EnbW3dTriangleBindingPool();
            var bindings = Enumerable.Range(0, indices.Length / 3).Select(i =>
                triangleStates.Create(i, checked((int)indices[i * 3]), null, null, textures, semantics) with
                    { UsesImplicitVertexMaterial = true, UsesImplicitShader = true }).ToArray();
            var pass = new EnbW3dRenderMaterialPass(0, [], EnbW3dRenderBindingCardinality.None,
                [], EnbW3dRenderBindingCardinality.None, [], [], [], stages, bindings, semantics);
            Vector3 center = (min + max) / 2;
            meshes.Add(new(meshes.Count, $"terrain.{px}.{py}", $"land{terrain.Sector}",
                Vector3.Distance(min, max) / 2, positions, normals, [], indices,
                new EnbW3dRenderTriangleMetadata[indices.Length / 3], [material], [pass])
                { BoundingBoxMin = min, BoundingBoxMax = max, BoundingSphereCenter = center });
        }
        return meshes;
    }
}
