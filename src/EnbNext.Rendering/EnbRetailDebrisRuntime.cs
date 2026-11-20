using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;
using X87 = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

public readonly record struct EnbRetailDebrisSample(Vector3 ViewPosition, Vector4 Color, byte Selector);

/// <summary>Retained 006B8F20 owner; 006B9BF0 generates a field at draw time, without advancing an RNG.</summary>
public sealed class EnbRetailDebrisRuntime(EnbRetailDebrisDefinition definition)
{
    private sealed record Topology(uint Shader, EnbResolvedTextureResource? Texture,
        EnbW3dRenderTextureBinding? Binding, EnbW3dCompiledMaterialSemantics Semantics,
        uint[] Indices, EnbW3dRenderTriangleBinding[] Bindings,
        EnbW3dRenderTriangleMetadata[] Metadata, Vector3[] Normals)
    {
        // The retained helper owns this index/binding layout. New positions,
        // colors and atlas coordinates do not replace its registered topology.
        public readonly EnbW3dGeneratedTopology Generated=new(Indices,Indices.Length,Normals.Length);
        public readonly IReadOnlyList<Vector3> NormalSource=(EnbW3dNormalStream)System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(Normals);
    }
    private Topology? _topology;
    private sealed class InvocationSlot
    {
        internal readonly EnbW3dUvOwner Uv=new();
        internal Topology? Topology;
        internal EnbW3dRenderMaterialPass? Pass;
        internal EnbW3dRenderMesh? Mesh;
        internal EnbW3dDrawTask? Task;
        internal int Layer;
        internal string Name="",Path="";
    }
    private sealed class InvocationPool(EnbW3dDrawTaskArena arena)
    {
        internal readonly EnbW3dDrawTaskArena Arena=arena;
        internal readonly List<InvocationSlot> Slots=[];
        internal long Serial;
        internal int Cursor;
    }
    private readonly List<InvocationPool> _invocations=[];
    private InvocationSlot ForInvocation(EnbW3dDrawTaskArena arena)
    {
        InvocationPool? pool=null;
        foreach(var item in _invocations)if(ReferenceEquals(item.Arena,arena)){pool=item;break;}
        if(pool is null){pool=new(arena);_invocations.Add(pool);}
        if(pool.Serial!=arena.Invocation){pool.Serial=arena.Invocation;pool.Cursor=0;}
        if(pool.Cursor==pool.Slots.Count)pool.Slots.Add(new());
        return pool.Slots[pool.Cursor++];
    }

    // Sampling storage never escapes BuildPacket. Public Sample callers retain
    // their own result; queued views use distinct checked invocation ranges.
    private readonly List<EnbRetailDebrisSample> _samples = [];
    private float[]? _fade;
    private (float Minimum, float Maximum, float Amplitude) _fadeKey;
    private float[] RetainedFade()
    {
        var key=(MinimumDistance,MaximumDistance,Amplitude);
        if(_fade is null || _fadeKey!=key) {_fade=FadeTable();_fadeKey=key;}
        return _fade;
    }
    private static readonly Vector2[] TriangleUv=[new(.5f,0),new(0,.866f),new(1,.866f)];
    private static readonly Vector2[] QuadUv=[new(0,0),new(0,1),new(1,1),new(1,0)];
    private static readonly Vector3[] Ring=new[]{1.5707964f,3.6651917f,5.759587f}
        .Select(a=>new Vector3(MathF.Cos(a),MathF.Sin(a),0)).ToArray();
    public const int Capacity = 10000;
    // Separate from monotonically assigned scene objects, high-bit effect owners,
    // and descending orbital identities (which include ulong.MaxValue-1).
    public const ulong RenderIdentity = 0x6000000000000000;
    public EnbRetailDebrisDefinition Definition { get; } = definition;
    public float MinimumDistance { get; private set; } = definition.MinimumDistance;
    public float MaximumDistance { get; private set; } = definition.MaximumDistance;
    public float Amplitude { get; set; } = 1;
    public int BlendMode { get; set; } = 2;
    public Vector3 Offset { get; private set; }
    public Vector3 OffsetDelta { get; private set; }
    public int LastCount { get; private set; }
    public int PrimitiveCost => LastCount * (Definition.Primitive switch { 0 => 1, 1 => 2, 2 => 4, 3 => 8, _ => 0 });
    public uint Shader => Definition.Additive ? 0x94033u : BlendMode switch { 0 => 0x980b3u, 1 => 0xd401bu, 2 => 0xd80bbu, _ => 0x8441bu };

