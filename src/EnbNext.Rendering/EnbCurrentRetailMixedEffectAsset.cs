using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbCurrentRetailStreakAttachment(EnbW3dRenderResourceKey Key,
    EnbCurrentRetailParticleEmitterDefinition Definition, int PivotIndex)
{
    public EnbW3dRenderTextureLocation? Texture { get; init; }
    public string? RuntimeTextureName { get; init; }
    public EnbResolvedTextureResource? RetainedTexture { get; init; }

    internal EnbCurrentRetailStreakAttachment PrepareTexture(EnbRetailAssetBundle assets)
    {
        if(string.IsNullOrEmpty(Definition.TextureFileName)) return this with {Texture=null,RuntimeTextureName=null,RetainedTexture=null};
        var resolved=assets.Textures.ResolveCurrentRetailMissingTexture(Definition.TextureFileName,assets.Paths.ClientExecutablePath);
        if(resolved.Image is null) throw new InvalidDataException($"Streak texture unavailable: {Definition.TextureFileName}: {resolved.Diagnostic}");
        if(resolved.ResourceLocation is {} location) return this with {Texture=EnbW3dRenderTextureLocation.From(location),RetainedTexture=resolved};
        return this with {RuntimeTextureName=resolved.LogicalName,RetainedTexture=resolved};
    }

    public EnbCurrentRetailStreakAttachment ApplyHsv(EnbRetailAssetBundle assets,Vector3 shift)
    {
        var texture=RetainedTexture is {} retained
            ? EnbCurrentRetailEffectMeshHsvRuntime.ResolveRetainedTexture(assets.Textures,retained,shift) : null;
        return this with {RetainedTexture=texture,RuntimeTextureName=texture?.LogicalName??RuntimeTextureName,
            Definition=Definition.Copy().SetTextureFileName(texture?.LogicalName??Definition.TextureFileName).SetColor(EnbCurrentRetailHsvRuntime.ShiftParticleColor(Definition.Color,shift))};
    }

    public static EnbCurrentRetailStreakAttachment LoadStandalone(EnbRetailAssetBundle assets,int baseAssetId)
        => LoadStandalone(assets,assets.BaseAssetResolver!.Resolve(baseAssetId));

    internal static EnbCurrentRetailStreakAttachment LoadStandalone(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved)
    {
        if(resolved.Definition.File is not {} file ||
            !assets.FileResources.TryReadResource(file,out var bytes,out var location) ||
            !EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var d,out _,assets.Textures))
            throw new InvalidDataException("Standalone streak definition unavailable");
        if(!string.Equals(d.Name,resolved.Definition.Name,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Standalone streak prototype name does not match BaseAsset NAME");
        EnbCurrentRetailStreakBufferRuntime.Validate(d);
        // A standalone emitter uses the supplied root pose, with no hierarchy.
        return new EnbCurrentRetailStreakAttachment(EnbW3dRenderResourceKey.From(location),d,0).PrepareTexture(assets);
    }
}

/// <summary>HLOD or HMODEL with bone-attached sprite or streak emitters.
/// Emitters participate as zero-bounds/cost RenderObjs; their independent
/// particle buffers draw separately. No fabricated texture binding.</summary>
public sealed record EnbCurrentRetailMixedEffectAsset(EnbW3dRenderResource Resource,
    IReadOnlyList<EnbCurrentRetailStreakAttachment> Streaks)
{
    public IReadOnlyList<EnbCurrentRetailRObjParticleAsset> Sprites { get; init; } = [];
    public IReadOnlyList<(bool Streak,int Index)> ParticleConstructionOrder {get;init;}=[];
    public EnbW3dRenderHierarchyAnimation? Animation { get; init; }
    public EnbRslStateFlags? AnimationFlags { get; init; }
    // Static finite lines submit through the same native-validated coalescer
    // as other mode-2 buffers; there is no separate bounded-strip fallback.
    public string? RecoveryDiagnostic => null;
    public float MaximumParticleLifetimeSeconds => Streaks.Select(s=>s.Definition.LifetimeSeconds).Concat(Sprites.Select(s=>s.Definition.LifetimeSeconds)).Max();
    public float EmitterFixedCost => (Streaks.Count + Sprites.Count) * EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback;
    public static EnbCurrentRetailMixedEffectAsset Load(EnbRetailAssetBundle assets, EnbW3dRenderResource resource,
        EnbCurrentRetailW3dAggregateDefinition aggregate,
        EnbW3dRenderHierarchyAnimation? animation=null, EnbRslStateFlags? animationFlags=null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? prototypes=null)
        => LoadCore(assets,resource,aggregate,animation,animationFlags,false,prototypes);

    internal static EnbCurrentRetailMixedEffectAsset LoadInline(EnbRetailAssetBundle assets,EnbW3dRenderResource resource)
        => LoadCore(assets,resource,null,null,null,true,null);

    internal static bool IsRetainedNonParticleChild(EnbW3dRenderResource resource,string name)
        => EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(name) ||
           resource.Meshes.Any(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,name)) ||
           resource.ExternalRenderObjects.Any(child=>child.Name.Equals(name,StringComparison.OrdinalIgnoreCase) &&
               (child.BoxDefinition is not null || child.DazzleDefinition is not null || child.DazzleTypeName is not null ||
                child.SpecialMaterialDefinition is not null || child.Kind==EnbW3dExternalRenderObjectKind.NullObject));

    private static EnbCurrentRetailMixedEffectAsset LoadCore(EnbRetailAssetBundle assets,EnbW3dRenderResource resource,
        EnbCurrentRetailW3dAggregateDefinition? aggregate,EnbW3dRenderHierarchyAnimation? animation,
        EnbRslStateFlags? animationFlags,bool inline,IReadOnlyDictionary<string,EnbResolvedW3dResource>? prototypes)
    {
        resource=EnbCurrentRetailRObjEffectAsset.BindDazzleDefinitions(assets,resource);
        var h = resource.HierarchicalLods.SingleOrDefault();
        var tree = resource.Hierarchies.SingleOrDefault();
        // ParticleEmitterClass 00B12CD8+120 ->00408F03 ->00462E30 is
        // RET4: parent-forced detail does not change its detached buffer.
        bool hmodel=h is null && !inline && resource.HierarchyModels.Count==1;
        if(hmodel)EnbCurrentRetailRObjEffectAsset.ValidateHmodel(resource);
        if (tree is null || (animation is not null && animationFlags is null) ||
            (hmodel ? aggregate!.Attachments.Count<1 : h is null || h.Levels.Count < 1 ||
            (inline ? h.Aggregate is null : h.HasAggregateExtension) ||
            (animation is not null && animationFlags is null) ||
            // Native 009A3A50 iterates the authored count; GE_blind01 has 18.
            // Retain per-emitter validation; external aggregates have no count ceiling.
            !h.HierarchyName.Equals(tree.Name, StringComparison.OrdinalIgnoreCase) ||
            //0098BAD0 allocates the authored0703 count for inline0705 too.
            (inline ? h.Aggregate!.Members.Count <1 : aggregate!.Attachments.Count <1) ||
            h.Levels.SelectMany(l => l.Members).Any(m => m.ReferenceIndex >= tree.Pivots.Count ||
                (!EnbCurrentRetailRenderObjectSemantics.IsNoOpDrawRenderObject(resource, m.Name) &&
                 !resource.Meshes.Any(mesh => EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh, m.Name))))))
            throw new InvalidDataException("Mixed RObjEffect requires a locally bound HLOD or HMODEL with supported animation");
        var children = new List<EnbCurrentRetailStreakAttachment>();
        var sprites = new List<EnbCurrentRetailRObjParticleAsset>();
        var constructionOrder=new List<(bool Streak,int Index)>();
        var members = new List<EnbW3dRenderHlodMember>();
        var external = resource.ExternalRenderObjects.ToList();
        // 0098B5D0/0098BAD0: inline 0705 members already carry hierarchy
        // indices. Do not reinterpret them through 0600's bone-name fallback.
        var attachments=inline ? h!.Aggregate!.Members.Select(m=>(SubobjectName:m.Name,Pivot:checked((int)m.ReferenceIndex))) :
            aggregate!.Attachments.Select(a=>(a.SubobjectName,Pivot:EnbCurrentRetailAggregateBoneLookup.Resolve(tree,a.BoneName)));
        foreach (var attachment in attachments)
        {
            int pivot = attachment.Pivot;
            //0098C420 ->0098E4B0 retains every acquired permanent child;
            //0098DD50 dispatches each child's own render consumer. Only
            //emitters construct particle buffers here.
            if(inline && pivot>=0 && pivot<tree.Pivots.Count &&
                IsRetainedNonParticleChild(resource,attachment.SubobjectName))
            {
                members.Add(new((uint)pivot,attachment.SubobjectName));
                continue;
            }
            if (pivot >= tree.Pivots.Count || !EnbCurrentRetailParticlePrototypeLookup.TryResolve(assets,
                attachment.SubobjectName,prototypes,out var d,out var location))
                throw new InvalidDataException("Mixed RObjEffect requires named bone-bound particle attachments");
            bool retainedFiniteLine=animation is null && d.MaximumEmissions>0 &&
                (EnbCurrentRetailParticleEmitterRuntime.SupportsEngineTrail(d) ||
                 EnbCurrentRetailParticleEmitterRuntime.SupportsJetLine(d));
            // Static finite mode-2 buffers have the same allowance/endpoint
            // ownership as animated ones (0097E530/0097EB80, 0095E242).
            // Rendering uses the shared view-space subdivision/coalescer path.
            if (d.RenderMode is 0 or 1 || (d.RenderMode==2 && (animation is not null || d.MaximumEmissions==0 || retainedFiniteLine)))
            {
                if(animation is not null || d.RenderMode==2) EnbCurrentRetailRObjParticleAsset.ValidateAnimated(d);
                else if (d.MaximumEmissions == 0) EnbCurrentRetailRObjParticleAsset.Validate(d);
                else EnbCurrentRetailRObjParticleAsset.ValidateFinite(d);
                if(animation is null && d.RenderMode==2 && !retainedFiniteLine)
                    EnbCurrentRetailShipParticleRuntime.Validate(d,allowInheritedMotion:true);
                constructionOrder.Add((false,sprites.Count));
                sprites.Add(new EnbCurrentRetailRObjParticleAsset(EnbW3dRenderResourceKey.From(location), null, d, pivot).PrepareTexture(assets));
            }
            else
            {
                EnbCurrentRetailStreakBufferRuntime.Validate(d);
                constructionOrder.Add((true,children.Count));
                children.Add(new EnbCurrentRetailStreakAttachment(EnbW3dRenderResourceKey.From(location),d,pivot).PrepareTexture(assets));
            }
            members.Add(new((uint)pivot, d.Name));
            external.Add(new(d.Name, EnbW3dExternalRenderObjectKind.ParticleEmitter, location.Name, location.ArchiveName)
                { ParticleEmitterDefinition = d });
        }
        // 0600 attachment setup enters the same secondary HLOD child array as
        // 0705. Reuse its bounds/cost/draw semantics, including zero-size emitters.
        //0098C7A0's HMODEL has the same secondary child array at runtime.
        //Keep its authored mesh representation; the mixed owner holds the
        //zero-bounds emitters and their independent fixed contributions.
        resource = resource.Copy().SetHierarchicalLods(h is null ? resource.HierarchicalLods : [h with { Aggregate = new(inline?h.Aggregate!.RawValue:0, members) }]).SetExternalRenderObjects(external);
        resource=EnbCurrentRetailMeshTexturePreparation.Prepare(resource,assets);
        return new(resource, children) { Sprites = sprites.AsReadOnly(), Animation=animation, AnimationFlags=animationFlags,
            ParticleConstructionOrder=constructionOrder.AsReadOnly() };
    }

    public EnbHlodDetailOwner Candidate(Matrix4x4 world, float radius, EnbResolvedRenderCamera camera, int currentLevel)
        => EnbCurrentRetailRObjEffectAsset.BuildCandidate(Resource, world, radius, camera, currentLevel);

}

