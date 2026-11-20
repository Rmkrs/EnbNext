using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    // 0072D0E0 mutates the parent hierarchy before attaching children. Lego
    // mounting and the six fixed visuals read this same invocation's owner;
    // neither publishes a second immutable description of its pose/materials.
    private sealed class ShipAttachmentOwner(LiveRootInstance? instance)
    {
        public readonly LiveRootInstance? Instance=instance;
        public int BaseAsset;
        public LiveCpuRoot Root=null!;
        public EnbW3dRenderHierarchy Hierarchy=null!;
        public Matrix4x4 RigidWorld;
        public float Scale;
        public string Path=string.Empty;
        public IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? Animations;
        public Dictionary<int,EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride> HierarchyOverrides=null!;
        public Dictionary<int,Matrix4x4> DrawHierarchyOverrides=null!;
        public IReadOnlyCollection<EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride> QueryOverrides=[];
        public IReadOnlyDictionary<int,Matrix4x4>? DrawOverrides;
        public bool InScene=true,SuppressDraw;
        public EnbW3dHierarchyPoseOwner? AttachmentPose;
        public long FixedQueryEpoch=-1;
        public (bool Ready,Matrix4x4[] Pose,string? Diagnostic) FixedQuery;
        public ShipAttachmentOwner SetScene(bool inScene,bool suppressDraw)
        {InScene=inScene;SuppressDraw=suppressDraw;return this;}
    }

    private static ShipAttachmentOwner PrepareLegoAttachment(int BaseAsset,LiveCpuRoot Root,
        EnbW3dRenderHierarchy Hierarchy,Matrix4x4 RigidWorld,float Scale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? Animations,
        Dictionary<int,EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride> HierarchyOverrides,
        Dictionary<int,Matrix4x4> DrawHierarchyOverrides,LiveRootInstance Instance)
    {
        var owner=Instance.AttachmentOwner??=new(Instance);
        owner.BaseAsset=BaseAsset;owner.Root=Root;owner.Hierarchy=Hierarchy;
        owner.RigidWorld=RigidWorld;owner.Scale=Scale;owner.Animations=Animations;
        owner.HierarchyOverrides=HierarchyOverrides;owner.DrawHierarchyOverrides=DrawHierarchyOverrides;
        owner.QueryOverrides=HierarchyOverrides.Values;owner.DrawOverrides=DrawHierarchyOverrides;
        owner.AttachmentPose=null;owner.FixedQueryEpoch=-1;
        return owner.SetScene(true,false);
    }

    private static ShipAttachmentOwner PrepareFixedAttachment(string Path,LiveCpuRoot Root,
        EnbW3dRenderHierarchy Hierarchy,Matrix4x4 RigidWorld,float Scale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? Animations,
        IReadOnlyCollection<EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride> HierarchyOverrides,
        LiveRootInstance? Instance,IReadOnlyDictionary<int,Matrix4x4>? DrawOverrides=null)
    {
        // The ambiguous-root diagnostic path has no actual occurrence to own
        // state. It remains local; ordinary, Lego and aggregate roots retain it.
        var owner=Instance is null?new ShipAttachmentOwner(null):Instance.AttachmentOwner??=new(Instance);
        owner.Path=Path;owner.Root=Root;owner.Hierarchy=Hierarchy;
        owner.RigidWorld=RigidWorld;owner.Scale=Scale;owner.Animations=Animations;
        owner.QueryOverrides=HierarchyOverrides;owner.DrawOverrides=DrawOverrides;
        owner.FixedQueryEpoch=-1;
        return owner;
    }
}
