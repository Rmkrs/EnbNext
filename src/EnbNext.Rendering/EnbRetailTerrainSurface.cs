using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public readonly record struct EnbRetailTerrainPatchDraw(int PatchIndex,EnbW3dRenderMesh Mesh,
    Matrix4x4 WorldTransform,EnbW3dWorldBounds WorldBounds);

/// <summary>GPU patch geometry selected by the current-retail terrain update and draw paths.</summary>
public sealed class EnbRetailTerrainSurface
{
    public IReadOnlyList<EnbW3dRenderMesh> Patches { get; }
    public IReadOnlyList<EnbRetailTerrainPatch> CurrentPatches { get; private set; }=[];
    private readonly EnbRetailTerrainPatchState state;
    private readonly EnbRetailTerrainIndices[] indices;
    private readonly Matrix4x4 scale;
    private readonly Dictionary<(int Patch,int Level,EnbTerrainLodNeighborMask Mask),EnbW3dRenderMesh> variants=[];

    public EnbRetailTerrainSurface(EnbRetailTerrain terrain,EnbW3dRenderTextureLocation color,EnbW3dRenderTextureLocation detail)
    {
        Patches=EnbRetailTerrainMesh.Build(terrain,color,detail,true,out var midpoints);
        int cells=terrain.Width/terrain.PatchGridWidth;
        indices=EnbRetailTerrainIndices.Build(cells+1);
        float horizontal=terrain.HorizontalScale/terrain.Width;
        state=new(terrain.PatchGridWidth,cells,terrain.OriginalMaximum,horizontal,horizontal,
            terrain.VerticalScale,terrain.Wrapping,midpoints);
        scale=Matrix4x4.CreateScale(horizontal,horizontal,terrain.VerticalScale);
    }

    public IReadOnlyList<EnbRetailTerrainPatchDraw> Evaluate(EnbResolvedRenderCamera camera,int landLod)
    {
        var updated=state.Update(camera,landLod);
        CurrentPatches=updated;
        var draws=new List<EnbRetailTerrainPatchDraw>();
        // 00474CFB..0047541F visits visible patches in ascending LOD order.
        for(int level=0;level<5;level++)
        for(int i=0;i<updated.Count;i++)
        {
            var patch=updated[i];if(!patch.Visible||patch.Level!=level)continue;
            var key=(i,level,patch.Neighbors);
            if(!variants.TryGetValue(key,out var mesh))
            {
                var source=Patches[i];uint[] triangles=indices[level].Triangles(patch.Neighbors);
                var pass=source.MaterialPasses[0];
                var binding=pass.TriangleBindings[0];
                var bindings=Enumerable.Range(0,triangles.Length/3).Select(n=>binding with
                    {TriangleOrdinal=n,MaterialVertexIndex=(int)triangles[n*3]}).ToArray();
                // The GPU cache keys immutable topology by mesh ordinal. A patch
                // retains its render identity while changing LOD and seam ranges.
                mesh=(source).Copy().SetOrdinal(checked(source.Ordinal*80+level*16+(int)patch.Neighbors)).SetIndices(triangles).SetTriangleMetadata(new EnbW3dRenderTriangleMetadata[bindings.Length]).SetMaterialPasses([(pass).Copy().SetTriangleBindings(bindings)]);
                variants.Add(key,mesh);
            }
            var world=scale*Matrix4x4.CreateTranslation(patch.Translation);
            Vector3 min=Vector3.Transform(mesh.BoundingBoxMin,world),max=Vector3.Transform(mesh.BoundingBoxMax,world);
            draws.Add(new(i,mesh,world,new((min+max)*0.5f,Vector3.Distance(min,max)*0.5f)));
        }
        return draws;
    }
}