/// <summary>Retained startup poses are consumed once; each emitter owns its own
/// interval and burst stream. Existing particles keep their world-space births.</summary>
public sealed class EnbCurrentRetailMixedParticleRuntime
{
    private int _processed;
    public IReadOnlyList<EnbCurrentRetailStreakBufferRuntime> Buffers { get; }
    public IReadOnlyList<SpriteEmitter> Sprites { get; }
    public EnbCurrentRetailAnimatedParticleRuntime? AnimatedSprites { get; }
    private readonly IReadOnlyList<(bool Streak,int Index)> _order;
    public uint LastAge { get; private set; }
    public EnbCurrentRetailMixedParticleRuntime(EnbCurrentRetailMixedEffectAsset asset, float radius, uint seed,
        EnbRetailParticleRandomStreams? randomStreams=null)
    {
        var buffers=new EnbCurrentRetailStreakBufferRuntime[asset.Streaks.Count];
        var sprites=new SpriteEmitter[asset.Sprites.Count];
        var order=asset.ParticleConstructionOrder.Count!=0?asset.ParticleConstructionOrder:
            Enumerable.Range(0,buffers.Length).Select(i=>(Streak:true,Index:i)).Concat(Enumerable.Range(0,sprites.Length).Select(i=>(Streak:false,Index:i))).ToArray();
        _order=order;
        foreach(var child in order)
            if(child.Streak)buffers[child.Index]=new(asset.Streaks[child.Index].Definition,radius,seed,randomStreams);
            else sprites[child.Index]=new(asset.Sprites[child.Index],asset.Animation is not null,radius,randomStreams);
        Buffers=buffers;Sprites=sprites;
        if(asset.Animation is not null && asset.Sprites.Count!=0)
        {
            AnimatedSprites=new(sprites.Select(s=>s.Animated!));
        }
    }

