using System.Numerics;
namespace EnbNext.Rendering;

/// <summary>The hierarchy's mutable animation inputs and pivot fields. Set inputs
/// at a transform/animation mutation; consumers share the evaluated owner state.</summary>
public sealed class EnbW3dRetainedHierarchyPose
{
    private EnbW3dRenderHierarchy? hierarchy;
    private Matrix4x4 world;
    private float? scale;
    private readonly List<EnbW3dResolvedWeightedAnimationSample> animations=[];
    private readonly Dictionary<int,Matrix4x4> overrides=[];
    private readonly EnbW3dHierarchyPoseOwner storage=new();
    public EnbW3dHierarchyPoseOwner Pose {get;private set;}=null!;
    public EnbW3dRetainedHierarchyPose(){}
    public EnbW3dRetainedHierarchyPose(EnbW3dRenderHierarchy hierarchy,Matrix4x4 world,float? scale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,IReadOnlyDictionary<int,Matrix4x4>? overrides,
        EnbW3dHierarchyPoseOwner pose)
    {SetInputs(hierarchy,world,scale,animations,overrides);Pose=pose;}
    public bool Matches(EnbW3dRenderHierarchy next,Matrix4x4 matrix,float? nextScale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> clips,IReadOnlyDictionary<int,Matrix4x4>? transforms)
    {
        if(Pose is null||!ReferenceEquals(hierarchy,next)||world!=matrix||scale!=nextScale||
            animations.Count!=clips.Count||overrides.Count!=(transforms?.Count??0))return false;
        for(int i=0;i<clips.Count;i++)if(animations[i]!=clips[i])return false;
        foreach(var entry in overrides)if(!transforms!.TryGetValue(entry.Key,out var value)||value!=entry.Value)return false;
        return true;
    }
    private void SetInputs(EnbW3dRenderHierarchy next,Matrix4x4 matrix,float? nextScale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> clips,IReadOnlyDictionary<int,Matrix4x4>? transforms)
    {
        bool names=animations.Count!=clips.Count;
        if(!names)for(int i=0;i<clips.Count;i++)if(!ReferenceEquals(animations[i].Animation,clips[i].Animation)){names=true;break;}
        hierarchy=next;world=matrix;scale=nextScale;
        animations.Clear();for(int i=0;i<clips.Count;i++)animations.Add(clips[i]);
        overrides.Clear();if(transforms is not null)foreach(var item in transforms)overrides.Add(item.Key,item.Value);
        if(names)storage.AnimationName=clips.Count==0?null:clips.Count==1?clips[0].Animation.Name:string.Join(" + ",clips.Select(x=>x.Animation.Name));
    }
    public bool Update(EnbW3dRenderHierarchy next,Matrix4x4 matrix,float? nextScale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> clips,IReadOnlyDictionary<int,Matrix4x4>? transforms,
        out EnbW3dHierarchyPoseOwner result,out string? diagnostic)
    {
        diagnostic=null;
        if(Matches(next,matrix,nextScale,clips,transforms)){result=Pose;return true;}
        // The literal evaluator validates before consuming the same owner buffers.
        bool valid=nextScale is {} pendingScale
            ?EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(next,matrix,pendingScale,clips,transforms,out result,out diagnostic,storage)
            :EnbW3dHierarchyPose.TryEvaluate(next,matrix,clips,null,out result,out diagnostic,storage:storage);
        if(!valid){Pose=null!;return false;}
        SetInputs(next,matrix,nextScale,clips,transforms);Pose=result;
        // Label is diagnostic metadata; resolve only when the clip membership changes.
        result.AnimationName=storage.AnimationName;
        return true;
    }
}
