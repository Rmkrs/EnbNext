using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public static class EnbRetailTerrainDecorationMesh
{
    public static IReadOnlyList<EnbW3dRenderMesh> Build(EnbRetailTerrain terrain,
        EnbRetailTerrainDecorations decorations,IReadOnlyList<EnbRetailTerrainDecorationPrototype> prototypes,
        out int[] patchIndices)
    {
        var result=new List<EnbW3dRenderMesh>();var batches=new List<int>();
        var regular=prototypes.FirstOrDefault(p=>p.Decoration is not null);
        var shadowPrototype=prototypes.FirstOrDefault(p=>p.Shadow is not null);
        var regularMaterial=regular.DecorationMaterial;var shadowMaterial=shadowPrototype.ShadowMaterial;
        // 0047A2F0 compares every prototype texture, shader and vertex-material
        // checksum with the first and returns without decoration buffers on a
        // mismatch; LiveWorldTerrain keeps the terrain and publishes none.
        foreach(var prototype in prototypes)
        {
            if(prototype.Decoration is not null&&(prototype.DecorationMaterial is not {} material||regularMaterial is null||
                !material.Matches(regularMaterial,regularMaterial.TextureIdentity)))
                throw new InvalidDataException("Decoration prototypes have different regular texture/shader/material state.");
            if(prototype.Shadow is not null&&(prototype.ShadowMaterial is not {} shadow||shadowMaterial is null||
                !shadow.Matches(shadowMaterial,regularMaterial?.TextureIdentity)))
                throw new InvalidDataException("Decoration prototypes have different shadow texture/shader/material state.");
        }
        var sampler=EnbRetailTerrainSampler.For(terrain);
        var groups=decorations.Placements.GroupBy(p=>sampler.DecorationPatch(p.Position,terrain.PatchGridWidth)).OrderBy(p=>p.Key).ToArray();
        // 474B30: regular buffers, then shadow buffers, ascending patch order.
        foreach(bool shadow in new[]{false,true})
        {
            var material=shadow?shadowMaterial:regularMaterial;
            var template=shadow?shadowPrototype.Shadow:regular.Decoration;
            if(material is null||template is null)continue;
            foreach(var group in groups)
            {
                var positions=new List<Vector3>();var normals=new List<Vector3>();var coordinates=new List<Vector2>();var indices=new List<uint>();
                ushort firstVertex=0;
                foreach(var placement in group)
                {
                    var source=shadow?prototypes[placement.Prototype].Shadow:prototypes[placement.Prototype].Decoration;
                    if(source is null)continue;
                    var sampled=sampler.Sample(placement.Position);
                    var matrix=placement.GeneratedTransform??EnbRetailTerrainDecorationGeometry.Rotation(placement.Rotation);
                    if(placement.GeneratedTransform is null)matrix.Translation=new(placement.Position,sampled.Height);
                    if(shadow)matrix=EnbRetailTerrainDecorationGeometry.ShadowTransform(matrix,sampled.Normal);
                    // 47C6A0 selects the descriptor primary UV array before material coordinate remapping.
                    int coordinateIndex=source.MaterialPasses[0].TextureStages.FirstOrDefault()?.RetailDescriptorCoordinateSetIndex??-1;
                    var uv=coordinateIndex<0?[]:source.RetailTextureCoordinateSets.Single(set=>set.Index==coordinateIndex).Coordinates.ToArray();
                    var baked=EnbRetailTerrainDecorationGeometry.Bake(source.Positions.ToArray(),source.Normals.ToArray(),uv,
                        source.Indices.Select(i=>checked((ushort)i)).ToArray(),matrix,placement.Scale,firstVertex);
                    positions.AddRange(baked.Positions);normals.AddRange(baked.Normals);coordinates.AddRange(baked.TextureCoordinates);
                    indices.AddRange(baked.Indices.Select(i=>(uint)i));
                    firstVertex=unchecked((ushort)(firstVertex+baked.MaximumIndex+1));
                }
                if(positions.Count==0)continue;
                var texture=regularMaterial?.Texture;
                var stage=new EnbW3dRenderTextureStage(0,texture is null?[]:[texture],coordinates,EnbW3dRenderBindingCardinality.Uniform)
                    {EffectiveRuntimeTexCoords=coordinates,RetailDescriptorCoordinateSetIndex=0,
                        EffectiveTextureCoordinateSetIndex=material.Material.CurrentRetailRuntimeCoordinates!.Value.Stage0,CurrentRetailUniformTexture=texture};
                var triangleStates = new EnbW3dTriangleBindingPool();
                var triangles=Enumerable.Range(0,indices.Count/3).Select(i=>triangleStates.Create(i,(int)indices[i*3],material.Material,null,
                    [texture],material.Semantics) with{UsesImplicitShader=true}).ToArray();
                var pass=new EnbW3dRenderMaterialPass(0,[material.Material],EnbW3dRenderBindingCardinality.Uniform,[],
                    EnbW3dRenderBindingCardinality.None,[],[],[],[stage],triangles,material.Semantics)
                    {CurrentRetailUniformMaterial=material.Material,CurrentRetailUniformLighting=material.Semantics.VertexLighting,
                        CurrentRetailUniformShader=material.Semantics.FixedFunction.PackedRetailState};
                Vector3 min=positions.Aggregate(Vector3.Min),max=positions.Aggregate(Vector3.Max);
                result.Add((template).Copy().SetOrdinal(result.Count).SetName($"decoration.{result.Count}.{(shadow?"shadow":"regular")}").SetAttributes(template.Attributes&~EnbCurrentRetailCameraGeometryRuntime.GeometryTypeMask).SetBoneIndices([]).SetPositions(positions).SetNormals(normals).SetIndices(indices).SetMaterialPasses([pass]).SetCurrentRetailRenderPassCount(1).SetCurrentRetailDepthBias(shadow?1u:0u).SetTriangleMetadata(new EnbW3dRenderTriangleMetadata[indices.Count/3]).SetEffectiveColor1([]).SetRetailTextureCoordinateSets([]).SetBoundingBoxMin(min).SetBoundingBoxMax(max).SetBoundingSphereCenter((min+max)/2).SetBoundingRadius(Vector3.Distance(min,max)/2));
                batches.Add(group.Key);
            }
        }
        patchIndices=batches.ToArray();return result;
    }
}
