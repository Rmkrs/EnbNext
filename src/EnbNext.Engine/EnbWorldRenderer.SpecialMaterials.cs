using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private Vector3 _bridgeSpecialAmbient;
    private Vector3 _specialSceneAmbient;
    private void SubmitSpecialScene(EnbGlApi gl,int framebuffer,int width,int height,
        IEnumerable<(LiveLanePlan Lane,LiveGpuResource Gpu)> draws,IReadOnlyList<EnbW3dDrawTask> special,
        EnbResolvedRenderCamera camera,EnbW3dLightEnvironment lights,Vector3 ambient,uint clock)
    {
        _specialDeviceLights=lights;
        _specialSceneAmbient=ambient;
        bool flushed=false;
        void Flush()
        {
            flushed=true;
            if(special.Count==0)return;
            var receipt=FlushSpecialMaterials(gl,framebuffer,width,height,special,_=>camera,lights,ambient,
                _retailAssetsAccessor()!,EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault);
            if(receipt.Diagnostics.Count!=0)System.Diagnostics.Trace.WriteLine(string.Join(";",receipt.Diagnostics));
        }
        foreach(var draw in draws)
        {
            if(!flushed && draw.Lane.Textured!.Submission.Packet.OrderKey.StaticSort.RuntimeLevel!=0)Flush();
            SubmitGpuResource(gl,framebuffer,width,height,draw.Gpu,draw.Lane,new(false,0,0,0),default,null,clock);
        }
        if(!flushed)Flush();
    }

    private EnbW3dLightEnvironment? _specialDeviceLights;
    private readonly EnbOpenGlSpecialMaterialBackend _specialMaterialBackend = new();
    private readonly HashSet<EnbRetailSpecialMaterialInstance> _specialCompositeUsed=[];
    private readonly Dictionary<(ulong Identity,ulong Generation,string Path,int Pivot,EnbCurrentRetailSpecialMaterialDefinition Definition,bool IsRsl,object? Factory),EnbRetailSpecialMaterialInstance> _specialCompositeInstances=[];

    private void AppendSpecialEffect(List<EnbW3dDrawTask> packets,EffectObjectInstance effectInstance,int child,
        EnbCurrentRetailSpecialMaterialDefinition definition,EnbW3dRenderResourceKey key,Matrix4x4 world,
        EnbW3dLightEnvironment lights,long clock,IEnbRenderObject owner,string path,ref int draws)
    {
        if((definition.Flags&0x1000)!=0)return;
        var children=effectInstance.SpecialMaterials;
        if(!children.TryGetValue(child,out var instance))children.Add(child,instance=_prototypeResources.CreateSpecialMaterialInstance(definition));
        foreach(var packet in EnbRetailSpecialMaterialDrawPacket.Build(instance,key,world,lights,clock,
            owner.RenderIdentity,owner.VisualGeneration,owner.GameId,out _))
        {packets.Add((packet).ForkTask().SetRenderInstancePath(path));draws++;}
    }

    private EnbW3dDrawTask RetainSpecialPacket(EnbW3dDrawTask packet)
    {
        if(packet.SpecialMaterial is not {BindRetainedInstance:true} draw)return packet;
        var key=(packet.RenderIdentity,draw.RetainedPhysicalFactory is not null?0:draw.RetainedRslGeneration??packet.VisualGeneration,draw.RetainedOwnerPath??packet.RenderInstancePath,packet.PivotIndex,draw.Instance.Definition,draw.RetainedRslGeneration.HasValue,draw.RetainedPhysicalFactory);
        if(!_specialCompositeInstances.TryGetValue(key,out var instance))_specialCompositeInstances.Add(key,instance=_prototypeResources.CreateSpecialMaterialInstance(draw.Instance.Definition));
        _specialCompositeUsed.Add(instance);
        if(!instance.TryGetGeometry(draw.MemberOrdinal,out var geometry,out var error))throw new InvalidDataException(error);
        return (packet).ForkTask().SetSpecialMaterial(draw with {Instance=instance,Geometry=geometry,BindRetainedInstance=false});
    }

    private EnbOpenGlSpecialMaterialReceipt FlushSpecialMaterials(EnbGlApi gl,int framebuffer,int width,int height,
        IReadOnlyList<EnbW3dDrawTask> packets,Func<EnbW3dDrawTask,EnbResolvedRenderCamera> camera,
        EnbW3dLightEnvironment lights,Vector3 ambient,EnbRetailAssetBundle assets,
        EnbRenderTextureRuntimeSettings settings,EnbRenderTextureBackendCapabilities capabilities, int viewportInset=0, bool retailCapturePixelCenters=false, Action<EnbW3dDrawTask>? beforeDraw=null)
    {
        EnsureSwatchCatalog(assets);
        _specialSceneAmbient=ambient;
        var special=packets.Where(p=>p.SpecialMaterial is not null).Select(RetainSpecialPacket).ToArray();
        if(special.Length==0)return new(0,0,[]);
        if(!_specialMaterialBackend.IsInitialized)_specialMaterialBackend.Initialize(gl);
        foreach(var packet in special)packet.SpecialMaterial!.Instance.RetainTextures(_prototypeResources.SpecialMaterialTextures);
        foreach(var group in special.GroupBy(p=>p.SpecialMaterial!.Instance))
        {
            var first=group.First();
            group.Key.ObserveContext(first.CurrentRetailRslPassContext?.Flags??0,first.SpecialMaterialProjection);
        }
        return _specialMaterialBackend.Submit(gl,framebuffer,width,height,special.Select(p=>new EnbOpenGlSpecialMaterialSubmission(p,camera(p))).ToArray(),
            _specialDeviceLights??lights,ambient,resource=>
            {
                var owner=special.Select(p=>p.SpecialMaterial!.Instance.Textures(resource)).FirstOrDefault(o=>o is not null)??_prototypeResources.SpecialMaterialTextures(resource)
                    ??throw new InvalidDataException("Special-material FILE texture owner is unavailable.");
                return new(Plan(owner.Base),Plan(owner.Normal),owner.Cube);
            },viewportInset:viewportInset,retailCapturePixelCenters:retailCapturePixelCenters,beforeDraw:beforeDraw);
        EnbOpenGlTextureStageUploadPlan? Plan(EnbW3dRenderTextureBinding? binding)
        {
            if(binding is null)return null;
            var image=ResolveTextureImage(binding,assets.Textures,new Dictionary<string,EnbTextureImage>(),null)
                ??throw new InvalidDataException($"Special-material texture '{binding.LogicalName}' is unavailable.");
            var mip=EnbTextureUploadMipPlanner.Compile(image,settings,retailCreationSelector:binding.Sampling.RetailCreationSelector,
                retailCreationMipLevels:binding.CurrentRetailTextureState?.RetailCreationMipLevels);
            var sampler=EnbW3dTextureSamplerCompiler.Compile(binding,settings,capabilities);
            if(!EnbOpenGlStage0TextureUploadPlanner.TryPlanStage(0,binding,image,mip,sampler,out var plan,out var error))
                throw new InvalidDataException(error);
            return plan;
        }
    }

    private bool TryBuildSpecialMaterialDrawList(LiveRootInstance instance,long clock,EnbW3dLightEnvironment lights,
        out EnbW3dDrawInvocation drawList,out string? diagnostic)
    {
        drawList=default!; diagnostic=null;
        if(!TryResolvePrimitiveFactoryTransform(instance,out var world,out _,out _))
        {diagnostic="Special-material owner transform is unavailable.";return false;}
        // Class24 inherits the no-op Scale and HSV virtuals. The containing
        // hierarchy still scales pivot translations, independently of this mesh.
        // LiveRootInstance is a frame traversal node, not the retained native
        // class24 clone (00A09090). Keep delegates/context until owner removal.
        var factory=PhysicalParticleFactory(instance);
        var key=(instance.Object.RenderIdentity,factory is not null?0:instance.IsRsl?instance.Object.RenderObjectGeneration:instance.Object.VisualGeneration,
            RetainedModelOwnerPath(instance),-1,instance.Root.SpecialMaterial!.Definition,instance.IsRsl,factory);
        if(!_specialCompositeInstances.TryGetValue(key,out var owner))
            _specialCompositeInstances.Add(key,owner=instance.Root.SpecialMaterial.Clone());
        _specialCompositeUsed.Add(owner);
        var packets=EnbRetailSpecialMaterialDrawPacket.Build(owner,instance.Root.Resource.Key,world,lights,clock,
            instance.Object.RenderIdentity,instance.Object.VisualGeneration,instance.Object.GameId,out var errors);
        diagnostic=errors.FirstOrDefault();
        drawList=EnbW3dDrawInvocation.Create(instance.Root.Resource.Key,instance.Object.RenderIdentity,instance.Object.VisualGeneration,clock,null,null);
        drawList.Packets.AddRange(packets);
        return true;
    }
}
