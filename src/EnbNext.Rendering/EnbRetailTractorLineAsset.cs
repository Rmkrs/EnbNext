using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
namespace EnbNext.Rendering;

// Current retail 004983A0 / 006EAC50: textured procedural LineEffect assets.
// SegLine mapping and edge planes
// share the established 009BEB20 / 009BF2CD connected-line GPU geometry.
public sealed record EnbRetailTractorLineAsset(float Width,float Tile,Vector2 Rate,
    EnbW3dRenderResourceKey Key,EnbW3dRenderTextureBinding Texture)
{
    public Vector4 Color { get; init; } = Vector4.One;
    public int ShaderPreset { get; init; } = 1;
    public int UvMode { get; init; } = 1;
    public int Subdivisions { get; init; }
    public float Noise { get; init; }
    public bool Freeze { get; init; }
    // ProceduralLineGeometry uses native model/view arithmetic and the shared
    // process birth/line stream (009C2390). Noise is authored behavior, not a
    // runtime recovery. Failures still propagate from Load/Build to the caller.
    public string? RecoveryDiagnostic => null;

    public static EnbRetailTractorLineAsset Load(EnbRetailAssetBundle assets,int id,Vector3 hsvShift=default)
    {
        var d=assets.PresentationCatalog!.ResolveBaseAsset(id);
        var p=d.Properties??throw new InvalidDataException("Missing line definition");
        float F(string key,float fallback=0)
        {
            if(!p.ContainsKey(key))return fallback;
            // Current009CA770 uses %f: the installed Noise=4.0F has numeric
            // value4 (LineNoiseNumericAndRng-20260911). Keep this local to the
            // evidenced noise spelling, without weakening other INI readers.
            bool suffix=key=="Noise" && p[key].Trim().EndsWith('F');
            float value;
            bool parsed=suffix ? float.TryParse(p[key].Trim()[..^1],System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,out value) : EnbRetailIniDocument.TryReadFloat(p,key,out value);
            if(!parsed || !float.IsFinite(value))
                throw new InvalidDataException($"Unsupported line numeric value: {key}={p[key]}");
            return value;
        }
        // 004983A0 reads Shader, UVMode and SubDivs through 009CA1C0. Any Shader
        // selects a 0093B2xx preset (unknown values keep 0x8441B); 00969380
        // stores SubDivs unsigned, raising anything above 6 to 7; 00969340 keeps
        // |Noise*scale|. Only UVMode outside 0..2 leaves the mode local unset.
        int shader=EnbRetailIniDocument.ReadCurrentRetailInteger(p,"Shader",0);
        int uvMode=EnbRetailIniDocument.ReadCurrentRetailInteger(p,"UVMode",0);
        uint subdivisions=unchecked((uint)EnbRetailIniDocument.ReadCurrentRetailInteger(p,"SubDivs",0));
        if(d.Kind!=EnbBaseAssetDefinitionKind.ProceduralWidth || d.Width is null || uvMode is not (0 or 1 or 2))
            throw new InvalidDataException($"Unsupported line asset parameters: BASE-{id}, kind={d.Kind}, " +
                $"Width={p.GetValueOrDefault("Width","<absent>")}, Shader={p.GetValueOrDefault("Shader","<absent>")}, " +
                $"UVMode={p.GetValueOrDefault("UVMode","<absent>")}, SubDivs={p.GetValueOrDefault("SubDivs","<absent>")}, " +
                $"Noise={p.GetValueOrDefault("Noise","<absent>")}, Freeze={p.GetValueOrDefault("Freeze","<absent>")}");
        var rgb=p.GetValueOrDefault("Color","1,1,1").Split(',')
            .Select(s=>float.Parse(s,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if(rgb.Length!=3 || rgb.Any(v=>!float.IsFinite(v) || v is <0 or >1) ||
            (p.ContainsKey("Opacity") && !EnbRetailIniDocument.TryReadFloat(p,"Opacity",out _)))
            throw new InvalidDataException("Invalid line colour or opacity");
        float opacity=F("Opacity",1);
        if(!float.IsFinite(opacity) || opacity is <0 or >1)throw new InvalidDataException("Invalid line opacity");
        // 004983A0 round-trips the authored RGB through the retail HSV routines
        // even with a zero shift, with no prototype threshold for this colour.
        var color=new Vector4(EnbCurrentRetailHsvRuntime.ShiftRgb(new(rgb[0],rgb[1],rgb[2]),hsvShift),opacity);
        // 004983A0 leaves the texture null when Texture is absent; no installed
        // effect or captured object selects such a BASE (line corpus receipt).
        if(!p.TryGetValue("Texture",out var name))throw new InvalidDataException("Untextured line BASE-"+id);
        // 0048B7B0 gates texture recolouring independently of line RGB.
        var textureShift=MathF.Abs(hsvShift.X)<=1 && MathF.Abs(hsvShift.Y)<=.01f && MathF.Abs(hsvShift.Z)<=.01f?Vector3.Zero:hsvShift;
        var resolved=EnbCurrentRetailEffectMeshHsvRuntime.ResolveNamedTexture(assets.Textures,name,textureShift);
        if(resolved.Image is null)throw new InvalidDataException("Line texture unavailable");
        // Texture resolver supports authored TGA names backed by DDS resources.
        string actual=assets.RetailMixResources!.TryLocateResource(name,out var location)?name:Path.ChangeExtension(name,"dds");
        if(!assets.RetailMixResources.TryLocateResource(actual,out location))throw new InvalidDataException("Line texture location unavailable");
        var rate=p.GetValueOrDefault("UVRate","0,0").Split(',').Select(s=>float.Parse(s,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if(!float.IsFinite(d.Width!.Value) || rate.Length!=2 || rate.Any(v=>!float.IsFinite(v)))
            throw new InvalidDataException("Invalid line width or texture coordinates");
        // 009693C0 -> 009BEAB0 clamps finite tile factors to [0,256].
        return new(d.Width!.Value,Math.Clamp(F("UVTile",1),0,256),new(rate[0],rate[1]),EnbW3dRenderResourceKey.From(location) with {ResourceName=resolved.LogicalName},
            new(0,resolved.LogicalName,EnbW3dRenderTextureLocation.From(location))
            {
                Sampling=EnbW3dTextureSamplingSemantics.RetailDefault,
                CurrentRetailTextureState=EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(resolved),
                CurrentRetailResolvedTexture=resolved,
            })
            {Color=color,ShaderPreset=shader,UvMode=uvMode,Subdivisions=(int)Math.Min(subdivisions,7u),Noise=MathF.Abs(F("Noise")),Freeze=EnbRetailIniDocument.ReadBool(p,"Freeze")};
    }

    public EnbW3dDrawTask? Build(ulong owner,ulong generation,uint gameId,Vector3 start,Vector3 end,
        EnbResolvedRenderCamera camera,uint age,float scale,int? detailLevel=null)
        => Build(owner,generation,gameId,[start,end],camera,age,scale,detailLevel);

    public EnbW3dDrawTask? Build(ulong owner,ulong generation,uint gameId,IReadOnlyList<Vector3> points,
        EnbResolvedRenderCamera camera,uint age,float scale,int? detailLevel=null,IReadOnlyList<Vector3>? boundsPoints=null,
        Matrix4x4? geometryModelToWorld=null)
    {
        static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
        if(points.Count is <2 or >32 || points.Any(p=>!Finite(p)) || !float.IsFinite(scale) || scale<=0)throw new InvalidDataException("Invalid line endpoints or scale");
        if(!Finite(new(Color.X,Color.Y,Color.Z)) || !float.IsFinite(Color.W) ||
            Color.X is <0 or >1 || Color.Y is <0 or >1 || Color.Z is <0 or >1 || Color.W is <0 or >1)
            throw new InvalidDataException("Invalid line colour or opacity");
        Vector3[] positions;Vector2[] uv;uint[] indices;
        float Scroll(float rate){double offset=age*(double)(rate*.001f);return (float)(offset-Math.Floor(offset));}
        var scroll=new Vector2(Scroll(Rate.X),Scroll(Rate.Y));
        int depth=EnbSegLineDetailBudgetSemantics.ClampCurrentLevel(detailLevel??Subdivisions,Subdivisions);
        var geometry=EnbRetailProceduralLineGeometry.Build(points,camera,depth,Noise*scale,Freeze,Width*scale*.5f,UvMode,Tile,scroll,
            boundsPoints,Subdivisions,modelToWorld:geometryModelToWorld);
        positions=geometry.Positions;uv=geometry.Uv;indices=geometry.Indices;
        // Contain non-finite geometry locally rather than submitting invalid
        // bounds or vertices to the retained GPU resource owner.
        if(indices.Length==0 || positions.Any(p=>!Finite(p)))return null;
        // 004986AD..004986FD compares every channel with 0.9961. Coloured
        // lines enable primary-gradient modulation; white/opaque lines omit it.
        bool modulate=Color.X<=.9961f || Color.Y<=.9961f || Color.Z<=.9961f || Color.W<=.9961f;
        // 004983A0 preset switch over the 0093B2xx static initializers; preset 3
        // retains alpha test and depth writes (gravity beams).
        uint preset=ShaderPreset switch {1=>0x94033u,2=>0x980b3u,3=>0xd401bu,4=>0xd80bbu,5=>0x94073u,6=>0x90053u,_=>0x8441bu};
        uint packed=modulate?(preset&0xffffe7ffu)|0x400u:preset&0xffffe3ffu;
        // 009BEB20 packs constant SegLine RGBA with FPU rounding mode 0xC00
        // (toward zero), then uses that same diffuse value for every vertex.
        var vertexColor=new EnbRenderColor32((byte)(Color.X*255.0),(byte)(Color.Y*255.0),(byte)(Color.Z*255.0),(byte)(Color.W*255.0));
        var white=new EnbRenderPackedRgb8(255,255,255,0);
        var vm=new EnbW3dRenderVertexMaterial(0,"Line",0,white,white,white,new(0,0,0,0),1,1,0,null,null);
        var prelit=new EnbW3dVertexLightingSemantics(false,EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Color1,
            EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material);
        if(!EnbW3dMaterialCompiler.TryCompilePackedRetailState(packed,vm,1,prelit,out var semantics,out var error))throw new InvalidDataException(error);
        var triangleStates = new EnbW3dTriangleBindingPool();
        var bindings=Enumerable.Range(0,indices.Length/3).Select(i=>triangleStates.Create(i,(int)indices[i*3],null,null,[Texture],semantics) with
            {UsesImplicitVertexMaterial=true,UsesImplicitShader=true}).ToArray();
        var stage=new EnbW3dRenderTextureStage(0,[Texture],uv,EnbW3dRenderBindingCardinality.Uniform){EffectiveTextureCoordinateSetIndex=0,EffectiveRuntimeTexCoords=uv};
        var pass=new EnbW3dRenderMaterialPass(0,[],EnbW3dRenderBindingCardinality.None,[],EnbW3dRenderBindingCardinality.None,[],[],[],[stage],bindings,semantics);
        var min=positions.Aggregate(Vector3.Min);var max=positions.Aggregate(Vector3.Max);var center=(min+max)*.5f;float radius=positions.Max(v=>Vector3.Distance(v,center));
        var normals=new Vector3[positions.Length];
        var mesh=new EnbW3dRenderMesh(int.MinValue,"TractorLine","TractorLine",radius,positions,normals,[],indices,
            Enumerable.Repeat(new EnbW3dRenderTriangleMetadata(0,Vector3.Zero,0),indices.Length/3).ToArray(),[],[pass]){BoundingBoxMin=min,BoundingBoxMax=max,BoundingSphereCenter=center,
                EffectiveColor1=Enumerable.Repeat(vertexColor,positions.Length).ToArray(),CurrentRetailGeometrySortRequestOverride=true};
        var sort=EnbW3dStaticSortSemantics.FromSingleShader(packed);
        var order=new EnbW3dDrawOrderKey(EnbRenderPassKind.OrdinaryWorld,
            semantics.FixedFunction.BlendEnabled?EnbW3dDrawBlendClass.Blended:EnbW3dDrawBlendClass.Opaque,sort,Key,mesh.Ordinal,0,owner,0);
        return new(order,owner,generation,gameId,Matrix4x4.Identity,new(center,radius),mesh,
            new(positions.ToImmutableArray(),normals.ToImmutableArray()),new(pass,semantics),EnbW3dLightEnvironment.BlackNoLights,age,"",0,"TractorLine")
            {CurrentRetailViewPositions=geometry.ViewPositions};
    }
}
