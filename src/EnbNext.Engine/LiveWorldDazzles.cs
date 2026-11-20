using System.Collections.Immutable;
using System.Numerics;
using System.Diagnostics;

using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private readonly List<(EnbW3dDazzleDrawInstance Draw,float OrbitalVisibility,EnbResolvedRenderCamera Camera,float DepthMinimum,float DepthMaximum)> _frameDazzles=[];
    private readonly Dictionary<string,(EnbOpenGlBackdropRenderer Renderer,EnbUiRaster Raster)> _dazzleTextures=new(StringComparer.OrdinalIgnoreCase);
    private string _dazzleStatus="dazzles inactive";
    private readonly EnbDazzleVisibilityProfile _dazzleVisibilityProfile=new();
    private long _dazzleDrawTicks;
    private readonly Dictionary<(ulong Owner,ulong Generation,string Path,EnbW3dRenderResourceKey Asset,string Hlod,int Level,int Member,object? Factory),EnbRetailDazzleHistoryState> _dazzleHistory=[];
    private readonly Dictionary<(string Screen,string Name,int Ordinal,EnbDazzleDefinition Definition),EnbRetailDazzleHistoryState> _frontendDazzleHistory=[];
    private readonly Dictionary<(ulong Owner,ulong Generation,string Path,EnbW3dRenderResourceKey Asset,object? Factory),uint> _dazzleCreationClocks=[];
    private uint _dazzlePreviousFrameClock,_dazzleFrameElapsed=1;
    private readonly HashSet<EnbRetailDazzleHistoryState> _borrowedDazzleHistories=[];
    private void BeginDazzleFrame(uint now)
    {
        _dazzleVisibilityProfile.Reset();_dazzleDrawTicks=0;
        if(_shipExhaustOwner is {} source)_borrowedDazzleHistories.IntersectWith(source._dazzleHistory.Values);
        _dazzleFrameElapsed=Math.Max(1u,unchecked(now-_dazzlePreviousFrameClock));
        _dazzlePreviousFrameClock=now;
    }
    private (uint Now,uint Elapsed) DazzleFrameClock(EnbW3dDazzleDrawInstance draw)
    {
        var owner=draw.History is {} history && _borrowedDazzleHistories.Contains(history)?_shipExhaustOwner!:this;
        return(owner._dazzlePreviousFrameClock,owner._dazzleFrameElapsed);
    }
    private uint DazzleConstructionClock(LiveRootInstance instance)
    {
        for(var owner=instance;owner is not null;owner=owner.Parent)
        {
            if(owner.Root.DazzleFactoryCreationClock is {} clock)return clock;
            if(owner.Root.RslFactoryStateId>=0)break;
        }
        return _dazzlePreviousFrameClock;
    }
    private EnbW3dDazzleDrawInstance RetainDazzleHistory(LiveRootInstance instance,EnbW3dDazzleDrawInstance draw)
    {
        //4800A0 renders the retained model.96BFC0 updates that dazzle's94/98
        //history and reads its F8 clone epoch; selecting it creates no clone.
        if(_shipExhaustOwner is {} source && TryResolveScannerPrimitiveSource(instance,source,out var retained))
        {
            var borrowed=source.RetainDazzleHistory(retained,draw);
            if(borrowed.History is {} shared)_borrowedDazzleHistories.Add(shared);
            return borrowed;
        }
        if(BindOwnedDazzleRoot(instance) is {} bound)
        {
            var ownedHistory=RetainOwnedDazzleMember(bound,instance,draw);
            if(draw.HasConfiguredDazzleScale&&instance.Root.PrimitiveConfigurationOwner is not null&&
                TryResolvePrimitiveFactoryTransform(instance,out _,out float configuredScale,out var configuredHsv))
            {
                ResolvePrimitivePropertyInputs(instance,ref configuredScale,ref configuredHsv);
                draw=draw with {CurrentRetailDazzleScale=configuredScale};
            }
            return draw with {History=ownedHistory};
        }
        var factory=PhysicalParticleFactory(instance);
        var key=(instance.Object.RenderIdentity,VisualGeneration:factory is not null?0:instance.IsRsl?instance.Object.RenderObjectGeneration:instance.Object.VisualGeneration,InstancePath:RetainedModelOwnerPath(instance),
            instance.Root.Resource.Key,draw.HlodName,draw.LevelOrdinal,draw.MemberOrdinal,Factory:factory);
        var rootKey=(key.RenderIdentity,key.VisualGeneration,key.InstancePath,key.Key,key.Factory);
        //The object can remain retained after leaving the world's current
        //scene tree. Existing exact owner/member keys still identify its state.
        if(_shipExhaustOwner is {} retainedOwner &&
            (retainedOwner._dazzleHistory.ContainsKey(key)||retainedOwner._dazzleCreationClocks.ContainsKey(rootKey)||
                ScannerSourceRetainsFactory(instance,retainedOwner)))
        {
            var borrowed=retainedOwner.RetainDazzleHistory(instance,draw);
            if(borrowed.History is {} shared)_borrowedDazzleHistories.Add(shared);
            return borrowed;
        }
        if(!_dazzleCreationClocks.TryGetValue(rootKey,out uint created))
            _dazzleCreationClocks.Add(rootKey,created=DazzleConstructionClock(instance));
        _compatDazzleRoots.Add(rootKey);
        if(!_dazzleHistory.TryGetValue(key,out var history))_dazzleHistory.Add(key,history=new(created));
        _compatDazzleMembers.Add(key);
        if(draw.HasConfiguredDazzleScale && instance.Root.PrimitiveConfigurationOwner is not null &&
            TryResolvePrimitiveFactoryTransform(instance,out _,out float scale,out var hsv))
        {
            ResolvePrimitivePropertyInputs(instance,ref scale,ref hsv);
            draw=draw with {CurrentRetailDazzleScale=scale};
        }
        return draw with {History=history};
    }
    private readonly HashSet<(ulong Owner,ulong Generation,string Path,EnbW3dRenderResourceKey Asset,object? Factory)> _dazzlePruneOwners=[];
    private void PruneDazzleHistory(IReadOnlyList<LiveRootInstance> roots,
        IReadOnlySet<(ulong RenderIdentity,ulong VisualGeneration)> liveRslOwners)
    {
        var owners=_dazzlePruneOwners;
        owners.Clear();
        foreach(var root in roots)
        {
            if(BindOwnedDazzleRoot(root) is not null)continue;
            var factory=PhysicalParticleFactory(root);
            var owner=(root.Object.RenderIdentity,factory is not null?0:root.IsRsl?root.Object.RenderObjectGeneration:root.Object.VisualGeneration,
                RetainedModelOwnerPath(root),root.Root.Resource.Key,factory);
            owners.Add(owner);
            _dazzleCreationClocks.TryAdd(owner,DazzleConstructionClock(root));
            _compatDazzleRoots.Add(owner);
        }
        // 0045EED0 hides the state-local RenderObj; it does not destroy it.
        // Its dazzle clone epoch/history (0096BCE0/0096BFC0) survives until
        // the RSL owner is released, even while another state is selected.
        bool Retained(ulong identity,ulong generation,string path,object? factory)=>
            factory is not null?(_shipExhaustOwner??this)._livePhysicalParticleFactories.Contains(factory):path.StartsWith("rsl/",StringComparison.Ordinal)&&liveRslOwners.Contains((identity,generation));
        // Materialization precedes culling/LOD selection. An initially hidden
        // member must age from creation, not from its first visible draw.
        foreach(var key in _compatDazzleRoots.ToArray())
            if(!owners.Contains(key)&&!Retained(key.Owner,key.Generation,key.Path,key.Factory)){_dazzleCreationClocks.Remove(key);_compatDazzleRoots.Remove(key);}
        foreach(var key in _compatDazzleMembers.ToArray())
            if(!owners.Contains((key.Owner,key.Generation,key.Path,key.Asset,key.Factory))&&!Retained(key.Owner,key.Generation,key.Path,key.Factory)){_dazzleHistory.Remove(key);_compatDazzleMembers.Remove(key);}
        owners.Clear();
    }
    private void RenderDazzles(EnbGlApi gl,int framebuffer,int width,int height,EnbRetailAssetBundle assets)
        => RenderDazzles(gl, framebuffer, width, height, assets, null);

    private void RenderDazzles(EnbGlApi gl,int framebuffer,int width,int height,EnbRetailAssetBundle assets,bool? orbitalOnly,bool collectAudit=true)
    {
        if((DeveloperRenderDiagnostics.Available && !DeveloperRenderDiagnostics.Dazzles))return;
        int drawn=0,hidden=0,failedLayers=0;
        string? textureFailure=null;
        List<string>? visibilityAudit=collectAudit?new():null;
        foreach(var (draw,orbitalVisibility,camera,depthMinimum,depthMaximum) in _frameDazzles)
        {
            if (orbitalOnly.HasValue && (depthMinimum > 0) != orbitalOnly.Value) continue;
            var d=draw.Definition;
            var clock=DazzleFrameClock(draw);
            if(d.BlinkPeriod>0)
            {
                if(draw.History is not {} owner)
                {hidden++;textureFailure??="Dazzle blink owner unavailable";failedLayers++;continue;}
                // Retail skips preparation but requeues stale intensity/halo
                // at0096C099/7ED;0096EB60 draws them and0096EC30 only releases
                // queue membership. Correct that bug by suppressing this draw,
                // while preserving the native skipped history update.
                if(!owner.IsBlinkOn(clock.Now,d.BlinkPeriod,d.BlinkOnTime)){hidden++;continue;}
            }
            Vector3 position=draw.World.Translation,view=camera.WorldToCanonicalView(position);
            if(view.Z>=0||!camera.Projection.TryProjectViewPoint(view,out var ndc)){hidden++;continue;}
            float facing=-view.Z/view.Length();
            float area=Math.Clamp((facing-(1-d.Area))/d.Area,0,1);
            area=EnbRetailDazzleDirection.Apply(area,d.DirectionArea,d.Direction,draw.World,camera.Basis.Forward);

            // 0075DEE0: sixteen sphere samples multiplied by the root's
            // OrbitalManager visibility (004F5090). Halo is independent.
            float distanceFactor=d.DistanceIntensityFactor(view.LengthSquared());
            float intensity=MathF.Pow(area,d.IntensityPower)*d.Intensity*distanceFactor;
            bool queryVisibility=intensity>0; //0096C739..76B: zero/negative/unordered skips the provider.
            float visibility;
            int visibleSamples;
            if (!queryVisibility)
            {
                visibility=0;visibleSamples=0;
            }
            else if (_frontendDazzleCollisionPrepared)
            {
                visibility=EnbRetailDazzleVisibility.EvaluateScene(camera.Position,position,_frontendDazzleCollision);
                visibleSamples=visibility==0?0:16;
            }
            else
            {
                float radius = d.Radius * (draw.DazzleInstanceScale * (draw.CurrentRetailDazzleScale ?? new Vector3(draw.World.M11,draw.World.M12,draw.World.M13).Length()));
                var previousProfile=EnbDazzleVisibilityProfile.Active;
                try
                {
                    EnbDazzleVisibilityProfile.Active=_dazzleVisibilityProfile;
                    visibility = EnbRetailDazzleVisibility.Evaluate(position, radius, orbitalVisibility, camera, _dazzleCollisionRoots, _dazzleVisibilityProfile, _dazzleQueryStorage);
                }
                finally {EnbDazzleVisibilityProfile.Active=previousProfile;}
                visibleSamples = orbitalVisibility == 0 ? 0 : (int)(visibility / orbitalVisibility * 16);
            }
            visibilityAudit?.Add($"{visibleSamples}/16 orbital {orbitalVisibility:0.####}");
            bool visible=visibility>0;
            if(!visible)hidden++;
            if(queryVisibility)intensity*=visibility;
            float size=MathF.Pow(area,d.SizePower);
            if(d.HistoryWeight!=0)
            {
                if(draw.History is not {} history)
                {hidden++;textureFailure??="Dazzle history owner unavailable";failedLayers++;continue;}
                history.Value=history.Value.Advance(intensity,size,visibility,d.HistoryWeight,clock.Elapsed);
                intensity=history.Value.Intensity;size=history.Value.Size;
            }
            // 0096AC1F..27 fades both outputs. Correct the native early-return
            // bug at0096AAB5: it clears only dazzle intensity, leaving the halo
            // initialized to1 by0096C6CF beyond the fade end. Both stay dark.
            float halo=d.HaloIntensity*(d.HaloPower>0?MathF.Pow(facing,d.HaloPower):1)*distanceFactor;
            float scale=draw.DazzleInstanceScale * (draw.CurrentRetailDazzleScale ?? new Vector3(draw.World.M11,draw.World.M12,draw.World.M13).Length());
            Vector2 aspect=width>height?new(1,(float)width/height):new((float)height/width,1);
            Vector2 center=new(ndc.X,ndc.Y);
            // Retail 0096C890 submits the depth-tested halo independently of
            // the visibility-controlled dazzle and lens ghosts.
            // Mesh fragments store logarithmic scene depth. A depth-tested
            // screen quad must use that same encoding, then map to GL clip Z.
            float encodedHaloDepth=EnbOpenGlSceneDepth.Encode(camera.Projection.ClipRange,-view.Z);
            float haloDepth=(depthMinimum+(depthMaximum-depthMinimum)*encodedHaloDepth)*2-1;
            // 0096C890 reads HaloScale at definition +28/+2C directly. Only
            // DazzleScale (+74/+78) is multiplied by the instance scale (+E8).
            // Scaling the halo too spreads orbital color across the whole sky.
            Draw(d.HaloTexture,center,aspect*d.HaloScale,new(0,0,1,1),d.HaloColor*halo,haloDepth);
            if(!(intensity>0))continue;
            Draw(d.Texture,center,aspect*d.Scale*size*scale,new(0,0,1,1),d.Color*intensity);
            foreach(var flare in d.Flares)
                Draw(d.FlareTexture,center*flare.Location,aspect*flare.Size*(1+center.Length()),flare.Uv,
                    EnbRetailDazzleFlareColor.Evaluate(flare.Color,intensity));
        }
        if(collectAudit || failedLayers!=0)
            _dazzleStatus=$"dazzles {_frameDazzles.Count} layers {drawn} hidden {hidden} samples [{(visibilityAudit is null?"not sampled":string.Join("; ",visibilityAudit))}]"+
            (failedLayers==0?"":$" rejected textures {failedLayers}: {textureFailure}");
        void Draw(string name,Vector2 center,Vector2 extent,Vector4 uv,Vector3 color,float? depth=null)
        {
            long drawStart=Stopwatch.GetTimestamp();
            if(string.IsNullOrWhiteSpace(name))
            {failedLayers++;textureFailure??="Missing dazzle texture name";return;}
            if(!_dazzleTextures.TryGetValue(name,out var cached))
            {
                EnbTextureImage? image;
                //0096C890 acquires each named texture through manager+30, whose
                //failed load keeps the shared 009AF0F0 image.
                try{image=assets.Textures.ResolveCurrentRetailMissingTexture(name,assets.Paths.ClientExecutablePath).Image;}
                catch(Exception ex) when(ex is InvalidDataException or IOException or NotSupportedException)
                {failedLayers++;textureFailure??=$"{name}: {ex.Message}";return;}
                // Optional layer failures must not abort the world/frontend frame.
                // Keep the other native halo/dazzle/flare layers independent;
                // do not invent a replacement texture for a missing resource.
                if(image is null){failedLayers++;textureFailure??=$"Missing dazzle texture {name}";return;}
                var bytes=ImmutableArray.CreateBuilder<byte>(image.Width*image.Height*4);
                foreach(var p in image.Pixels.Span){bytes.Add(p.B);bytes.Add(p.G);bytes.Add(p.R);bytes.Add(p.A);}
                var renderer=new EnbOpenGlBackdropRenderer();renderer.InitializeDazzle(gl);
                cached=(renderer,new(image.Width,image.Height,bytes.MoveToImmutable()){CompressedSource=image});_dazzleTextures.Add(name,cached);
            }
            cached.Renderer.RenderDazzle(gl,framebuffer,width,height,cached.Raster,center,extent,uv,color,depth);drawn++;
            _dazzleDrawTicks+=Stopwatch.GetTimestamp()-drawStart;
        }
    }
    private void DeinitializeDazzles(EnbGlApi? gl)
    {
        foreach(var item in _dazzleTextures.Values)item.Renderer.Deinitialize(gl);
        _dazzleCollisionRoots.Clear();_dazzleMeshRays.Clear();_effectDazzleCollision.Clear();_effectEmitterCollision.Clear();_jetBufferCollision.Clear();
        _frontendDazzleCollision.Clear();_frontendDazzleMeshRays.Clear();_frontendDazzleCollisionPrepared=false;
        _dazzleTextures.Clear();_frameDazzles.Clear();
    }
}
