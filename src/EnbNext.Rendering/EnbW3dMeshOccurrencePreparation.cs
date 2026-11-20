using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Authored mesh/pass definitions owned by one render occurrence.
/// Hierarchy traversal still chooses admission and supplies current transforms,
/// lighting and skin results. No frame input is retained in the definitions.</summary>
public sealed class EnbW3dMeshOccurrencePreparation(string instancePath = "root")
{
    internal string InstancePath => instancePath;
    internal EnbW3dDrawPacketCompiler.PreparedTraversal? Traversal;
    public long TraversalPreparationCount {get;internal set;}
    public long TraversalUseCount {get;internal set;}
    internal void PreparedDefinitionCreated()=>PreparationCount++;
    public EnbW3dRetainedHierarchyPose? RigidHierarchyPose {get;set;}
    public long BorrowedPoseReuseCount {get;private set;}
    internal bool TryBorrowRigidPose(EnbW3dRenderHierarchy hierarchy,Matrix4x4 world,float? scale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,IReadOnlyDictionary<int,Matrix4x4>? overrides,
        out EnbW3dHierarchyPoseOwner pose)
    {
        if(RigidHierarchyPose is {} retained&&retained.Matches(hierarchy,world,scale,animations,overrides))
        {pose=retained.Pose;BorrowedPoseReuseCount++;return true;}
        pose=null!;return false;
    }
    private EnbW3dRenderResource? _layoutResource;
    private EnbW3dDrawInvocation? _layout;
    private Matrix4x4 _layoutWorld;
    private float _layoutScale;
    private uint _layoutGameId;
    private EnbRenderPassKind _layoutPass;
    private bool _layoutEligible;
    private EnbW3dWeightedAnimationSample[] _layoutAnimations = [];
    private string? _layoutHierarchy;
    private Matrix4x4? _layoutRigidWorld;
    private float? _layoutRenderScale, _layoutPendingScale;
    private KeyValuePair<int, Matrix4x4>[] _layoutOverrides = [];
    internal void InvalidateLayout() => _layout = null;
    private bool MatchesLayoutInputs(IReadOnlyList<EnbW3dWeightedAnimationSample> animations,
        EnbW3dHierarchyState? transforms)
    {
        if (_layoutAnimations.Length != animations.Count || _layoutHierarchy != transforms?.HierarchyName ||
            _layoutRigidWorld != transforms?.RigidWorldTransform || _layoutRenderScale != transforms?.RenderObjectScale ||
            _layoutPendingScale != transforms?.PendingHierarchyScale ||
            _layoutOverrides.Length != (transforms?.Transforms.Count ?? 0)) return false;
        for (int i = 0; i < animations.Count; i++)
            if (_layoutAnimations[i] != animations[i]) return false;
        foreach (var entry in _layoutOverrides)
            if (!transforms!.Transforms.TryGetValue(entry.Key, out var value) || value != entry.Value) return false;
        return true;
    }
    public long LayoutPreparationCount { get; private set; }
    internal bool CanRetainLayout(EnbW3dRenderResource resource,bool retainablePose)
    {
        if(!ReferenceEquals(resource,_layoutResource))
        {
            _layoutResource=resource;_layout=null;
            _layoutEligible=resource.Meshes.All(m=>m.BoneIndices.Count==0)&&
                resource.ExternalRenderObjects.All(o=>o.SpecialMaterialDefinition is null);
        }
        if(!retainablePose||!_layoutEligible){_layout=null;return false;}
        return true;
    }
    internal bool TrySnapshotLayout(IEnbRenderObject owner,Matrix4x4 world,EnbRenderPassKind pass,
        EnbW3dHlodSelection? selection,EnbW3dLightEnvironment lights,long time,
        IReadOnlyList<EnbW3dWeightedAnimationSample> animations, EnbW3dHierarchyState? transforms,
        out EnbW3dDrawInvocation list,float renderScale)
    {
        list=default!;
        if(_layout is not {} layout||layout.RenderIdentity!=owner.RenderIdentity||
            layout.VisualGeneration!=owner.VisualGeneration||_layoutGameId!=owner.GameId||
            _layoutWorld!=world||_layoutScale!=renderScale||_layoutPass!=pass||layout.HlodSelection!=selection||
            !MatchesLayoutInputs(animations,transforms))return false;
        list=layout.CopyForInvocation();list.FrameTimeMilliseconds=time;
        for(int i=0;i<list.Packets.Count;i++)
            list.Packets[i]=list.Packets[i].ForkTask().SetLightEnvironment(lights).SetFrameTimeMilliseconds(time);
        return true;
    }
    internal void StoreLayout(IEnbRenderObject owner,Matrix4x4 world,EnbRenderPassKind pass,EnbW3dDrawInvocation list,
        IReadOnlyList<EnbW3dWeightedAnimationSample> animations, EnbW3dHierarchyState? transforms,float renderScale)
    {
        list.RetainAfterDrain();
        _layout=list;_layoutWorld=world;_layoutScale=renderScale;_layoutGameId=owner.GameId;_layoutPass=pass;
        // Copy mutable input containers: a queued layout must never become its own
        // validation authority when a caller edits an override dictionary in place.
        _layoutAnimations=animations.ToArray(); _layoutHierarchy=transforms?.HierarchyName;
        _layoutRigidWorld=transforms?.RigidWorldTransform; _layoutRenderScale=transforms?.RenderObjectScale;
        _layoutPendingScale=transforms?.PendingHierarchyScale; _layoutOverrides=transforms?.Transforms.ToArray()??[];
        LayoutPreparationCount++;
    }
    public long TransientSkinBuildCount {get;private set;}
    private EnbW3dTransientGeometryArena.Frame? _geometryFrame;
    public GeometryFrameScope UseGeometryFrame(EnbW3dTransientGeometryArena.Frame? frame)=>new(this,frame);
    public readonly struct GeometryFrameScope : IDisposable
    {
        private readonly EnbW3dMeshOccurrencePreparation _owner;
        private readonly EnbW3dTransientGeometryArena.Frame? _previous;
        internal GeometryFrameScope(EnbW3dMeshOccurrencePreparation owner,EnbW3dTransientGeometryArena.Frame? frame)
        {_owner=owner;_previous=owner._geometryFrame;owner._geometryFrame=frame;}
        public void Dispose()=>_owner._geometryFrame=_previous;
    }
    private readonly List<EnbW3dDrawTask> _definitions = [];
    private object? _meshes, _hierarchies;
    private readonly Dictionary<EnbW3dRenderMesh, (EnbW3dHierarchyPoseOwner Pose, long Revision, long[]? BoneVersions, EnbW3dDeformedGeometry Geometry)> _skins = new(ReferenceEqualityComparer.Instance);

