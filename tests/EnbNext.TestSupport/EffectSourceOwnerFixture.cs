using EnbNext.Presentation;
namespace EnbNext.Rendering;
// Diagnostic driver for old list-oriented test cases. Production has no table,
// sweep or publication adapter; scheduler retirement directly clears the owner.
internal sealed class EffectSourceOwnerFixture
{
    private readonly List<EnbCurrentRetailEffectOwner> previous=[];
    public int Count=>previous.Count(n=>n.SourceBinding?.Captured is not null);
    public void Update(IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,IReadOnlyList<IEnbRenderObject> objects)
    {
        foreach(var old in previous)if(!nodes.Contains(old))old.ClearSource();
        previous.Clear();
        foreach(var node in nodes){node.UpdateSource(objects);previous.Add(node);}
    }
    public void Clear(){foreach(var node in previous)node.ClearSource();previous.Clear();}
    public bool IsDetached(EnbCurrentRetailEffectOwner node)=>node.SourceBinding?.IsDetached==true;
    public IEnbRenderObject? Resolve(EnbCurrentRetailEffectOwner node,IReadOnlyList<IEnbRenderObject> objects)=>node.ResolveSource(objects);
}