    // 006BA280..006BA34B; stored binary32 words, not random samples.
    private static readonly Vector3[] Jitter = [new(.4f,.57f,.56f),new(.58f,.57f,.41f),new(.28f,.25f,.27f),new(.26f,.25f,.73f),
        new(.28f,.7f,.26f),new(.71f,.72f,.75f),new(.75f,.3f,.26f),new(.7f,.25f,.73f)];
    private static readonly byte[] Permutation = Convert.FromHexString("18BB5DD61D9CD0AFDD760C17C79F0E7CEE660854BCA39AEF8406C195394CB9B499B5C8C96293978125BD40F26BF01A1C15E8F54E807A9DDED20D4ABE8509FEECB33707AB271204D7653D5564885600F78F2B44E477EBED786AF13BCC79878D74478E53D1BAE51957B7C0B6144663FA6E490FE124B87383C45E9431501E713E1FCDA14F82617020CA6C48FC68DC60A8727F9B3F2E45A4D5D841FF6DDF3675D9AC3A8A67915C59FB10A74DA0E32F2A2DC6F4E65101E2D40AE09630895A212CE95B11A598380335CBAD237EDB526F4B4292DA0B58F8B1B0AEB23290A6C269F6291BAAC5027D33BFA22628055F34F98643E73CCFFD7BD3EA9E16A922CE13C3F38C8B");
    public static byte CellSelector(int x, int y, int z) { int h=x^y^z; return Permutation[(h^(h>>8)^(h>>16))&255]; }

    public void SetOffset(Vector3 offset)
    {
        if (Offset.Z*Offset.Z + Offset.Y*Offset.Y + Offset.X*Offset.X > 0) OffsetDelta = Offset-offset;
        Offset = offset;
    }

    public float[] FadeTable()
    {
        float q=(1/MaximumDistance)*MinimumDistance, a=(1-q)*Definition.FadeOut, b=(1-Definition.FadeIn)*(1-q), c=1-b;
        var table=new float[512];
        for(int i=0;i<table.Length;i++)
        {
            float r=MathF.Sqrt(i*(1f/512));
            table[i]=r<q?0:r<q+a?(r-q)*Amplitude*(1/a):r<c?Amplitude:(1-(r-c)*(1/b))*Amplitude;
        }
        return table;
    }

    public IReadOnlyList<EnbRetailDebrisSample> Sample(EnbResolvedRenderCamera camera)
        => Sample(camera,new List<EnbRetailDebrisSample>());