    internal bool TryDeform(EnbW3dRenderMesh mesh,EnbW3dHierarchyPoseOwner pose,
        out EnbW3dDeformedGeometry geometry,out string? diagnostic)
    {
        // Changed animation/world/scale/overrides still supply the current pose.
        // Only unchanged matrices actually consumed by this mesh can reuse its
        // immutable output; unrelated hierarchy bones do not invalidate it.
        bool hasPrior=_skins.TryGetValue(mesh,out var prior);
        bool same=hasPrior && (ReferenceEquals(prior.Pose,pose) ? prior.Revision==pose.MutationRevision || EnbW3dSkinDeformer.SameOwnedBoneVersions(mesh,pose,prior.BoneVersions) :
            !prior.Pose.IsOwnerHeld&&!pose.IsOwnerHeld&&EnbW3dSkinDeformer.UsesSameTransforms(mesh,prior.Pose,pose));
        if(same && prior.Geometry.IsActive)
        {geometry=prior.Geometry;diagnostic=null;return true;}
        // A paused pose gets a detached result once, then reuses it. Changing
        // poses use this invocation's ranges until its final flush (009797A0).
        if(!EnbW3dSkinDeformer.TryDeformPrepared(mesh,pose,out geometry,out diagnostic,
            hasPrior&&!same?_geometryFrame:null))return false;
        if(geometry.Positions is EnbW3dFrameStream<Vector3>)TransientSkinBuildCount++;
        _skins[mesh]=(pose,pose.MutationRevision,EnbW3dSkinDeformer.RetainOwnedBoneVersions(mesh,pose,hasPrior?prior.BoneVersions:null),geometry);return true;
    }
    private readonly Dictionary<string,HierarchyDrawOwner> _poses=new(StringComparer.OrdinalIgnoreCase);
    internal HierarchyDrawOwner HierarchyOwner(EnbW3dRenderHierarchy hierarchy)
    {
        if(!_poses.TryGetValue(hierarchy.Name,out var owner)||!ReferenceEquals(owner.Hierarchy,hierarchy))
            _poses[hierarchy.Name]=owner=new(hierarchy);
        return owner;
    }
    internal sealed class HierarchyDrawOwner(EnbW3dRenderHierarchy hierarchy)
    {
        internal readonly EnbW3dRenderHierarchy Hierarchy=hierarchy;
        private readonly EnbW3dRetainedHierarchyPose rigid=new();
        private readonly EnbW3dHierarchyPoseOwner scaled=new();
        private readonly List<EnbW3dResolvedWeightedAnimationSample> clips=[];
        private EnbW3dHierarchyPoseOwner? lastRigid;
        private long lastRevision=-1;
        private float lastScale;
        private bool scaledCurrent;
        internal EnbW3dHierarchyPoseOwner Pose=null!;
        internal bool Update(EnbW3dMeshOccurrencePreparation? preparation,Matrix4x4 world,
            IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,EnbW3dHierarchyState? post,
            out string? diagnostic)
        {
            clips.Clear();
            foreach(var clip in animations)if(string.Equals(clip.Animation.HierarchyName,Hierarchy.Name,StringComparison.OrdinalIgnoreCase))clips.Add(clip);
            bool overridden=post is not null&&string.Equals(post.HierarchyName,Hierarchy.Name,StringComparison.OrdinalIgnoreCase);
            var matrix=overridden?post!.RigidWorldTransform:world;
            float? scale=overridden?post!.HierarchyTranslationScale:null;
            var transforms=overridden?post!.Transforms:null;
            diagnostic=null;
            EnbW3dHierarchyPoseOwner current=null!;
            if(preparation?.TryBorrowRigidPose(Hierarchy,matrix,scale,clips,transforms,out current)!=true&&
                !rigid.Update(Hierarchy,matrix,scale,clips,transforms,out current,out diagnostic))return false;
            if(!overridden){Pose=current;scaledCurrent=false;return true;}
            if(!scaledCurrent||!ReferenceEquals(lastRigid,current)||lastRevision!=current.MutationRevision||lastScale!=post!.RenderObjectScale||Pose is null)
            {
                scaled.Prepare(current.WorldTransforms.Length);
                var matrices=scaled.World;var visibility=scaled.VisibilityWriter;
                var scaling=Matrix4x4.CreateScale(post!.RenderObjectScale);
                for(int i=0;i<current.WorldTransforms.Length;i++)
                {matrices.Add(scaling*current.WorldTransforms[i]);visibility.Add(current.Visibility[i]);}
                Pose=scaled.Complete(current.AnimationName,current.AnimationFrame);
                lastRigid=current;lastRevision=current.MutationRevision;lastScale=post.RenderObjectScale;scaledCurrent=true;
            }
            return true;
        }
    }
    internal readonly Dictionary<string, EnbW3dHierarchyPoseOwner> FramePoses = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<EnbW3dResolvedWeightedAnimationSample> FrameAnimations = [];

