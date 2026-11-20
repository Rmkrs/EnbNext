using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public static class EnbRetailSphereDrawPacket
{
    // Production integration must supply instance identity, binding ordinal,
    // lifecycle and the resolved attachment/billboard transform.
    public static EnbW3dDrawTask Build(EnbCurrentRetailSphereHeader header,EnbRetailSphereValues values,EnbRetailSphereGeometry geometry,
        EnbW3dRenderResourceKey key,EnbW3dRenderTextureBinding? texture,Matrix4x4 world,bool optimize,uint frameTime,
        ulong renderIdentity=1,ulong visualGeneration=1,uint gameId=1,int bindingOrdinal=0)
    {
        if(bindingOrdinal<0)throw new ArgumentOutOfRangeException(nameof(bindingOrdinal));
        float[] diffuse=[values.Color.X,values.Color.Y,values.Color.Z,values.Opacity];
        if(diffuse.Any(v=>!float.IsFinite(v)||v<0||v>1)||!float.IsFinite(values.Strength)||
            !float.IsFinite(values.Orientation.LengthSquared()))
            throw new InvalidDataException("Unsupported sphere diffuse or direction range");
        var bytes=header.ShaderBytes;
        var shader=new EnbW3dRenderShaderRecord(BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)),BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        var state=EnbRetailSphereMaterial.Decode(shader,optimize,texture is not null);
        var colors=geometry.Normals.Select(n=>Color(header,values,n,state.PackedRetailState)).ToArray();
        var positions=geometry.Positions.Select(p=>Vector3.Transform(p,world)).ToArray();
        var normals=geometry.Normals.Select(p=>Vector3.TransformNormal(p,world)).ToArray();
        if(positions.Any(p=>!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)))
            throw new InvalidDataException("Nonfinite sphere world geometry");
        var white=new EnbRenderPackedRgb8(255,255,255,0);
        var vm=new EnbW3dRenderVertexMaterial(0,"Sphere",0,white,white,white,new(0,0,0,0),1,1,0,null,null);
        var lighting=new EnbW3dVertexLightingSemantics(true,EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material,
            EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material);
        if(!EnbW3dMaterialCompiler.TryCompilePackedRetailState(state.PackedRetailState,vm,texture is null?0:1,lighting,out var semantics,out var error))
            throw new InvalidDataException(error);
        semantics=semantics with {Uniforms=EnbRetailSphereMaterial.ApplyColor(semantics.Uniforms,values.Color,values.Opacity,state.PackedRetailState)};
        var indices=geometry.Indices;
        var triangleStates = new EnbW3dTriangleBindingPool();
        var bindings=Enumerable.Range(0,indices.Length/3).Select(i=>triangleStates.Create(i,(int)indices[i*3],null,null,
            texture is null?[]:[texture],semantics) with{UsesImplicitVertexMaterial=true,UsesImplicitShader=true}).ToArray();
        var uv=geometry.TexCoords;
        EnbW3dRenderTextureStage[] stages=texture is null?[]:[new(0,[texture],uv,EnbW3dRenderBindingCardinality.Uniform)
            {EffectiveTextureCoordinateSetIndex=0,EffectiveRuntimeTexCoords=uv}];
        var pass=new EnbW3dRenderMaterialPass(0,[],EnbW3dRenderBindingCardinality.None,[],EnbW3dRenderBindingCardinality.None,[],[],[],stages,bindings,semantics);
        var min=positions.Aggregate(Vector3.Min);var max=positions.Aggregate(Vector3.Max);var center=min*.5f+max*.5f;
        float radius=positions.Max(p=>Vector3.Distance(center,p));
        if(!float.IsFinite(radius))throw new InvalidDataException("Sphere bounds overflow");
        var mesh=new EnbW3dRenderMesh(int.MinValue+bindingOrdinal,header.Name,header.Name,radius,positions,normals,[],indices,
            Enumerable.Range(0,indices.Length/3).Select(_=>new EnbW3dRenderTriangleMetadata(0,Vector3.Zero,0)).ToArray(),[],[pass])
            {BoundingBoxMin=min,BoundingBoxMax=max,BoundingSphereCenter=center,EffectiveColor1=colors,
                CurrentRetailGeometrySortRequestOverride=true};
        var sort=EnbW3dStaticSortSemantics.FromSingleShader(state.PackedRetailState);
        var order=new EnbW3dDrawOrderKey(EnbRenderPassKind.OrdinaryWorld,state.BlendEnabled?EnbW3dDrawBlendClass.Blended:EnbW3dDrawBlendClass.Opaque,
            sort,key,mesh.Ordinal,0,renderIdentity,bindingOrdinal);
        return new(order,renderIdentity,visualGeneration,gameId,Matrix4x4.Identity,new(center,radius),mesh,new(positions.ToImmutableArray(),normals.ToImmutableArray()),
            new(pass,semantics),EnbW3dLightEnvironment.BlackNoLights,frameTime,"",0,header.Name);
    }

    private static EnbRenderColor32 Color(EnbCurrentRetailSphereHeader header,EnbRetailSphereValues values,Vector3 normal,uint packed)
    {
        bool additive=(packed&0xe0)==0x20;
        var vertex=(header.Flags&1)!=0
            ?EnbRetailSphereFalloff.Evaluate(normal,values.Orientation,values.Strength,(header.Flags&4)!=0,additive)
            :Vector4.One;
        // Retain the native COLO stream independently. Sphere material sources
        // are Material, so no synthetic multiplication into emissive is valid.
        return new((byte)(vertex.X*255),(byte)(vertex.Y*255),(byte)(vertex.Z*255),(byte)(vertex.W*255));
    }
}
