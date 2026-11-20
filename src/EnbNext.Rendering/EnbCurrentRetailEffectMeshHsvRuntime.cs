using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>0048D1F0: post-load material, vertex-color and texture adjustment.
/// Geometry, mapper programs, alpha, lighting selectors and shader bits survive.</summary>
public static class EnbCurrentRetailEffectMeshHsvRuntime
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbRetailTextureResolver,
        SurfaceDevice> SurfaceDevices = new();
    private sealed class SurfaceDevice
    {
        public EnbCurrentRetailOrdinaryTextureOwner? Owner;
        public Func<string,EnbCurrentRetailTextureRegistry.Lease>? Acquire;
        public System.Runtime.CompilerServices.ConditionalWeakTable<EnbCurrentRetailTextureObjectState,
            EnbResolvedTextureResource> SourceImages = new();
    }
    public static void SetOrdinaryTextureDevice(EnbRetailTextureResolver textures,
        EnbCurrentRetailOrdinaryTextureOwner owner,Func<string,EnbCurrentRetailTextureRegistry.Lease>? acquire=null)
    {
        var device=SurfaceDevices.GetOrCreateValue(textures);
        if(device.Owner!=owner)device.SourceImages=new();
        device.Owner=owner;device.Acquire=acquire;
    }
    /// <summary>Unshifted source publication through the same retained object
    /// and allocated surface consumers. Generated and already-recolored images
    /// remain with their explicit runtime owners.</summary>
    public static EnbResolvedTextureResource ResolveSourceTexture(EnbRetailTextureResolver textures,
        string name,EnbCurrentRetailTextureObjectState? state=null)
    {
        var decoded=textures.Resolve(name);
        if(name.StartsWith('!') || decoded.CurrentRetailOriginalTextureName is not null ||
            decoded.Image?.Format is EnbTextureFormat.CurrentRetailRuntimeRgb8 or
                EnbTextureFormat.CurrentRetailRuntimeArgb8 or EnbTextureFormat.CurrentRetailRuntimeArgb4444 or
                EnbTextureFormat.CurrentRetailMissingArgb8 or EnbTextureFormat.CurrentRetailPackedSurface ||
            !SurfaceDevices.TryGetValue(textures,out var device) || device.Owner is not {} owner)
            return decoded;
        using var lease=state is null?device.Acquire?.Invoke(name):null;
        state??=lease?.State;
        if(state is null)return decoded;
        var result=device.SourceImages.GetValue(state,source=>
        {
            try
            {
                var allocated=owner.Load(textures,source,name,Vector3.Zero);
                if(allocated?.Image is {Format:EnbTextureFormat.CurrentRetailPackedSurface} surface)
                    source.PublishSurfaceFormat(surface.CurrentRetailSurfaceFormat,markReady:true);
                return allocated??decoded;
            }
            catch(Exception ex) when(ex is InvalidDataException or ArgumentException or OverflowException)
            {
                return new(name,EnbTextureResolutionStatus.UnsupportedOrInvalidTexture,
                    Diagnostic:$"Current-retail source allocation failed: {ex.Message}");
            }
        });
        if(result.Image is {} image && !ReferenceEquals(result,decoded))
            RecoloredImageStates.GetValue(image,_=>state);
        return result;
    }
    public static EnbResolvedTextureResource ResolveNamedTexture(EnbRetailTextureResolver textures,string name,Vector3 shift)
    {
        if(!SurfaceDevices.TryGetValue(textures,out var device) || device.Owner is not {} owner || device.Acquire is null)
            return textures.ResolveCurrentRetailHsv(name,shift);
        using var original=device.Acquire(name);
        var texture=textures.ResolveCurrentRetailHsv(name,shift,(source,hsv)=>owner.Load(textures,original.State,source,hsv));
        if(EnbCurrentRetailAssetVariantName.ChangesHsv(shift) && texture.CurrentRetailOriginalTextureName is not null)
            RetainRecoloredState(texture,original.State);
        return texture;
    }
    /// <summary>0048DD00/0048E0F0 use0048CF90 then0048CB40 on their
    /// retained texture, without0048B7B0's separate named-acquisition gate.</summary>
    public static EnbResolvedTextureResource ResolveRetainedTexture(EnbRetailTextureResolver textures,
        EnbResolvedTextureResource source,Vector3 shift,EnbCurrentRetailTextureObjectState? state=null)
    {
        if(source.Image?.Format==EnbTextureFormat.CurrentRetailMissingArgb8)return source;
        SurfaceDevices.TryGetValue(textures,out var device);
        using var lease=state is null?device?.Acquire?.Invoke(source.CurrentRetailOriginalTextureName??source.LogicalName):null;
        state??=lease?.State;
        var texture=textures.ResolveCurrentRetailMeshHsv(source,shift,
            state is {} retained && device?.Owner is {} owner?(name,hsv)=>owner.Load(textures,retained,name,hsv):null);
        if(state is not null)RetainRecoloredState(texture,state);
        return texture;
    }
    public static EnbCurrentRetailTextureObjectState? GetRecoloredTextureState(EnbResolvedTextureResource texture)
        => RecoloredTextureStates.TryGetValue(texture,out var state)?state:null;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbResolvedTextureResource,
        EnbCurrentRetailTextureObjectState> RecoloredTextureStates = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbTextureImage,
        EnbCurrentRetailTextureObjectState> RecoloredImageStates = new();
    private static EnbCurrentRetailTextureObjectState RetainRecoloredState(EnbResolvedTextureResource texture,
        EnbCurrentRetailTextureObjectState original)
    {
        var state=RecoloredTextureStates.GetValue(texture,_=>
        {
            //0048CC6C constructs a distinct owner from the original current
            //format.0094A4B0 publishes its completed allocation to that owner,
            //never back into the source used to prepare the recolor.
            var copy=original.CloneForRecolor();
            if(texture.Image is {Format:EnbTextureFormat.CurrentRetailPackedSurface} surface)
                copy.PublishSurfaceFormat(surface.CurrentRetailSurfaceFormat,markReady:true);
            return copy;
        });
        if(texture.Image is {} image)RecoloredImageStates.GetValue(image,_=>state);
        return state;
    }
    internal static EnbCurrentRetailTextureObjectState? GetRecoloredImageState(EnbTextureImage image)
        => RecoloredImageStates.TryGetValue(image,out var state)?state:null;
    public static EnbW3dRenderResource Apply(EnbW3dRenderResource source,
        EnbRetailTextureResolver textures, Vector3 shift,bool? conditionalMoveColorClamp=null)
    {
        if (shift == Vector3.Zero) return source;
        bool colorClamp=conditionalMoveColorClamp??(System.Runtime.Intrinsics.X86.X86Base.IsSupported&&
            (System.Runtime.Intrinsics.X86.X86Base.CpuId(1,0).Edx&0x8000)!=0);
        Vector3 Rgb(Vector3 rgb) => EnbCurrentRetailHsvRuntime.ShiftRgb(rgb, shift);
        Vector3 Normalize(EnbRenderPackedRgb8 rgb) => EnbW3dMaterialCompiler.DecodeMaterialRgb(rgb);
        var materials = new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dRenderVertexMaterial>(ReferenceEqualityComparer.Instance);
        var materialVisits = new Dictionary<EnbW3dRenderVertexMaterial,int>(ReferenceEqualityComparer.Instance);
        EnbW3dRenderVertexMaterial ShiftMaterial(EnbW3dRenderVertexMaterial original)
        {
            var value = original.CurrentRetailUniformOverride ?? new EnbW3dRenderMaterialUniformOverride(
                Normalize(original.Ambient), Normalize(original.Diffuse), Normalize(original.Specular),
                Normalize(original.Emissive), original.Power, original.Opacity);
            var result = original with { CurrentRetailUniformOverride = value with
            {
                Ambient = Rgb(value.Ambient), Diffuse = Rgb(value.Diffuse),
                Specular = Rgb(value.Specular), Emissive = Rgb(value.Emissive)
            }};
            //0048D1F0 edits the same native material; its+64 identity word
            //survives this immutable value replacement.
            EnbCurrentRetailMeshModelOwner.PreserveMaterialRuntimeWord64(original,result);
            return result;
        }
        //0048D1F0 visits model+80's material table, not the descriptor union.
        //Repeated pointers are visited repeatedly; all aliases see the final value.
        foreach(var mesh in source.Meshes)
            foreach(var material in mesh.CurrentRetailLoadedMaterials)
            {
                materials[material]=ShiftMaterial(materials.GetValueOrDefault(material,material));
                materialVisits[material]=materialVisits.GetValueOrDefault(material)+1;
            }
        EnbW3dRenderVertexMaterial Material(EnbW3dRenderVertexMaterial original)
            => materials.GetValueOrDefault(original,original);
        EnbW3dCompiledMaterialSemantics ShiftSemantics(EnbW3dCompiledMaterialSemantics value)
            => value with { Uniforms = value.Uniforms with
            {
                Ambient = Rgb(value.Uniforms.Ambient), Diffuse = Rgb(value.Uniforms.Diffuse),
                Specular = Rgb(value.Uniforms.Specular), Emissive = Rgb(value.Uniforms.Emissive)
            }};
        EnbW3dCompiledMaterialSemantics Semantics(EnbW3dCompiledMaterialSemantics value,EnbW3dRenderVertexMaterial? material)
        {
            int visits=material is null?0:materialVisits.GetValueOrDefault(material);
            for(int index=0;index<visits;index++)value=ShiftSemantics(value);
            return value;
        }
        var recoloredTextures=new Dictionary<string,EnbResolvedTextureResource>(StringComparer.OrdinalIgnoreCase);
        var textureBindings=new Dictionary<EnbW3dRenderTextureBinding,EnbW3dRenderTextureBinding>(ReferenceEqualityComparer.Instance);
        var textureStates=new Dictionary<EnbResolvedTextureResource,EnbCurrentRetailTextureObjectState>(ReferenceEqualityComparer.Instance);
        EnbW3dRenderTextureBinding Texture(EnbW3dRenderTextureBinding binding)
        {
            if(binding.IsUnbound || !recoloredTextures.TryGetValue(binding.LogicalName,out var recolored))return binding;
            if(textureBindings.TryGetValue(binding,out var retained))return retained;
            EnbCurrentRetailTextureObjectState? state=null;
            if(binding.CurrentRetailTextureState is {} originalState && !textureStates.TryGetValue(recolored,out state))
                textureStates.Add(recolored,state=RetainRecoloredState(recolored,originalState));
            retained=binding with
            {
                LogicalName = recolored.LogicalName,
                Sampling = binding.Sampling,
                CurrentRetailTextureState = state,
                CurrentRetailResolvedTexture = recolored,
            };
            textureBindings.Add(binding,retained);
            return retained;
        }
        EnbRenderColor32 Color(EnbRenderColor32 color)
        {
            uint word=(uint)color.A<<24|(uint)color.R<<16|(uint)color.G<<8|color.B;
            uint packed=EnbCurrentRetailHsvRuntime.ShiftMeshColor(word,shift,colorClamp);
            return new((byte)(packed>>16),(byte)(packed>>8),(byte)packed,(byte)(packed>>24));
        }
        EnbW3dRenderMaterialPass Pass(EnbW3dRenderMaterialPass pass)
        {
            // 009BBD10 skips authored 003E. SpecularColors is source
            // provenance, not the runtime COLO buffer queried by 0048D1F0.
            return (pass).Copy().SetCurrentRetailUniformMaterial(pass.CurrentRetailUniformMaterial is {} uniform
                    ? uniform with { Material = Material(uniform.Material) } : null).SetVertexMaterials(Array.AsReadOnly(pass.VertexMaterials.Select(b => b with { Material = Material(b.Material) }).ToArray())).SetTextureStages(Array.AsReadOnly(pass.TextureStages.Select(stage => (stage).Copy().SetTextures(Array.AsReadOnly(stage.Textures.Select(Texture).ToArray())).SetCurrentRetailUniformTexture(stage.BindingCardinality != EnbW3dRenderBindingCardinality.PerTriangle &&
                        stage.CurrentRetailUniformTexture is {} retainedTexture
                        ? Texture(retainedTexture) : stage.CurrentRetailUniformTexture)).ToArray())).SetTriangleBindings(EnbW3dTriangleBindingPool.Freeze(pass.TriangleBindings.Select(triangle => triangle with
                {
                    VertexMaterial = triangle.VertexMaterial is { } binding ? binding with { Material = Material(binding.Material) } : null,
                    Textures = Array.AsReadOnly(triangle.Textures.Select(t => t is null ? null : Texture(t)).ToArray()),
                    Semantics = triangle.Semantics is { } semantics ? Semantics(semantics,triangle.VertexMaterial?.Material) : null
                }).ToArray())).SetSingleBindingSemantics(pass.SingleBindingSemantics is { } single ? Semantics(single,(pass.TriangleBindings.Count==0?null:pass.TriangleBindings[0].VertexMaterial?.Material) ?? pass.CurrentRetailUniformMaterial?.Material) : null);
        }
        var meshes = source.Meshes.Select(mesh =>
        {
            //0048D1F0 reads colors from primary+7C;009587E0 also replaces
            //only that descriptor. Retained secondary+78 source chunks survive.
            recoloredTextures.Clear();
            textureBindings.Clear();
            textureStates.Clear();
            //0048D1F0 walks model+80's retained material-info table.0048CF90
            //reuses existing variants,009587E0 replaces primary descriptor
            //pointers, then the same material-info table entry is replaced.
            var loadedTextures = new List<EnbResolvedTextureResource>();
            var loadedStates = new List<EnbCurrentRetailTextureObjectState>();
            for(int i=0;i<mesh.CurrentRetailLoadedTextureNames.Count;i++)
            {
                string name=mesh.CurrentRetailLoadedTextureNames[i];
                var prepared=mesh.CurrentRetailLoadedTextureResources.Count==mesh.CurrentRetailLoadedTextureNames.Count
                    ? mesh.CurrentRetailLoadedTextureResources[i] : null;
                prepared ??= textures.Resolve(name);
                var originalTextureState=mesh.CurrentRetailLoadedTextureStates.Count==mesh.CurrentRetailLoadedTextureNames.Count
                    ? mesh.CurrentRetailLoadedTextureStates[i] : null;
                SurfaceDevices.TryGetValue(textures,out var surfaceDevice);
                var recolored=textures.ResolveCurrentRetailMeshHsv(prepared,shift,
                    originalTextureState is {} retainedState && surfaceDevice?.Owner is {} surfaceOwner
                        ? (name,hsv)=>surfaceOwner.Load(textures,retainedState,name,hsv) : null);
                loadedTextures.Add(recolored);
                if(mesh.CurrentRetailLoadedTextureStates.Count==mesh.CurrentRetailLoadedTextureNames.Count)
                {
                    var originalState=mesh.CurrentRetailLoadedTextureStates[i];
                    var recoloredState=RetainRecoloredState(recolored,originalState);
                    loadedStates.Add(recoloredState);
                    textureStates.TryAdd(recolored,recoloredState);
                }
                recoloredTextures.TryAdd(prepared?.LogicalName??name,recolored);
                recoloredTextures.TryAdd(name,recolored);
            }
            return (mesh).Copy().SetVertexMaterials(Array.AsReadOnly(mesh.VertexMaterials.Select(Material).ToArray())).SetCurrentRetailLoadedMaterials(Array.AsReadOnly(mesh.CurrentRetailLoadedMaterials.Select(Material).ToArray())).SetCurrentRetailLoadedMaterialRuntime(Array.AsReadOnly(mesh.CurrentRetailLoadedMaterialRuntime.Select(value=>value with {Material=Material(value.Material)}).ToArray())).SetCurrentRetailLoadedTextureResources(loadedTextures.AsReadOnly()).SetCurrentRetailLoadedTextureStates(loadedStates.AsReadOnly()).SetMaterialPasses(Array.AsReadOnly(mesh.MaterialPasses.Select(Pass).ToArray())).SetEffectiveColor1(Array.AsReadOnly(mesh.EffectiveColor1.Select(Color).ToArray()));
        }).ToArray();
        string variant = $"hsv:{BitConverter.SingleToInt32Bits(shift.X):X8}:{BitConverter.SingleToInt32Bits(shift.Y):X8}:{BitConverter.SingleToInt32Bits(shift.Z):X8}:{source.Key.ResourceName}";
        return source.Copy().SetKey(source.Key with { ResourceName = variant }).SetMeshes(Array.AsReadOnly(meshes));
    }
}
