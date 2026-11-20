using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Already evaluated world-space ParticleBuffer endpoints. Emission,
/// blur, colour curves and lifetime belong to the buffer, not LineGroup.</summary>
public readonly record struct EnbCurrentRetailStreakSample(
    Vector3 Head, Vector3 Tail, float Size, EnbRenderColor32 HeadColor, EnbRenderColor32 TailColor)
{
    public float Frame { get; init; }
}

/// <summary>0097BAF0 / 0097C360 / 0097C1D0: mode-3/4 LineGroup geometry.
/// Produces transient geometry for the production GPU lane, not a CPU rasterizer.
/// Deliberately does not admit an emitter or synthesize its missing motion state.</summary>
public static class EnbCurrentRetailStreakRuntime
{
    //009634F0 selects four tail modes;0095E500 supplies their RGBA arrays.
    public static EnbRenderColor32 TailColor(EnbCurrentRetailParticleEmitterDefinition definition,EnbRenderColor32 head)
    {
        if(definition.TextureFileName.Length!=0)return head;
        Span<byte> bytes=stackalloc byte[16]; definition.Shader.CopyTo(bytes);
        var shader=new EnbW3dRenderShaderRecord(BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        if(!EnbW3dMaterialCompiler.TryDecodeShader(shader,out var state,out var error))throw new InvalidDataException(error);
        uint packed=state.PackedRetailState,destination=packed&0xe0;
        if(destination==0x40)return new(255,255,255,255);
        if((packed&0xc000)==0x4000 && destination is 0x20 or 0x60)return new(0,0,0,0);
        if((packed&0xc000)==0x8000 && destination==0xa0 || (packed&0x40000)!=0)return head with {A=0};
        return head;
    }
    private static readonly float[] Angles = [1.5707964f, 3.6651917f, 5.759587f];
    private static readonly uint[] Indices = [0,1,2, 0,3,1, 1,3,4, 1,4,5, 1,5,2, 0,2,5, 0,5,3, 3,5,4];
    private static readonly uint[] TetraIndices = [0,2,1, 0,3,2, 0,1,3, 1,2,3];

    public static bool TryBuildPacket(EnbW3dRenderResourceKey resourceKey, ulong renderIdentity,
        ulong visualGeneration, uint gameId, string name, int bindingOrdinal,
        EnbCurrentRetailParticleEmitterDefinition definition, IReadOnlyList<EnbCurrentRetailStreakSample> samples,
        EnbResolvedRenderCamera camera, long frameTime, out EnbW3dDrawTask? packet, out string? error,
        EnbW3dRenderTextureLocation? textureLocation = null, string? runtimeTextureName = null,EnbResolvedTextureResource? retainedTexture=null,string renderInstancePath="root")
    {
        packet = null; error = null;
        bool textured = !string.IsNullOrEmpty(definition.TextureFileName);
        // 0095BA90/0095E500 configure LineGroup without sprite frame mode or
        // orientation. Its frame property is a float U coordinate, not an atlas index.
        if (definition.RenderMode is not (3 or 4) || (textured && textureLocation is null && string.IsNullOrEmpty(runtimeTextureName) && retainedTexture is null))
        { error = "Streak renderer requires mode 3/4 and a resolved texture with frame coordinates."; return false; }
        if (samples.Count > EnbRetailParticleAdmission.MaximumParticles || frameTime < 0)
        { error = "Streak sample count or frame time exceeds the bounded draw slice."; return false; }
        foreach (var s in samples)
            if (!Finite(s.Head) || !Finite(s.Tail) || !float.IsFinite(s.Size) || s.Size < 0)
            { error = "Streak endpoints and size must be finite; size must be nonnegative."; return false; }
        if (samples.Count == 0) return true;

        Span<byte> bytes = stackalloc byte[16];
        definition.Shader.CopyTo(bytes);
        var shader = new EnbW3dRenderShaderRecord(BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        if (!EnbW3dMaterialCompiler.TryDecodeShader(shader, out var authored, out error)) return false;
        // Native LineGroup enables culling and diffuse modulation when supplied
        // per-particle colours. Texturing follows the actual texture pointer.
        uint packed = ((authored.PackedRetailState | 0x80000u) & ~0x1C00u & ~0x10000u) | 0x400u | (textured ? 0x10000u : 0);
        var white = new EnbRenderPackedRgb8(255,255,255,0);
        var vm = new EnbW3dRenderVertexMaterial(0,name+".StreakMaterial",0,white,white,white,
            new(0,0,0,0),1,1,0,null,null);
        var prelit = new EnbW3dVertexLightingSemantics(false,EnbW3dMaterialColorSource.Material,
            EnbW3dMaterialColorSource.Color1,EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material);
        if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(packed,vm,textured ? 1 : 0,prelit,out var semantics,out error)) return false;
        var texture = textured ? new EnbW3dRenderTextureBinding(0,retainedTexture?.LogicalName ?? runtimeTextureName ?? definition.TextureFileName,textureLocation)
        {
            CurrentRetailResolvedTexture=retainedTexture,
            CurrentRetailTextureState=retainedTexture is null?null:EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(retainedTexture),
            ResolveLogicalNameAtRuntime=runtimeTextureName is not null,
            Sampling=EnbW3dTextureSamplingSemantics.RetailDefault,
            SourceKind=EnbW3dRenderBindingSourceKind.PackedRetailState,
        } : null;
        bool tetra = definition.RenderMode==3;
        int vertexCount = tetra ? 4 : 6;
        var topology = tetra ? TetraIndices : Indices;
        int triangleCount = topology.Length/3;
        var positions = ImmutableArray.CreateBuilder<Vector3>(samples.Count*vertexCount);
        var normals = ImmutableArray.CreateBuilder<Vector3>(samples.Count*vertexCount);
        var colors = new EnbRenderColor32[samples.Count*vertexCount];
        var uvs = textured ? new Vector2[samples.Count*vertexCount] : [];
        var indices = new uint[samples.Count*topology.Length];
        var triangles = new EnbW3dRenderTriangleMetadata[samples.Count*triangleCount];
        var bindings = new EnbW3dRenderTriangleBinding[triangles.Length];
        var triangleStates = new EnbW3dTriangleBindingPool();
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var ring = Angles.Select(a => camera.Basis.Right*MathF.Cos(a)+camera.Basis.Up*MathF.Sin(a)).ToArray();
        for (int i=0;i<samples.Count;i++)
        {
            var sample = samples[i];
            if(!float.IsFinite(sample.Frame)) {error="Non-finite streak texture coordinate.";return false;}
            for (int v=0;v<vertexCount;v++)
            {
                // Mode 3: tail point then three head-ring vertices. Mode 4:
                // three head-ring then three tail-ring vertices, same width.
                bool head = tetra ? v!=0 : v<3;
                var position = tetra && v==0 ? sample.Tail :
                    (head ? sample.Head : sample.Tail) + ring[tetra ? v-1 : v%3]*sample.Size;
                if (!Finite(position)) { error="Streak vertex overflow."; return false; }
                positions.Add(position); normals.Add(Vector3.Zero);
                colors[i*vertexCount+v] = head ? sample.HeadColor : sample.TailColor;
                // 0097C597/5A3 and 0097C6C8/6CE: U is the particle frame
                // as a float, not a sprite-atlas cell; V=0 at the head and V=1 at the tail.
                if(textured) uvs[i*vertexCount+v]=new(sample.Frame,head ? 0 : 1);
                minimum=Vector3.Min(minimum,position); maximum=Vector3.Max(maximum,position);
            }
            for (int n=0;n<topology.Length;n++) indices[i*topology.Length+n]=(uint)(i*vertexCount)+topology[n];
            for (int t=0;t<triangleCount;t++)
            {
                int ordinal=i*triangleCount+t;
                triangles[ordinal]=new(0,Vector3.Zero,0);
                bindings[ordinal]=triangleStates.Create(ordinal,(int)indices[ordinal*3],null,null,textured ? [texture] : [],semantics) with
                    { UsesImplicitVertexMaterial=true, UsesImplicitShader=true };
            }
        }
        EnbW3dRenderTextureStage[] stages = textured ? [new(0,[texture!],uvs,EnbW3dRenderBindingCardinality.Uniform)
        {EffectiveTextureCoordinateSetIndex=0,EffectiveRuntimeTexCoords=uvs}] : [];
        var pass = new EnbW3dRenderMaterialPass(0,[],EnbW3dRenderBindingCardinality.None,
            [],EnbW3dRenderBindingCardinality.None,[],[],[],stages,bindings,semantics);
        Vector3 center=minimum*.5f+maximum*.5f;
        float radius=positions.Max(p=>Vector3.Distance(center,p));
        if (!float.IsFinite(radius)) { error="Streak bounds overflow."; return false; }
        var mesh = new EnbW3dRenderMesh(int.MinValue+bindingOrdinal,name+".LineGroup",name,radius,
            positions.MoveToImmutable(),normals.MoveToImmutable(),[],indices,triangles,[],[pass])
        {
            BoundingBoxMin=minimum, BoundingBoxMax=maximum, BoundingSphereCenter=center,
            EffectiveColor1=colors, CurrentRetailGeometrySortRequestOverride=true,
        };
        var sort=EnbW3dStaticSortSemantics.FromSingleShader(packed);
        var order=new EnbW3dDrawOrderKey(EnbRenderPassKind.OrdinaryWorld,EnbW3dDrawBlendClass.Blended,
            sort,resourceKey,mesh.Ordinal,0,renderIdentity,bindingOrdinal);
        packet=new(order,renderIdentity,visualGeneration,gameId,Matrix4x4.Identity,new(center,radius),mesh,
            new(mesh.Positions.ToImmutableArray(),mesh.Normals.ToImmutableArray()),new(pass,semantics),
            EnbW3dLightEnvironment.BlackNoLights,frameTime,"",0,name,renderInstancePath);
        return true;
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