    public sealed class SpriteEmitter
    {
        public EnbCurrentRetailParticlePoseHistory Poses { get; } = new();
        public EnbCurrentRetailShipParticleRuntime? Continuous { get; }
        public EnbCurrentRetailAnimatedParticleRuntime.Emitter? RetainedLine { get; }
        public EnbCurrentRetailAnimatedParticleRuntime.Emitter? Animated {get;}
        public EnbRetailParticleReplayHistory? Replay {get;}
        internal uint? LastAge;
        public Matrix4x4 CurrentWorld { get; internal set; } = Matrix4x4.Identity;
        public SpriteEmitter(EnbCurrentRetailRObjParticleAsset asset,bool animated,float nativeBirthScale=1f,
            EnbRetailParticleRandomStreams? randomStreams=null)
        {
            if(animated)Animated=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(asset.Definition,randomStreams);
            if (!animated && asset.Definition.MaximumEmissions == 0) Continuous = new(asset.Definition, allowInheritedMotion:true,nativeBirthScale:nativeBirthScale,randomStreams:randomStreams);
            if (!animated && asset.Definition.RenderMode==2 && asset.Definition.MaximumEmissions>0)
                RetainedLine=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(asset.Definition,randomStreams);
            if(!animated && Continuous is null && RetainedLine is null)Replay=new(asset.Definition,randomStreams);
        }
    }

