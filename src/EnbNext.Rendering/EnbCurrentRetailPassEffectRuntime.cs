using System.Numerics;
using System.Globalization;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public enum EnbPassProjectionKind { Box, World, Cylinder }

/// <summary>Current retail 006EB9B0 / 006EDC00 / 006EE260 projection inputs.</summary>
public sealed record EnbCurrentRetailPassEffectDefinition(string Swatch, Vector2 BoxScale, bool HideBaseMesh,
    bool ProjectStage0 = true, Vector2? Stage1BoxScale = null, bool ResetMappers = false,
    char ProjectionAxis = 'Z', char Stage1ProjectionAxis = 'Z', Vector3 HsvShift = default)
{
    public EnbPassProjectionKind ProjectionKind { get; init; }
    public EnbPassProjectionKind Stage1ProjectionKind { get; init; }
    public string SwatchCacheKey => $"{Swatch}:{BitConverter.SingleToInt32Bits(HsvShift.X):X8}:{BitConverter.SingleToInt32Bits(HsvShift.Y):X8}:{BitConverter.SingleToInt32Bits(HsvShift.Z):X8}";
    public static bool TryRead(EnbCurrentRetailEffectOwner node, out EnbCurrentRetailPassEffectDefinition result, out string? error)
    {
        result = default!; error = null;
        // 006E8B60 binds unary descendants to an ordinary source/target object.
        // The immutable packet still describes the binary ancestor, not this pass.
        bool linkedObject = node.LinkKind != EnbCurrentRetailEffectLinkKind.Root &&
            node.Root.GeometryKind is EnbNext.Presentation.EnbSceneEffectGeometryKind.ObjectToObject or
                EnbNext.Presentation.EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked;
        // 006E8DF1..006E8E08 binds the target's render object through unary
        // +28. Neither the source bone nor binary target offset is forwarded.
        bool targetLinked=linkedObject && node.BoundGameId==node.Root.TargetGameId &&
            node.BoundGameId!=node.Root.SourceGameId;
        if ((!linkedObject && node.Root.GeometryKind != EnbNext.Presentation.EnbSceneEffectGeometryKind.Object) ||
            node.BoundGameId is null or 0 || linkedObject && !targetLinked &&
                node.BoundToTarget is null && node.Root.SourceGameId==node.Root.TargetGameId && !string.IsNullOrEmpty(node.Root.BoneName) ||
            !linkedObject && node.Root.SpatialOffset != Vector3.Zero)
        { error = "PassEffect requires an ordinary object binding"; return false; }
        return TryReadFactory(node.Definition,node.Root.HsvShift+node.Root.EffectOffset,out result,out error);
    }

    public static bool TryReadFactory(EnbEffectDefinition definition,Vector3 incomingHsv,out EnbCurrentRetailPassEffectDefinition result,out string? error)
    {
        result=default!;error=null;
        var p=definition.Properties;
        //006EB9B0 ignores the command/RObj scale and requests its swatch at
        //1.0.006EDC00 sizes projection from the bound object's own box.
        //The original command scale still belongs to linked effect factories.
        string swatch=p.GetValueOrDefault("Swatch","");
        if (!EnbCurrentRetailEffectFactoryHsv.TryRead(p,incomingHsv, out var hsv, out error)) return false;
        Vector2?[] scales = new Vector2?[2];
        char[] axes = ['Z', 'Z'];
        EnbPassProjectionKind[] kinds = new EnbPassProjectionKind[2];
        for (int stage=0; stage<2; stage++)
        {
            string prefix=$"Projection{stage}";
            if (!p.TryGetValue(prefix+"Axis",out var axis))
                continue;
            //B92E68 is X/Y/Z; unrecognized enum input uses factory default0.
            axes[stage]=axis.Equals("Y",StringComparison.OrdinalIgnoreCase)?'Y':axis.Equals("Z",StringComparison.OrdinalIgnoreCase)?'Z':'X';
            if(p.TryGetValue(prefix+"WorldScale",out var world) || !p.ContainsKey(prefix+"BoxScale"))
            {
                kinds[stage]=world is not null?EnbPassProjectionKind.World:EnbPassProjectionKind.Cylinder;
                string value=world??p.GetValueOrDefault(prefix+"CylinderScale","1");
                if(!float.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out float scalar) || !float.IsFinite(scalar))
                {error="Invalid PassEffect projection scalar";return false;}
                scales[stage]=new Vector2(scalar);continue;
            }
            string box=p[prefix+"BoxScale"];
            var parts=box.Split(',');
            if (parts.Length!=2 || !float.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out float x) ||
                !float.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out float y) ||
                // 006EC07B..0BF takes signed reciprocals, and 006EDBB0
                // stores them unchanged. Negative scales mirror projection.
                !float.IsFinite(x) || !float.IsFinite(y))
            { error="Invalid PassEffect BoxScale"; return false; }
            scales[stage]=new(x,y);
        }
        result=new(swatch.Trim(),scales[0] ?? Vector2.One,EnbRetailIniDocument.ReadBool(p,"HideBaseMesh"),
            scales[0].HasValue,scales[1],EnbRetailIniDocument.ReadBool(p,"ResetMappers"),axes[0],axes[1],hsv)
            {ProjectionKind=kinds[0],Stage1ProjectionKind=kinds[1]}; return true;
    }

    public Matrix4x4 ProjectionFromWorld(Matrix4x4 ownerFromWorld, Vector3 boxExtent, int textureWidth)
    {
        float horizontalExtent=ProjectionAxis=='Y'?boxExtent.X:boxExtent.Y;
        float verticalExtent=ProjectionAxis=='Z'?boxExtent.X:boxExtent.Z;
        static float Reciprocal(float value)=>value==0?0:1/value;
        var extent=ProjectionKind switch
        {
            EnbPassProjectionKind.World=>new Vector2(BoxScale.X*.5f),
            EnbPassProjectionKind.Cylinder=>new Vector2(MathF.Sqrt(horizontalExtent*horizontalExtent+verticalExtent*verticalExtent)*Reciprocal(BoxScale.X)),
            _=>new Vector2(horizontalExtent*Reciprocal(BoxScale.X),verticalExtent*Reciprocal(BoxScale.Y))
        };
        // Z projector rotates the source frame (-Y,X,Z), uses bounding-box
        // half extents / authored BoxScale, then 0097F810 applies the texture
        // width guard-band and V inversion. The bounding-box center is not used.
        // X's 006EE504 branch uses basis (-Y,Z,-X), hence U=-Y and V=-Z.
        float bias = (textureWidth - 2f) * .5f / textureWidth;
        var uv = Matrix4x4.Identity;
        uv.M11 = ProjectionAxis=='Y'?bias/extent.X:0; uv.M21 = ProjectionAxis=='Y'?0:-bias/extent.X;
        uv.M12 = ProjectionAxis=='Z'?-bias/extent.Y:0; uv.M22 = 0;
        uv.M32 = ProjectionAxis=='Z'?0:-bias/extent.Y;
        uv.M41 = bias; uv.M42 = bias;
        return ownerFromWorld * uv;
    }

    public void ValidateSwatch(EnbW3dRenderMaterialPass pass)
    {
        if(pass.SingleBindingSemantics is not {} s || pass.TextureStages.Count>2)
            throw new InvalidDataException("PassEffect requires a uniform material with at most two texture stages");
        foreach(var program in new[]{s.Mapper0Program,s.Mapper1Program})
            if(!Enum.IsDefined(program.Kind))throw new InvalidDataException("Unknown PassEffect mapper program");
    }

    //930ED0's stock material1 (C00700), initialized by92E8D0.
    private static readonly EnbW3dRenderVertexMaterial MissingSwatchMaterial=new(-1,"retail-pass-default",0,
        new(255,255,255,0),new(255,255,255,0),new(0,0,0,0),new(0,0,0,0),0,1,0,null,null);

    /// <summary>6EB9B0's native failed-acquisition descriptor: stock material1,
    /// shader C01140 (93B240), and the manager's missingtexture.tga object.</summary>
    public static EnbW3dRenderMaterialPass CreateMissingSwatchDescriptor(EnbW3dRenderTextureBinding texture)
    {
        const uint shader=0x9441b;
        var binding=new EnbW3dRenderVertexMaterialBinding(uint.MaxValue,MissingSwatchMaterial)
        {SourceKind=EnbW3dRenderBindingSourceKind.PackedRetailState,CurrentRetailRuntimeCoordinates=new(0,1)};
        var lighting=new EnbW3dVertexLightingSemantics(true,EnbW3dMaterialColorSource.Material,
            EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material);
        if(!EnbW3dMaterialCompiler.TryCompilePackedRetailState(shader,MissingSwatchMaterial,1,lighting,out var semantics,out var why))
            throw new InvalidDataException(why);
        return new(0,[binding],EnbW3dRenderBindingCardinality.Uniform,
            [new(shader,default,semantics.FixedFunction){SourceKind=EnbW3dRenderBindingSourceKind.PackedRetailState}],
            EnbW3dRenderBindingCardinality.Uniform,[],[],[],
            [new(0,[texture],[],EnbW3dRenderBindingCardinality.Uniform){CurrentRetailUniformTexture=texture}],[],semantics)
        {CurrentRetailUniformMaterial=binding,CurrentRetailUniformLighting=lighting,CurrentRetailUniformShader=shader};
    }

    /// <summary>006EBB9B..006EBC62 copies descriptor pass zero's uniform
    /// texture, shader and material slots. Expanded draw bindings and later
    /// passes belong to the swatch mesh, not to the new MaterialPassClass.</summary>
    public static EnbW3dRenderMaterialPass CaptureSwatchPass(EnbW3dRenderMesh mesh)
    {
        var descriptor = mesh.MaterialPasses.FirstOrDefault(pass => pass.Ordinal == 0)
            ?? throw new InvalidDataException("PassEffect swatch has no pass-zero descriptor");
        if (!EnbW3dRenderResourceCompiler.TryReplaceUniformDescriptor(
                (mesh).Copy().SetMaterialPasses([]), [descriptor], out var passes, out var diagnostic))
            throw new InvalidDataException(diagnostic);
        var pass = passes[0];
        // Preserve the existing absent-second-texture admission convention.
        // Both native texture slots were read above; an empty second slot
        // does not activate a second projector or texture stage.
        int stages=pass.TextureStages[1].CurrentRetailUniformTexture is not null?2:
            pass.TextureStages[0].CurrentRetailUniformTexture is not null?1:0;
        return (pass).Copy().SetTextureStages(pass.TextureStages.Take(stages).ToArray());
    }

    public string? GetRecoveryDiagnostic(EnbW3dRenderMaterialPass pass)
        => Stage1BoxScale.HasValue && pass.TextureStages.Count<2
            ? "Authored second projector has no texture; rendering stage zero, second stage inactive" : null;
    public static bool IsProjectedLinearParent(EnbW3dMapperRuntimeProgram program)
        => program.Kind == EnbW3dMapperRuntimeProgramKind.ProjectedLinear &&
           program.CoordinateSource == EnbW3dMapperCoordinateSource.CameraSpacePosition &&
           program.TransformShape == EnbW3dMapperTransformShape.ProjectedCount3 &&
           !program.ConfiguresBumpEnvironment;

    /// <summary>00980E50 / 00938840: parent matrix applied to (U,V,1,1).
    /// The wrapper selects COUNT2, so the resulting XY is not divided by Z/W.</summary>
    public static Matrix4x4 ComposeProjectedLinearParent(Matrix4x4 projectorFromWorld,
        Matrix4x4 canonicalProjection, EnbW3dMapperRuntimeEvaluation mapper)
    {
        // Native rows [0,1,3,3], transposed for System.Numerics row vectors.
        var lifted = projectorFromWorld;
        lifted.M13 = lifted.M14; lifted.M23 = lifted.M24;
        lifted.M33 = lifted.M34; lifted.M43 = lifted.M44;
        // Retail generated camera uses positive forward Z; canonical view uses -Z.
        var retailProjection = Matrix4x4.CreateScale(1,1,-1) * canonicalProjection;
        var linear = Matrix4x4.Identity;
        linear.M11 = mapper.TextureScale.X; linear.M22 = mapper.TextureScale.Y;
        linear.M41 = mapper.TextureOffset.X; linear.M42 = mapper.TextureOffset.Y;
        return lifted * retailProjection * linear;
    }

    /// <summary>00980E50 calls the parent's matrix virtual, not its apply
    /// virtual: generated-coordinate selection and bump-state writes do not
    /// replace the wrapper's COUNT2 camera-position input.</summary>
    public static Matrix4x4 ComposeParent(Matrix4x4 projectorFromWorld, Matrix4x4 projection,
        Matrix4x4 retailView, EnbW3dMapperRuntimeProgram program, EnbW3dMapperRuntimeEvaluation mapper)
    {
        if(IsProjectedLinearParent(program))return ComposeProjectedLinearParent(projectorFromWorld,projection,mapper);
        var lifted=projectorFromWorld;
        lifted.M13=lifted.M14;lifted.M23=lifted.M24;lifted.M33=lifted.M34;lifted.M43=lifted.M44;
        var parent=Matrix4x4.Identity;
        parent.M11=mapper.TextureScale.X;parent.M21=mapper.TextureCross.X;
        parent.M12=mapper.TextureCross.Y;parent.M22=mapper.TextureScale.Y;
        parent.M41=mapper.TextureOffset.X;parent.M42=mapper.TextureOffset.Y;
        if(program.Kind==EnbW3dMapperRuntimeProgramKind.VerticalEnvironmentScroll)
        {
            parent.M11=0;parent.M31=mapper.TextureScale.X;
        }
        else if(program.Kind is EnbW3dMapperRuntimeProgramKind.AxisProjection or EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection)
        {
            //936160/939AD0 multiply the chosen axes by the inverse view
            //basis. Translation is absent; the wrapper supplies Z=1.
            var inverseBasis=Matrix4x4.Transpose(retailView);
            inverseBasis.M14=inverseBasis.M24=inverseBasis.M34=0;
            inverseBasis.M41=inverseBasis.M42=inverseBasis.M43=0;inverseBasis.M44=1;
            var axes=Matrix4x4.Identity;
            if(program.Axis==EnbW3dMapperProjectionAxis.X)
            {axes.M11=0;axes.M21=1;axes.M22=0;axes.M32=1;}
            else if(program.Axis==EnbW3dMapperProjectionAxis.Y)
            {axes.M22=0;axes.M32=1;}
            parent=inverseBasis*axes*parent;
        }
        return lifted*parent;
    }

    public static bool IsDisabledSecondStage(EnbW3dRenderMaterialPass pass)
        => pass.SingleBindingSemantics is { } s && s.FixedFunction.TextureCombiners.Selector1 == 0;

    // 00940380 derives RenderObj +18 bit 4000 from the loader SORT latch
    // and pass-zero destination blend / alpha test (including per-triangle
    // shaders). 0093F53D skips our pass when this bit is set: the PassEffect
    // factory explicitly clears MaterialPass +30. Source blend alone is NOT
    // a reason to exclude a mesh.
    public static bool CanApplyToMesh(EnbW3dRenderMesh mesh)
    {
        if (mesh.GeometrySort.Requested || mesh.MaterialPasses.Count == 0) return false;
        var pass = mesh.MaterialPasses[0];
        if (pass.SingleBindingSemantics is { } single) return CanApplyToShader(single.FixedFunction.PackedRetailState);
        return pass.TriangleBindings.Count > 0 && pass.TriangleBindings.All(t =>
            t.Shader is { } shader && CanApplyToShader(shader.FixedFunction.PackedRetailState));
    }

    public static bool CanApplyToShader(uint packedState) => (packedState & 0x000400e0u) == 0;

    //006BB7E0 queues the whole owner before its Render virtual when the
    //front pass hides the base and has destination blending.0097D060 rejects
    //bucket0; unlike a procedural Render's zero branch, this is not immediate.
    public bool TryResolveSubmissionSort(uint shader,EnbW3dStaticSortSemantics meshSort,
        out EnbW3dStaticSortSemantics sort)
    {
        sort=meshSort;
        if(!HideBaseMesh || (shader&0xe0)==0)return true;
        sort=EnbW3dStaticSortSemantics.FromSingleShader(shader);
        return sort.Disposition==EnbW3dStaticSortDisposition.DeferredBucket;
    }

    public EnbW3dRenderMaterialPass BindToTargetMesh(EnbW3dRenderMaterialPass swatch,EnbW3dRenderMesh mesh)
    {
        var bound=BindToMesh(swatch,mesh);
        var stages=bound.TextureStages.ToArray();
        for(int i=0;i<stages.Length;i++)
        {
            if(i==0 && ProjectStage0 || i==1 && (Stage1BoxScale.HasValue || IsDisabledSecondStage(swatch)))continue;
            var mapper=i==0?swatch.SingleBindingSemantics!.Value.Mapper0Program:swatch.SingleBindingSemantics!.Value.Mapper1Program;
            if(mapper.CoordinateSource!=EnbW3dMapperCoordinateSource.PassThrough)continue;
            int index=swatch.TextureStages[i].EffectiveTextureCoordinateSetIndex;
            var set=mesh.RetailTextureCoordinateSets.SingleOrDefault(s=>s.Index==index);
            if(set is null || set.Coordinates.Count!=mesh.Positions.Count)
                throw new InvalidDataException($"PassEffect target mesh {mesh.Name} stage {i} requires authored UV set {index} with {mesh.Positions.Count} coordinates");
            // 0092EB90 copies each swatch material index; 009322F0 applies that TCI
            // against the target VB populated by 00978B70, not the swatch UV array.
            stages[i]=(stages[i]).Copy().SetRuntimeTexCoords(set.Coordinates).SetEffectiveRuntimeTexCoords(set.Coordinates).SetEffectiveTextureCoordinateSetIndex(index);
        }
        return (bound).Copy().SetTextureStages(stages);
    }
    public static EnbW3dRenderMaterialPass BindToMesh(EnbW3dRenderMaterialPass swatch, EnbW3dRenderMesh mesh)
    {
        if (swatch.SingleBindingSemantics is null || swatch.TextureStages.Count > 2 || swatch.VertexMaterials.Count != 1 || swatch.Shaders.Count != 1)
            throw new InvalidDataException("PassEffect requires a uniform swatch with zero to two texture stages");
        //A stock MaterialPassClass has descriptor slots but no source mesh
        //triangles. Its uniform slots bind directly to each target triangle.
        var template = swatch.TriangleBindings.Count>0 ? swatch.TriangleBindings[0] : new EnbW3dRenderTriangleBinding(0,0,
            swatch.VertexMaterials[0],swatch.Shaders[0],
            swatch.TextureStages.Select(stage=>stage.CurrentRetailUniformTexture).ToArray(),swatch.SingleBindingSemantics);
        var triangles = Enumerable.Range(0, mesh.Indices.Count / 3).Select(i => template with { TriangleOrdinal = i, MaterialVertexIndex = (int)mesh.Indices[i * 3] }).ToArray();
        var uv = new Vector2[mesh.Positions.Count];
        var stages=swatch.TextureStages.Select((stage,i)=>(stage).Copy().SetRuntimeTexCoords(uv).SetEffectiveRuntimeTexCoords(uv).SetEffectiveTextureCoordinateSetIndex(i)).ToArray();
        return (swatch).Copy().SetDiffuseColors([]).SetDiffuseIllumination([]).SetSpecularColors([]).SetTextureStages(stages).SetTriangleBindings(triangles);
    }
}
