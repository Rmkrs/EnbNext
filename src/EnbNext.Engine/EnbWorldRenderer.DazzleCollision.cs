using System.Numerics;
using System.Runtime.CompilerServices;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // Factory-local appearance is not an alternate world object or pose publication.
    private readonly record struct FactoryAppearance(float Scale,float HsvH,float HsvS,float HsvV)
    {
        public static FactoryAppearance From(EnbNext.Presentation.IEnbRenderObject owner)
            =>new(owner.Scale,owner.HsvH,owner.HsvS,owner.HsvV);
        public Vector3 Hsv=>new(HsvH,HsvS,HsvV);
        public static FactoryAppearance Neutral=>new(1,0,0,0);
    }
    private readonly Dictionary<string,(LiveCpuRoot Root,float Scale,Vector3 Hsv)> _configuredDirectMeshRoots=new(StringComparer.OrdinalIgnoreCase);

    private sealed record DirectConfiguration((int Scale,int H,int S,int V) Input,LiveCpuRoot Root,float Scale,Vector3 Hsv);
    private sealed class DirectConfigurationSlot { public DirectConfiguration? Last; }
    private readonly ConditionalWeakTable<LiveCpuRoot,DirectConfigurationSlot> _directConfigurations=new();
    private LiveCpuRoot RetainConfiguredDirectMeshRoot(LiveCpuRoot source,ref FactoryAppearance appearance)
    {
        if(_shipExhaustOwner is {} primary)return primary.RetainConfiguredDirectMeshRoot(source,ref appearance);
        if(!float.IsFinite(appearance.Scale)||!float.IsFinite(appearance.HsvH)||!float.IsFinite(appearance.HsvS)||!float.IsFinite(appearance.HsvV))return source;
        var input=(BitConverter.SingleToInt32Bits(appearance.Scale),BitConverter.SingleToInt32Bits(appearance.HsvH),
            BitConverter.SingleToInt32Bits(appearance.HsvS),BitConverter.SingleToInt32Bits(appearance.HsvV));
        var slot=_directConfigurations.GetValue(source,static _=>new());
        if(slot.Last is {} retained && retained.Input==input)
        {
            if(appearance.Scale!=retained.Scale || appearance.HsvH!=retained.Hsv.X || appearance.HsvS!=retained.Hsv.Y || appearance.HsvV!=retained.Hsv.Z)
                appearance=appearance with {Scale=retained.Scale,HsvH=retained.Hsv.X,HsvS=retained.Hsv.Y,HsvV=retained.Hsv.Z};
            return retained.Root;
        }
        var root=ConfigureDirectMeshRoot(source,ref appearance);
        slot.Last=new(input,root,appearance.Scale,new(appearance.HsvH,appearance.HsvS,appearance.HsvV));
        return root;
    }
    private LiveCpuRoot ConfigureDirectMeshRoot(LiveCpuRoot source,ref FactoryAppearance appearance)
    {
        var hsv=new Vector3(appearance.HsvH,appearance.HsvS,appearance.HsvV);
        if(!float.IsFinite(appearance.Scale)||!float.IsFinite(hsv.X)||!float.IsFinite(hsv.Y)||!float.IsFinite(hsv.Z))return source;
        bool scale=EnbCurrentRetailPrototypeVariantName.ChangesScale(appearance.Scale);
        float rawScale=appearance.Scale;
        bool changesHsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv);
        string factoryName=source.FactoryLookupName??source.RootRenderObjectName;
        string name=EnbCurrentRetailPrototypeVariantName.Create(factoryName,appearance.Scale,hsv);
        appearance=appearance with {Scale=scale?appearance.Scale:1,HsvH=changesHsv?hsv.X:0,HsvS=changesHsv?hsv.Y:0,HsvV=changesHsv?hsv.Z:0};
        if(!scale&&!changesHsv)return source;
        if(_configuredDirectMeshRoots.TryGetValue(name,out var retained))
        {
            appearance=appearance with {Scale=retained.Scale,HsvH=retained.Hsv.X,HsvS=retained.Hsv.Y,HsvV=retained.Hsv.Z};
            //The cached variant shares configured model data, not the newly
            //created RenderObj's lifetime.48B220 returns a fresh instance even
            //when the variant prototype is already registered.
            return retained.Root with {PhysicalFactoryOwner=source.PhysicalFactoryOwner};
        }
        if(source.Hlod is not null || source.Collection is not null)
        {
            //006F51D0's FILE/NAME branch calls the same manager+78 factory
            //as RSL Object construction.48B220/48BE60 copy every HLOD member
            //before scale/HSV mutation and48E9D0 postprocessing.
            var assets=_retailAssetsAccessor()??throw new InvalidDataException("Configured composite assets unavailable.");
            var variant=RetainConfiguredMeshTree(factoryName,rawScale,hsv,assets,source);
            var root=source with {Resource=variant.CompositeRoot?.Resource??variant.Model.Resource,
                Hlod=variant.CompositeRoot?.Hlod,
                HmodelChildren=variant.CompositeRoot?.HmodelChildren??source.HmodelChildren,
                CollectionChildren=variant.CompositeRoot?.CollectionChildren??source.CollectionChildren,
                PrimitiveConfigurationOwner=variant.CompositeRoot?.PrimitiveConfigurationOwner,
                HierarchyConfigurationOwner=variant.CompositeRoot?.HierarchyConfigurationOwner,
                ConfiguredHierarchyTranslations=variant.CompositeRoot?.ConfiguredHierarchyTranslations,
                AuthoredHlodMemberNames=variant.CompositeRoot?.AuthoredHlodMemberNames,
                ConfiguredFactoryInputs=(variant.Scale,variant.Hsv)};
            appearance=appearance with {Scale=variant.Scale,HsvH=variant.Hsv.X,HsvS=variant.Hsv.Y,HsvV=variant.Hsv.Z};
            _configuredDirectMeshRoots.Add(name,(root,variant.Scale,variant.Hsv));
            return root;
        }
        var meshes=source.Resource.Meshes.ToArray();
        for(int i=0;i<meshes.Length;i++)
        {
            var mesh=meshes[i];
            if(!EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,source.RootRenderObjectName) || mesh.CurrentRetailLoadedGeometry is not {} loaded)continue;
            //48BCF0 copies the loaded materials/mappers as well as geometry.
            //Keep those concrete owners through packet/appearance preparation.
            var assets=_retailAssetsAccessor()??throw new InvalidDataException("Configured mesh assets unavailable.");
            var single=source.Resource.Copy().SetMeshes([mesh]).SetHierarchicalLods([]).SetHierarchyModels([]).SetSpecialMaterials([]);
            uint clock=ReadMapperClock();
            var variant=RetainConfiguredDirectMeshVariant(factoryName,rawScale,hsv,assets,clock,
                ()=>new(single,clock,CameraMeshBatches,_prototypeResources,[mesh],_configuredDirectMeshModels));
            meshes[i]=variant.Model.Resource.Meshes[0];
            appearance=appearance with {Scale=variant.Scale,HsvH=variant.Hsv.X,HsvS=variant.Hsv.Y,HsvV=variant.Hsv.Z};
        }
        var configured=source with {Resource=source.Resource.Copy().SetMeshes(meshes),
            ConfiguredFactoryInputs=(appearance.Scale,new(appearance.HsvH,appearance.HsvS,appearance.HsvV))};
        _configuredDirectMeshRoots.Add(name,(configured,appearance.Scale,new(appearance.HsvH,appearance.HsvS,appearance.HsvV)));
        return configured;
    }

    private readonly Dictionary<EnbW3dRenderMesh,EnbCurrentRetailRObjMeshMappers> _configuredDirectMeshModels=new(ReferenceEqualityComparer.Instance);
    private sealed record ConfiguredDirectMeshVariant(EnbCurrentRetailRObjMeshMappers Model,float Scale,Vector3 Hsv,LiveCpuRoot? CompositeRoot=null)
    {
        public LiveCpuRoot? UnconfiguredCompositeRoot {get;init;}
    }
    private readonly Dictionary<string,ConfiguredDirectMeshVariant> _configuredDirectMeshVariants=new(StringComparer.OrdinalIgnoreCase);

    private ConfiguredDirectMeshVariant RetainConfiguredDirectMeshVariant(string name,float scale,Vector3 hsv,
        EnbNext.Assets.EnbRetailAssetBundle assets,uint clock,Func<EnbCurrentRetailRObjMeshMappers> acquire,bool composite=false)
    {
        string key=EnbCurrentRetailPrototypeVariantName.Create(name,scale,hsv);
        if(_configuredDirectMeshVariants.TryGetValue(key,out var retained))return retained;
        float effectiveScale=EnbCurrentRetailPrototypeVariantName.ChangesScale(scale)?scale:1;
        var effectiveHsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv)?hsv:Vector3.Zero;
        using var source=acquire();
        var model=source.Configure(clock,assets.Textures,effectiveHsv,effectiveScale,compositeOccurrences:composite);
        retained=new(model,source.Resource.Meshes[0].CurrentRetailLoadedGeometryScale*effectiveScale,effectiveHsv);
        foreach(var mesh in model.Resource.Meshes)_configuredDirectMeshModels.Add(mesh,model);
        _configuredDirectMeshVariants.Add(key,retained);
        _prototypeResources.RegisterConfiguredPrototypeName(key);
        return retained;
    }

    //98C420/98C7A0 acquire named children through the session manager +14,
    //48B0F0. Unlike48B220's unchanged direct BASE branch, that constructor
    //always postprocesses. This mutates shared geometry at construction,
    //never during a visibility query; collision projections detect the change.
    private static void PostprocessOrdinaryNamedMesh(EnbW3dRenderMesh mesh)
    {
        if(mesh.CurrentRetailLoadedGeometry is not {} geometry)return;
        var random=EnbRetailCrtRandomSource.Current;
        uint seed=random.State;
        try{geometry.PostprocessFactoryTree(mesh.CurrentRetailRenderPassCount,ref seed);}
        finally{random.State=seed;}
    }

    private readonly EnbDazzleQueryStorage _dazzleQueryStorage=new();
    private readonly List<EnbDazzleCollisionRoot> _dazzleCollisionRoots = [];
    private bool _frontendDazzleCollisionPrepared;
    private readonly List<EnbDazzleRayCast> _frontendDazzleCollision = [];
    // Candidate remains opt-in until exact histories and calibrated timing pass.
    private static readonly bool DazzlePlaneBlocks=Environment.GetEnvironmentVariable("ENB_EXPERIMENT_DAZZLE_PLANE_BLOCKS")!="0";
    private static readonly bool DazzleTreePlaneBlocks=Environment.GetEnvironmentVariable("ENB_EXPERIMENT_TREE_PLANE_BLOCKS")=="1";
    private readonly Dictionary<EnbW3dRenderMesh,EnbRetailDazzleMeshRay> _frontendDazzleMeshRays = [];

    private void PrepareFrontendDazzleCollision(System.Collections.Immutable.ImmutableArray<FrontendCollisionMesh> meshes)
    {
        _frontendDazzleCollisionPrepared=true;
        _frontendDazzleCollision.Clear();
        var active=new HashSet<EnbW3dRenderMesh>();
        if(!meshes.IsDefault)
            foreach(var member in meshes)
            {
                active.Add(member.Mesh);
                if(!_frontendDazzleMeshRays.TryGetValue(member.Mesh,out var ray) || !ray.IsCurrentGeometry)
                    _frontendDazzleMeshRays[member.Mesh]=ray=new(member.Mesh,1,queryMask:4,usePlaneBlocks:DazzlePlaneBlocks,useTreePlaneBlocks:DazzleTreePlaneBlocks,retainTriangleOperands:true,usePlaneOnlyRange:Environment.GetEnvironmentVariable("ENB_EXPERIMENT_PLANE_ONLY_RANGE")=="1");
                _frontendDazzleCollision.Add((start,end,fraction,inside)=>ray.CastSegment(member.World,start,end,fraction,inside));
            }
        foreach(var mesh in _frontendDazzleMeshRays.Keys.Where(m=>!active.Contains(m)).ToArray())
            _frontendDazzleMeshRays.Remove(mesh);
    }

    private static readonly IEqualityComparer<(EnbW3dRenderMesh Mesh,float Scale,uint Mask,float? RemainingScale)> DazzleMeshKeys=
        EqualityComparer<(EnbW3dRenderMesh Mesh,float Scale,uint Mask,float? RemainingScale)>.Create(
            static (a,b)=>ReferenceEquals(a.Mesh,b.Mesh) && a.Scale.Equals(b.Scale) && a.Mask==b.Mask && a.RemainingScale.Equals(b.RemainingScale),
            static key=>HashCode.Combine(RuntimeHelpers.GetHashCode(key.Mesh),key.Scale,key.Mask,key.RemainingScale));
    private readonly Dictionary<(EnbW3dRenderMesh Mesh,float Scale,uint Mask,float? RemainingScale), EnbRetailDazzleMeshRay> _dazzleMeshRays = new(DazzleMeshKeys);
    private readonly HashSet<(EnbW3dRenderMesh Mesh,float Scale,uint Mask,float? RemainingScale)> _dazzleUsedMeshes = new(DazzleMeshKeys);
    private sealed class DazzleMeshQuery
    {
        public EnbRetailDazzleMeshRay? Ray;
        public Matrix4x4 World;
        public readonly EnbDazzleRayCast Cast;
        public DazzleMeshQuery()=>Cast=Execute;
        private (float,bool) Execute(Vector3 start,Vector3 end,float fraction,bool inside)
            =>Ray!.CastOcclusionSegment(World,start,end,fraction,inside);
    }
    private readonly List<DazzleMeshQuery> _dazzleQueryTasks=[];
    private int _dazzleQueryCount;
    private EnbDazzleRayCast RentDazzleMeshQuery(EnbRetailDazzleMeshRay ray,Matrix4x4 world)
    {
        if(_dazzleQueryCount==_dazzleQueryTasks.Count)_dazzleQueryTasks.Add(new());
        var task=_dazzleQueryTasks[_dazzleQueryCount++];task.Ray=ray;task.World=world;return task.Cast;
    }
    private sealed class DazzleRootQuery
    {
        internal LiveRootInstance? Root;
        internal Action<LiveRootInstance,List<EnbDazzleRayCast>>? Builder;
        internal readonly List<EnbDazzleRayCast> Leaves = [];
        internal bool Prepared;
        internal readonly Action Prepare;
        internal readonly EnbDazzleRayCast Cast;
        internal DazzleRootQuery() { Prepare = PrepareCore; Cast = Execute; }
        private void PrepareCore()
        {
            if (Prepared) return;
            Builder!(Root!, Leaves);
            Prepared = true;
            EnbWorkReuseDiagnostics.Add("collision.dazzle.admittedTreesPrepared");
        }
        private (float,bool) Execute(Vector3 start,Vector3 end,float fraction,bool inside)
        {
            foreach (var cast in Leaves)
            {
                (fraction,inside) = cast(start,end,fraction,inside);
                if (fraction < 1) break;
            }
            return (fraction,inside);
        }
        internal void Clear() { Root = null; Builder = null; Leaves.Clear(); Prepared = false; }
    }
    private readonly List<DazzleRootQuery> _dazzleRootQueries = [];
    private int _dazzleRootQueryCount;
    private QueryOccurrenceIndex? _dazzleQueryOccurrences;
    private DazzleRootQuery RentDazzleRootQuery(LiveRootInstance root,
        Action<LiveRootInstance,List<EnbDazzleRayCast>> builder)
    {
        if (_dazzleRootQueryCount == _dazzleRootQueries.Count) _dazzleRootQueries.Add(new());
        var task = _dazzleRootQueries[_dazzleRootQueryCount++];
        task.Root = root; task.Builder = builder;
        return task;
    }
    private void ClearDazzleQueryTasks()
    {
        // These invocation records belong only to this view's synchronous
        // query phase. Every repeated occurrence has a distinct slot until all
        // dazzle passes have consumed it. Do not retain retired model owners.
        _dazzleCollisionRoots.Clear();
        for (int i=0;i<_dazzleRootQueryCount;i++) _dazzleRootQueries[i].Clear();
        _dazzleRootQueryCount=0;
        for(int i=0;i<_dazzleOtherQueryCount;i++)_dazzleOtherQueries[i].Rays=null;
        _dazzleOtherQueryCount=0;
        if (_dazzleQueryOccurrences is {} index)
        {
            index.Dispose(); _dazzleQueryOccurrences=null;
            foreach (var key in _dazzleMeshRays.Keys)
                if (!_dazzleUsedMeshes.Contains(key)) _dazzleMeshRays.Remove(key);
        }
        for(int i=0;i<_dazzleQueryCount;i++)_dazzleQueryTasks[i].Ray=null;
        _dazzleQueryCount=0;_dazzleUsedMeshes.Clear();
    }

    private readonly EnbRetailDazzleBoxRay _dazzleBoxRay = new();
    private readonly ConditionalWeakTable<EnbNext.Assets.EnbCurrentRetailSpecialMaterialDefinition, EnbRetailDazzleMeshRay[]> _dazzleSpecialRays = new();

    private void PrepareDazzleCollision(RootTaskList roots, EnbRetailTerrain? terrain)
    {
        using var reuseCollision = EnbWorkReuseDiagnostics.Measure("collision.dazzle");
        ObserveReuseRoots("collision.dazzle.input", roots);
        ClearDazzleQueryTasks();
        var childrenByParent=_dazzleQueryOccurrences=IndexQueryOccurrences(roots);
        var usedMeshes = _dazzleUsedMeshes;
        Action<LiveRootInstance,List<EnbDazzleRayCast>> prepareRoot=Append;
        foreach (var root in roots.Where(r => r.Parent is null && !r.OrbitalCamera.HasValue))
        {
            if (root.CurrentRetailWorldSphere is not {} sphere) continue;
            // 0075DEE0 checks scene visibility (+178) before sphere admission
            // or Cast_Ray. Keep that scene entry/order, but do not materialize
            // a collision tree which this query cannot visit. Child normal-
            // hidden/engine-hidden flags remain separate collision semantics.
            if (!root.ShouldDraw)
            {
                EnbWorkReuseDiagnostics.Add("collision.dazzle.hiddenTreesSkipped");
                _dazzleCollisionRoots.Add(new(new(sphere.Center, sphere.Radius), false, false,
                    static (start, end, fraction, inside) => (fraction, inside))
                    { DiagnosticName = root.Root.Resource.Key.ResourceName });
                continue;
            }
            // 0075DEE0 reaches Cast_Ray only after sphere admission and
            // sample projection. Query-only topology and pose belong there,
            // not to preparation of every scene root on every frame.
            var task=RentDazzleRootQuery(root,prepareRoot);
            _dazzleCollisionRoots.Add(new(new(sphere.Center,sphere.Radius),true,false,task.Cast)
                {PrepareCast=task.Prepare,DiagnosticName=root.Root.Resource.Key.ResourceName});
        }
        foreach (var effect in _effectDazzleCollision.Concat(_warpTunnelCollision))
        {
            try
            {
                var leaves = new List<EnbDazzleRayCast>();
                var sphere = AppendEffect(effect.Asset, effect.Resource, effect.World, effect.Scale, effect.Animations, leaves,effect.RemainingGeometryScale,effect.HierarchySourceScale);
                if (sphere is not {} bounds) continue;
                _dazzleCollisionRoots.Add(new(new(bounds.Center, bounds.Radius),
                    EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(effect.Camera, bounds), false,
                    (start, end, fraction, inside) =>
                    {
                        foreach (var cast in leaves)
                        {
                            (fraction, inside) = cast(start, end, fraction, inside);
                            if (fraction < 1) break;
                        }
                        return (fraction, inside);
                    }) {DiagnosticName=effect.Resource?.LogicalFileName??"effect"});
            }
            catch (InvalidDataException error)
            {
                // A malformed optional effect must not abort the whole frame.
                _effectObjectVisualStatus += $" · collision {effect.Resource?.LogicalFileName ?? "procedural line"}: {error.Message}";
            }
        }
        foreach (var line in _preparedEffectLines.Values)
            if (line.CollisionRoot is {} candidate) _dazzleCollisionRoots.Add(candidate);
        //90C4D0/911B30 defer before property sampling. All installed sphere/
        //ring shaders use bucket10; a dazzle in ordinary traversal therefore
        //sees BeforeAllocation bounds. Attached members belong to their HLOD,
        //not to this scene root list. Immediate-shader ordering remains separate.
        foreach(var owner in _effectSphereOwners.Values)
            if(owner.Asset.Resource is null && owner.Before?.Serial==_ringFrameSerial)
                foreach(var sphere in owner.Before.Spheres)
                    AppendDeferredPrimitive(sphere.Binding.Asset.Header.ShaderBytes,true,sphere.Binding.Texture is not null,sphere.Bounds,owner.Camera);
        foreach(var owner in _effectRingOwners.Values)
            if(owner.Asset.Resource is null && owner.Before?.Serial==_ringFrameSerial)
                foreach(var ring in owner.Before.Rings)
                    AppendDeferredPrimitive(ring.Binding.Asset.Header.ShaderBytes,false,ring.Binding.Texture is not null,ring.Bounds,owner.Camera);

        void AppendDeferredPrimitive(byte[] bytes,bool sphere,bool textured,EnbW3dWorldBounds bounds,EnbResolvedRenderCamera camera)
        {
            var shader=new EnbW3dRenderShaderRecord(BitConverter.ToUInt32(bytes,0),BitConverter.ToUInt32(bytes,4),BitConverter.ToUInt32(bytes,8),BitConverter.ToUInt32(bytes,12));
            uint packed=(sphere?EnbRetailSphereMaterial.Decode(shader,true,textured):EnbRetailRingMaterial.Decode(shader,true,textured)).PackedRetailState;
            if(EnbW3dStaticSortSemantics.FromSingleShader(packed).Disposition!=EnbW3dStaticSortDisposition.DeferredBucket)return;
            //Both native +D0 slots resolve to462CD0: return false untouched.
            _dazzleCollisionRoots.Add(new(new(bounds.Center,bounds.Radius),
                EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds),false,
                static (start,end,fraction,inside)=>(fraction,inside)));
        }
        _dazzleCollisionRoots.AddRange(_effectEmitterCollision);
        AppendRetainedBufferDazzleCollision();
        _dazzleCollisionRoots.AddRange(_compositeBufferCollision);
        _dazzleCollisionRoots.AddRange(_jetBufferCollision);
        _dazzleCollisionRoots.AddRange(_exhaustBufferCollision);
        _dazzleCollisionRoots.AddRange(_warpTunnelBufferCollision);
        _dazzleCollisionRoots.AddRange(_attachmentBufferCollision);
        if (terrain is not null)
        {
            var sampler = EnbRetailTerrainSampler.For(terrain);
            _dazzleCollisionRoots.Add(new(default, true, true, (start, end, fraction, inside) =>
            {
                return sampler.CastSegment(start, end, fraction, inside);
            }));
        }
        // Used mesh rays are retired when the query phase drains.

        EnbW3dWorldBounds? AppendEffect(EnbCurrentRetailRObjEffectAsset asset, EnbW3dRenderResource? resource,
            Matrix4x4 world, float scale, IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,
            List<EnbDazzleRayCast> leaves,float? remainingScale=null,float? hierarchySourceScale=null)
        {
            //006EA830/006EEEA0/006EF250 preserve the procedural SegLine mask1;
            //006EF200 still registers it. No mask16 hit, but retain admission.
            if (asset.ObjectLine is {} line) return line.Bounds(world, scale);
            if (resource is null) return null;
            var hlod = resource.HierarchicalLods.SingleOrDefault();
            if (hlod is null && resource.HierarchyModels.SingleOrDefault() is {} model)
                hlod = EnbW3dHmodelConversion.Create(model, _ => true);
            if (hlod is not null)
            {
                var hierarchy = resource.Hierarchies.Single(h => h.Name.Equals(hlod.HierarchyName, StringComparison.OrdinalIgnoreCase));
                var post = new EnbW3dHierarchyState(hierarchy.Name, world, scale, new Dictionary<int, Matrix4x4>())
                    {PendingHierarchyScale=asset.ResolveHierarchyScale(scale,hierarchySourceScale)};
                var queried=asset.RetainedBoundsResource??resource;
                if (!asset.TryComputeBounds(queried, asset.RetainedBoundsResource?.HierarchicalLods.Single()??hlod, world, animations, post, out var bounds, out var error) ||
                    !EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(hierarchy, world, post.HierarchyTranslationScale, animations,
                        post.Transforms, out var pose, out error)) throw new InvalidDataException(error);
                if (hlod.Levels.Count != 0)
                    foreach (var member in hlod.Levels[^1].Members) Member(member);
                if (hlod.Aggregate is {} aggregate)
                    foreach (var member in aggregate.Members) Member(member);
                return bounds.WorldSphere;

                void Member(EnbW3dRenderHlodMember member)
                {
                    if (member.ReferenceIndex >= pose.WorldTransforms.Length || !pose.Visibility[(int)member.ReferenceIndex]) return;
                    var rigid = pose.WorldTransforms[(int)member.ReferenceIndex];
                    for (int index = 0; index < asset.ModelAttachments.Count; index++)
                    {
                        var child = asset.ModelAttachments[index];
                        int boundsOrdinal=child.BoundsOrdinal<0?index:child.BoundsOrdinal;
                        if (child.Ancestors.Count!=0 || member.Name != (child.BoundsMemberName??$"<0600:{hlod.Name}:{boundsOrdinal}:{child.Name}>")) continue;
                        AppendEffect(child.Asset, child.Asset.Resource!, rigid, scale, [], leaves,remainingScale,hierarchySourceScale);
                        return;
                    }
                    AddLeaf(resource, null, member.Name, rigid, scale, leaves,remainingScale:remainingScale);
                }
            }
            if (asset.SpecialMaterial is {} special)
            {
                AddLeaf(resource, special, "", world, scale, leaves);
                return new(Vector3.Transform(special.SphereCenter, world), special.SphereRadius);
            }
            if (resource.Meshes.Count == 1)
            {
                var mesh = resource.Meshes[0];
                string name = mesh.ContainerName.Length == 0 ? mesh.Name : mesh.ContainerName + "." + mesh.Name;
                AddLeaf(resource, null, name, world, scale, leaves,remainingScale:remainingScale);
                if(remainingScale is {} radiusScale && mesh.CurrentRetailLoadedGeometry is {} geometry)
                    return new(Vector3.Transform(geometry.Center*radiusScale,world),geometry.Radius*radiusScale);
                return new(Vector3.Transform(mesh.BoundingSphereCenter * scale, world), mesh.BoundingRadius * scale);
            }
            if (asset.StandaloneBox is {} box)
            {
                var external = resource.ExternalRenderObjects[0];
                AddLeaf(resource, null, external.Name, world, scale, leaves);
                return new(Vector3.Transform(box.Center, world), box.Extents.Length());
            }
            return null;
        }

        void Append(LiveRootInstance instance, List<EnbDazzleRayCast> leaves)
        {
            if (EnbWorkReuseDiagnostics.Active)
            {
                using var inspection = EnbWorkReuseDiagnostics.Inspect();
                EnbWorkReuseDiagnostics.Observe("collision.dazzle.visited", ReuseRootKey(instance), ReuseRootStructure(instance));
            }
            if (!TryResolvePrimitiveFactoryTransform(instance, out var world, out float scale, out var hsv)) return;
            using var queryChildren=RetainedCollisionChildren(instance, roots, childrenByParent);
            var children=queryChildren.Children;
            if (instance.Root.Collection is not null)
            { foreach (var child in children) Append(child, leaves); return; }
            var resource = RetainedDrawResource(instance);
            if (RetainedDrawRoot(instance).Hlod is {} hlod)
            {
                if (!TryResolveConfiguredAttachmentHierarchy(instance.Root, out var hierarchy, out _) || hierarchy is null) return;
                var animations = instance.AnimationBlend?.ForHierarchy(hierarchy.Name)??Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
                if (!TryResolveRetainedHierarchyPose(instance, hierarchy, world, ResolvePendingHierarchyScale(instance,scale), animations,
                    instance.HierarchyPostTransforms?.Transforms, out var pose, out _)) return;
                // 0098E680: last retained level, then aggregate members. The
                // currently drawn level is not an input to Cast_Ray.
                if (hlod.Levels.Count != 0)
                    foreach (var member in hlod.Levels[^1].Members) Member(member, hlod.Levels[^1].Ordinal, false);
                if (hlod.Aggregate is {} aggregate)
                    foreach (var member in aggregate.Members) Member(member, null, true);
                for(int i=0;i<children.Count;i++)
                    if(!queryChildren.IsConsumed(i)&&!children[i].IsOrdinaryHlodChild)Append(children[i],leaves);
                //98E680 visits dynamic aggregate children too, independently
                //of their draw flag and the parent's selected mesh level.
                foreach(var (key,clone) in (_shipExhaustOwner??this)._warpPlumes)
                {
                    if(key.Parent!=CompositeMembershipKey(instance) || clone.Particles.All(p=>p.State.Destroyed))continue;
                    var asset=clone.Asset;
                    if(!EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),clone.World,out var clonePose,out _))continue;
                    foreach(var member in asset.Resource.HierarchicalLods.Single().Levels[^1].Members)
                        if(member.ReferenceIndex<clonePose.Length)
                            AddLeaf(asset.Resource,null,member.Name,clonePose[(int)member.ReferenceIndex],1,leaves);
                }
                return;

                void Member(EnbW3dRenderHlodMember member, int? level, bool aggregate)
                {
                    var child=queryChildren.TakeMember(member,level,aggregate,hlod.IsConvertedHierarchyModel);
                    if(child is not null){Append(child,leaves);return;}
                    if (member.ReferenceIndex >= pose.WorldTransforms.Length) return;
                    if (!pose.Visibility[(int)member.ReferenceIndex]) return;
                    Leaf(member.Name, pose.WorldTransforms[(int)member.ReferenceIndex]);
                }
            }
            else
            {
                //940530/A09580 reject hierarchy-hidden (+188), not the
                //normal-hidden flag written by RSL/engine visibility (+184).
                if (instance.PivotVisible)
                    Leaf(instance.Root.RootRenderObjectName, world);
                foreach (var child in children) Append(child, leaves);
            }

            void Leaf(string name, Matrix4x4 rigid)
                => AddLeaf(resource, instance.Root.SpecialMaterial?.Definition, name, rigid, scale, leaves,
                    //733700 ->6F51D0 ->48B220 returns a plain mesh clone in
                    //the unchanged branch. HLOD/aggregate child factories and
                    //RSL/effect owners have separate construction paths.
                    loadedMaskOnly:instance.Parent is null && !instance.IsRsl && instance.Root.IsDirectMeshPrototype &&
                        !EnbCurrentRetailPrototypeVariantName.ChangesScale(scale) && !EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv));
        }

        void AddLeaf(EnbW3dRenderResource resource, EnbCurrentRetailSpecialMaterialDefinition? specialOverride,
            string name, Matrix4x4 rigid, float scale, List<EnbDazzleRayCast> leaves,bool loadedMaskOnly=false,float? remainingScale=null)
        {
                var mesh=EnbW3dRenderObjectNameSemantics.FindFirstMesh(resource.Meshes,name);
                if (mesh is not null)
                {
                    uint mask=loadedMaskOnly?EnbRetailDazzleCollision.LoadedMeshMask(mesh):EnbRetailDazzleCollision.ConfiguredMeshMask(mesh);
                    var key=(mesh,scale,mask,remainingScale);
                    usedMeshes.Add(key);
                    if (!_dazzleMeshRays.TryGetValue(key, out var ray) || !ray.IsCurrentGeometry)
                        _dazzleMeshRays[key] = ray = new(mesh, scale,mask,remainingGeometryScale:remainingScale,usePlaneBlocks:DazzlePlaneBlocks,useTreePlaneBlocks:DazzleTreePlaneBlocks,retainTriangleOperands:true,usePlaneOnlyRange:Environment.GetEnvironmentVariable("ENB_EXPERIMENT_PLANE_ONLY_RANGE")=="1");
                    leaves.Add(RentDazzleMeshQuery(ray,rigid));
                    return;
                }
                AppendDazzleOtherLeaf(resource,specialOverride,name,rigid,leaves);
        }

    }
}