    internal FrameScope Begin(EnbW3dRenderResource resource)
    {
        // The resource may acquire another animation array without replacing
        // its mesh or hierarchy ownership. Invalidate those independently.
        if (!ReferenceEquals(_meshes, resource.Meshes)) { _definitions.Clear(); _skins.Clear(); _meshes = resource.Meshes; }
        if (!ReferenceEquals(_hierarchies, resource.Hierarchies)) { _poses.Clear(); _skins.Clear(); _hierarchies = resource.Hierarchies; }
        FramePoses.Clear(); FrameAnimations.Clear();
        return new(this);
    }
    internal readonly struct FrameScope(EnbW3dMeshOccurrencePreparation owner) : IDisposable
    { public void Dispose() { owner.FramePoses.Clear(); owner.FrameAnimations.Clear(); } }

    public long PreparationCount { get; private set; }

    internal EnbW3dDrawTask Snapshot(IEnbRenderObject owner, EnbW3dDrawOrderKey order,
        EnbW3dRenderMesh mesh, EnbW3dDrawMaterialBinding material, string? hierarchy, int pivot, string name,
        Matrix4x4 world, EnbW3dWorldBounds bounds, EnbW3dDeformedGeometry? deformed,
        EnbW3dLightEnvironment lights, long time, float scale,EnbW3dDrawTask? sharedMeshFrame=null)
    {
        int slot = order.BindingOrdinal;
        // A selected level or hidden pivot can change ordinal assignment.
        // Replace that slot explicitly; never reuse a definition by ordinal alone.
        EnbW3dDrawTask? definition = slot < _definitions.Count ? _definitions[slot] : null;
        if (definition is null || definition.OrderKey != order ||
            definition.RenderIdentity != owner.RenderIdentity || definition.VisualGeneration != owner.VisualGeneration ||
            definition.GameId != owner.GameId || !ReferenceEquals(definition.Mesh, mesh) ||
            !ReferenceEquals(definition.Material, material) || definition.HierarchyName != hierarchy ||
            definition.PivotIndex != pivot || definition.RenderObjectName != name)
        {
            definition = new(order, owner.RenderIdentity, owner.VisualGeneration, owner.GameId,
                Matrix4x4.Identity, default, mesh, null, material, EnbW3dLightEnvironment.BlackNoLights,
                0, hierarchy, pivot, name) {RenderInstancePath=instancePath};
            while (_definitions.Count <= slot) _definitions.Add(null!);
            definition.RetainDefinition();
            _definitions[slot] = definition;
            PreparationCount++;
        }
        return sharedMeshFrame is null?definition.SnapshotFrame(world,bounds,deformed,lights,time,scale):definition.SnapshotFrameFrom(sharedMeshFrame);
    }
}
