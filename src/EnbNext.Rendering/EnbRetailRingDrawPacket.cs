using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

public static class EnbRetailRingDrawPacket
{
    // Production integration must supply instance identity, binding ordinal,
    // lifecycle and the resolved attachment/billboard transform.
    public static EnbW3dDrawTask Build(EnbRetailRingAsset asset,EnbRetailRingSample sample,
        EnbW3dRenderResourceKey key,EnbW3dRenderTextureBinding? texture,Matrix4x4 world,bool optimize,uint frameTime,
        ulong renderIdentity=1,ulong visualGeneration=1,uint gameId=1,int bindingOrdinal=0)
    {
        if(bindingOrdinal<0)throw new ArgumentOutOfRangeException(nameof(bindingOrdinal));
        var bytes=asset.Header.ShaderBytes;
        var shader=new EnbW3dRenderShaderRecord(BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)),BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        var state=EnbRetailRingMaterial.Decode(shader,optimize,texture is not null);
        var color=PackColor(sample.Color,sample.Opacity,state.PackedRetailState);
        var positions=sample.Geometry.Positions.Select(p=>Vector3.Transform(p,world)).ToArray();
        var normals=sample.Geometry.Normals.Select(p=>Vector3.TransformNormal(p,world)).ToArray();
        if(positions.Any(p=>!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)))
            throw new InvalidDataException("Nonfinite ring world geometry");
        var white=new EnbRenderPackedRgb8(255,255,255,0);
        var vm=new EnbW3dRenderVertexMaterial(0,"Ring",0,white,white,white,new(0,0,0,0),1,1,0,null,null);
        var lighting=new EnbW3dVertexLightingSemantics(false,EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Color1,
            EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material);
        if(!EnbW3dMaterialCompiler.TryCompilePackedRetailState(state.PackedRetailState,vm,texture is null?0:1,lighting,out var semantics,out var error))
            throw new InvalidDataException(error);
        var indices=sample.Geometry.Indices;
        var triangleStates = new EnbW3dTriangleBindingPool();
        var bindings=Enumerable.Range(0,indices.Length/3).Select(i=>triangleStates.Create(i,(int)indices[i*3],null,null,
            texture is null?[]:[texture],semantics) with{UsesImplicitVertexMaterial=true,UsesImplicitShader=true}).ToArray();
        var uv=sample.Geometry.TexCoords;
        EnbW3dRenderTextureStage[] stages=texture is null?[]:[new(0,[texture],uv,EnbW3dRenderBindingCardinality.Uniform)
            {EffectiveTextureCoordinateSetIndex=0,EffectiveRuntimeTexCoords=uv}];
        var pass=new EnbW3dRenderMaterialPass(0,[],EnbW3dRenderBindingCardinality.None,[],EnbW3dRenderBindingCardinality.None,[],[],[],stages,bindings,semantics);
        var min=positions.Aggregate(Vector3.Min);var max=positions.Aggregate(Vector3.Max);var center=min*.5f+max*.5f;
        float radius=positions.Max(p=>Vector3.Distance(center,p));
        if(!float.IsFinite(radius))throw new InvalidDataException("Ring bounds overflow");
        var mesh=new EnbW3dRenderMesh(int.MinValue+bindingOrdinal,asset.Header.Name,asset.Header.Name,radius,positions,normals,[],indices,
            Enumerable.Range(0,indices.Length/3).Select(_=>new EnbW3dRenderTriangleMetadata(0,Vector3.Zero,0)).ToArray(),[],[pass])
            {BoundingBoxMin=min,BoundingBoxMax=max,BoundingSphereCenter=center,EffectiveColor1=Enumerable.Repeat(color,positions.Length).ToArray(),
                CurrentRetailGeometrySortRequestOverride=true};
        var sort=EnbW3dStaticSortSemantics.FromSingleShader(state.PackedRetailState);
        var order=new EnbW3dDrawOrderKey(EnbRenderPassKind.OrdinaryWorld,state.BlendEnabled?EnbW3dDrawBlendClass.Blended:EnbW3dDrawBlendClass.Opaque,
            sort,key,mesh.Ordinal,0,renderIdentity,bindingOrdinal);
        return new(order,renderIdentity,visualGeneration,gameId,Matrix4x4.Identity,new(center,radius),mesh,new(positions.ToImmutableArray(),normals.ToImmutableArray()),
            new(pass,semantics),EnbW3dLightEnvironment.BlackNoLights,frameTime,"",0,asset.Header.Name);
    }

    public static EnbRenderColor32 PackColor(Vector3 rgb,float opacity,uint packed)
    {
        if(!float.IsFinite(rgb.X)||!float.IsFinite(rgb.Y)||!float.IsFinite(rgb.Z)||!float.IsFinite(opacity)||
            rgb.X is <0 or >1 || rgb.Y is <0 or >1 || rgb.Z is <0 or >1 || opacity is <0 or >1)
            throw new InvalidDataException("Unsupported ring diffuse range");
        if((packed&0xe0)==0x20){rgb*=opacity;opacity=1;}
        return new((byte)(rgb.X*255),(byte)(rgb.Y*255),(byte)(rgb.Z*255),(byte)(opacity*255));
    }
}
