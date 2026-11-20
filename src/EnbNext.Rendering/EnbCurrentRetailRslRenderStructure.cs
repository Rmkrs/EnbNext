using EnbNext.Assets;

namespace EnbNext.Rendering;

public readonly record struct EnbCurrentRetailRslRenderBinding(int StateId,EnbResolvedRslStateResource? State);

/// <summary>Resolved render membership owned by the RSL object lifetime.
/// Activation, current-object selection and graph replacement publish a new
/// structure. Frame clocks, animation samples and callbacks are not captured.</summary>
public sealed class EnbCurrentRetailRslRenderStructure
{
    private readonly EnbResolvedRslLibraryResourceGraph _graph;
    private readonly IReadOnlyList<int> _renderIds,_activeOrder;
    private readonly int? _current;
    private readonly EnbCurrentRetailRslRenderStructure? _query;
    public IReadOnlyList<EnbCurrentRetailRslRenderBinding> Bindings {get;}
    public IReadOnlyList<(int StateId,EnbResolvedRslStateResource State)> Animations {get;}
    public IReadOnlyList<(int StateId,EnbResolvedRslStateResource State)> OrderedAnimations {get;}
    internal EnbCurrentRetailRslRenderStructure(EnbResolvedRslLibraryResourceGraph graph,
        IReadOnlyList<int> renderIds,IReadOnlyList<int> activeOrder,int? current,bool buildQuery=true)
    {
        _graph=graph;_renderIds=renderIds;_activeOrder=activeOrder;_current=current;
        var bindings=new EnbCurrentRetailRslRenderBinding[renderIds.Count];
        var animations=new List<(int StateId,EnbResolvedRslStateResource State)>();
        for(int i=0;i<renderIds.Count;i++)
        {
            graph.TryFindState(renderIds[i],out var state);
            bindings[i]=new(renderIds[i],state);
            if(state?.Definition.Type==EnbRslStateType.Animation)animations.Add((renderIds[i],state));
        }
        Bindings=Array.AsReadOnly(bindings);Animations=animations.AsReadOnly();
        // Match the active-list gather, independently of sorted render IDs.
        // The source lists are immutable publications of the native list order.
        OrderedAnimations=Array.AsReadOnly(activeOrder.Join(animations,id=>id,entry=>entry.StateId,(_,entry)=>entry).ToArray());
        if(current is {} currentId)
            _query=!buildQuery||animations.Count!=0||renderIds.Count==1&&renderIds[0]==currentId
                ?this:new(graph,Array.AsReadOnly(new[]{currentId}),activeOrder,current,false);
    }
    public IReadOnlyList<int> StateIds=>_renderIds;
    public EnbCurrentRetailRslRenderStructure? CurrentObjectQuery=>_query;
    public bool Matches(EnbCurrentRetailEffectRslObjectRuntime.ObjectState owner,EnbResolvedRslLibraryResourceGraph graph)
        =>_current==owner.CurrentObjectStateId&&ReferenceEquals(_graph,graph)&&ReferenceEquals(_renderIds,owner.RenderStateIds)&&ReferenceEquals(_activeOrder,owner.ActiveList.Snapshot());
    public bool Matches(EnbCurrentRetailEffectRslObjectUpdate update,EnbResolvedRslLibraryResourceGraph graph)
        =>_current==update.CurrentObjectStateId&&ReferenceEquals(_graph,graph)&&ReferenceEquals(_renderIds,update.ActiveStateIds)&&ReferenceEquals(_activeOrder,update.ActiveListStateIds);
}
