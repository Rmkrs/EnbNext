namespace EnbNext.Rendering;

/// <summary>0098C7A0 creates one level from successful global child creations.</summary>
public static class EnbW3dHmodelConversion
{
    public static EnbW3dRenderHlod Create(EnbW3dRenderHierarchyModel model,
        Func<EnbW3dRenderHierarchyModelNode, bool> createRegisteredChild)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(createRegisteredChild);
        var members = new List<EnbW3dRenderHlodMember>();
        foreach (var node in model.Nodes)
            if (createRegisteredChild(node))
                members.Add(new(node.PivotIndex, node.RenderObjectName));
        return new(model.Name, model.HierarchyName,
            [new(0, float.MaxValue, 0x7f7fffff, members.AsReadOnly())])
            { IsConvertedHierarchyModel = true };
    }
}
