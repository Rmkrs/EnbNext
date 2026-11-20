namespace EnbNext.Rendering;

/// <summary>Retained base/HLOD RenderObj child membership. HMODEL construction
///0098C7A0 installs the same00B1327C HLOD virtual table. Ordinary level members
///precede aggregate members in0098E290/0098E300 and removal0098E0F0.</summary>
public sealed class EnbCurrentRetailRslBoneRenderObjectRuntime:IEnbCurrentRetailRslBoneRenderObject
{
    private readonly List<List<EnbCurrentRetailRslBoneMember>> _levels;
    private readonly List<EnbCurrentRetailRslBoneMember> _aggregate;
    private readonly EnbW3dRenderHierarchy? _hierarchy;
    private readonly bool[] _pivotVisibility;
    private readonly bool _hasLeafLodBias;
    private readonly bool _unclampedLodBias;

    public EnbCurrentRetailRslBoneRenderObjectRuntime(EnbW3dRenderHierarchy? hierarchy,
        IEnumerable<IEnumerable<EnbCurrentRetailRslBoneMember>>? levels=null,
        IEnumerable<EnbCurrentRetailRslBoneMember>? aggregate=null,bool isCollection=false,
        bool hasLeafLodBias=false,bool unclampedLodBias=false,float initialLodBias=1,bool isParticleEmitter=false)
    {
        if(isCollection&&hierarchy is not null)throw new ArgumentException("Class23 has no hierarchy.",nameof(hierarchy));
        IsCollection=isCollection;
        IsParticleEmitter=isParticleEmitter;
        _hasLeafLodBias=hasLeafLodBias;
        _unclampedLodBias=unclampedLodBias;
        LodBias=initialLodBias;
        _hierarchy=hierarchy;
        _pivotVisibility=Enumerable.Repeat(true,hierarchy?.Pivots.Count??0).ToArray();
        _levels=levels?.Select(level=>level.ToList()).ToList()??[];
        _aggregate=aggregate?.ToList()??[];
        foreach(var member in Members()){member.Child.SetParent(this);ObserveChild(member.Child);}
    }

    // Renderer definition invalidation follows actual membership edges, not
    // Parent: duplicate membership and retained aliases can outlive SetParent(null).
    public ulong DefinitionRevision {get;private set;}
    public event Action? DefinitionChanged;
    private void InvalidateDefinition()
    {unchecked{DefinitionRevision++;}DefinitionChanged?.Invoke();}
    private void ObserveChild(IEnbCurrentRetailRslBoneRenderObject child)
    {if(child is EnbCurrentRetailRslBoneRenderObjectRuntime node)node.DefinitionChanged+=InvalidateDefinition;}
    private void ForgetChild(IEnbCurrentRetailRslBoneRenderObject child)
    {if(child is EnbCurrentRetailRslBoneRenderObjectRuntime node)node.DefinitionChanged-=InvalidateDefinition;}

