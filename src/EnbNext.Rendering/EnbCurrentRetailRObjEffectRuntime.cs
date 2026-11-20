using System.Numerics;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>006EA830 / 006EF160 / 006EF200 / 006EF250 / 006EF4A0:
/// source-tracked objects, source-radius scaling and camera-side front placement.
/// Reset and shared mapper epochs are selected by the authored effect definition.</summary>
public sealed record EnbCurrentRetailRObjEffectDefinition(int BaseAssetId, bool ScaleToRadius, bool InFrontOfObject = false, bool ResetMappers = true, float AssetScale = 1, Vector3 HsvShift = default, bool TrackSource = true, bool FaceCamera = false)
{
    //48B220 formats the raw arguments when either adjustment passes its gate.
    //The effective values alone cannot identify the native cached variant.
    public (float Scale,Vector3 Hsv)? FactoryInputs {get;init;}
    public static bool TryReadConstruction(EnbCurrentRetailEffectOwner node, out EnbCurrentRetailRObjEffectDefinition result, out string? error, EnbEffectDefinition? definitionOverride=null)
    {
        result = default!; error = null;
        var definition=definitionOverride??node.Definition;
        var p = definition.Properties;
        bool seek = definition.EffectClassKind is EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect;
        bool look = definition.EffectClassKind==EnbEffectClassKind.RObjLookAtEffect;
        if ((!seek && !look && definition.EffectClassKind != EnbEffectClassKind.RObjEffect) || definition.BaseAssetId is not int id)
        { error = "not a BaseAsset RObjEffect"; return false; }
        bool pointChild=node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint && node.LinkKind!=EnbCurrentRetailEffectLinkKind.Root;
        bool pointLook=look && node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint;
        bool linkedBinary = (node.Root.IsObjectToObject || pointChild) && node.LinkKind != EnbCurrentRetailEffectLinkKind.Root;
        bool point = definition.EffectClassKind==EnbEffectClassKind.RObjEffect && node.Root.GeometryKind==EnbSceneEffectGeometryKind.Point;
        //006E8DD0 and006EFC20 give a point-target unary child a matrix
        //through006E48A0, never a source pointer or removal observer.
        bool sourceLessPoint = node.HasUnboundEndpoints || point || definition.EffectClassKind==EnbEffectClassKind.RObjEffect &&
            pointChild && node.UsesTargetBinding;
        if (!node.HasUnboundEndpoints && ((!seek && !look && !linkedBinary && !point && node.Root.GeometryKind != EnbSceneEffectGeometryKind.Object) || (!point && !pointLook && !(pointChild && node.UsesTargetBinding) && node.BoundGameId is null or 0) ||
            (seek && !node.Root.IsObjectToObject && node.Root.GeometryKind!=EnbSceneEffectGeometryKind.ObjectToPoint) ||
            (!seek && !look && !linkedBinary && (node.Root.SpatialOffset != Vector3.Zero ||
                point && !string.IsNullOrEmpty(node.Root.BoneName)))))
        { error = $"RObjEffect requires an ordinary object binding (geometry={node.Root.GeometryKind}, source={node.Root.SourceGameId}, target={node.Root.TargetGameId}, bound={node.BoundGameId}, link={node.LinkKind}, spatial={node.Root.SpatialOffset})"; return false; }
        if(point && (!float.IsFinite(node.Root.WorldPoint.X)||!float.IsFinite(node.Root.WorldPoint.Y)||!float.IsFinite(node.Root.WorldPoint.Z)))
        { error="RObjEffect point is non-finite"; return false; }
        //Seek constructors clear0x80;006EA005..006EA05F applies an explicit
        //TrackSource afterward, including installed StraightSeek E10158.
        bool tracks = p.ContainsKey("TrackSource") ? EnbRetailIniDocument.ReadBool(p,"TrackSource") : !seek;
        //006EF291 skips displacement for a point's null source pointer,
        //independently of tracking. Ordinary unary objects retain displacement.
        //006E7770/006E7ED0 observer lifetimes and seek constructor duration
        //normalization belong to the scheduler, independently of TrackSource.
        float authoredScale = 1;
        if (p.ContainsKey("RObjScale") && !EnbRetailIniDocument.TryReadFloat(p, "RObjScale", out authoredScale))
        { error = "RObjEffect authored scale unavailable"; return false; }
        float scale = authoredScale * node.Root.Scale;
        if (!float.IsFinite(scale) || scale <= 0)
        { error = "RObjEffect requires a finite positive render scale"; return false; }
        // 0048B220 / 00AEA58C: the prototype factory treats scales within
        // 0.01 of unity as unchanged. RSL Object initialization forwards here
        // too (0045F2AF). Source-radius scaling happens later, without this gate.
        float factoryScale=scale;
        if (!EnbCurrentRetailPrototypeVariantName.ChangesScale(scale)) scale = 1;
        if (!EnbCurrentRetailEffectFactoryHsv.TryRead(definition.Properties,node.Root.HsvShift+node.Root.EffectOffset, out var factoryHsv, out error,applyPrototypeThreshold:false)) return false;
        var hsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(factoryHsv)?factoryHsv:Vector3.Zero;
        // 006EF200/006EF250 skip source-radius/front work for a null source.
        // Seek factories 006EAFA0/006EB150/006EB330/006EB590 do not read
        // ScaleToRadius. Their binary setters preserve factory scale even when
        // obsolete authored definitions contain the unary property.
        //006EA5E0 and006EAFA0/006EB150/006EB330/006EB590 do not read unary
        //camera flags. Their updates submit endpoint/trajectory matrices directly.
        result = new(id, !sourceLessPoint && !seek && EnbRetailIniDocument.ReadBool(p, "ScaleToRadius"),!seek && !look && !sourceLessPoint && EnbRetailIniDocument.ReadBool(p,"InFrontOfObject"),EnbRetailIniDocument.ReadBool(p,"ResetMappers"), scale, hsv, tracks,!seek && !look && EnbRetailIniDocument.ReadBool(p,"FaceCamera"))
            {FactoryInputs=(factoryScale,factoryHsv)}; return true;
    }

    public float ResolveRenderScale(float sourceRadius)
    {
        if (ScaleToRadius && (!float.IsFinite(sourceRadius) || sourceRadius < 0))
            throw new InvalidDataException("RObjEffect source radius unavailable");
        float scale = AssetScale * (ScaleToRadius ? sourceRadius : 1);
        if (!float.IsFinite(scale) || scale < 0)
            throw new InvalidDataException("RObjEffect combined render scale unavailable");
        return scale;
    }

    public Matrix4x4 BuildWorld(Matrix4x4 ownerRigidWorld, float sourceRadius)
    {
        // Factory scale is RObjScale * root scale. The native source setter then
        // scales the render object by source v+30 radius;
        // it does NOT normalize by the effect mesh's own bounding sphere.
        return Matrix4x4.CreateScale(ResolveRenderScale(sourceRadius)) * ownerRigidWorld;
    }

    public Matrix4x4 ApplyCameraPlacement(Matrix4x4 ownerRigidWorld, EnbResolvedRenderCamera camera, float sourceRadius)
    {
        var position=ownerRigidWorld.Translation;
        if(InFrontOfObject)
        {
            if(!float.IsFinite(sourceRadius) || sourceRadius<0) throw new InvalidDataException("RObjEffect front placement requires source radius");
            position=EnbCurrentRetailRObjCameraPlacement.MoveInFront(position,camera.Position,sourceRadius);
        }
        if(FaceCamera)
        {
            // 006EF3A9–006EF3F4 copies camera Matrix3D columns 2,0,1.
            // Camera +Z is -Forward; transpose into row-vector convention.
            var x=-camera.Basis.Forward;var y=camera.Basis.Right;var z=camera.Basis.Up;
            ownerRigidWorld=new(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,0,0,0,1);
        }
        ownerRigidWorld.Translation=position;
        return ownerRigidWorld;
    }
}