    private IReadOnlyList<EnbRetailDebrisSample> Sample(EnbResolvedRenderCamera camera,List<EnbRetailDebrisSample> result)
    {
        result.Clear();
        LastCount=0;
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var d=Definition;
        if(d.Primitive is <0 or >3 || !float.IsFinite(d.Spacing) || d.Spacing<=0 || !float.IsFinite(d.Size) || d.Size<0)
            throw new InvalidDataException("Invalid debris geometry; the layer cannot be submitted.");
        var bounds=camera.Projection.StoredProjectionBounds ?? throw new InvalidDataException("Debris requires retained camera projection bounds.");
        if(!EnbCurrentRetailCameraFieldOfView.TryGet(bounds.X,bounds.Z,EnbRetailLinePrecision.Single24,out double fov))
            throw new PlatformNotSupportedException("Debris requires the native camera field-of-view getter.");
        float clip=(float)((X87)camera.Projection.ClipRange.NearPlane/X87.Cosine((X87)fov*.5)+d.Size);
        if(MinimumDistance<clip) {MinimumDistance=clip;if(MaximumDistance<=clip)MaximumDistance=clip*1.1f;}
        var shifted=camera with {Position=camera.Position+Offset};
        Vector3 min=new(float.PositiveInfinity),max=new(float.NegativeInfinity);
        foreach(float distance in new[]{MinimumDistance,MaximumDistance})
            foreach(float x in new[]{bounds.X,bounds.Z}) foreach(float y in new[]{bounds.Y,bounds.W})
            {
                // 008DD7A0: projection bounds times negative distance, then camera transform.
                float vx=-distance*x,vy=-distance*y,vz=-distance;
                var right=camera.Basis.Right;var up=camera.Basis.Up;var back=-camera.Basis.Forward;
                var p=new Vector3((float)(((R)vy*up.X+(R)vx*right.X)+(R)vz*back.X+shifted.Position.X),
                    (float)(((R)vx*right.Y+(R)vz*back.Y)+(R)vy*up.Y+shifted.Position.Y),
                    (float)(((R)vx*right.Z+(R)vz*back.Z)+(R)vy*up.Z+shifted.Position.Z));
                min=Vector3.Min(min,p);max=Vector3.Max(max,p);
            }
        min-=new Vector3(d.Size);max+=new Vector3(d.Size);
        float reciprocal=1/d.Spacing;
        int Lower(float value)=>checked((int)MathF.Floor(value*reciprocal));
        int Upper(float value)=>checked((int)MathF.Ceiling(value*reciprocal));
        int x0=Lower(min.X),y0=Lower(min.Y),z0=Lower(min.Z),x1=Upper(max.X),y1=Upper(max.Y),z1=Upper(max.Z);
        EnbRenderCameraMatrixCompiler.TryCreateWorldToCanonicalView(shifted,out var view);
        Vector3 Rotate(Vector3 p)=>RotateToView(camera,p);
        // 008C2BD0 transposes the basis, rotates the translation, then negates the stored floats.
        var translation=-Rotate(shifted.Position);
        var jitter=Jitter.Select(p=>Rotate(p*d.Spacing)).ToArray();
        Vector3 dx=Rotate(new(d.Spacing,0,0)),dy=Rotate(new(0,d.Spacing,0)),dz=Rotate(new(0,0,d.Spacing));
        var origin=new Vector3(x0*d.Spacing,y0*d.Spacing,z0*d.Spacing);
        Vector3 p0=new((float)((R)view.M31*origin.Z+(R)view.M21*origin.Y+(R)view.M11*origin.X+translation.X),
            (float)((R)view.M32*origin.Z+(R)view.M22*origin.Y+(R)view.M12*origin.X+translation.Y),
            (float)((R)view.M33*origin.Z+(R)view.M23*origin.Y+(R)view.M13*origin.X+translation.Z));
        Vector3 yReset=dy*(y1-y0),zReset=dz*(z1-z0);
        var fade=RetainedFade();float invMax=1/MaximumDistance,scale=invMax*invMax;
        bool rgb=d.Additive || BlendMode!=0;
        for(int x=x0;x<x1;x++)
        {
            for(int y=y0;y<y1;y++)
            {
                for(int z=z0;z<z1;z++)
                {
                    int h=x^y^z;var p=p0+jitter[(h^(h>>3)^(h>>6))&7];
                    float distance=(p.Z*p.Z+p.Y*p.Y+p.X*p.X)*scale;
                    if(distance<1)
                    {
                        float alpha=fade[(int)MathF.Floor(distance*512)];
                        var color=rgb?new Vector4(d.Color.X*alpha,d.Color.Y*alpha,d.Color.Z*alpha,d.Color.W):d.Color with {W=d.Color.W*alpha};
                        result.Add(new(p,color,CellSelector(x,y,z)));
                        if(result.Count==Capacity) {LastCount=result.Count;return result;}
                    }
                    p0+=dz;
                }
                p0=dy+(p0-zReset);
            }
            p0=dx+(p0-yReset);
        }
        LastCount=result.Count;return result;
    }