    public IEnbCurrentRetailRslBoneRenderObject? Parent {get;private set;}
    public bool IsCollection {get;}
    public bool IsParticleEmitter {get;}
    public bool Hidden {get;private set;}
    public bool AnimationVisible {get;private set;}=true;
    public float LodBias {get;private set;}=1;
    public void SetLodBias(float bias)
    {
        //98D2F0 walks aggregate storage only.90AF30 clamps primitive bias;
        //97D5B0/95C2E0 stores emitter bias without the positive-value clamp.
        if(_hierarchy is null && !_hasLeafLodBias)return;
        float next=_unclampedLodBias?bias:bias>0?bias:0;
        bool changed=BitConverter.SingleToInt32Bits(LodBias)!=BitConverter.SingleToInt32Bits(next);
        LodBias=next;
        if(_hierarchy is not null)foreach(var member in _aggregate)member.Child.SetLodBias(LodBias);
        if(changed)InvalidateDefinition();
    }
    /// <summary>Opaque RenderObj user data: native+84. World insertion00514270
    ///sets the GameObject lifetime; Bone copies it recursively without decoding.</summary>
    public object? InheritedProperty {get;private set;}
    public IReadOnlyList<IReadOnlyList<EnbCurrentRetailRslBoneMember>> Levels=>_levels;
    public IReadOnlyList<EnbCurrentRetailRslBoneMember> Aggregate=>_aggregate;
    public void SetParent(IEnbCurrentRetailRslBoneRenderObject? parent)=>Parent=parent;
    public void SetHidden(bool hidden)
    {
        //98FB10 calls +184 only on direct aggregate children of class15.
        //It does not visit the ordinary level, nested models, or Collection.
        if(_hierarchy is not null)
            foreach(var member in _aggregate)
                if(member.Child is EnbCurrentRetailRslBoneRenderObjectRuntime {IsParticleEmitter:true} emitter)
                    emitter.SetHidden(hidden);
        if(Hidden!=hidden){Hidden=hidden;InvalidateDefinition();}
    }
    public void SetAnimationVisible(bool visible)=>AnimationVisible=visible;
    public int BoneIndex(string name)=>_hierarchy is null?0:EnbCurrentRetailAggregateBoneLookup.Resolve(_hierarchy,name);
    public int ChildCountOnBone(int boneIndex)=>_hierarchy is null?0:Members().Count(member=>member.BoneIndex==boneIndex);
    public IEnbCurrentRetailRslBoneRenderObject? ChildOnBone(int boneIndex,int ordinal)
        =>_hierarchy is null || ordinal<0?null:Members().Where(member=>member.BoneIndex==boneIndex).Skip(ordinal).FirstOrDefault()?.Child;
    public void SetPivotVisibility(int boneIndex,bool visible)=>_pivotVisibility[boneIndex]=visible;

    public bool AddChild(IEnbCurrentRetailRslBoneRenderObject child)
    {
        if(!IsCollection)return AddChildAtBone(child,0);
        //00990D90 retains and parents without HLOD's pivot-visibility write.
        //The App collection draw tree supplies the shared parent transform.
        child.SetParent(this);
        _aggregate.Add(new(0,child));
        ObserveChild(child);InvalidateDefinition();
        return true;
    }
    public bool AddChildToBone(IEnbCurrentRetailRslBoneRenderObject child,string name)=>AddChildAtBone(child,BoneIndex(name));
    public bool AddChildAtBone(IEnbCurrentRetailRslBoneRenderObject child,int boneIndex)
    {
        //Base00462A30 is a no-op; HLOD0098E4B0 accepts only an existing pivot.
        if(_hierarchy is null || boneIndex<0 || boneIndex>=_hierarchy.Pivots.Count)return false;
        child.SetLodBias(LodBias);
        child.SetParent(this);
        child.SetAnimationVisible(_pivotVisibility[boneIndex]);
        _aggregate.Add(new(boneIndex,child));
        ObserveChild(child);InvalidateDefinition();
        return true;
    }

    public bool RemoveChild(IEnbCurrentRetailRslBoneRenderObject child)
    {
        if(_hierarchy is null&&!IsCollection)return false;
        foreach(var level in _levels)
            if(RemoveFirst(level,child))return true;
        return RemoveFirst(_aggregate,child);
    }

    public void SetInheritedProperty(object? value,bool recursive)
    {
        InheritedProperty=value;
        if(recursive)foreach(var member in Members())member.Child.SetInheritedProperty(value,true);
    }

    private bool RemoveFirst(List<EnbCurrentRetailRslBoneMember> members,IEnbCurrentRetailRslBoneRenderObject child)
    {
        int index=members.FindIndex(member=>ReferenceEquals(member.Child,child));
        if(index<0)return false;
        members.RemoveAt(index);
        ForgetChild(child);
        child.SetParent(null);
        InvalidateDefinition();
        return true;
    }

    private IEnumerable<EnbCurrentRetailRslBoneMember> Members()=>_levels.SelectMany(level=>level).Concat(_aggregate);
}

public sealed record EnbCurrentRetailRslBoneMember(int BoneIndex,IEnbCurrentRetailRslBoneRenderObject Child);
