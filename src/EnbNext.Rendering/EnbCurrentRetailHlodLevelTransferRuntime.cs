namespace EnbNext.Rendering;

/// <summary>Concrete child dispatch used by0098BF30's level transfer loop.
/// RetainedChild and RemoveChild perform their native class ownership roles;
/// the explicit retain/release callbacks represent constructor-owned refs.</summary>
public interface IEnbCurrentRetailHlodLevelTransferProvider<TChild> where TChild : class
{
    int ClassId(TChild child);
    int ChildCount(TChild child);
    TChild RetainedChild(TChild child, int index);
    int ChildPivot(TChild parent, TChild child);
    void RemoveChild(TChild parent, TChild child);
    void Retain(TChild child);
    void Release(TChild child);
    void SetParentToHlod(TChild child);
    void SetHierarchyPivotMatrix(TChild child, int pivot);
    bool HasScene();
    int CurrentLevel();
    void AttachToCurrentScene(TChild child);
}

public sealed record EnbCurrentRetailHlodTransferredMember<TChild>(TChild Child, int PivotIndex) where TChild : class;

/// <summary>0098C103..0098C31C within whole0098BF30. Registered0400
/// supplies roots in reverse temporary-distance-owner order. Hierarchy
/// selection precedes this loop; bounds/cost/detail preparation follows it.
/// Every returned member owns the constructor's retained child reference.</summary>
public static class EnbCurrentRetailHlodLevelTransferRuntime
{
    public static IReadOnlyList<IReadOnlyList<EnbCurrentRetailHlodTransferredMember<TChild>>> Transfer<TChild>(
        IReadOnlyList<TChild> orderedRoots, IEnbCurrentRetailHlodLevelTransferProvider<TChild> provider) where TChild : class
    {
        List<IReadOnlyList<EnbCurrentRetailHlodTransferredMember<TChild>>> levels = [];
        for (int level = 0; level < orderedRoots.Count; level++)
        {
            TChild root = orderedRoots[level];
            List<EnbCurrentRetailHlodTransferredMember<TChild>> members = [];
            levels.Add(members);
            // Native repeats the class virtual when the first result is not1.
            if (provider.ClassId(root) == 1 || provider.ClassId(root) == 0x19 || provider.ChildCount(root) > 1)
            {
                while (provider.ChildCount(root) > 0)
                {
                    TChild child = provider.RetainedChild(root, 0);
                    int pivot = provider.ChildPivot(root, child);
                    provider.RemoveChild(root, child);
                    provider.Retain(child);
                    provider.SetParentToHlod(child);
                    provider.SetHierarchyPivotMatrix(child, pivot);
                    if (provider.HasScene() && level == provider.CurrentLevel()) provider.AttachToCurrentScene(child);
                    members.Add(new(child, pivot));
                    provider.Release(child);
                }
            }
            else
            {
                provider.Retain(root);
                provider.SetParentToHlod(root);
                provider.SetHierarchyPivotMatrix(root, 0);
                if (provider.HasScene() && level == provider.CurrentLevel()) provider.AttachToCurrentScene(root);
                members.Add(new(root, 0));
            }
        }
        return levels;
    }
}