    public void Advance(EnbCurrentRetailMixedEffectAsset asset, EnbCurrentRetailParticlePoseHistory history, float radius,
        IReadOnlyDictionary<uint,EnbCurrentRetailRslAnimationSequenceRuntime.Sample>? animationSamples=null)
    {
        for (; _processed < history.Samples.Count; _processed++)
        {
            var sample = history.Samples[_processed];
            if (_processed > 0 && sample.Age <= LastAge) continue;
            var world = Matrix4x4.CreateFromQuaternion(sample.Rotation) * Matrix4x4.CreateTranslation(sample.Position);
            float frame=0;
            var animation=asset.Animation;
            string? error=null;
            if(animationSamples is not null)
            {
                if(!animationSamples.TryGetValue(sample.Age,out var observed))
                    throw new InvalidDataException("Mixed pose has no matching animation observation");
                animation=observed.BoundAnimation;frame=observed.Frame;
            }
            else if(animation is not null && !EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(animation,asset.AnimationFlags!,0,sample.Age,out frame,out error))
                throw new InvalidDataException(error);
            if (!EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(), world, animation, frame,null,out var pose,out error,storedTranslationScale:radius))
                throw new InvalidDataException(error);
            foreach(var child in _order)
            {
                int i=child.Index;
                if(child.Streak)
                {
                    int pivot = asset.Streaks[i].PivotIndex;
                    if (!Matrix4x4.Decompose(pose.WorldTransforms[pivot], out _, out var rotation, out var position))
                        throw new InvalidDataException("Mixed effect attachment pose is not decomposable");
                    Buffers[i].Advance(sample.Age, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position), pose.Visibility[pivot]);
                }
                else AdvanceSprite(i);
            }
            void AdvanceSprite(int i)
            {
                int pivot = asset.Sprites[i].PivotIndex;
                if (!Matrix4x4.Decompose(pose.WorldTransforms[pivot], out _, out var rotation, out var position))
                    throw new InvalidDataException("Mixed sprite attachment pose is not decomposable");
                var rigid = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                Sprites[i].CurrentWorld = Sprites[i].Continuous is not null || Sprites[i].RetainedLine is not null || Sprites[i].Animated is not null
                    ? pose.WorldTransforms[pivot] : Matrix4x4.CreateScale(radius) * rigid;
                if ((Sprites[i].Animated??Sprites[i].RetainedLine) is {} retained)
                {
                    var previous=Sprites[i].LastAge;
                    EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(retained,asset.Sprites[i].Definition,
                        sample.Age,previous.HasValue?sample.Age-previous.Value:0,Sprites[i].CurrentWorld,
                        pose.Visibility[pivot],radius,!previous.HasValue);
                    Sprites[i].LastAge=sample.Age;
                }
                else if (Sprites[i].Continuous is { } continuous) continuous.Advance(sample.Age, rigid, pose.Visibility[pivot]);
                else
                {
                    if (!pose.Visibility[pivot]) throw new InvalidDataException("Static finite sprite visibility gate unavailable");
                    Sprites[i].Poses.Record(sample.Age, rigid);
                    Sprites[i].Replay!.ObserveFinite(sample.Age,b=>Sprites[i].Poses.Resolve(b,radius),
                        b=>Sprites[i].Poses.ResolveInheritedVelocity(b,asset.Sprites[i].Definition.VelocityInheritanceFactor));
                }
            }
            LastAge = sample.Age;
            AnimatedSprites?.ObserveOwnerClock(sample.Age);
        }
        history.RetainLatestSample();
        _processed=history.Samples.Count;
    }
}