    public EnbW3dDrawTask? BuildPacket(EnbResolvedRenderCamera camera, EnbResolvedTextureResource? texture, uint time, int layer, EnbW3dTransientGeometryArena.Frame? frameStreams=null)
    {
        var samples=Sample(camera,_samples);if(samples.Count==0)return null;
        bool line=Definition.Primitive>=2,quad=Definition.Primitive==1,tetra=Definition.Primitive==2;
        int vertices=line?(tetra?4:6):(quad?4:3);
        uint[] topology=line?(tetra?[0,2,1,0,3,2,0,1,3,1,2,3]:[0,1,2,0,3,1,1,3,4,1,4,5,1,5,2,0,2,5,0,5,3,3,5,4]):quad?[0,1,2,2,3,0]:[0,1,2];
        int vertexCount=samples.Count*vertices;
        var borrowedPositions=frameStreams?.RentPositions(vertexCount);
        var borrowedView=frameStreams?.RentPositions(vertexCount);
        var borrowedColors=frameStreams?.RentColors(vertexCount);
        var positionArray=borrowedPositions is null?new Vector3[vertexCount]:null;
        var viewArray=borrowedView is null?new Vector3[vertexCount]:null;
        var colorArray=borrowedColors is null?new EnbRenderColor32[vertexCount]:null;
        Span<Vector3> positions=borrowedPositions is not null?borrowedPositions.WriteSpan:positionArray;
        Span<Vector3> viewPositions=borrowedView is not null?borrowedView.WriteSpan:viewArray;
        Span<EnbRenderColor32> colors=borrowedColors is not null?borrowedColors.WriteSpan:colorArray;
        IReadOnlyList<Vector3> worldStream=borrowedPositions is not null?borrowedPositions:positionArray!;
        IReadOnlyList<Vector3> viewStream=borrowedView is not null?borrowedView:System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(viewArray);
        IReadOnlyList<EnbRenderColor32> colorStream=borrowedColors is not null?borrowedColors:System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(colorArray);
        var owner=frameStreams is not null && EnbW3dDrawTaskArena.Current is {} taskArena?ForInvocation(taskArena):null;
        var uvArray=owner is null?new Vector2[positions.Length]:null;
        Span<Vector2> uv=owner is null?uvArray:owner.Uv.Prepare(positions.Length,0,[]);
        uint shader=(Shader&~0x81c00u)|0x400u|(line?0x80000u:0);
        shader=texture is null?shader&~0x10000u:shader|0x10000u;
        var retained=owner?.Topology??_topology;
        bool reuse=retained is not null && retained.Shader==shader && ReferenceEquals(retained.Texture,texture) &&
            retained.Indices.Length==samples.Count*topology.Length &&
            ReferenceEquals(retained.Binding?.CurrentRetailTextureState,
                texture is null?null:EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(texture));
        var indices=reuse?retained!.Indices:new uint[samples.Count*topology.Length];
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Vector3 delta=RotateToView(camera,OffsetDelta);
        R square=(R)delta.Y*delta.Y+(R)delta.Z*delta.Z+(R)delta.X*delta.X;
        if(square!=0)
        {
            R inverse=EnbRetailLineCoalescing.ReciprocalSquareRoot((float)square);
            delta=new((float)((R)delta.X*inverse),(float)((R)delta.Y*inverse),(float)((R)delta.Z*inverse));
        }
        delta*=Definition.BlurTime;
        int side=1<<Definition.RowsColumnsLog2;
        for(int i=0;i<samples.Count;i++)
        {
            var s=samples[i];
            for(int v=0;v<vertices;v++)
            {
                int n=i*vertices+v;Vector3 p;var color=s.Color;
                if(line)
                {
                    bool head=tetra?v!=0:v<3;
                    p=(head?s.ViewPosition:s.ViewPosition+delta)+(tetra&&v==0?Vector3.Zero:Ring[tetra?v-1:v%3]*Definition.Size);
                    if(!head)color=Definition.Additive?Vector4.Zero:color with {W=0};
                    uv[n]=new(s.Selector*BitConverter.Int32BitsToSingle(0x3b808081),head?0:1);
                }
                else
                {
                    p=EnbRetailPointGroupExpansion.Expand(s.ViewPosition,Definition.Size,Definition.Primitive,s.Selector,v);
                    int frame=s.Selector&((1<<(Definition.RowsColumnsLog2*2))-1);
                    uv[n]=((quad?QuadUv:TriangleUv)[v]+new Vector2(frame&(side-1),frame>>Definition.RowsColumnsLog2))/side;
                }
                viewPositions[n]=p;positions[n]=camera.Position+camera.Basis.Right*p.X+camera.Basis.Up*p.Y-camera.Basis.Forward*p.Z;
                // 00955AD5/AE6 changes RC to truncate before packing float RGBA.
                static int B(float value)=>unchecked((int)(value*255));
                uint packed=unchecked((uint)(B(color.Z)|(B(color.W)<<24)|(B(color.X)<<16)|(B(color.Y)<<8)));
                colors[n]=new((byte)(packed>>16),(byte)(packed>>8),(byte)packed,(byte)(packed>>24));
            }
            if(!reuse)for(int n=0;n<topology.Length;n++)indices[i*topology.Length+n]=(uint)(i*vertices)+topology[n];
        }
        if(!reuse)
        {
            var white=new EnbRenderPackedRgb8(255,255,255,0);
            var vm=new EnbW3dRenderVertexMaterial(0,"Debris",0,white,white,white,new(0,0,0,0),1,1,0,null,null);
            var lighting=new EnbW3dVertexLightingSemantics(false,EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Color1,EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material);
            if(!EnbW3dMaterialCompiler.TryCompilePackedRetailState(shader,vm,texture is null?0:1,lighting,out var createdSemantics,out var error))throw new InvalidDataException(error);
            EnbW3dRenderTextureBinding? createdBinding=texture is null?null:new(0,texture.LogicalName,texture.ResourceLocation is {} location?EnbW3dRenderTextureLocation.From(location):null){CurrentRetailResolvedTexture=texture,ResolveLogicalNameAtRuntime=texture.ResourceLocation is null,
                CurrentRetailTextureState=EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(texture),Sampling=EnbW3dTextureSamplingSemantics.RetailDefault,SourceKind=EnbW3dRenderBindingSourceKind.PackedRetailState};
            var triangleStates = new EnbW3dTriangleBindingPool();
            var createdBindings=Enumerable.Range(0,indices.Length/3).Select(i=>triangleStates.Create(i,(int)indices[i*3],null,null,createdBinding is null?[]:[createdBinding],createdSemantics) with{UsesImplicitVertexMaterial=true,UsesImplicitShader=true}).ToArray();
            retained=_topology=new(shader,texture,createdBinding,createdSemantics,indices,createdBindings,
                Enumerable.Range(0,indices.Length/3).Select(_=>new EnbW3dRenderTriangleMetadata(0,Vector3.Zero,0)).ToArray(),
                new Vector3[positions.Length]);
        }
        var binding=retained!.Binding;
        var semantics=retained.Semantics;
        var bindings=retained.Bindings;
        IReadOnlyList<Vector2> coordinates=owner is null?System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(uvArray):owner.Uv;
        if(owner is not null && (!ReferenceEquals(owner.Topology,retained)||owner.Layer!=layer))
        {
            owner.Topology=retained;owner.Layer=layer;
            owner.Pass=null;owner.Mesh=null;owner.Task=null;
            owner.Name=$"Debris{layer:D2}";owner.Path="environment/"+owner.Name;
        }
        var pass=owner?.Pass;
        if(pass is null)
        {
            EnbW3dRenderTextureStage[] stages=binding is null?[]:[new(0,[binding],coordinates,EnbW3dRenderBindingCardinality.Uniform){EffectiveTextureCoordinateSetIndex=0,EffectiveRuntimeTexCoords=coordinates}];
            pass=new EnbW3dRenderMaterialPass(0,[],EnbW3dRenderBindingCardinality.None,[],EnbW3dRenderBindingCardinality.None,[],[],[],stages,bindings,semantics);
            if(owner is not null)owner.Pass=pass;
        }
        var min=worldStream.Aggregate(Vector3.Min);var max=worldStream.Aggregate(Vector3.Max);var center=(min+max)*.5f;float radius=worldStream.Max(p=>Vector3.Distance(center,p));
        var name=owner?.Name??$"Debris{layer:D2}";
        var mesh=owner?.Mesh;
        mesh??=new EnbW3dRenderMesh(int.MinValue+layer,name,name,radius,worldStream,owner is null?retained.Normals:retained.NormalSource,[],retained.Generated.Indices,
            retained.Metadata,[],[pass])
            {GeneratedTopology=retained.Generated,BoundingBoxMin=min,BoundingBoxMax=max,BoundingSphereCenter=center,EffectiveColor1=colorStream,CurrentRetailGeometrySortRequestOverride=true};
        mesh.Positions=worldStream;mesh.EffectiveColor1=colorStream;mesh.BoundingRadius=radius;
        mesh.BoundingBoxMin=min;mesh.BoundingBoxMax=max;mesh.BoundingSphereCenter=center;
        if(owner is not null){owner.Mesh=mesh;mesh.RetainsParticleFrame=true;}
        var bounds=new EnbW3dWorldBounds(center,radius);
        var deformed=borrowedPositions is not null?null:new EnbW3dDeformedGeometry(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(positionArray),System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(retained.Normals));
        if(owner?.Task is {} current)
        {
            current.UpdateGeneratedFrame(bounds,deformed,EnbW3dLightEnvironment.BlackNoLights,time,viewStream);
            return current;
        }
        var sort=EnbW3dStaticSortSemantics.FromSingleShader(shader);
        var order=new EnbW3dDrawOrderKey(EnbRenderPassKind.OrdinaryWorld,EnbW3dDrawBlendClass.Blended,sort,new(name,"settings.ini",layer,0),mesh.Ordinal,0,RenderIdentity,layer);
        var result=new EnbW3dDrawTask(order,RenderIdentity,1,0,Matrix4x4.Identity,bounds,mesh,deformed,new(pass,semantics),EnbW3dLightEnvironment.BlackNoLights,time,"",0,name,owner?.Path??"environment/"+name)
            {CurrentRetailParticleBuffer=true,CurrentRetailViewPositions=viewStream};
        if(owner is not null)owner.Task=result;
        return result;
    }

    // 008C26C0 accumulation order is distinct for the first and remaining rows.
    private static Vector3 RotateToView(EnbResolvedRenderCamera camera,Vector3 p)
    {
        var x=camera.Basis.Right;var y=camera.Basis.Up;var z=-camera.Basis.Forward;
        return new((float)((R)x.Z*p.Z+(R)x.Y*p.Y+(R)x.X*p.X),
            (float)((R)y.Z*p.Z+(R)y.X*p.X+(R)y.Y*p.Y),
            (float)((R)z.Z*p.Z+(R)z.X*p.X+(R)z.Y*p.Y));
    }
}
