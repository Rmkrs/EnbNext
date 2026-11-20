using System.Numerics;
using System.Runtime.InteropServices;
namespace EnbNext.Rendering;

/// <summary>Actual mutable pivot owner, read directly by traversal and skinning.
/// Identity is stable; revisions describe mutations. No record equality, with
/// copies, or immutable-array facade hides the lifetime of these fields.</summary>
public sealed class EnbW3dHierarchyPoseOwner
{
    public Matrix4x4[] WorldTransforms {get;private set;}=[];
    public bool[] Visibility {get;private set;}=[];
    public string? AnimationName {get;internal set;}
    public float? AnimationFrame {get;internal set;}
    public long MutationRevision {get;internal set;}
    internal bool IsOwnerHeld {get;private set;}
    internal long[]? TransformRevisions {get;private set;}
    private readonly ChangeFlag changes=new();
    public EnbW3dHierarchyPoseOwner(){}
    public EnbW3dHierarchyPoseOwner(IReadOnlyList<Matrix4x4> world,IReadOnlyList<bool> visibility,string? name,float? frame)
    {WorldTransforms=world as Matrix4x4[]??world.ToArray();Visibility=visibility as bool[]??visibility.ToArray();AnimationName=name;AnimationFrame=frame;}
    internal void Prepare(int count)
    {
        changes.Changed=false;
        if(WorldTransforms.Length==count&&IsOwnerHeld)return;
        WorldTransforms=new Matrix4x4[count];Visibility=new bool[count];TransformRevisions=new long[count];IsOwnerHeld=true;
    }
    internal Writer<Matrix4x4> World=>new(WorldTransforms,changes,TransformRevisions);
    internal Writer<bool> VisibilityWriter=>new(Visibility,changes);
    internal EnbW3dHierarchyPoseOwner Complete(string? name,float? frame)
    {AnimationName=name;AnimationFrame=frame;if(changes.Changed||MutationRevision==0)MutationRevision++;return this;}
    internal sealed class ChangeFlag {internal bool Changed;}
    internal struct Writer<T>(T[] values,ChangeFlag? changed=null,long[]? revisions=null) where T:unmanaged
    {
        private int count;
        internal T this[int index]=>values[index];
        internal void Add(T value)
        {
            if(changed is not null&&!MemoryMarshal.AsBytes(values.AsSpan(count,1)).SequenceEqual(
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value,1))))
            {changed.Changed=true;if(revisions is not null)revisions[count]++;}
            values[count++]=value;
        }
        internal T[] MoveToArray()=>values;
    }
}
