namespace EnbNext.Rendering;

/// <summary>004609C0 and00461D60, reached by the Bone class activation/deactivation
/// virtuals. RenderObj virtuals retain their own ownership and hierarchy policy.</summary>
public static class EnbCurrentRetailRslBoneAttachmentRuntime
{
    /// <summary>00458B20, after common00457E10 enables/relinks the state.</summary>
    public static void Activate(IEnbCurrentRetailRslBoneOwner owner,
        IEnbCurrentRetailRslBoneRenderObject? child,string? bonePath)
    {
        child?.SetHidden(false);
        Attach(owner,child,bonePath);
    }

    /// <summary>00458B70, after common004582E0 disables/unlinks the state.</summary>
    public static void Deactivate(IEnbCurrentRetailRslBoneOwner owner,
        IEnbCurrentRetailRslBoneRenderObject? child,string? bonePath)
    {
        if(child is null)return;
        child.SetHidden(true);
        Detach(owner,child,bonePath);
    }

    public static bool Attach(IEnbCurrentRetailRslBoneOwner owner,
        IEnbCurrentRetailRslBoneRenderObject? child, string? bonePath)
    {
        if(child is null || child.Parent is not null)return false;
        var parent=owner.CurrentRenderObject;
        if(parent is null)return false;
        if(string.IsNullOrEmpty(bonePath) || bonePath[0]=='\0')parent.AddChild(child);
        else
        {
            string[] tokens=Tokens(bonePath);
            if(tokens.Length==0)return false;
            for(int i=0;i<tokens.Length;i++)
            {
                int index=parent.BoneIndex(tokens[i]);
                //Raw00460A8F/00460AE7 reject zero, including a root pivot.
                if(index==0)return false;
                if(i==tokens.Length-1){parent.AddChildToBone(child,tokens[i]);break;}
                _=parent.ChildCountOnBone(index);
                parent=parent.ChildOnBone(index,0);
                if(parent is null)return false;
            }
        }
        child.SetInheritedProperty(owner.GetInheritedProperty(),recursive:true);
        //The native controller ignores AddChild/AddChildToBone return values.
        return true;
    }

    public static bool Detach(IEnbCurrentRetailRslBoneOwner owner,
        IEnbCurrentRetailRslBoneRenderObject? child, string? bonePath)
    {
        if(child is null || child.Parent is null)return false;
        var parent=owner.CurrentRenderObject;
        if(parent is null)return false;
        if(string.IsNullOrEmpty(bonePath) || bonePath[0]=='\0')return owner.RemoveChild(child);
        string[] tokens=Tokens(bonePath);
        if(tokens.Length==0)return false;
        for(int i=0;i<tokens.Length-1;i++)
        {
            int index=parent.BoneIndex(tokens[i]);
            //Raw00461E1F compares-1; this differs from attachment's zero guard.
            if(index==-1)return false;
            _=parent.ChildCountOnBone(index);
            parent=parent.ChildOnBone(index,0);
            if(parent is null)return false;
        }
        parent.RemoveChild(child);
        //The last token is not looked up, and this branch returns one even if
        //the RenderObj removal virtual returns false (00461E6B..00461E80).
        return true;
    }

    private static string[] Tokens(string path)
    {
        int terminator=path.IndexOf('\0');
        if(terminator>=0)path=path[..terminator];
        //00A27187 strtok with the sole delimiter00B6E914 '\\'.
        return path.Split('\\',StringSplitOptions.RemoveEmptyEntries);
    }
}

public interface IEnbCurrentRetailRslBoneOwner
{
    IEnbCurrentRetailRslBoneRenderObject? CurrentRenderObject { get; }
    object? GetInheritedProperty();
    bool RemoveChild(IEnbCurrentRetailRslBoneRenderObject child);
}

public interface IEnbCurrentRetailRslBoneRenderObject
{
    IEnbCurrentRetailRslBoneRenderObject? Parent { get; }
    void SetParent(IEnbCurrentRetailRslBoneRenderObject? parent);
    int BoneIndex(string name);
    int ChildCountOnBone(int boneIndex);
    IEnbCurrentRetailRslBoneRenderObject? ChildOnBone(int boneIndex,int ordinal);
    bool AddChild(IEnbCurrentRetailRslBoneRenderObject child);
    bool AddChildToBone(IEnbCurrentRetailRslBoneRenderObject child,string name);
    bool RemoveChild(IEnbCurrentRetailRslBoneRenderObject child);
    void SetInheritedProperty(object? value,bool recursive);
    void SetHidden(bool hidden);
    void SetAnimationVisible(bool visible);
    void SetLodBias(float bias) { }
}