/// <summary>Immutable worker-loaded asset plan. Reuses the current generic
/// LINK, class activation and lazy HAnim rules; not a second RSL state machine.</summary>
public sealed record EnbCurrentRetailRObjEffectAsset(
    EnbW3dRenderResource? Resource, EnbW3dHlodSelection? Selection, bool OverbrightOff,
    EnbW3dRenderHierarchyAnimation? Animation, EnbRslStateFlags? AnimationFlags,
    EnbCurrentRetailRObjParticleAsset? Particle = null,
    IReadOnlyList<EnbCurrentRetailRObjParticleAsset>? AggregateParticles = null,
    EnbCurrentRetailMixedEffectAsset? Mixed = null,
    EnbRetailRingAssembly? Rings = null,
    EnbRetailSphereAssembly? Spheres = null,
    EnbCurrentRetailStreakAttachment? StandaloneStreak = null)
{
    private readonly EnbW3dHlodConstructorBoundsOwner _constructorBounds=new();
    public bool TryComputeBounds(EnbW3dRenderResource resource,EnbW3dRenderHlod hlod,Matrix4x4 world,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,EnbW3dHierarchyState post,
        out EnbW3dHlodBoundsOwner bounds,out string? diagnostic)
        =>_constructorBounds.TryCompute(resource,hlod,world,animations,post,out bounds,out diagnostic);

    //Retained parent bounds can precede an empty child's latest membership.
    public EnbW3dRenderResource? RetainedBoundsResource {get;init;}
    public EnbResolvedRslReference? RslOwner {get;init;}
    public EnbPreparedRslAnimationSequence? Sequence {get;init;}
    public IReadOnlyList<EnbCurrentRetailParticleEmitterDefinition> DiscardedParticleConstructions {get;init;}=[];
    public IReadOnlyList<EnbRObjCompositeEmitter> CompositeEmitters {get;init;}=[];
    // Preserve the prepared source so render-thread material ownership can
    // perform0048BCF0's copy before0048D1F0's HSV writes.
    public EnbW3dRenderResource? UnconfiguredMeshResource {get;init;}
    //0045EF80 ->0091D4B0 publishes the lazy Object file in the session
    //registry before returning a clone. Keep its full decoded prototype table,
    //not the named/filtered draw resource, for render-thread acquisition.
    [System.Text.Json.Serialization.JsonIgnore]
    public EnbResolvedW3dResource? LazyPrototypeSource {get;init;}
    public EnbW3dRenderResourceKey? RegisteredBaseMeshSourceKey {get;init;}
    public IReadOnlyList<EnbResolvedW3dResource> RegisteredBaseMeshSources {get;init;}=[];
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<EnbResolvedW3dResource> PrototypeLoadSources {get;init;}=[];
    [System.Text.Json.Serialization.JsonIgnore]
    public long FactoryPrototypeRegistration {get;init;}
    public string? FactoryObjectName {get;init;}
    public bool IsMissingFactoryObject {get;init;}
    public bool IsPreconfiguredFactoryObject {get;init;}
    public bool HasConfiguredHierarchy {get;init;}
    public bool IgnoresRequestedFactoryInputs=>IsMissingFactoryObject||IsPreconfiguredFactoryObject;
    public EnbCurrentRetailRObjEffectDefinition ResolveFactoryDefinition(EnbCurrentRetailRObjEffectDefinition definition)
        => IgnoresRequestedFactoryInputs ? definition with {AssetScale=1,HsvShift=Vector3.Zero,FactoryInputs=null} : definition;
    public EnbRetailObjectLineAsset? ObjectLine {get;init;}
    public bool IsNullObject {get;init;}
    public EnbCurrentRetailSpecialMaterialDefinition? SpecialMaterial {get;init;}
    public EnbW3dBox? StandaloneBox => Resource is {Meshes.Count:0,HierarchyModels.Count:0,HierarchicalLods.Count:0,ExternalRenderObjects.Count:1} resource
        ? resource.ExternalRenderObjects[0].BoxDefinition : null;
    public EnbW3dExternalRenderObject? StandaloneDazzle => Resource is {Meshes.Count:0,HierarchyModels.Count:0,HierarchicalLods.Count:0,ExternalRenderObjects.Count:1} resource &&
        resource.ExternalRenderObjects[0].DazzleDefinition is not null ? resource.ExternalRenderObjects[0] : null;
    public IReadOnlyList<EnbRObjModelAttachment> ModelAttachments {get;init;}=[];
    public bool ForceSubObjectLod {get;init;}
    public bool UsesDetailBudget => Resource?.HierarchicalLods.SingleOrDefault()?.Levels.Count > 1;

    //98A0A0 retains factory stores before6EF160 applies the source radius.
    //The total model scale cannot recover that input, especially at zero.
    public float ResolveHierarchyScale(float totalScale,float? sourceScale)
        => HasConfiguredHierarchy ? sourceScale??throw new InvalidDataException("Configured effect source hierarchy scale unavailable") : totalScale;

    public EnbHlodDetailOwner Candidate(Matrix4x4 world,float radius,EnbResolvedRenderCamera camera,int currentLevel,uint start,uint now,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null,Matrix4x4? rigidWorld=null,float? hierarchySourceScale=null,EnbW3dHlodQueryOwner? queryOwner=null)
    {
        EnbHlodDetailOwner Build(EnbW3dRenderHierarchyAnimation? clip,float at)
        {
            if(Rings is null && Spheres is null && !HasConfiguredHierarchy)return BuildCandidate(Resource!,world,radius,camera,currentLevel,clip,at,RetainedBoundsResource,queryOwner?.Bounds);
            //Mesh storage and pivot translations scale; primitive construction
            //extents do not. Uniformly scaling the final union is incorrect.
            if(rigidWorld is not {} rigid)throw new InvalidDataException("Primitive parent detail requires its rigid owner transform");
            var resource=Resource!;var hlod=resource.HierarchicalLods.Single();
            queryOwner??=new();
            var post=queryOwner.PreparePost(hlod.HierarchyName,rigid,radius,ResolveHierarchyScale(radius,hierarchySourceScale));
            var samples=queryOwner.Samples(clip,at);
            var queried=RetainedBoundsResource??resource;
            if(!queryOwner.Constructor.TryCompute(queried,queried.HierarchicalLods.Single(),rigid,samples,post,out var bounds,out var why) ||
                !EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(resource,hlod,_=>true,out var costs,out why) ||
                !EnbW3dHlodBudgetCompiler.TryCreateCandidate(hlod,currentLevel,camera,bounds.WorldSphere,costs,1,out var candidate,out why))
                throw new InvalidDataException(why);
            return candidate;
        }
        if(Sequence is not null)
        {
            if(observed is not {} sample)throw new InvalidDataException("Sequence bounds require an animation observation");
            return Build(sample.BoundAnimation,sample.Frame);
        }
        float frame=0;
        if(Animation is not null && !EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(Animation,AnimationFlags!,start,now,out frame,out var error))
            throw new InvalidDataException(error);
        return Build(Animation,frame);
    }

    internal static EnbHlodDetailOwner BuildCandidate(EnbW3dRenderResource resource,Matrix4x4 world,float radius,
        EnbResolvedRenderCamera camera,int currentLevel,EnbW3dRenderHierarchyAnimation? animation=null,float frame=0,EnbW3dRenderResource? boundsResource=null,EnbW3dHlodBoundsOwner? boundsOwner=null)
    {
        var h=resource.HierarchicalLods.Single();
        var queried=boundsResource??resource;
        if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(queried,queried.HierarchicalLods.Single(),world,animation,frame,out var bounds,out var error,boundsOwner) ||
            !EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(resource,h,_=>true,out var costs,out error))
            throw new InvalidDataException(error);
        // Native composite bounds retain the unscaled mesh radius; apply the source radius once.
        var sphere=bounds.WorldSphere with {Radius=bounds.WorldSphere.Radius*radius};
        if(!EnbW3dHlodBudgetCompiler.TryCreateCandidate(h,currentLevel,camera,sphere,costs,1,out var candidate,out error))
            throw new InvalidDataException(error);
        return candidate;
    }

    public static EnbCurrentRetailRObjEffectAsset Load(EnbRetailAssetBundle assets, int baseAssetId, Vector3 hsvShift,
        EnbResolvedW3dResource? registeredFactorySource=null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredRslSources=null,EnbResolvedW3dResource? registeredMissingSource=null)
        => ApplyHsv(assets, LoadFactory(assets,baseAssetId,registeredFactorySource,registeredRslSources,registeredMissingSource), hsvShift);

    public static EnbCurrentRetailRObjEffectAsset LoadPrototype(EnbRetailAssetBundle assets, string name, Vector3 hsvShift)
    {
        if(EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(name))
            return new(null,null,false,null,null){IsNullObject=true};
        if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,name,out var source,out var error))
            throw new InvalidDataException(error);
        return LoadPrototype(assets,source,hsvShift);
    }

    public static EnbCurrentRetailRObjEffectAsset LoadPrototype(EnbRetailAssetBundle assets,
        EnbResolvedW3dResource source,Vector3 hsvShift,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? selectedModelChildren=null)
    {
        if(source.AggregatePrototype is not null &&
            !EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(assets.FileResources,assets.RetailMixResources,
                source,registeredPrototypes,out source,out var error))throw new InvalidDataException(error);
        var definition=new EnbBaseAssetDefinition(0,EnbBaseAssetDefinitionKind.FileName,false,
            File:source.LogicalFileName,Name:source.RequestedObjectName);
        var resolved=new EnbBaseAssetResolution(0,EnbBaseAssetResolutionStatus.W3d,definition,source);
        if(source.CurrentRetailAggregate is null || selectedModelChildren is not null)
            return ApplyHsv(assets,Load(assets,resolved,false,registeredPrototypes,selectedModelChildren:selectedModelChildren),hsvShift);
        var prepared=PreparePrototype(assets,source,hsvShift,registeredPrototypes);
        return prepared.Build() with {PrototypeLoadSources=prepared.PrototypePublications};
    }

    /// <summary>Complete ordered constructor acquisitions for an already
    /// selected source without building its render assets. Primitive bases
    /// still construct their authored children before discarding insertion.</summary>
    public static FactoryPreparation PreparePrototype(EnbRetailAssetBundle assets,
        EnbResolvedW3dResource source,Vector3 hsvShift,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null,
        EnbCurrentRetailW3dFilePublicationContext? publicationContext=null)
    {
        if(publicationContext is not null)registeredPrototypes=publicationContext.CreateNamespace(registeredPrototypes);
        if(source.AggregatePrototype is not null &&
            !EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(assets.FileResources,assets.RetailMixResources,
                source,registeredPrototypes,out source,out var error))throw new InvalidDataException(error);
        var definition=new EnbBaseAssetDefinition(0,EnbBaseAssetDefinitionKind.FileName,false,
            File:source.LogicalFileName,Name:source.RequestedObjectName);
        var resolved=new EnbBaseAssetResolution(0,EnbBaseAssetResolutionStatus.W3d,definition,source);
        var children=new PreparedModelChildren();
        var names=StagePublications(registeredPrototypes,source.AcquisitionPublications);
        var selected=new FactoryPreparation(source,()=>Load(assets,resolved,false,names,selectedModelChildren:children))
            {PrototypeSources=names,PrototypePublications=source.AcquisitionPublications};
        return PrepareSelectedFactory(assets,selected,children,registeredPrototypes,hsvShift) with
            {PublicationsApplied=publicationContext is not null};
    }

    private static EnbCurrentRetailRObjEffectAsset ApplyHsv(EnbRetailAssetBundle assets, EnbCurrentRetailRObjEffectAsset loaded, Vector3 hsvShift)
    {
        if (hsvShift == Vector3.Zero || loaded.IsMissingFactoryObject) return loaded;
        EnbCurrentRetailRObjParticleAsset Shift(EnbCurrentRetailRObjParticleAsset particle)
            => particle.ApplyHsv(assets,hsvShift);
        var shiftedResource = loaded.Resource is { } resource ? EnbCurrentRetailEffectMeshHsvRuntime.Apply(resource, assets.Textures, hsvShift) : null;
        EnbCurrentRetailMixedEffectAsset? shiftedMixed = null;
        if (loaded.Mixed is { } mixed)
        {
            // 0048D710 recursively dispatches mesh and emitter children,
            // including each textured emitter's factory texture variant.
            var streaks = mixed.Streaks.Select(streak => streak.ApplyHsv(assets,hsvShift)).ToArray();
            var sprites = mixed.Sprites.Select(Shift).ToArray();
            shiftedResource = shiftedResource!.Copy().SetExternalRenderObjects(Array.AsReadOnly(shiftedResource!.ExternalRenderObjects.Select(external =>
                    external with { ParticleEmitterDefinition = streaks.FirstOrDefault(s =>
                        s.Definition.Name.Equals(external.Name, StringComparison.OrdinalIgnoreCase))?.Definition
                        ?? sprites.FirstOrDefault(s => s.Definition.Name.Equals(external.Name, StringComparison.OrdinalIgnoreCase))?.Definition
                        ?? external.ParticleEmitterDefinition }).ToArray()));
            shiftedMixed = mixed with { Resource = shiftedResource, Streaks = Array.AsReadOnly(streaks), Sprites = Array.AsReadOnly(sprites) };
        }
        var shiftedRings = loaded.Rings is {} rings ? rings with {Resource=shiftedResource!,Emitters=shiftedMixed,Rings=rings.Rings.Select(r=>r.ApplyHsv(assets,hsvShift)).ToArray()} : null;
        return loaded with
        {
            Resource = shiftedResource,
            UnconfiguredMeshResource = loaded.UnconfiguredMeshResource ?? loaded.Resource,
            ModelAttachments = loaded.ModelAttachments.Select(model=>model with {Asset=ApplyHsv(assets,model.Asset,hsvShift)}).ToArray(),
            Mixed = shiftedMixed,
            Rings = shiftedRings,
            Spheres = loaded.Spheres is {} spheres ? spheres with
            {
                Resource=shiftedResource!,Emitters=shiftedMixed,Rings=shiftedRings?.Rings??[],
                Spheres=spheres.Spheres.Select(s=>s.ApplyHsv(assets,hsvShift)).ToArray()
            } : null,
            Particle = loaded.Particle is { } particle ? Shift(particle) : null,
            CompositeEmitters = loaded.CompositeEmitters.Select(e=>e with {Asset=Shift(e.Asset)}).ToArray(),
            StandaloneStreak = loaded.StandaloneStreak?.ApplyHsv(assets,hsvShift),
            AggregateParticles = loaded.AggregateParticles is { } particles
                ? Array.AsReadOnly(particles.Select(Shift).ToArray()) : null,
            ObjectLine = loaded.ObjectLine is {} line ? EnbRetailObjectLineAsset.Load(assets,line.BaseAssetId,hsvShift) : null
        };
    }

    public static EnbCurrentRetailRObjEffectAsset Load(EnbRetailAssetBundle assets, int baseAssetId)
        => LoadFactory(assets,baseAssetId,null);

    public sealed record FactoryPreparation(EnbResolvedW3dResource? Source,Func<EnbCurrentRetailRObjEffectAsset> Build)
    {
        public bool IsMissingFactoryObject {get;init;}
        public bool IsPreconfiguredFactoryObject {get;init;}
        public IReadOnlyList<EnbCurrentRetailParticleEmitterDefinition> DiscardedParticleConstructions {get;init;}=[];
        public IReadOnlyList<EnbResolvedW3dResource> PrototypePublications {get;init;}=[];
        public bool PublicationsApplied {get;init;}
        internal IReadOnlyDictionary<string,EnbResolvedW3dResource>? PrototypeSources {get;init;}
        public IReadOnlyDictionary<string,EnbResolvedW3dResource> ModelChildren {get;init;}=
            new Dictionary<string,EnbResolvedW3dResource>(StringComparer.OrdinalIgnoreCase);
    }

    //006F51D0 selection without constructing an effect wrapper or acquiring
    //its children twice. The world consumer retains the selected object tree.
    public static FactoryPreparation SelectOrdinaryBaseFactory(EnbRetailAssetBundle assets,int baseAssetId,
        EnbCurrentRetailW3dFilePublicationContext publicationContext,
        IReadOnlyDictionary<string,EnbResolvedW3dResource> registeredPrototypes,float scale=1,Vector3 hsv=default)
    {
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(baseAssetId);
        if(definition.Kind!=EnbBaseAssetDefinitionKind.FileName && !HasMissingFactoryBranch(definition))
            throw new ArgumentException("Ordinary factory selection requires FILE/NAME or a proven missing branch.",nameof(baseAssetId));
        var names=publicationContext.CreateNamespace(registeredPrototypes);
        if(definition.Kind==EnbBaseAssetDefinitionKind.FileName && definition.Name is {} configuredName &&
            (EnbCurrentRetailPrototypeVariantName.ChangesScale(scale)||EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv)))
            return SelectConfiguredOrdinaryBase(assets,definition,names,configuredName,scale,hsv);
        EnbResolvedW3dResource? registered=null;
        if(definition.Name is {} name)names.TryGetValue(name,out registered);
        names.TryGetValue("missing",out var missing);
        return SelectFactory(assets,baseAssetId,registered,names,missing) with {PublicationsApplied=true};
    }

    private static FactoryPreparation SelectConfiguredOrdinaryBase(EnbRetailAssetBundle assets,
        EnbBaseAssetDefinition definition,EnbCurrentRetailW3dPrototypeNamespace names,string name,float scale,Vector3 hsv,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? selectedModelChildren=null)
    {
        string variant=EnbCurrentRetailPrototypeVariantName.Create(name,scale,hsv);
        var loads=new List<EnbResolvedW3dResource>();
        FactoryPreparation? Acquire()
        {
            //6F5555 and6F5585 each call48B220: variant before original on
            //both sides of the authored FILE load. A found variant is final.
            foreach(string requested in new[]{variant,name})
            {
                bool found=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
                    assets.FileResources,assets.RetailMixResources,requested,out var selected,out var why,
                    out var publications,out bool missing,names);
                foreach(var publication in publications){loads.Add(publication);PublishSelection(publication,names);}
                if(!found)
                {
                    if(!missing)throw new InvalidDataException(why);
                    continue;
                }
                bool preconfigured=requested==variant;
                if(EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(requested))
                    return new(null,()=>new(null,null,definition.OverbrightOff,null,null){IsNullObject=true})
                        {PublicationsApplied=names.PublicationContext is not null,PrototypePublications=loads.ToArray()};
                return new(selected,()=>Load(assets,new(definition.Selector,EnbBaseAssetResolutionStatus.W3d,
                    definition with {Name=requested,File=selected.LogicalFileName},selected),false,names,selectedModelChildren:selectedModelChildren)
                    with {OverbrightOff=definition.OverbrightOff})
                    {PublicationsApplied=names.PublicationContext is not null,PrototypePublications=loads.ToArray(),PrototypeSources=names,
                        IsPreconfiguredFactoryObject=preconfigured};
            }
            return null;
        }
        if(Acquire() is {} first)return first;
        if(definition.File is {} file)
        {
            bool decoded=assets.W3dFiles.TryResolve(file,name,out var authored,out var why,out bool opened);
            if(!decoded && opened)throw new InvalidDataException(why);
            if(decoded && authored.ResourceLocation.DataLength!=0)
            {
                if(names.PublicationContext is {} context)authored=context.Publish(authored);
                loads.Add(authored);PublishSelection(authored,names);
            }
        }
        if(Acquire() is {} retry)return retry;
        names.TryGetValue("missing",out var missingSource);
        return SelectMissingFactory(assets,definition,loads,missingSource,names,selectedModelChildren) with {PublicationsApplied=names.PublicationContext is not null};
    }

    private sealed class PreparedModelChildren : Dictionary<string,EnbResolvedW3dResource>
    {
        public PreparedModelChildren():base(StringComparer.OrdinalIgnoreCase) { }
        public HashSet<int> MissingOccurrences {get;}=[];
        public int AttachmentCount {get;set;}
        public bool RetainsAttachmentModels {get;set;}
        public IReadOnlyDictionary<string,EnbResolvedW3dResource>? PrototypeSources {get;set;}
        public Dictionary<string,EnbResolvedW3dResource> PrimitiveChildren {get;}=new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,EnbResolvedW3dResource> AcquiredChildren {get;}=new(StringComparer.OrdinalIgnoreCase);
        public List<(EnbResolvedW3dResource? Source,PreparedModelChildren? Plan)> BaseOccurrences {get;}=[];
        public Dictionary<int,(EnbResolvedW3dResource Source,PreparedModelChildren Plan)> AttachmentOccurrences {get;}=[];
    }

    public static IEnumerable<(string Name,EnbResolvedW3dResource Source)> EnumerateRetainedModelChildren(
        EnbResolvedW3dResource source,IReadOnlyDictionary<string,EnbResolvedW3dResource> sources)
    {
        if(sources is PreparedModelChildren plan)
        {
            if(!plan.RetainsAttachmentModels)yield break;
            foreach(var occurrence in plan.AttachmentOccurrences.OrderBy(p=>p.Key))
            {
                var child=occurrence.Value.Source;
                if(SelectedPrototype(child)?.Kind is not (EnbW3dPrototypeKind.HierarchyModel or EnbW3dPrototypeKind.HierarchicalLod or EnbW3dPrototypeKind.Mesh))continue;
                string name=source.CurrentRetailAggregate!.RenderDefinition.Attachments[occurrence.Key].SubobjectName;
                yield return (name,child);
                foreach(var descendant in EnumerateRetainedModelChildren(child,occurrence.Value.Plan))yield return descendant;
            }
            yield break;
        }
        foreach(var attachment in source.CurrentRetailAggregate?.RenderDefinition.Attachments??[])
            if(sources.TryGetValue(attachment.SubobjectName,out var child))yield return (attachment.SubobjectName,child);
    }

    private static EnbResolvedW3dResource WithAggregateAttachments(EnbResolvedW3dResource source,
        IReadOnlyList<EnbCurrentRetailW3dAggregateAttachment> attachments)
    {
        var aggregate=source.CurrentRetailAggregate!;
        return source with {CurrentRetailAggregate=new(aggregate.RenderDefinition with {Attachments=attachments},
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)
            {BaseRegisteredPrototype=aggregate.BaseRegisteredPrototype,BasePrototypeRegistration=aggregate.BasePrototypeRegistration,
                BaseLooseFilePath=aggregate.BaseLooseFilePath}};
    }

    //Select the ordinary factory source before dispatching expensive mesh and
    //texture preparation. The caller can acquire its models while the selected
    //prototype registrations are still alive (0091D4B0/009A4540).
    /// <summary>Production factory: an RSL is a retained controller, not a
    /// projection of its initial Object/Animation pair. The static preparation
    /// API below remains available for callers explicitly preparing that pair.</summary>
    public static FactoryPreparation PrepareOwnedFactory(EnbRetailAssetBundle assets,int baseAssetId,Vector3 hsvShift,
        EnbResolvedW3dResource? registeredFactorySource=null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null,EnbResolvedW3dResource? registeredMissingSource=null,
        EnbCurrentRetailW3dFilePublicationContext? publicationContext=null,
        Action<EnbResolvedW3dResource>? namedMeshConstructed=null,(float Scale,Vector3 Hsv)? factoryInputs=null)
    {
        if(assets.PresentationCatalog!.ResolveBaseAsset(baseAssetId).Kind==EnbBaseAssetDefinitionKind.Rsl &&
            assets.BaseAssetResolver!.Resolve(baseAssetId).Rsl is {ResourceGraph:not null} rsl)
            return new(null,()=>new(null,null,false,null,null){RslOwner=rsl});
        return PrepareFactory(assets,baseAssetId,hsvShift,registeredFactorySource,registeredPrototypes,registeredMissingSource,publicationContext,namedMeshConstructed,factoryInputs);
    }

    public static FactoryPreparation PrepareFactory(EnbRetailAssetBundle assets,int baseAssetId,Vector3 hsvShift,
        EnbResolvedW3dResource? registeredFactorySource=null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null,EnbResolvedW3dResource? registeredMissingSource=null,
        EnbCurrentRetailW3dFilePublicationContext? publicationContext=null,
        Action<EnbResolvedW3dResource>? namedMeshConstructed=null,(float Scale,Vector3 Hsv)? factoryInputs=null)
    {
        if(publicationContext is not null)registeredPrototypes=publicationContext.CreateNamespace(registeredPrototypes);
        var children=new PreparedModelChildren();
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(baseAssetId);
        var selected=factoryInputs is {} inputs && definition.Kind==EnbBaseAssetDefinitionKind.FileName &&
            definition.Name is {} name && (EnbCurrentRetailPrototypeVariantName.ChangesScale(inputs.Scale)||EnbCurrentRetailPrototypeVariantName.ChangesHsv(inputs.Hsv))
            ? SelectConfiguredOrdinaryBase(assets,definition,new EnbCurrentRetailW3dPrototypeNamespace(registeredPrototypes),name,inputs.Scale,inputs.Hsv,children)
            : SelectFactory(assets,baseAssetId,registeredFactorySource,registeredPrototypes,registeredMissingSource,children);
        return PrepareSelectedFactory(assets,selected,children,registeredPrototypes,hsvShift,
            namedMeshConstructed:namedMeshConstructed) with
            {PublicationsApplied=publicationContext is not null};
    }

    private static FactoryPreparation PrepareSelectedFactory(EnbRetailAssetBundle assets,FactoryPreparation selected,
        PreparedModelChildren children,IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes,Vector3 hsvShift,
        HashSet<string>? primitiveCreationStack=null,Action<EnbResolvedW3dResource>? namedMeshConstructed=null,bool namedChild=false)
    {
        var publications=new List<EnbResolvedW3dResource>(selected.PrototypePublications);
        var childSources=StagePublications(selected.PrototypeSources??registeredPrototypes,[]);
        //Creation consumes the namespace after ordered child FILE publication,
        //including names published by a preceding sibling's different FILE.
        children.PrototypeSources=childSources;
        PreparedModelChildren PrepareChild(EnbResolvedW3dResource child,string name)
        {
            primitiveCreationStack??=new(StringComparer.OrdinalIgnoreCase);
            if(!primitiveCreationStack.Add(name))throw new InvalidDataException("Recursive render-object child creation");
            var nestedChildren=new PreparedModelChildren();
            var nested=PrepareSelectedFactory(assets,new(child,selected.Build)
                {PrototypeSources=childSources,PrototypePublications=[]},nestedChildren,childSources,Vector3.Zero,primitiveCreationStack,namedMeshConstructed,namedChild:true);
            primitiveCreationStack.Remove(name);
            foreach(var publication in nested.PrototypePublications)
            {publications.Add(publication);PublishSelection(publication,childSources);}
            return nestedChildren;
        }
        if(selected.Source is {} source)
        {
            var prototype=source.CurrentRetailAggregate?.BaseRegisteredPrototype??source.RegisteredPrototype;
            if(prototype is null && EnbW3dRenderResourceCompiler.TryCompile(source,out var table,out _))
                foreach(var entry in source.Model.PrototypeEntries)
                    if(EnbW3dRenderResourceCache.PrototypeName(table,entry).Equals(source.CurrentRetailAggregate?.BaseRequestedObjectName??source.RequestedObjectName,StringComparison.OrdinalIgnoreCase))
                    {prototype=entry;break;}
            //The ordinary named constructor48B0F0 postprocesses before
            //returning to its parent. This includes9A3AB0 aggregate bases
            //and children discarded by a no-op insertion virtual.
            if(prototype?.Kind==EnbW3dPrototypeKind.Mesh &&
                (namedChild || source.CurrentRetailAggregate is not null || selected.IsMissingFactoryObject || selected.IsPreconfiguredFactoryObject))
                namedMeshConstructed?.Invoke(source);
            IEnumerable<string> members=prototype?.Kind switch
            {
                EnbW3dPrototypeKind.HierarchyModel=>source.Model.HierarchyModels[prototype.Value.Ordinal].Nodes.Select(node=>node.RenderObjectName),
                EnbW3dPrototypeKind.HierarchicalLod=>source.Model.HierarchicalLods[prototype.Value.Ordinal].Levels.SelectMany(level=>level.Members)
                    .Concat(source.Model.HierarchicalLods[prototype.Value.Ordinal].Aggregate?.Members??[]).Select(member=>member.Name),
                EnbW3dPrototypeKind.Collection=>source.Model.Collections[prototype.Value.Ordinal].ChildNames,
                _=>[]
            };
            //98C7A0 /98C420 /990450 complete every base member factory
            //in authored order before009A3A50 begins wrapper attachments.
            foreach(string name in members)
            {
                EnbResolvedW3dResource child;IReadOnlyList<EnbResolvedW3dResource> loads;
                bool created=childSources.TryGetValue(name,out var registered)
                    ? EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(assets.FileResources,assets.RetailMixResources,
                        registered,childSources,out child,out _,out loads)
                    : EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,
                        name,out child,out _,out loads,childSources);
                foreach(var publication in loads){publications.Add(publication);PublishSelection(publication,childSources);}
                if(created && child.CurrentRetailAggregate is null)
                {
                    created=EnbW3dRenderResourceCompiler.TryCompile(child,out var childTable,out _) &&
                        child.Model.PrototypeEntries.Any(entry=>EnbW3dRenderResourceCache.PrototypeName(childTable,entry).Equals(name,StringComparison.OrdinalIgnoreCase));
                }
                if(!created)
                {
                    children.BaseOccurrences.Add((null,null));
                    if(prototype?.Kind==EnbW3dPrototypeKind.Collection)
                        throw new InvalidDataException($"Collection member '{name}' could not materialize");
                    continue;
                }
                children.AcquiredChildren[name]=child;
                children.BaseOccurrences.Add((child,PrepareChild(child,name)));
            }
        }
        if(selected.Source?.CurrentRetailAggregate is {} aggregate)
        {
            children.AttachmentCount=aggregate.RenderDefinition.Attachments.Count;
            var basePrototype=aggregate.BaseRegisteredPrototype;
            if(basePrototype is null && EnbW3dRenderResourceCompiler.TryCompile(selected.Source,out var baseTable,out _))
                foreach(var entry in selected.Source.Model.PrototypeEntries)
                    if(EnbW3dRenderResourceCache.PrototypeName(baseTable,entry).Equals(aggregate.BaseRequestedObjectName,StringComparison.OrdinalIgnoreCase))
                    {basePrototype=entry;break;}
            children.RetainsAttachmentModels=basePrototype?.Kind is not (EnbW3dPrototypeKind.NullObject or EnbW3dPrototypeKind.Mesh or EnbW3dPrototypeKind.Box or EnbW3dPrototypeKind.Dazzle or EnbW3dPrototypeKind.ParticleEmitter or EnbW3dPrototypeKind.Sphere or EnbW3dPrototypeKind.Ring or EnbW3dPrototypeKind.SpecialMaterial or EnbW3dPrototypeKind.Collection);
            for(int index=0;index<aggregate.RenderDefinition.Attachments.Count;index++)
            {
                var attachment=aggregate.RenderDefinition.Attachments[index];
                //Each occurrence creates before insertion, including repeated NAMEs.
                {
                    bool created=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAggregateChild(assets.FileResources,assets.RetailMixResources,
                        attachment.SubobjectName,out var child,out var childFailure,out var childPublications,out bool nativeMissing,childSources);
                    if(nativeMissing)children.MissingOccurrences.Add(index);
                    //009A3A50 creates children in authored order. A FILE loaded
                    //for this child publishes all its names before the next
                    //sibling's009A3AB0 lookup, even if that sibling has no FILE.
                    foreach(var publication in childPublications)
                    {publications.Add(publication);PublishSelection(publication,childSources);}
                    if(!created && !nativeMissing && (childSources as EnbCurrentRetailW3dPrototypeNamespace)?.PublicationContext is not null)
                        throw new InvalidDataException(childFailure??$"Aggregate child '{attachment.SubobjectName}' is outside the represented creation boundary");
                    if(created)
                    {
                        children.AcquiredChildren[attachment.SubobjectName]=child;
                        children.AttachmentOccurrences[index]=(child,PrepareChild(child,attachment.SubobjectName));
                        var childBase=child.CurrentRetailAggregate?.BaseRegisteredPrototype??child.RegisteredPrototype;
                        if(childBase is null && EnbW3dRenderResourceCompiler.TryCompile(child,out var childTable,out _))
                        {
                            string childName=child.CurrentRetailAggregate?.BaseRequestedObjectName??child.RequestedObjectName;
                            foreach(var entry in child.Model.PrototypeEntries)
                                if(EnbW3dRenderResourceCache.PrototypeName(childTable,entry).Equals(childName,StringComparison.OrdinalIgnoreCase))
                                {childBase=entry;break;}
                        }
                        if(childBase?.Kind is EnbW3dPrototypeKind.Sphere or EnbW3dPrototypeKind.Ring)
                        {
                            child=child.CurrentRetailAggregate is {} primitiveAggregate
                                ? child with {CurrentRetailAggregate=primitiveAggregate with {BaseRegisteredPrototype=childBase}}
                                : child with {RegisteredPrototype=childBase};
                            children.PrimitiveChildren[attachment.SubobjectName]=child;
                        }
                    }
                    //NULL's insertion virtual00462AD0 does not retain the
                    //created child. Keep FILE effects, not pending model leases.
                    if(created && basePrototype?.Kind is not (EnbW3dPrototypeKind.NullObject or EnbW3dPrototypeKind.Mesh or EnbW3dPrototypeKind.Box or EnbW3dPrototypeKind.Dazzle or EnbW3dPrototypeKind.ParticleEmitter or EnbW3dPrototypeKind.Sphere or EnbW3dPrototypeKind.Ring or EnbW3dPrototypeKind.SpecialMaterial or EnbW3dPrototypeKind.Collection) &&
                        SelectedPrototype(child)?.Kind is EnbW3dPrototypeKind.HierarchyModel or EnbW3dPrototypeKind.HierarchicalLod or EnbW3dPrototypeKind.Mesh)
                        children[attachment.SubobjectName]=child;
                }
            }
        }
        var discarded=new List<EnbCurrentRetailParticleEmitterDefinition>();
        if(selected.Source is {} discardSource)
        {
            var kind=SelectedPrototype(discardSource)?.Kind;
            if(kind is EnbW3dPrototypeKind.NullObject or EnbW3dPrototypeKind.Mesh or EnbW3dPrototypeKind.Box or
                EnbW3dPrototypeKind.Dazzle or EnbW3dPrototypeKind.ParticleEmitter or EnbW3dPrototypeKind.Sphere or EnbW3dPrototypeKind.Ring or EnbW3dPrototypeKind.SpecialMaterial or EnbW3dPrototypeKind.Collection)
                foreach(var occurrence in children.AttachmentOccurrences.OrderBy(p=>p.Key).Select(p=>p.Value))
                    CollectParticleConstructions(occurrence.Source,occurrence.Plan,discarded);
        }
        return selected with {ModelChildren=children,DiscardedParticleConstructions=discarded,
            PrototypePublications=publications.ToArray(),Build=()=>
            {
                var built=selected.Build();
                return ApplyHsv(assets,built,selected.IsPreconfiguredFactoryObject?Vector3.Zero:hsvShift) with
                    {IsPreconfiguredFactoryObject=selected.IsPreconfiguredFactoryObject,DiscardedParticleConstructions=discarded,
                    //48B220 keys the requested wrapper, not its underlying
                    //base HLOD (several wrappers may share that same base).
                    FactoryObjectName=selected.Source?.RequestedObjectName??built.FactoryObjectName};
            }};
    }

    private static EnbW3dPrototypeEntry? SelectedPrototype(EnbResolvedW3dResource source)
    {
        var selected=source.CurrentRetailAggregate?.BaseRegisteredPrototype??source.RegisteredPrototype;
        if(selected is not null)return selected;
        if(EnbW3dRenderResourceCompiler.TryCompile(source,out var table,out _))
            foreach(var entry in source.Model.PrototypeEntries)
                if(EnbW3dRenderResourceCache.PrototypeName(table,entry).Equals(source.CurrentRetailAggregate?.BaseRequestedObjectName??source.RequestedObjectName,StringComparison.OrdinalIgnoreCase))return entry;
        return null;
    }

    private static void CollectParticleConstructions(EnbResolvedW3dResource source,PreparedModelChildren plan,
        List<EnbCurrentRetailParticleEmitterDefinition> result)
    {
        if(SelectedPrototype(source) is {Kind:EnbW3dPrototypeKind.ParticleEmitter} particle)
            result.Add(source.Model.ParticleEmitters[particle.Ordinal]);
        foreach(var child in plan.BaseOccurrences)
            if(child.Source is not null && child.Plan is not null)CollectParticleConstructions(child.Source,child.Plan,result);
        foreach(var child in plan.AttachmentOccurrences.OrderBy(p=>p.Key).Select(p=>p.Value))
            CollectParticleConstructions(child.Source,child.Plan,result);
    }

    private static EnbCurrentRetailRObjEffectAsset LoadFactory(EnbRetailAssetBundle assets,int baseAssetId,
        EnbResolvedW3dResource? registeredFactorySource,IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredRslSources=null,
        EnbResolvedW3dResource? registeredMissingSource=null)
        => PrepareFactory(assets,baseAssetId,Vector3.Zero,registeredFactorySource,registeredRslSources,registeredMissingSource).Build();

    private static FactoryPreparation SelectFactory(EnbRetailAssetBundle assets,int baseAssetId,
        EnbResolvedW3dResource? registeredFactorySource,IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredRslSources,
        EnbResolvedW3dResource? registeredMissingSource,IReadOnlyDictionary<string,EnbResolvedW3dResource>? selectedModelChildren=null)
    {
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(baseAssetId);
        if(HasMissingFactoryBranch(definition))
            return SelectMissingFactory(assets,definition,[],registeredMissingSource,registeredRslSources,selectedModelChildren);
        if(definition.Kind==EnbBaseAssetDefinitionKind.Rsl)
        {
            var resolved=assets.BaseAssetResolver!.Resolve(baseAssetId);
            if(resolved.Rsl is {ResourceGraph:{} graph} rsl)
            {
                var selected=SelectRslObject(assets,rsl,graph,registeredRslSources);
                return new(selected.Source,()=>Load(assets,resolved,false,selected.PrototypeSources??registeredRslSources,selected,selectedModelChildren))
                    {PrototypeSources=selected.PrototypeSources,PrototypePublications=selected.Publications.Count!=0?selected.Publications:
                        selected.Source?.PrototypeRegistration==0?selected.Source.AcquisitionPublications:[]};
            }
            return new(null,()=>Load(assets,resolved,false,registeredRslSources));
        }
        if(definition.Kind!=EnbBaseAssetDefinitionKind.FileName)
            return new(null,()=>Load(assets,assets.BaseAssetResolver!.Resolve(baseAssetId),particleJet:false,registeredRslSources));
        //Do not decode an unused FILE before the registered/lazy NAME probe.
        //Resolve is a pure classifier here; the subsequent ordinary acquisition
        //owns FILE construction. Do not carry publication authority into it.
        return SelectFactory(assets,new(baseAssetId,EnbBaseAssetResolutionStatus.UnsupportedDefinition,definition),
            registeredFactorySource,()=>assets.BaseAssetResolver!.Resolve(baseAssetId,
                registeredRslSources is null?null:new Dictionary<string,EnbResolvedW3dResource>(registeredRslSources,StringComparer.OrdinalIgnoreCase)),
            registeredMissingSource,registeredRslSources,selectedModelChildren);
    }

    /// <summary>006F51D0 tries configured NAME before its authored FILE
    /// fallback. The ordinary decoded NAME families can establish that success
    /// without treating a missing or unused FILE as the selected resource.</summary>
    public static EnbCurrentRetailRObjEffectAsset LoadFactory(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved,
        EnbResolvedW3dResource? registeredFactorySource=null,EnbResolvedW3dResource? registeredMissingSource=null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null)
    {
        var children=new PreparedModelChildren();
        var selected=SelectFactory(assets,resolved,registeredFactorySource,null,registeredMissingSource,registeredPrototypes,children);
        return PrepareSelectedFactory(assets,selected,children,registeredPrototypes,Vector3.Zero).Build();
    }

    private static FactoryPreparation SelectFactory(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved,
        EnbResolvedW3dResource? registeredFactorySource,Func<EnbBaseAssetResolution>? resolveFile,
        EnbResolvedW3dResource? registeredMissingSource,IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? selectedModelChildren=null)
    {
        if(HasMissingFactoryBranch(resolved.Definition))
            return SelectMissingFactory(assets,resolved.Definition,[],registeredMissingSource,registeredPrototypes,selectedModelChildren);
        //0091D4E2..0091D543 selects the built-in prototype before either
        //registry lookup or lazy FILE work, including case-insensitive names.
        if(resolved.Definition.Kind==EnbBaseAssetDefinitionKind.FileName && resolved.Definition.Name is {} builtin &&
            EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(builtin))
            return new(null,()=>new(null,null,resolved.Definition.OverbrightOff,null,null){IsNullObject=true});
        if(registeredFactorySource is {} registered)
        {
            if(resolved.Definition.Kind!=EnbBaseAssetDefinitionKind.FileName ||
                !registered.RequestedObjectName.Equals(resolved.Definition.Name,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The registered factory source must match the selected NAME.",nameof(registeredFactorySource));
            var retainedRegistered=registered;
            if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(assets.FileResources,assets.RetailMixResources,
                registered,registeredPrototypes,out registered,out var error,out var partialPublications))
            {
                //006F5558 tests created-object success before5570 loads FILE.
                //Keep the selected wrapper and all preceding publications.
                var retryNames=StagePublications(registeredPrototypes,[]);
                retryNames[retainedRegistered.RequestedObjectName]=retainedRegistered;
                var recovered=AcquireOrdinaryObject(assets,resolved.Definition.Name!,resolved.Definition.File,
                    out var retryLoads,out var retrySources,retryNames,skipFirstLookup:true,priorPublications:partialPublications);
                if(recovered is null)return SelectMissingFactory(assets,resolved.Definition,retryLoads,registeredMissingSource,retrySources,selectedModelChildren);
                return new(recovered,()=>Load(assets,resolved with {Status=EnbBaseAssetResolutionStatus.W3d,W3d=recovered,
                    Definition=resolved.Definition with {File=recovered.LogicalFileName},Diagnostic=null},false,retrySources,selectedModelChildren:selectedModelChildren) with
                    {FactoryPrototypeRegistration=retainedRegistered.PrototypeRegistration,PrototypeLoadSources=retryLoads})
                    {PrototypeSources=retrySources,PrototypePublications=ExpandPublications(retryLoads)};
            }
            //0091D4B0 returns this exact registered source before deriving any
            //filename. Its prototype ordinal also survives duplicate names.
            var selectedSources=StagePublications(registeredPrototypes,partialPublications);
            return new(registered,()=>Load(assets,resolved with {Status=EnbBaseAssetResolutionStatus.W3d,W3d=registered,
                Definition=resolved.Definition with {File=registered.LogicalFileName},Diagnostic=null},false,selectedSources,selectedModelChildren:selectedModelChildren) with
                {FactoryPrototypeRegistration=registered.PrototypeRegistration,PrototypeLoadSources=partialPublications})
                {PrototypeSources=selectedSources,PrototypePublications=partialPublications};
        }
        IReadOnlyList<EnbResolvedW3dResource> precedingPublications=[];
        if(resolved.Definition.Kind==EnbBaseAssetDefinitionKind.FileName && resolved.Definition.Name is {} name)
        {
            bool initiallyAcquired=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.MixResources,name,
                out var lazy,out _,out var initialPublications,out bool nativeMissing,registeredPrototypes);
            precedingPublications=initialPublications;
            if(!initiallyAcquired && nativeMissing && initialPublications.Count!=0)
            {
                var names=StagePublications(registeredPrototypes,initialPublications);
                var recovered=AcquireOrdinaryObject(assets,name,resolved.Definition.File,out var retryLoads,out var retrySources,
                    names,skipFirstLookup:true,priorPublications:initialPublications);
                if(recovered is null)return SelectMissingFactory(assets,resolved.Definition,retryLoads,registeredMissingSource,retrySources,selectedModelChildren);
                return new(recovered,()=>Load(assets,resolved with {Status=EnbBaseAssetResolutionStatus.W3d,W3d=recovered,
                    Definition=resolved.Definition with {File=recovered.LogicalFileName},Diagnostic=null},false,retrySources,selectedModelChildren:selectedModelChildren) with
                    {PrototypeLoadSources=retryLoads}){PrototypeSources=retrySources,PrototypePublications=ExpandPublications(retryLoads)};
            }
            if(initiallyAcquired)
            {
                bool found=lazy.CurrentRetailAggregate is not null;
                if(!found && EnbW3dRenderResourceCompiler.TryCompile(lazy,out var table,out _))
                    found=lazy.Model.PrototypeEntries.Any(entry=>EnbW3dRenderResourceCache.PrototypeName(table,entry).Equals(name,StringComparison.OrdinalIgnoreCase));
                if(found)
                {
                    var selected=resolved with {Status=EnbBaseAssetResolutionStatus.W3d,W3d=lazy,
                        Definition=resolved.Definition with {File=lazy.LogicalFileName},Diagnostic=null};
                    var selectedSources=StagePublications(registeredPrototypes,lazy.AcquisitionPublications);
                    return new(lazy,()=>Load(assets,selected,false,selectedSources,selectedModelChildren:selectedModelChildren) with
                        {LazyPrototypeSource=lazy.CurrentRetailAggregate is null?lazy:null,
                         PrototypeLoadSources=lazy.CurrentRetailAggregate is null?[]:lazy.AcquisitionPublications})
                        {PrototypeSources=selectedSources,PrototypePublications=lazy.AcquisitionPublications};
                }
            }
        }
        if((registeredPrototypes as EnbCurrentRetailW3dPrototypeNamespace)?.PublicationContext is not null &&
            resolved.Definition is {Kind:EnbBaseAssetDefinitionKind.FileName,Name:{} configuredName,File:{} configuredFile})
        {
            //The configured operation already attempted NAME above. Perform
            //the authored FILE and fresh NAME acquisition directly: a pure
            //legacy classifier cannot substitute for this publication step.
            var selected=AcquireOrdinaryObject(assets,configuredName,configuredFile,out var configuredLoads,out var configuredSources,
                registeredPrototypes,skipFirstLookup:true,priorPublications:precedingPublications);
            if(selected is null)return SelectMissingFactory(assets,resolved.Definition,configuredLoads,registeredMissingSource,configuredSources,selectedModelChildren);
            return new(selected,()=>Load(assets,resolved with {Status=EnbBaseAssetResolutionStatus.W3d,W3d=selected,
                Definition=resolved.Definition with {File=selected.LogicalFileName},Diagnostic=null},false,configuredSources,selectedModelChildren:selectedModelChildren) with
                {PrototypeLoadSources=configuredLoads})
                {PrototypeSources=configuredSources,PrototypePublications=ExpandPublications(configuredLoads)};
        }
        var authored=resolveFile?.Invoke()??resolved;
        //Keep the existing special-family handlers until their full file
        //publication paths are represented. A decoder rejection is not proof
        //that retail failed to acquire the object.
        if(authored.Definition.Kind!=EnbBaseAssetDefinitionKind.FileName ||
            authored.Status is not (EnbBaseAssetResolutionStatus.MissingResource or EnbBaseAssetResolutionStatus.W3d or EnbBaseAssetResolutionStatus.UnsupportedDefinition) ||
            authored.Definition.Name is null || authored.Definition.File is null ||
            authored.W3d?.CurrentRetailAggregate is not null)
        {
            var publications=authored.W3d?.AcquisitionPublications??[];
            var selectedSources=StagePublications(registeredPrototypes,publications);
            return new(authored.W3d,()=>Load(assets,authored,false,selectedSources,selectedModelChildren:selectedModelChildren) with
                {PrototypeLoadSources=publications}){PrototypeSources=selectedSources,PrototypePublications=publications};
        }
        var acquired=AcquireOrdinaryObject(assets,authored.Definition.Name!,authored.Definition.File,out var loads,out var acquiredSources,registeredPrototypes);
        if(acquired is not null)
            return new(acquired,()=>Load(assets,authored with {Status=EnbBaseAssetResolutionStatus.W3d,W3d=acquired,
                Definition=authored.Definition with {File=acquired.LogicalFileName},Diagnostic=null},false,acquiredSources,selectedModelChildren:selectedModelChildren) with
                {PrototypeLoadSources=loads,LazyPrototypeSource=loads.Count==0?acquired:null})
                {PrototypeSources=acquiredSources,PrototypePublications=loads.Count==0?acquired.AcquisitionPublications:ExpandPublications(loads)};
        //006F55C9..006F55CE: after both configured NAME attempts fail, +14 acquires
        //"missing" without scale/HSV. The effect still applies its later
        //source setter and ResetMappers to that ordinary render object.
        return SelectMissingFactory(assets,authored.Definition,loads,registeredMissingSource,registeredPrototypes,selectedModelChildren);
    }

    private static FactoryPreparation SelectMissingFactory(EnbRetailAssetBundle assets,EnbBaseAssetDefinition definition,
        IReadOnlyList<EnbResolvedW3dResource> loads,EnbResolvedW3dResource? registeredMissingSource,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? selectedModelChildren=null)
    {
        EnbResolvedW3dResource? missingSource=registeredMissingSource;
        if(missingSource is not null && !missingSource.RequestedObjectName.Equals("missing",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The registered missing source must match 'missing'.",nameof(registeredMissingSource));
        //0091D370 rejects duplicates: the existing registry, then the first
        //successful file publication, wins before missing.w3d is considered.
        foreach(var publication in loads)
        {
            if(missingSource is not null)break;
            if(!EnbW3dRenderResourceCompiler.TryCompile(publication,out var table,out var why))throw new InvalidDataException(why);
            foreach(var entry in publication.Model.PrototypeEntries)
                if(EnbW3dRenderResourceCache.PrototypeName(table,entry).Equals("missing",StringComparison.OrdinalIgnoreCase))
                {missingSource=publication with {RequestedObjectName="missing",RegisteredPrototype=entry};break;}
        }
        bool lazyMissing=missingSource is null;
        if(lazyMissing && !EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,
            "missing",out missingSource,out var missingError,registeredPrototypes))throw new InvalidDataException(missingError);
        IReadOnlyList<EnbResolvedW3dResource> publications=[..ExpandPublications(loads),..lazyMissing?missingSource!.AcquisitionPublications:[]];
        var selectedSources=StagePublications(registeredPrototypes,publications);
        return new(missingSource,()=>LoadPrototype(assets,missingSource!,Vector3.Zero,selectedSources,selectedModelChildren) with {IsMissingFactoryObject=true,OverbrightOff=definition.OverbrightOff,
            PrototypeLoadSources=loads,LazyPrototypeSource=lazyMissing?missingSource:null,
            FactoryPrototypeRegistration=missingSource!.PrototypeRegistration})
            {PrototypeSources=selectedSources,PrototypePublications=publications,IsMissingFactoryObject=true};
    }

    public static bool HasMissingFactoryBranch(EnbBaseAssetDefinition definition)
        //006F52AC/006F53C8: an absent section or an actual -1 selector.
        //A managed parse rejection alone does not prove retail selected -1.
        => definition.Kind==EnbBaseAssetDefinitionKind.Missing && (!definition.IsCatalogEntry ||
            definition.Properties is {} values && EnbRetailIniDocument.ReadCurrentRetailInteger(values,"RSLID",-1)==-1);

    public static EnbCurrentRetailRObjEffectAsset Load(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredRslSources=null)
        => Load(assets,resolved,particleJet:false,registeredRslSources);

    public static EnbCurrentRetailRObjEffectAsset LoadParticleJet(EnbRetailAssetBundle assets,int baseAssetId,Vector3 hsvShift=default)
        => ApplyHsv(assets,Load(assets,assets.BaseAssetResolver!.Resolve(baseAssetId),particleJet:true),hsvShift);

    public EnbCurrentRetailRObjParticleAsset RequireParticleJet()
    {
        var particle=Particle ?? (AggregateParticles is {Count:1} particles ? particles[0] : null);
        if(particle is null)throw new InvalidDataException("ParticleJetEffect requires one standalone emitter");
        ValidateParticleJet(particle.Definition);
        return particle;
    }

    public static void ValidateParticleJet(EnbCurrentRetailParticleEmitterDefinition d)
        => EnbRetailParticleAdmission.Validate(d);
    private static EnbCurrentRetailRObjEffectAsset Load(EnbRetailAssetBundle assets, EnbBaseAssetResolution resolved,bool particleJet,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredRslSources=null,RslObjectSelection? rslSelection=null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? selectedModelChildren=null)
    {
        if(selectedModelChildren is PreparedModelChildren prepared)
        {
            registeredRslSources=prepared.PrototypeSources??registeredRslSources;
            if(prepared.PrimitiveChildren.Count!=0)
            {
                var created=StagePublications(registeredRslSources,[]);
                foreach(var (name,child) in prepared.PrimitiveChildren)created[name]=child;
                registeredRslSources=created;
            }
        }
        //Ordered FILE construction belongs to preparation. Retained workers
        //receive exact selected sources without authority to mutate the cache.
        if(registeredRslSources is EnbCurrentRetailW3dPrototypeNamespace)
            registeredRslSources=new Dictionary<string,EnbResolvedW3dResource>(registeredRslSources,StringComparer.OrdinalIgnoreCase);
        var loaded=LoadCore(assets,resolved,particleJet,registeredRslSources,out var lazySource,out var registration,out var loads,out var baseMeshSource,rslSelection,selectedModelChildren);
        //009A3A50 inserts emitter siblings of independent models as ordinary
        //permanent children. They retain scene-owned buffers and participate
        //in97DEF0/9662E0 finite removal, just like level emitter children.
        if(loaded.ModelAttachments.Count!=0 && loaded.AggregateParticles is not null)
            loaded=loaded with {AggregateParticles=null};
        if(loaded.Resource is {} compositeResource)
            loaded=loaded with {CompositeEmitters=EnbRObjCompositeEmitter.Prepare(assets,compositeResource)
                .Where(e=>e.LevelOrdinal>=0 || (loaded.Mixed is null && loaded.AggregateParticles is null)).ToArray()};
        if((loaded.CompositeEmitters.Count!=0 || loaded.Resource?.ExternalRenderObjects.Any(value=>value.DazzleDefinition is not null || value.BoxDefinition is not null || value.SpecialMaterialDefinition is not null || value.Kind==EnbW3dExternalRenderObjectKind.NullObject)==true) && loaded.Resource!.HierarchyModels.SingleOrDefault() is {} emitterModel)
        {
            //0098C7A0 materializes the connection array as one HLOD level.
            //Use that same traversal for retained non-mesh leaf children.
            var converted=EnbW3dHmodelConversion.Create(emitterModel,_=>true);
            loaded=loaded with {Resource=loaded.Resource.Copy().SetHierarchyModels([]).SetHierarchicalLods([converted]),Selection=new(converted.Name,0)};
        }
        var baseChildren=new List<EnbResolvedW3dResource>();
        if(selectedModelChildren is PreparedModelChildren retainedChildren && loaded.Resource is {} retainedResource)
        {
            foreach(var occurrence in retainedChildren.BaseOccurrences)
                if(occurrence.Source is {} child && child.RegisteredPrototype?.Kind==EnbW3dPrototypeKind.Mesh &&
                    retainedResource.Meshes.Any(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,child.RequestedObjectName)))baseChildren.Add(child);
        }
        else if(registeredRslSources is not null && loaded.Resource is {} resource)
            foreach(var mesh in resource.Meshes)
                if(registeredRslSources.TryGetValue(EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh),out var child) &&
                    child.RegisteredPrototype?.Kind==EnbW3dPrototypeKind.Mesh)baseChildren.Add(child);
        return loaded with {LazyPrototypeSource=lazySource,FactoryPrototypeRegistration=registration,PrototypeLoadSources=loads,
            RegisteredBaseMeshSourceKey=baseMeshSource,RegisteredBaseMeshSources=baseChildren};
    }

    private static IReadOnlyList<EnbResolvedW3dResource> ExpandPublications(IEnumerable<EnbResolvedW3dResource> loads)
        => loads.SelectMany(load=>load.AcquisitionPublications.Count==0?[load]:load.AcquisitionPublications).ToArray();

    private static Dictionary<string,EnbResolvedW3dResource> StagePublications(
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? existing,IEnumerable<EnbResolvedW3dResource> publications)
    {
        var names=new EnbCurrentRetailW3dPrototypeNamespace(existing);
        foreach(var publication in publications)PublishSelection(publication,names);
        return names;
    }

    private static void PublishSelection(EnbResolvedW3dResource publication,Dictionary<string,EnbResolvedW3dResource> names)
    {
        if(!EnbW3dRenderResourceCompiler.TryCompile(publication,out var table,out var failure))throw new InvalidDataException(failure);
        foreach(var entry in publication.PrototypeFile?.Entries??publication.Model.PrototypeEntries.Select(value=>new EnbCurrentRetailW3dPrototypeFileEntry(value.ChunkOffset,value,null)).ToArray())
        {
            string name=entry.Aggregate?.Name??EnbW3dRenderResourceCache.PrototypeName(table,entry.Ordinary!.Value);
            if(EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(name))continue;
            var actual=(names as EnbCurrentRetailW3dPrototypeNamespace)?.PublicationContext?.FindRegisteredSource(name);
            names.TryAdd(name,actual??publication with {RequestedObjectName=name,RegisteredPrototype=entry.Ordinary,
                AggregatePrototype=entry.Aggregate is null?null:entry});
        }
    }

    private static EnbResolvedW3dResource? AcquireOrdinaryObject(EnbRetailAssetBundle assets,string name,string? authoredFile,
        out IReadOnlyList<EnbResolvedW3dResource> prototypeLoads,
        out IReadOnlyDictionary<string,EnbResolvedW3dResource>? acquisitionSources,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes=null,bool skipFirstLookup=false,
        IReadOnlyList<EnbResolvedW3dResource>? priorPublications=null)
    {
        prototypeLoads=[];
        acquisitionSources=registeredPrototypes;
        var partialLoads=new List<EnbResolvedW3dResource>();
        string? lazyCreationFailure=null;
        IReadOnlyList<EnbResolvedW3dResource> lookupPublications=[];
        bool HasName(EnbResolvedW3dResource source)
        {
            if(source.CurrentRetailAggregate is not null)return true;
            if(!EnbW3dRenderResourceCompiler.TryCompile(source,out var table,out var diagnostic))
                throw new InvalidDataException(diagnostic);
            return source.Model.PrototypeEntries.Any(entry=>
                EnbW3dRenderResourceCache.PrototypeName(table,entry).Equals(name,StringComparison.OrdinalIgnoreCase));
        }
        EnbResolvedW3dResource? LazyLookup()
        {
            EnbResolvedW3dResource loaded;string? why;bool nativeMissing;
            bool found=registeredPrototypes?.TryGetValue(name,out var retained)==true
                ? EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(assets.FileResources,assets.RetailMixResources,retained,registeredPrototypes,
                    out loaded,out why,out lookupPublications,out nativeMissing)
                : EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,name,
                    out loaded,out why,out lookupPublications,out nativeMissing,registeredPrototypes);
            if(found)return loaded;
            var publications=lookupPublications;
            if(publications.Any(publication=>publication.ResourceLocation.DataLength!=0))
            {
                //The loader already decoded these FILEs before a later base
                //creation failed. Keep them ahead of the authored FILE retry.
                partialLoads.AddRange(publications.Where(publication=>publication.ResourceLocation.DataLength!=0));
                lazyCreationFailure=nativeMissing?null:why;
                return null;
            }
            if(nativeMissing){lazyCreationFailure=null;return null;}
            throw new InvalidDataException(why);
        }
        var first=skipFirstLookup?null:LazyLookup();
        if(first is not null && HasName(first))
        {
            if(first.CurrentRetailAggregate is not null)
            {
                prototypeLoads=lookupPublications;
                acquisitionSources=StagePublications(registeredPrototypes,prototypeLoads);
            }
            return first;
        }
        var loads=priorPublications is null?new List<EnbResolvedW3dResource>():new List<EnbResolvedW3dResource>(priorPublications);
        loads.AddRange(partialLoads);
        partialLoads.Clear();
        if(first is not null)loads.Add(first);
        registeredPrototypes=StagePublications(registeredPrototypes,ExpandPublications(loads));
        acquisitionSources=registeredPrototypes;
        //0045F078..0045F0C1: failed OBJECT acquisition loads authored FILE,
        //then performs a fresh ordinary acquisition. Preserve even the files
        //whose successful loads did not publish the requested name.
        if(authoredFile is {} fileName)
        {
            bool decoded=assets.W3dFiles.TryResolve(fileName,name,out var authored,out var why,out bool opened);
            if(!decoded && opened)throw new InvalidDataException(why);
            if(decoded && authored.ResourceLocation.DataLength!=0)
            {
                if((registeredPrototypes as EnbCurrentRetailW3dPrototypeNamespace)?.PublicationContext is {} context)
                    authored=context.Publish(authored);
                loads.Add(authored);
                //Stage the complete FILE publication before its fresh OBJECT
                //lookup. These entries have no invented global registrations;
                //the App publishes the returned load sources in its real cache.
                var names=StagePublications(registeredPrototypes,ExpandPublications(loads));
                registeredPrototypes=names;
                acquisitionSources=names;
                if(names.TryGetValue(name,out var selected))
                {
                    bool createdObject=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(assets.FileResources,assets.RetailMixResources,
                        selected,names,out var created,out why,out var createdPublications,out bool nativeMissing);
                    loads.AddRange(createdPublications);
                    foreach(var publication in createdPublications)PublishSelection(publication,names);
                    prototypeLoads=loads.ToArray();
                    if(!createdObject && !nativeMissing)throw new InvalidDataException(why);
                    return createdObject?created:null;
                }
            }
        }
        var retry=LazyLookup();
        if(retry is not null)loads.AddRange(lookupPublications);
        else loads.AddRange(partialLoads);
        prototypeLoads=loads.ToArray();
        acquisitionSources=StagePublications(registeredPrototypes,ExpandPublications(loads));
        if(retry is null && lazyCreationFailure is not null)throw new InvalidDataException(lazyCreationFailure);
        //0045F0CC..0045F115 constructs the built-in no-draw RenderObj only
        //after the second name lookup has actually failed.
        return retry is not null && HasName(retry)?retry:null;
    }

    private sealed record RslObjectSelection(EnbResolvedW3dResource? Source,EnbResolvedRslStateResource? Animation,
        EnbResolvedRslLibraryResourceGraph? Sequence,IReadOnlyList<EnbResolvedW3dResource> Publications)
    {public IReadOnlyDictionary<string,EnbResolvedW3dResource>? PrototypeSources {get;init;}}

    private static RslObjectSelection SelectRslObject(EnbRetailAssetBundle assets,EnbResolvedRslReference rsl,
        EnbResolvedRslLibraryResourceGraph graph,IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredRslSources)
    {
        EnbResolvedW3dResource? source;
        EnbResolvedRslStateResource? animationState=null;
        EnbResolvedRslLibraryResourceGraph? sequenceGraph=null;
        IReadOnlyList<EnbResolvedW3dResource> prototypeLoads=[];
        //Object +8/00458590 is RET8: RSID is not a second selector.
        //0045FFB0 may activate this Object and its LINK before startup;
        //00460880 deactivates/restores that same chain around the no-op,
        //then004604D0 only unhides an already-current Object.
        bool objectStartup=rsl.InitialStateCommands.Count>0 &&
            graph.TryFindState(rsl.InitialStateCommands[0].StateId,out var initialObject) &&
            initialObject.Definition.Type==EnbRslStateType.Object &&
            graph.States.All(s=>!s.Definition.Flags.Enabled &&
                (!s.Definition.Flags.StartsEnabled || s.Definition.StateId==initialObject.Definition.StateId));
        bool initializedObject=objectStartup && rsl.InitialStateCommands is [
            {Kind:EnbRslRenderStateCommandKind.InitializeState} initialize,
            {Kind:EnbRslRenderStateCommandKind.ActivateState} activate] && initialize.StateId==activate.StateId;
        //00460880 -> Link0045A1D0 replaces the instance target; the factory
        //then selects the Link itself. With no constructor-enabled states,
        //this passive prefix has no callbacks before Object -> Animation.
        bool linkStartup=rsl.InitialStateCommands.Count>0 &&
            graph.TryFindState(rsl.InitialStateCommands[0].StateId,out var initialLink) && initialLink.Definition.Type==EnbRslStateType.Link &&
            graph.States.All(s=>!s.Definition.Flags.Enabled && !s.Definition.Flags.StartsEnabled);
        //Message0045D9B0 calls this controller's00460BA0 RET8. It does
        //not enable/relink;00460440 still walks its authored LINK.
        bool messageStartup=rsl.InitialStateCommands.Count>0 &&
            graph.TryFindState(rsl.InitialStateCommands[0].StateId,out var initialMessage) && initialMessage.Definition.Type==EnbRslStateType.Message &&
            graph.States.All(s=>!s.Definition.Flags.Enabled && !s.Definition.Flags.StartsEnabled);
        bool initializedMessage=messageStartup && rsl.InitialStateCommands is [
            {Kind:EnbRslRenderStateCommandKind.InitializeState} messageInitialize,
            {Kind:EnbRslRenderStateCommandKind.ActivateState} messageActivate] && messageInitialize.StateId==messageActivate.StateId;
        bool initializedLink=linkStartup && rsl.InitialStateCommands is [
            {Kind:EnbRslRenderStateCommandKind.InitializeState} linkInitialize,
            {Kind:EnbRslRenderStateCommandKind.ActivateState} linkActivate] && linkInitialize.StateId==linkActivate.StateId;
        if(initializedLink)
        {
            var command=rsl.InitialStateCommands[0];
            int? target=command.ArgumentStateId is int id && id!=-1 && graph.TryFindState(id,out _)?id:null;
            graph=graph.WithResolvedLinkTarget(command.StateId,target);
        }
        if (!initializedObject && !initializedLink && !initializedMessage && (rsl.InitialStateCommands.Count != 1 || rsl.InitialStateCommands[0].Kind != EnbRslRenderStateCommandKind.ActivateState))
            throw new InvalidDataException("RObjEffect RSL initialization family not yet implemented");
        var traversal = EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, rsl.InitialStateCommands[0].StateId);
        var classes = EnbCurrentRetailRslClassActivationRuntime.Resolve(graph, traversal);
        var visual = EnbCurrentRetailRslVisualActivationRuntime.Resolve(classes);
        int passivePrefix=linkStartup || messageStartup?traversal.Steps.TakeWhile(s=>s.StateType is EnbRslStateType.Link or EnbRslStateType.Message).Count():0;
        if (traversal.UnresolvedOrUnfollowedLinkStateId is not null || !visual.IsExecutable ||
            traversal.Steps.Count < 1+passivePrefix || traversal.Steps.Count > 2+passivePrefix || visual.FinalCurrentObjectStateId is not int objectId ||
            !graph.TryFindState(objectId, out var objectState) || traversal.Steps[passivePrefix].StateId != objectId ||
            visual.FinalVisibleAnimationStateIds.Count != traversal.Steps.Count - 1-passivePrefix)
            throw new InvalidDataException("RObjEffect requires a simple Object -> Animation RSL path");
        foreach (var step in traversal.Steps)
        {
            graph.TryFindState(step.StateId, out var state);
            var f = state.Definition.Flags;
            bool nextAnimation=state.Definition.Type==EnbRslStateType.Animation && f.ActivateNext;
            bool inactiveAnimationTarget=state.Definition.Type==EnbRslStateType.Animation && !f.ActivateNext &&
                state.Definition.NextStateId is int inactive && graph.TryFindState(inactive,out var targetState) &&
                targetState.Definition.Type==EnbRslStateType.Animation;
            //45C430 dispatches anim directly to45AD90: that loader reads
            //neither DURATION, INITSTR nor MESSAGE. They do not reject a clip.
            bool animationOnly=state.Definition.Type==EnbRslStateType.Animation;
            if ((!nextAnimation && !inactiveAnimationTarget && state.Definition.NextStateId is not null) ||
                (!animationOnly && (state.Definition.Duration is not null || state.Definition.InitString is not null)) ||
                (state.Definition.Message is not null && !animationOnly && state.Definition.Type!=EnbRslStateType.Message) ||
                f.Delete || (f.Deactivate && state.Definition.Type!=EnbRslStateType.Animation) || (!nextAnimation && f.ActivateNext) || f.Enabled || (f.StartsEnabled && (!objectStartup || state.Definition.StateId!=rsl.InitialStateCommands[0].StateId)) ||
                ((f.RemovePrototype || f.Stationary) && state.Definition.Type!=EnbRslStateType.Animation) ||
                (!animationOnly && (f.InstanceData || f.DontStopSound || f.DontLoopSound)))
                throw new InvalidDataException("RObjEffect RSL state-control family not yet implemented");
            //4598B0/4597E0 ->457BE0 clears RemovePrototype/Stationary in
            //Animation copies. Neither is consumed by its template constructor.
            //Sound-only bits survive the copy, but Animation's vtable and
            //the shared activation/update/terminal paths do not consume them.
            //460BF0 reads InstanceData from controller+B4's current Object;
            //459D50 animates that Object without selecting itself there.
        }
        if (visual.FinalVisibleAnimationStateId is int animationId) graph.TryFindState(animationId, out animationState);
        if(animationState is not null && (animationState.Definition.Flags.ActivateNext || animationState.Definition.Flags.Deactivate))sequenceGraph=graph;
        //0045EE50 obtains the object through +20 -> +10/0045EF80 for
        //ordinary activation as well as self-initialization.0045F078
        //skips authored FILE loading after successful OBJECT lookup.
        if(EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(objectState.Definition.Object!))
            return new(null,null,null,prototypeLoads);
        if(registeredRslSources?.TryGetValue(objectState.Definition.Object!,out var registered)==true)
        {
            if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(assets.FileResources,assets.RetailMixResources,
                registered,registeredRslSources,out source,out var error,out var partialPublications))
            {
                //0045F078 tests creation success, not registration presence.
                //A retained wrapper can acquire its missing base from FILE.
                source=AcquireOrdinaryObject(assets,objectState.Definition.Object!,objectState.Definition.File,
                    out prototypeLoads,out var retrySources,registeredRslSources,skipFirstLookup:true,priorPublications:partialPublications);
                registeredRslSources=retrySources;
                if(source is null)return new(null,null,null,prototypeLoads){PrototypeSources=retrySources};
            }
            else if(partialPublications.Count!=0)
            {
                prototypeLoads=partialPublications;
                registeredRslSources=StagePublications(registeredRslSources,partialPublications);
            }
        }
        else
        {
            source=AcquireOrdinaryObject(assets,objectState.Definition.Object!,objectState.Definition.File,out prototypeLoads,out var acquiredSources,registeredRslSources);
            registeredRslSources=acquiredSources;
            if(source is null)return new(null,null,null,prototypeLoads);
        }
        return new(source,animationState,sequenceGraph,prototypeLoads){PrototypeSources=registeredRslSources};
    }

    private static EnbCurrentRetailRObjEffectAsset CreateParticle(EnbRetailAssetBundle assets,
        EnbCurrentRetailParticleEmitterDefinition emitter,EnbMixResourceLocation location,bool overbrightOff,bool particleJet)
    {
        EnbCurrentRetailStreakAttachment CreateStreak()
        {
            EnbCurrentRetailStreakBufferRuntime.Validate(emitter);
            return new EnbCurrentRetailStreakAttachment(EnbW3dRenderResourceKey.From(location),emitter,0).PrepareTexture(assets);
        }
            if(!particleJet && emitter.RenderMode is 3 or 4)
                return new(null,null,overbrightOff,null,null,
                    StandaloneStreak:CreateStreak());
            bool finiteHistory = false;
            try
            {
                if(particleJet)
                    EnbCurrentRetailRObjParticleAsset.ValidateAnimated(emitter);
                else EnbCurrentRetailRObjParticleAsset.Validate(emitter);
            }
            catch (InvalidDataException) when (emitter.MaximumEmissions > 0)
            {
                EnbCurrentRetailRObjParticleAsset.ValidateFinite(emitter);
                finiteHistory = true;
            }
            // 0097D929 skips acquisition for an authored empty name and passes
            // a null texture into the particle buffer; it is not a failed lookup.
            var particle = new EnbCurrentRetailRObjParticleAsset(EnbW3dRenderResourceKey.From(location), null, emitter).PrepareTexture(assets);
            // Every burst samples its own source pose; the detached buffer owns
            // those transforms for the remainder of each particle's lifetime.
            return finiteHistory
                ? new(null, null, overbrightOff, null, null, AggregateParticles: [particle])
                : new(null, null, overbrightOff, null, null, particle);
    }

    private static EnbCurrentRetailRObjEffectAsset CreatePrimitive(EnbRetailAssetBundle assets,EnbResolvedW3dResource source,bool overbrightOff)
    {
        //0090E230/00913180 construct from retained definitions. Their +84/+B0
        //insertions return zero and +CC exposes no animation hierarchy.
        var entry=source.CurrentRetailAggregate?.BaseRegisteredPrototype??source.RegisteredPrototype;
        if(entry is null)
        {
            string name=source.CurrentRetailAggregate?.BaseRequestedObjectName??source.RequestedObjectName;
            if(!EnbW3dRenderResourceCompiler.TryCompile(source,out var table,out var error))throw new InvalidDataException(error);
            foreach(var candidate in source.Model.PrototypeEntries)
                if(EnbW3dRenderResourceCache.PrototypeName(table,candidate).Equals(name,StringComparison.OrdinalIgnoreCase))
                {entry=candidate;break;}
        }
        return entry?.Kind switch
        {
            EnbW3dPrototypeKind.Sphere=>new(null,null,overbrightOff,null,null,
                Spheres:new(null,null,null,[EnbRetailSphereAttachment.LoadFromSource(assets,source)],[],null)),
            EnbW3dPrototypeKind.Ring=>new(null,null,overbrightOff,null,null,
                Rings:new(null,null,null,[EnbRetailRingAttachment.LoadFromSource(assets,source)],null)),
            _=>throw new InvalidDataException("Selected prototype is not a sphere or ring")
        };
    }

    private static EnbCurrentRetailRObjEffectAsset LoadCore(EnbRetailAssetBundle assets, EnbBaseAssetResolution resolved,bool particleJet,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredRslSources,
        out EnbResolvedW3dResource? lazySource,out long registration,out IReadOnlyList<EnbResolvedW3dResource> prototypeLoads,
        out EnbW3dRenderResourceKey? baseMeshSource,RslObjectSelection? rslSelection=null,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? selectedModelChildren=null)
    {
        lazySource=null;registration=0;prototypeLoads=[];baseMeshSource=null;
        // 006F51D0 NULLOBJECT branch constructs 00941330's no-draw object.
        if(resolved.Status==EnbBaseAssetResolutionStatus.NullObject ||
            resolved.Definition.Kind==EnbBaseAssetDefinitionKind.FileName && resolved.Definition.Name is {} builtin &&
            EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(builtin))
            return new(null,null,resolved.Definition.OverbrightOff,null,null){IsNullObject=true};
        if(resolved.Definition.Kind==EnbBaseAssetDefinitionKind.ProceduralWidth)
            return new(null,null,resolved.Definition.OverbrightOff,null,null)
                {ObjectLine=EnbRetailObjectLineAsset.Load(assets,resolved.Definition.Selector)};
        //0091D4B0 returns an acquired prototype without reopening its file.
        //Only unresolved standalone families need raw FILE classification.
        if(resolved.W3d is null && resolved.Definition.File is {} ringFile && assets.FileResources.TryReadResource(ringFile,out var ringBytes,out _) &&
            ringBytes.Length>=8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ringBytes)==0x742)
            return new(null,null,resolved.Definition.OverbrightOff,null,null,
                Rings:EnbRetailRingAssembly.LoadStandalone(assets,resolved));
        if(resolved.W3d is null && resolved.Definition.File is {} sphereFile && assets.FileResources.TryReadResource(sphereFile,out var sphereBytes,out _) &&
            sphereBytes.Length>=8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sphereBytes)==0x741)
            return new(null,null,resolved.Definition.OverbrightOff,null,null,
                Spheres:EnbRetailSphereAssembly.LoadStandalone(assets,resolved));
        if (resolved.W3d is null && resolved.Definition.File is { } file && assets.FileResources.TryReadResource(file, out var bytes, out var location) &&
            EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out var emitter, out _, assets.Textures))
        {
            if (!string.Equals(emitter.Name, resolved.Definition.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RObjEffect emitter prototype name does not match BaseAsset NAME");
            return CreateParticle(assets,emitter,location,resolved.Definition.OverbrightOff,particleJet);
        }
        var source = resolved.W3d;
        EnbResolvedRslStateResource? animationState = null;
        EnbResolvedRslLibraryResourceGraph? sequenceGraph=null;
        if (resolved.Rsl is { ResourceGraph: { } graph } rsl)
        {
            var selected=rslSelection??SelectRslObject(assets,rsl,graph,registeredRslSources);
            if(selectedModelChildren is not PreparedModelChildren)
                registeredRslSources=selected.PrototypeSources??registeredRslSources;
            source=selected.Source;animationState=selected.Animation;sequenceGraph=selected.Sequence;
            prototypeLoads=selected.Publications;
            if(source is null)return new(null,null,resolved.Definition.OverbrightOff,null,null){IsNullObject=true};
        }
        if (source is null) throw new InvalidDataException(resolved.Diagnostic ?? "RObjEffect mesh resource unavailable (particle-only assets require an emitter runtime)");
        if(source.CurrentRetailAggregate is null)
        {
            EnbW3dPrototypeEntry? particleEntry=source.RegisteredPrototype;
            if(particleEntry is null && (source.Model.ParticleEmitters.Count!=0 || source.Model.Spheres.Count!=0 || source.Model.Rings.Count!=0) &&
                EnbW3dRenderResourceCompiler.TryCompile(source,out var particleTable,out _))
                foreach(var candidate in source.Model.PrototypeEntries)
                    if(EnbW3dRenderResourceCache.PrototypeName(particleTable,candidate).Equals(source.RequestedObjectName,StringComparison.OrdinalIgnoreCase))
                    {particleEntry=candidate;break;}
            if(particleEntry?.Kind is EnbW3dPrototypeKind.Sphere or EnbW3dPrototypeKind.Ring)
            {
                registration=source.PrototypeRegistration;
                if(registration==0)lazySource=source;
                return CreatePrimitive(assets,source with {RegisteredPrototype=particleEntry},resolved.Definition.OverbrightOff);
            }
            if(particleEntry is {Kind:EnbW3dPrototypeKind.ParticleEmitter} selectedParticle)
            {
                registration=source.PrototypeRegistration;
                if(registration==0)lazySource=source;
                return CreateParticle(assets,source.Model.ParticleEmitters[selectedParticle.Ordinal],source.ResourceLocation,
                    resolved.Definition.OverbrightOff,particleJet);
            }
        }
        if(source.CurrentRetailAggregate is {} authoredAggregate && selectedModelChildren is PreparedModelChildren {MissingOccurrences.Count:>0} preparedChildren)
        {
            //009A3A75 skips only the occurrence whose creation returned NULL.
            //Keep unknown decoding failures visible, and preserve later
            //successful occurrences even when their authored names repeat.
            source=WithAggregateAttachments(source,authoredAggregate.RenderDefinition.Attachments
                .Where((_,index)=>!preparedChildren.MissingOccurrences.Contains(index)).ToArray());
        }
        registration=source.PrototypeRegistration;
        if(source.CurrentRetailAggregate is {BasePrototypeRegistration:>0} retainedBase)
        {
            if(registration==0)registration=retainedBase.BasePrototypeRegistration;
            baseMeshSource=EnbW3dRenderResourceKey.From(retainedBase.BaseResourceLocation);
        }
        if(resolved.Rsl is not null && registration==0 && source.CurrentRetailAggregate is null)lazySource=source;
        if (!EnbW3dRenderResourceCompiler.TryCompile(source, out var resource, out var error)) throw new InvalidDataException(error);
        //009A4540 creates the named base through0091D4B0. A wrapper does
        //not select every mesh or composite published by the base's file.
        string selectedPrototypeName=source.CurrentRetailAggregate?.BaseRequestedObjectName??source.RequestedObjectName;
        var selectedPrototype=source.CurrentRetailAggregate is {} aggregateBase?aggregateBase.BaseRegisteredPrototype:source.RegisteredPrototype;
        {
            //0091D4B0 retries the requested prototype name, not the file's
            //first composite.0093E680/0093ED00 clone a named MeshClass even
            //when the same file also publishes an HLOD or HMODEL.
            bool foundPrototype=false;
            foreach(var entry in source.Model.PrototypeEntries)
            {
                if(selectedPrototype is {} registered ? entry!=registered :
                    !EnbW3dRenderResourceCache.PrototypeName(resource,entry).Equals(selectedPrototypeName,StringComparison.OrdinalIgnoreCase))continue;
                foundPrototype=true;
                selectedPrototype=entry;
                if(entry.Kind==EnbW3dPrototypeKind.SpecialMaterial)
                {
                    var special=source.Model.SpecialMaterials[entry.Ordinal];
                    return new(resource.Copy().SetMeshes([]).SetHierarchies([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetSpecialMaterials([special]).SetExternalRenderObjects(resource.ExternalRenderObjects.Where(e=>ReferenceEquals(e.SpecialMaterialDefinition,special)).ToArray()).SetCollections([]).SetLegacyLods([]),null,resolved.Definition.OverbrightOff,null,null){SpecialMaterial=special};
                }
                if(entry.Kind is EnbW3dPrototypeKind.Sphere or EnbW3dPrototypeKind.Ring)
                    return CreatePrimitive(assets,source,resolved.Definition.OverbrightOff)
                        with {ForceSubObjectLod=source.CurrentRetailAggregate?.RenderDefinition.ForceSubObjectLod==true};
                if(entry.Kind==EnbW3dPrototypeKind.ParticleEmitter)
                {
                    //009A4540 names the created instance through0097F350.
                    //ParticleRenderObj+84/+B0 inherit no-op bone insertion;
                    //ordered child publication has already happened above.
                    var definition=source.Model.ParticleEmitters[entry.Ordinal];
                    if(source.CurrentRetailAggregate is {} particleAggregate)
                        definition=definition.Copy().SetName(particleAggregate.RenderDefinition.Name);
                    return CreateParticle(assets,definition,source.ResourceLocation,resolved.Definition.OverbrightOff,particleJet)
                        with {ForceSubObjectLod=source.CurrentRetailAggregate?.RenderDefinition.ForceSubObjectLod==true};
                }
                //009416A0 publishes0750;009415E0 clones the named no-draw
                //RenderObj, never another prototype from the containing file.
                //Its +CC returns zero (00462CB0).00459D50 therefore binds no
                //HAnim, and00459B10 returns before terminal/NEXT processing.
                if(entry.Kind==EnbW3dPrototypeKind.NullObject)
                    return new(null,null,resolved.Definition.OverbrightOff,null,null){IsNullObject=true,
                        ForceSubObjectLod=source.CurrentRetailAggregate?.RenderDefinition.ForceSubObjectLod==true};
                if(entry.Kind==EnbW3dPrototypeKind.Dazzle)
                {
                    //0096ED00 creates class32 directly. Its +CC also returns
                    //zero, so an RSL animation cannot bind a sibling hierarchy.
                    var dazzle=resource.ExternalRenderObjects.Where(e=>e.Kind==EnbW3dExternalRenderObjectKind.Dazzle).ElementAt(entry.Ordinal);
                    return new(BindDazzleDefinitions(assets,resource.Copy().SetMeshes([]).SetHierarchies([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetExternalRenderObjects([dazzle]).SetCollections([]).SetLegacyLods([])),null,resolved.Definition.OverbrightOff,null,null)
                        {ForceSubObjectLod=source.CurrentRetailAggregate?.RenderDefinition.ForceSubObjectLod==true};
                }
                if(entry.Kind==EnbW3dPrototypeKind.Box)
                {
                    //0099A910 creates class26/27. Both +CC slots return zero;
                    //00459D50 cannot bind a containing sibling's HAnim.
                    var box=resource.ExternalRenderObjects.Where(e=>e.Kind==EnbW3dExternalRenderObjectKind.Box).ElementAt(entry.Ordinal);
                    if(source.CurrentRetailAggregate is {} boxAggregate)
                        box=box with {Name=boxAggregate.RenderDefinition.Name,
                            BoxDefinition=box.BoxDefinition! with {Name=boxAggregate.RenderDefinition.Name}};
                    return new(resource.Copy().SetMeshes([]).SetHierarchies([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetExternalRenderObjects([box]).SetCollections([]).SetLegacyLods([]),null,resolved.Definition.OverbrightOff,null,null)
                        {ForceSubObjectLod=source.CurrentRetailAggregate?.RenderDefinition.ForceSubObjectLod==true};
                }
                if(entry.Kind==EnbW3dPrototypeKind.Mesh)
                {
                    resource=resource.Copy().SetMeshes([resource.Meshes[entry.Ordinal]]).SetHierarchyModels([]).SetHierarchicalLods([]).SetExternalRenderObjects([]).SetCollections([]).SetLegacyLods([]);
                    //009A3A50 creates/releases children, but MeshClass inherits
                    //the no-op bone insertion. Preserve the prototype's stable
                    //name rather than retail's shared-name/hash corruption.
                    if(source.CurrentRetailAggregate is not null)source=WithAggregateAttachments(source,[]);
                    //MeshClass +CC also returns zero (00B11270 ->00462CB0).
                    //RSL cannot bind a sibling HLOD's clip or advance NEXT.
                    animationState=null;
                    sequenceGraph=null;
                }
                else if(entry.Kind==EnbW3dPrototypeKind.HierarchicalLod)
                {
                    //0098C420 creates only this prototype's retained levels.
                    var prototypeHlod=resource.HierarchicalLods[entry.Ordinal];
                    resource=resource.Copy().SetHierarchicalLods([prototypeHlod]).SetHierarchyModels([]).SetHierarchies(resource.Hierarchies.Where(h=>h.Name.Equals(prototypeHlod.HierarchyName,StringComparison.OrdinalIgnoreCase)).Take(1).ToArray()).SetMeshes(resource.Meshes.Where(m=>prototypeHlod.Levels.SelectMany(l=>l.Members).Concat(prototypeHlod.Aggregate?.Members??[])
                            .Any(n=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,n.Name))).ToArray());
                }
                else if(entry.Kind==EnbW3dPrototypeKind.HierarchyModel)
                {
                    //0093E7B0 constructs0098C7A0 from this named prototype's
                    //connection array and hierarchy, not sibling file roots.
                    var model=resource.HierarchyModels[entry.Ordinal];
                    resource=resource.Copy().SetHierarchyModels([model]).SetHierarchicalLods([]).SetHierarchies(resource.Hierarchies.Where(h=>h.Name.Equals(model.HierarchyName,StringComparison.OrdinalIgnoreCase)).Take(1).ToArray()).SetMeshes(resource.Meshes.Where(m=>model.Nodes.Any(n=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,n.RenderObjectName))).ToArray()).SetExternalRenderObjects(resource.ExternalRenderObjects.Where(e=>model.Nodes.Any(n=>n.RenderObjectName.Equals(e.Name,StringComparison.OrdinalIgnoreCase))).ToArray()).SetCollections([]).SetLegacyLods([]);
                }
                break; // First file registration wins, including other classes.
            }
            //0091D6B2 returns null after a successful file load without the
            //requested name. Leave failure containment to the effect consumer.
            if(!foundPrototype)throw new InvalidDataException($"RObjEffect prototype '{selectedPrototypeName}' is not published by '{source.LogicalFileName}'");
        }
        if(selectedModelChildren is PreparedModelChildren capturedBase)
        {
            int memberIndex=0;
            bool KeepMember()=>memberIndex<capturedBase.BaseOccurrences.Count && capturedBase.BaseOccurrences[memberIndex++].Source is not null;
            if(selectedPrototype?.Kind==EnbW3dPrototypeKind.HierarchyModel && resource.HierarchyModels.Count==1)
                resource=resource.Copy().SetHierarchyModels([resource.HierarchyModels[0] with {Nodes=resource.HierarchyModels[0].Nodes.Where(_=>KeepMember()).ToArray()}]);
            else if(selectedPrototype?.Kind==EnbW3dPrototypeKind.HierarchicalLod && resource.HierarchicalLods.Count==1)
            {
                var retainedHlod=resource.HierarchicalLods[0];
                var levels=retainedHlod.Levels.Select(level=>level with {Members=level.Members.Where(_=>KeepMember()).ToArray()}).ToArray();
                var retainedAggregate=retainedHlod.Aggregate is {} value?value with {Members=value.Members.Where(_=>KeepMember()).ToArray()}:null;
                resource=resource.Copy().SetHierarchicalLods([retainedHlod with {Levels=levels,Aggregate=retainedAggregate}]);
            }
            else if(selectedPrototype?.Kind==EnbW3dPrototypeKind.Collection && resource.Collections.Count==1)
                resource=resource.Copy().SetCollections([resource.Collections[0] with {ChildNames=resource.Collections[0].ChildNames.Where(_=>KeepMember()).ToArray()}]);
        }
        if(registeredRslSources is not null &&
            selectedPrototype?.Kind is EnbW3dPrototypeKind.HierarchyModel or EnbW3dPrototypeKind.HierarchicalLod)
        {
            //0098C7A0/0098C420 acquire named children at construction, then
            //0098EA70/0098F1D0 read those actual children for cost and bounds.
            //The global name lookup is independent of the parent's file.
            var compiledChildren=new Dictionary<EnbW3dModel,EnbW3dRenderResource>();
            var childNames=resource.HierarchyModels.Count==1
                ? resource.HierarchyModels[0].Nodes.Select(node=>node.RenderObjectName)
                : resource.HierarchicalLods[0].Levels.SelectMany(level=>level.Members).Concat(resource.HierarchicalLods[0].Aggregate?.Members??[]).Select(member=>member.Name);
            var acquiredMeshes=resource.Meshes.Where(mesh=>childNames.Any(name=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,name))).ToList();
            var acquiredExternal=resource.ExternalRenderObjects.ToList();
            var selectedChildren=selectedModelChildren is PreparedModelChildren captured
                ? captured.BaseOccurrences.Where(value=>value.Source is not null).Select(value=>value.Source!)
                : childNames.Distinct(StringComparer.OrdinalIgnoreCase).Select(name=>registeredRslSources.GetValueOrDefault(name)).Where(value=>value is not null).Select(value=>value!);
            foreach(var child in selectedChildren)
            {
                string childName=child.RequestedObjectName;
                if(child.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.NullObject})
                {
                    //98C420 retains named class22 children too; their name
                    //need not be the built-in NULL spelling.
                    acquiredExternal.RemoveAll(e=>e.Name.Equals(childName,StringComparison.OrdinalIgnoreCase));
                    acquiredExternal.Add(new(childName,EnbW3dExternalRenderObjectKind.NullObject,
                        child.ResourceLocation.Name,child.ResourceLocation.ArchiveName));
                    continue;
                }
                if(child.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Box} boxEntry)
                {
                    //98C420 retains class26/27 children for bounds/cost even
                    //when their normal display mask produces no geometry.
                    acquiredExternal.RemoveAll(e=>e.Name.Equals(childName,StringComparison.OrdinalIgnoreCase));
                    acquiredExternal.Add(new(childName,EnbW3dExternalRenderObjectKind.Box,
                        child.ResourceLocation.Name,child.ResourceLocation.ArchiveName)
                        {BoxDefinition=child.Model.Boxes[boxEntry.Ordinal]});
                    continue;
                }
                if(child.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Dazzle} dazzleEntry)
                {
                    //98C420 retains the actual named child from91D4B0,
                    //including class32 from a different published FILE.
                    acquiredExternal.RemoveAll(e=>e.Name.Equals(childName,StringComparison.OrdinalIgnoreCase));
                    acquiredExternal.Add(new(childName,EnbW3dExternalRenderObjectKind.Dazzle,
                        child.ResourceLocation.Name,child.ResourceLocation.ArchiveName)
                        {DazzleTypeName=child.Model.Dazzles[dazzleEntry.Ordinal].Type});
                    continue;
                }
                if(child.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.SpecialMaterial} specialEntry)
                {
                    acquiredExternal.RemoveAll(e=>e.Name.Equals(childName,StringComparison.OrdinalIgnoreCase));
                    acquiredExternal.Add(new(childName,EnbW3dExternalRenderObjectKind.SpecialMaterial,child.ResourceLocation.Name,child.ResourceLocation.ArchiveName)
                        {SpecialMaterialDefinition=child.Model.SpecialMaterials[specialEntry.Ordinal]});
                    continue;
                }
                if(child.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.ParticleEmitter} emitterEntry)
                {
                    //0091D4B0 resolves the retained registry child, even when
                    //its definition comes from another FILE than the parent.
                    var acquiredEmitter=child.Model.ParticleEmitters[emitterEntry.Ordinal];
                    acquiredExternal.RemoveAll(e=>e.Name.Equals(childName,StringComparison.OrdinalIgnoreCase));
                    acquiredExternal.Add(new(childName,EnbW3dExternalRenderObjectKind.ParticleEmitter,
                        child.ResourceLocation.Name,child.ResourceLocation.ArchiveName){ParticleEmitterDefinition=acquiredEmitter});
                    continue;
                }
                if(child.RegisteredPrototype is not {Kind:EnbW3dPrototypeKind.Mesh} childEntry ||
                    ReferenceEquals(child.Model,source.Model))continue;
                if(!compiledChildren.TryGetValue(child.Model,out var compiled))
                {
                    if(!EnbW3dRenderResourceCompiler.TryCompile(child,out compiled,out var childError))throw new InvalidDataException(childError);
                    compiledChildren.Add(child.Model,compiled);
                }
                int existing=acquiredMeshes.FindIndex(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,childName));
                if(existing<0)acquiredMeshes.Add(compiled.Meshes[childEntry.Ordinal]);
                else acquiredMeshes[existing]=compiled.Meshes[childEntry.Ordinal];
            }
            resource=resource.Copy().SetMeshes(acquiredMeshes.ToArray()).SetExternalRenderObjects(acquiredExternal.ToArray());
        }
        EnbW3dRenderHierarchyAnimation? animation = null;
        if (animationState is not null)
        {
            //0093E7B0 builds HMODEL prototypes through0098C7A0 too;
            //00459D50 binds via the same HLOD/HAnim virtual+CC tree.
            var hierarchy = resource.HierarchicalLods.SingleOrDefault()?.HierarchyName ?? resource.HierarchyModels.Single().HierarchyName;
            var name = EnbCurrentRetailRslAnimationRuntime.BuildHAnimName(hierarchy, animationState.Definition.Animation, animationState.Definition.Suffix);
            if (!EnbCurrentRetailRslHAnimResolver.TryResolve(resource, animationState.FileDependency, assets.Paths,
                    assets.MixResources, assets.RetailMixResources, name, out var loaded, out error))
            {
                // Get_HAnim found nothing: 00459D50 retains the initial object with a NULL HAnim;
                // 00459B10 cannot reach terminal/NEXT logic without that clip.
                // This initial state never creates a sequence clock. The outer
                // effect and its static aggregate retain their own lifetimes.
                sequenceGraph=null;
            }
            else { animation = loaded.Animation; resource = loaded.DrawResource; }
        }
        var animationFlags=animationState is null?null:
            EnbCurrentRetailRslAnimationRuntime.CopyTemplateFlags(animationState.Definition.Flags);
        // Current manager 0091D3FE..403 queries prototype existence and
        // 0091D47E..484 destroys a later duplicate for direct and RSL loads.
        // Preserve file order and exact qualified identities; bare-name
        // ambiguity remains rejected. The raw compiler retains every chunk.
        resource=resource.Copy().SetMeshes(resource.Meshes.DistinctBy(m=>
            m.ContainerName.Length==0 ? m.Name : m.ContainerName+"."+m.Name,StringComparer.OrdinalIgnoreCase).ToArray());
        bool TryResolveModelChild(string name,out EnbResolvedW3dResource child)
        {
            if(selectedModelChildren is PreparedModelChildren captured)return captured.AcquiredChildren.TryGetValue(name,out child!);
            return selectedModelChildren?.TryGetValue(name,out child!)==true ||
                EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAggregateChild(assets.FileResources,assets.RetailMixResources,
                    name,out child,out _,registeredRslSources);
        }
        EnbCurrentRetailRObjEffectAsset BuildModelChild(int index,EnbResolvedW3dResource child)
        {
            PreparedModelChildren? plan=null;
            if(selectedModelChildren is PreparedModelChildren captured)
            {
                int originalIndex=Enumerable.Range(0,captured.AttachmentCount).Where(value=>!captured.MissingOccurrences.Contains(value)).ElementAt(index);
                if(captured.AttachmentOccurrences.TryGetValue(originalIndex,out var occurrence))
                {child=occurrence.Source;plan=occurrence.Plan;}
            }
            var loaded=LoadPrototype(assets,child,Vector3.Zero,registeredRslSources,plan);
            if(loaded.IsNullObject && SelectedPrototype(child) is {Kind:EnbW3dPrototypeKind.NullObject} nullEntry)
            {
                //009A3A50 retains the concrete no-draw child too. Preserve its
                //selected identity for the existing retained resource owners.
                if(!EnbW3dRenderResourceCompiler.TryCompile(child,out var nullResource,out var nullError))
                    throw new InvalidDataException(nullError);
                var leaf=nullResource.ExternalRenderObjects.Where(e=>e.Kind==EnbW3dExternalRenderObjectKind.NullObject).ElementAt(nullEntry.Ordinal);
                loaded=loaded with {Resource=nullResource.Copy().SetMeshes([]).SetHierarchies([]).SetAnimations([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetExternalRenderObjects([leaf]).SetCollections([]).SetLegacyLods([]).SetSpecialMaterials([]).SetParticleEmitters([]).SetSpheres([]).SetRings([])};
            }
            return loaded;
        }
        bool IsModelChild(EnbCurrentRetailW3dAggregateAttachment a)
        {
            if(!TryResolveModelChild(a.SubobjectName,out var child))return false;
            var kind=SelectedPrototype(child)?.Kind;
            //009A4540 returns the concrete base clone. These leaf classes'
            //+80 ->00941F60 ->+84/00462AD0 discards wrapper insertions;
            //they remain leaves even when authored through nested0600.
            return kind is EnbW3dPrototypeKind.HierarchicalLod or EnbW3dPrototypeKind.HierarchyModel or EnbW3dPrototypeKind.SpecialMaterial or EnbW3dPrototypeKind.Mesh or EnbW3dPrototypeKind.Box or EnbW3dPrototypeKind.Dazzle or EnbW3dPrototypeKind.NullObject;
        }
        bool independentModels=(resource.HierarchicalLods.Count==1 || resource.HierarchyModels.Count==1) &&
            source.CurrentRetailAggregate is {} modelAggregate && modelAggregate.RenderDefinition.Attachments.Count>0 &&
            modelAggregate.RenderDefinition.Attachments.Any(IsModelChild);
        EnbPreparedRslAnimationSequence? preparedSequence=null;
        bool HasPrimitiveChild(uint chunk)=>source.CurrentRetailAggregate?.RenderDefinition.Attachments.Any(a=>
            registeredRslSources?.TryGetValue(a.SubobjectName,out var selected)==true
                ? (selected.CurrentRetailAggregate?.BaseRegisteredPrototype??selected.RegisteredPrototype)?.Kind==(chunk==0x741?EnbW3dPrototypeKind.Sphere:EnbW3dPrototypeKind.Ring)
                : assets.RetailMixResources!.TryReadResource(a.SubobjectName+".w3d",out var child,out _) &&
                  child.Length>=8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(child)==chunk)==true;
        bool hasSphere=HasPrimitiveChild(0x741),hasRing=HasPrimitiveChild(0x742);
        if(sequenceGraph is not null)
        {
            preparedSequence=EnbPreparedRslAnimationSequence.Load(assets,sequenceGraph,animationState!.Definition.StateId,ref resource,animation);
            if(source.CurrentRetailAggregate is {} sequenceAggregate && sequenceAggregate.RenderDefinition.Attachments.Count!=0 && !independentModels && !hasRing && !hasSphere && !(resource.HierarchicalLods.Count==0 && resource.HierarchyModels.Count==1))
            {
                var mixed=EnbCurrentRetailMixedEffectAsset.Load(assets,resource,sequenceAggregate.RenderDefinition,animation,animationFlags,registeredRslSources);
                if(!EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(mixed.Resource,mixed.Resource.HierarchicalLods.Single(),_=>true,out _,out var sequenceError))
                    throw new InvalidDataException(sequenceError);
                return new(mixed.Resource,mixed.Resource.HierarchicalLods.Single().Levels.Count==1 ? new(mixed.Resource.HierarchicalLods.Single().Name,0) : null,
                    resolved.Definition.OverbrightOff,animation,animationFlags,Mixed:mixed){Sequence=preparedSequence,
                    ForceSubObjectLod=sequenceAggregate.RenderDefinition.ForceSubObjectLod};
            }
        }
        // 009A3A50 skips attachment work for count zero; 009A4540 still
        // returns the base object's clone. Preserve ordinary HLOD validation.
        if (source.CurrentRetailAggregate is { } aggregate && aggregate.RenderDefinition.Attachments.Count!=0)
        {
            if((hasSphere || hasRing) && resource.HierarchyModels.SingleOrDefault() is {} primitiveParent)
            {
                //0098C7A0's single retained level also owns primitive children
                //inserted by009A3A50; retain their independent detail allocation.
                ValidateHmodel(resource);
                resource=resource.Copy().SetHierarchyModels([]).SetHierarchicalLods([EnbW3dHmodelConversion.Create(primitiveParent,_=>true)]);
            }
            if(hasSphere && !independentModels)
            {
                var spheres=EnbRetailSphereAssembly.Load(assets,resource,aggregate.RenderDefinition,animation,animationFlags,registeredRslSources);
                var baseHlod=spheres.Resource!.HierarchicalLods.Single();
                //98E4B0 ->98F1D0 unions permanent children even for a
                //single-level parent; scene-frustum admission also needs them.
                spheres=spheres.BindParentBounds();
                var companion=spheres.Rings.Count==0 ? null : new EnbRetailRingAssembly(spheres.Resource,animation,animationFlags,spheres.Rings,spheres.Emitters);
                return new(spheres.Resource,baseHlod.Levels.Count==1?new(baseHlod.Name,0):null,resolved.Definition.OverbrightOff,animation,animationFlags,Mixed:spheres.Emitters,Rings:companion,Spheres:spheres){Sequence=preparedSequence,
                    ForceSubObjectLod=aggregate.RenderDefinition.ForceSubObjectLod};
            }
            if(hasRing && !independentModels)
            {
                //0098E9A0 budgets a multilevel parent and secondary children
                //independently; only a single-level parent is a fixed cost.
                if(resource.HierarchicalLods.SingleOrDefault() is not {} baseHlod || baseHlod.Levels.Count<1)
                    throw new InvalidDataException("Ring effect requires a base HLOD");
                var rings=EnbRetailRingAssembly.Load(assets,resource,aggregate.RenderDefinition,animation,animationFlags,registeredRslSources);
                rings=rings.BindParentBounds();
                return new(rings.Resource,baseHlod.Levels.Count==1?new(baseHlod.Name,0):null,resolved.Definition.OverbrightOff,animation,animationFlags,Mixed:rings.Emitters,Rings:rings){Sequence=preparedSequence,
                    ForceSubObjectLod=aggregate.RenderDefinition.ForceSubObjectLod};
            }
            bool attachedHmodel=resource.Meshes.Count!=0 && resource.HierarchicalLods.Count==0 && resource.HierarchyModels.Count!=0;
            if(attachedHmodel)
            {
                ValidateHmodel(resource);
                resource=EnbCurrentRetailMeshTexturePreparation.Prepare(resource,assets);
            }
            if (independentModels || (resource.Meshes.Count != 0 && !attachedHmodel))
            {
                if(independentModels)
                {
                    //009A4540 retains the base clone;009A3A50 attaches each
                    //independently acquired child, including an empty HLOD.
                    var parent=LoadPrototype(assets,source with {CurrentRetailAggregate=null,RegisteredPrototype=aggregate.BaseRegisteredPrototype,
                        PrototypeRegistration=aggregate.BasePrototypeRegistration,
                        LogicalFileName=aggregate.BaseLogicalFileName,RequestedObjectName=aggregate.BaseRequestedObjectName,
                        ResourceLocation=aggregate.BaseResourceLocation,LooseFilePath=aggregate.BaseLooseFilePath},Vector3.Zero,registeredRslSources,selectedModelChildren);
                    if(parent.Resource!.HierarchyModels.SingleOrDefault() is {} parentModel)
                    {
                        //0098C7A0 constructs one retained HLOD level from HMODEL
                        //nodes before009A3A50 inserts independent children.
                        var converted=EnbW3dHmodelConversion.Create(parentModel,_=>true);
                        parent=parent with {Resource=parent.Resource.Copy().SetHierarchyModels([]).SetHierarchicalLods([converted]),
                            Selection=new(converted.Name,0)};
                    }
                    var hierarchy=parent.Resource!.Hierarchies.Single();
                    var models=aggregate.RenderDefinition.Attachments.Select((attachment,index)=>(attachment,index)).Where(value=>IsModelChild(value.attachment)).Select(value=>
                    {
                        var a=value.attachment;
                        if(!TryResolveModelChild(a.SubobjectName,out var childSource))
                            throw new InvalidDataException($"Aggregate model attachment '{a.SubobjectName}' could not materialize");
                        var child=BuildModelChild(value.index,childSource);
                        if(child.Mixed is {} childMixed)
                            child=child with {Mixed=null,CompositeEmitters=EnbRObjCompositeEmitter.Prepare(assets,childMixed.Resource)};
                        if(child.Resource?.HierarchyModels.SingleOrDefault() is {} childModel)
                        {
                            //0093E7B0 clones an HMODEL through0098C7A0 before
                            //the parent's secondary-child insertion. Preserve
                            //the child's own hierarchy and fixed one-level cost.
                            ValidateHmodel(child.Resource);
                            var converted=EnbW3dHmodelConversion.Create(childModel,_=>true);
                            child=child with {Resource=child.Resource.Copy().SetHierarchyModels([]).SetHierarchicalLods([converted]),
                                Selection=new(converted.Name,0)};
                        }
                        EnbRObjModelAttachment.Validate(child);
                        return new EnbRObjModelAttachment(a.SubobjectName,EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,a.BoneName),child);
                    }).ToArray();
                    var parentResource=parent.Resource.Copy().SetAnimations(resource.Animations);
                    var particleAttachments=aggregate.RenderDefinition.Attachments.Where(a=>!IsModelChild(a)).ToArray();
                    IReadOnlyList<EnbCurrentRetailRObjParticleAsset>? companions=null;
                    EnbCurrentRetailMixedEffectAsset? mixedCompanions=null;
                    EnbRetailRingAssembly? ringCompanions=null;
                    EnbRetailSphereAssembly? sphereCompanions=null;
                    if(particleAttachments.Length!=0)
                    {
                        var remaining=aggregate.RenderDefinition with {Attachments=particleAttachments};
                        //009A3A50/0098E4B0 retain heterogeneous secondary
                        //children together; each keeps its own render consumer.
                        if(hasSphere)
                        {
                            sphereCompanions=EnbRetailSphereAssembly.Load(assets,parentResource,remaining,animation,animationFlags,registeredRslSources);
                            sphereCompanions=sphereCompanions.BindParentBounds();
                            parentResource=sphereCompanions.Resource!;
                            mixedCompanions=sphereCompanions.Emitters;
                            if(sphereCompanions.Rings.Count!=0)
                                ringCompanions=new(parentResource,animation,animationFlags,sphereCompanions.Rings,mixedCompanions);
                        }
                        else if(hasRing)
                        {
                            ringCompanions=EnbRetailRingAssembly.Load(assets,parentResource,remaining,animation,animationFlags,registeredRslSources);
                            ringCompanions=ringCompanions.BindParentBounds();
                            parentResource=ringCompanions.Resource!;
                            mixedCompanions=ringCompanions.Emitters;
                        }
                        else
                        {
                            var companionGroup=EnbCurrentRetailMixedEffectAsset.Load(assets,parentResource,remaining,animation,animationFlags,registeredRslSources);
                            parentResource=companionGroup.Resource;
                            if(companionGroup.Streaks.Count!=0)mixedCompanions=companionGroup;
                            else companions=companionGroup.Sprites;
                        }
                    }
                    var combinedResource=EnbRObjModelAttachment.BindBounds(parentResource,models);
                    mixedCompanions=mixedCompanions is null?null:mixedCompanions with {Resource=combinedResource};
                    return parent with {Resource=combinedResource,
                        Animation=animation,AnimationFlags=animationFlags,Sequence=preparedSequence,
                        OverbrightOff=resolved.Definition.OverbrightOff,ModelAttachments=EnbRObjModelAttachment.Flatten(models),AggregateParticles=companions,
                        Mixed=mixedCompanions,
                        Rings=ringCompanions is null?null:ringCompanions with {Resource=combinedResource,Emitters=mixedCompanions},
                        Spheres=sphereCompanions is null?null:sphereCompanions with {Resource=combinedResource,Emitters=mixedCompanions},
                        ForceSubObjectLod=aggregate.RenderDefinition.ForceSubObjectLod};
                }
                var mixed = EnbCurrentRetailMixedEffectAsset.Load(assets, resource, aggregate.RenderDefinition,animation,animationFlags,registeredRslSources);
                return new(mixed.Resource, mixed.Resource.HierarchicalLods.Single().Levels.Count==1 ? new(mixed.Resource.HierarchicalLods.Single().Name,0) : null,
                    resolved.Definition.OverbrightOff, animation, animationFlags, Mixed: mixed)
                    {ForceSubObjectLod=aggregate.RenderDefinition.ForceSubObjectLod};
            }
            // Empty particle-only HLODs retain authored attachment transforms.
            // Identity finite bursts can use the simpler owner-pose history.
            var h = resource.HierarchicalLods.SingleOrDefault();
            var tree = resource.Hierarchies.SingleOrDefault();
            bool emptyHlod = h is not null && tree is not null && h.Levels.Count == 1 &&
                h.Levels[0].Members.Count == 0 && !h.HasAggregateExtension &&
                h.HierarchyName.Equals(tree.Name,StringComparison.OrdinalIgnoreCase);
            // 0x0600 binds children to its base RenderObj's authored bones;
            // it does not require an HLOD. EXPLSK01 is an empty static HMODEL.
            bool emptyHmodel = h is null && tree is not null && resource.HierarchyModels.Count == 1 &&
                resource.HierarchyModels[0].Nodes.Count == 0 &&
                resource.HierarchyModels[0].HierarchyName.Equals(tree.Name,StringComparison.OrdinalIgnoreCase);
            if(emptyHmodel)ValidateHmodel(resource);
            if ((resource.Meshes.Count != 0 && !attachedHmodel) ||
                tree is null || (!emptyHlod && !emptyHmodel && !attachedHmodel) ||
                aggregate.RenderDefinition.Attachments.Count < 1)
                throw new InvalidDataException("RObjEffect aggregate requires a bound HLOD or hierarchy model");
            bool bound = attachedHmodel || animation is not null || tree.Pivots.Any(p => !p.LocalTransform.IsIdentity);
            // Native aggregate children retain their own RenderObj and LOD identity.
            // Preserve authored occurrence order, including repeated prototypes.
            // 009A3A50 iterates the complete authored count; child validation
            // is structural, independent of the number of occurrences.
            bool staticModelChildren=emptyHlod && animation is null &&
                aggregate.RenderDefinition.Attachments.Any(a=>
                    TryResolveModelChild(a.SubobjectName,out var childModel) &&
                    childModel.CurrentRetailAggregate is null &&
                    EnbW3dRenderResourceCompiler.TryCompile(childModel,out var childResource,out _) &&
                    childResource.Meshes.Count>0 && childModel.RegisteredPrototype?.Kind==EnbW3dPrototypeKind.HierarchicalLod);
            var modelChildren=new List<EnbRObjModelAttachment>();
            var particles = new List<EnbCurrentRetailRObjParticleAsset>();
            for(int attachmentIndex=0;attachmentIndex<aggregate.RenderDefinition.Attachments.Count;attachmentIndex++)
            {
                var attachment=aggregate.RenderDefinition.Attachments[attachmentIndex];
                int pivot = EnbCurrentRetailAggregateBoneLookup.Resolve(tree, attachment.BoneName);
                if (pivot >= tree.Pivots.Count)
                    throw new InvalidDataException("RObjEffect aggregate requires a nonempty attachment hierarchy");
                if(emptyHlod && (animation is not null || staticModelChildren) &&
                    TryResolveModelChild(attachment.SubobjectName,out var nestedModel) &&
                    nestedModel.CurrentRetailAggregate is null &&
                    EnbW3dRenderResourceCompiler.TryCompile(nestedModel,out var modelResource,out _) && modelResource.Meshes.Count>0)
                {
                    var modelAsset=BuildModelChild(attachmentIndex,nestedModel);
                    EnbRObjModelAttachment.Validate(modelAsset);
                    modelChildren.Add(new(attachment.SubobjectName,pivot,modelAsset));
                    continue;
                }
                if (!EnbCurrentRetailParticlePrototypeLookup.TryResolve(assets,attachment.SubobjectName,
                    registeredRslSources,out var child,out var childLocation))
                    throw new InvalidDataException("RObjEffect aggregate attachment is not a bound particle emitter");
                if(child.RenderMode is 3 or 4)
                {
                    var mixed=EnbCurrentRetailMixedEffectAsset.Load(assets,resource,aggregate.RenderDefinition,animation,animationFlags,registeredRslSources);
                    return new(mixed.Resource,null,resolved.Definition.OverbrightOff,animation,animationFlags,Mixed:mixed)
                        {Sequence=preparedSequence,ForceSubObjectLod=aggregate.RenderDefinition.ForceSubObjectLod};
                }
                //0098C7A0 uses the common secondary child array. The parent
                //kind does not restrict emitter mode; ValidateAnimated below
                //retains the supported line/triangle/quad data contracts.
                // Match standalone/mixed particles: an authored empty name has
                // no texture binding, rather than a missing texture resource.
                particles.Add(new EnbCurrentRetailRObjParticleAsset(EnbW3dRenderResourceKey.From(childLocation), null, child,pivot).PrepareTexture(assets));
            }
            bound |= particles.Any(p=>p.Definition.MaximumEmissions==0 || p.Definition.RenderMode==2);
            foreach(var particle in particles)
                if(bound) EnbCurrentRetailRObjParticleAsset.ValidateAnimated(particle.Definition);
                else EnbCurrentRetailRObjParticleAsset.ValidateFinite(particle.Definition);
            if(modelChildren.Count!=0)
                return new(resource,new(h!.Name,0),resolved.Definition.OverbrightOff,animation,animationFlags,
                    AggregateParticles:particles.Count==0 ? null : particles){ModelAttachments=modelChildren.ToArray(),Sequence=preparedSequence,
                    ForceSubObjectLod=aggregate.RenderDefinition.ForceSubObjectLod};
            //0098C420/0098C7A0 retain the parent even when its hierarchy is
            //static and empty.009A3A50 attaches each emitter to that parent's
            //secondary child array; finite completion removes the child, not
            //the parent. Dropping Resource selected a scene-less replay path.
            return new(resource, null, resolved.Definition.OverbrightOff, animation, animationFlags, AggregateParticles: particles){Sequence=preparedSequence,
                ForceSubObjectLod=aggregate.RenderDefinition.ForceSubObjectLod};
        }
        if(source.CurrentRetailAggregate is null && animation is null && resource.Meshes.Count>0 &&
            resource.HierarchicalLods.SingleOrDefault()?.Aggregate is {Members.Count:>0} inline &&
            inline.Members.Any(member=>!EnbCurrentRetailMixedEffectAsset.IsRetainedNonParticleChild(resource,member.Name)))
        {
            var mixed=EnbCurrentRetailMixedEffectAsset.LoadInline(assets,resource);
            return new(mixed.Resource,mixed.Resource.HierarchicalLods.Single().Levels.Count==1?new(mixed.Resource.HierarchicalLods.Single().Name,0):null,
                resolved.Definition.OverbrightOff,null,null,Mixed:mixed);
        }
        EnbW3dHlodSelection? selection = null;
        if (resource.HierarchicalLods.Count > 1) throw new InvalidDataException("RObjEffect multiple HLOD roots not yet implemented");
        bool staticModel=false;
        if(resource.HierarchicalLods.Count==0 && resource.HierarchyModels.Count!=0)
        {
            ValidateHmodel(resource);
            staticModel=true;
        }
        if (resource.HierarchicalLods.Count == 0 && resource.Meshes.Count != 1 && !staticModel)
            throw new InvalidDataException("RObjEffect requires one mesh or a locally bound HLOD");
        if (resource.HierarchicalLods.SingleOrDefault() is { } hlod)
        {
            var tree = resource.Hierarchies.SingleOrDefault(h => h.Name.Equals(hlod.HierarchyName, StringComparison.OrdinalIgnoreCase));
            //0706 records are queried through0098DAA0/0098DAC0; they do
            //not participate in0098DD50's level/permanent child traversal.
            if (hlod.Levels.Count == 0 ||
                tree is null ||
                hlod.Levels.SelectMany(l=>l.Members).Concat(hlod.Aggregate?.Members??[]).Any(member => member.ReferenceIndex >= tree.Pivots.Count || (!EnbCurrentRetailRenderObjectSemantics.IsNoOpDrawRenderObject(resource,member.Name) &&
                    !resource.Meshes.Any(mesh => EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh, member.Name)))))
                throw new InvalidDataException("RObjEffect requires a locally bound mesh HLOD");
            if(hlod.Levels.Count==1)selection = new(hlod.Name, 0);
        }
        // Predecode referenced textures off the render thread as well.
        resource=BindDazzleDefinitions(assets,resource);
        resource=EnbCurrentRetailMeshTexturePreparation.Prepare(resource,assets);
        return new(resource, selection, resolved.Definition.OverbrightOff, animation, animationFlags){Sequence=preparedSequence,
            FactoryObjectName=source.RequestedObjectName,
            ForceSubObjectLod=source.CurrentRetailAggregate?.RenderDefinition.ForceSubObjectLod==true};
    }

    internal static EnbW3dRenderResource BindDazzleDefinitions(EnbRetailAssetBundle assets,EnbW3dRenderResource resource)
    {
        if(!resource.ExternalRenderObjects.Any(e=>e.DazzleTypeName is not null && e.DazzleDefinition is null))return resource;
        var definitions=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"Dazzle.ini"));
        return resource.Copy().SetExternalRenderObjects(resource.ExternalRenderObjects.Select(e=>
            e.DazzleTypeName is {} type && e.DazzleDefinition is null
                ? e with {DazzleDefinition=EnbDazzleDefinition.Resolve(definitions,type)}:e).ToArray());
    }

    public EnbW3dDazzleDrawInstance PlaceDazzle(EnbW3dDazzleDrawInstance draw,Matrix4x4 ownerRigidWorld,float scale,uint start,uint now,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null,float? hierarchySourceScale=null)
    {
        //0096BAB0 scales +E8 only.0098EF60 scales stored hierarchy translations
        //before propagating scale to children; its basis stays unscaled.
        var world=ownerRigidWorld;
        if(draw.HlodName.Length!=0)
        {
            var hlod=Resource!.HierarchicalLods.Single(h=>h.Name==draw.HlodName);
            var tree=Resource.Hierarchies.Single(h=>h.Name.Equals(hlod.HierarchyName,StringComparison.OrdinalIgnoreCase));
            float frame=0;string? error=null;
            var animation=Animation;
            if(Sequence is not null)
            {
                if(observed is not {} sample)throw new InvalidDataException("Sequence dazzle requires an animation observation");
                animation=sample.BoundAnimation;frame=sample.Frame;
            }
            else if(animation is not null && !EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(animation,AnimationFlags!,start,now,out frame,out error))
                throw new InvalidDataException(error);
            if(!EnbW3dHierarchyPose.TryEvaluate(tree,ownerRigidWorld,animation,frame,null,out var pose,out error,storedTranslationScale:ResolveHierarchyScale(scale,hierarchySourceScale)))
                throw new InvalidDataException(error);
            //0098DD50 traverses selected level followed by permanent children,
            //matching the draw compiler's combined member ordinal.
            var levelMembers=hlod.Levels[draw.LevelOrdinal].Members;
            var member=draw.MemberOrdinal<levelMembers.Count?levelMembers[draw.MemberOrdinal]
                :hlod.Aggregate!.Members[draw.MemberOrdinal-levelMembers.Count];
            world=pose.WorldTransforms[(int)member.ReferenceIndex];
        }
        //96BAB0 stores factory scale on the dazzle even when its parent
        //hierarchy remains unconfigured in this query descriptor.6EF160's
        //source-radius scale is the only remaining draw multiplier.
        return draw with {World=world,CurrentRetailDazzleScale=draw.HasConfiguredDazzleScale
            ?HasConfiguredHierarchy?ResolveHierarchyScale(scale,hierarchySourceScale):hierarchySourceScale??scale
            :scale};
    }

    internal static void ValidateHmodel(EnbW3dRenderResource resource)
    {
        var model=resource.HierarchyModels.SingleOrDefault();
        var tree=resource.Hierarchies.SingleOrDefault();
        //0098C7A0 ->009C5210 initializes mode1 with no active HAnim.
        //Library clips do not activate it;009C5980 explicitly binds a clip.
        if(model is null || tree is null ||
            !model.HierarchyName.Equals(tree.Name,StringComparison.OrdinalIgnoreCase) ||
            model.Nodes.Any(n=>n.Kind is not (EnbW3dRenderHierarchyNodeKind.RenderObject or EnbW3dRenderHierarchyNodeKind.Collision or EnbW3dRenderHierarchyNodeKind.Skin) || n.PivotIndex>=tree.Pivots.Count ||
                (resource.Meshes.Count(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,n.RenderObjectName))!=1 &&
                 !resource.ExternalRenderObjects.Any(e=>(e.ParticleEmitterDefinition is not null || e.SpecialMaterialDefinition is not null || e.BoxDefinition is not null || e.Kind is EnbW3dExternalRenderObjectKind.Dazzle or EnbW3dExternalRenderObjectKind.NullObject) && e.Name.Equals(n.RenderObjectName,StringComparison.OrdinalIgnoreCase)))) ||
            resource.Meshes.Any(m=>!model.Nodes.Any(n=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,n.RenderObjectName))))
            throw new InvalidDataException("RObjEffect requires a complete HMODEL with local mesh bindings");
    }

    public IReadOnlyList<EnbW3dWeightedAnimationSample> Sample(uint start, uint now,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null)
    {
        if(Sequence is not null)
        {
            if(observed is not {} sample)throw new InvalidDataException("Sequence draw requires an animation observation");
            return sample.Active?[new(sample.Animation.Name,sample.Frame,1,sample.Animation.HierarchyName)]:[];
        }
        if (Animation is null) return [];
        if (!EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(Animation, AnimationFlags!, start, now, out var frame, out var error))
            throw new InvalidDataException(error);
        return [new(Animation.Name, frame, 1, Animation.HierarchyName)];
    }
}
