using System.Collections;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private long _effectiveActionEpoch;
    private readonly HashSet<ulong> _compatEffectiveActionOwners=[];
    private sealed class OwnedEffectiveActions(SceneOwner source) : IReadOnlyList<EnbCurrentRetailRslEffectiveAction>
    {
        public int Count=>source.EffectiveActions?.Count??0;
        public EnbCurrentRetailRslEffectiveAction this[int index]=>source.EffectiveActions![index];
        public IEnumerator<EnbCurrentRetailRslEffectiveAction> GetEnumerator()
            =>(source.EffectiveActions??Array.Empty<EnbCurrentRetailRslEffectiveAction>()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private void ClearEffectiveActions()
    {_rslEffectiveActionsByRenderIdentity.Clear();_compatEffectiveActionOwners.Clear();_effectiveActionEpoch++;}
    private void DetachOwnedEffectiveActions(SceneOwner source)
    {_rslEffectiveActionsByRenderIdentity.Remove(source.Definition.RenderIdentity);source.EffectiveActions=null;source.EffectiveActionEpoch=-1;}
    private void PublishEffectiveActions(IEnbRenderObject obj,IReadOnlyList<EnbCurrentRetailRslEffectiveAction> actions,SceneOwner? source=null)
    {
        source??=_buildingSceneOwner is {} current&&current.Definition.RenderIdentity==obj.RenderIdentity?current:null;
        if(source is not null)
        {
            source.EffectiveActions=actions;
            if(source.EffectiveActionEpoch!=_effectiveActionEpoch)
            {
                source.EffectiveActionEpoch=_effectiveActionEpoch;
                _rslEffectiveActionsByRenderIdentity[obj.RenderIdentity]=source.EffectiveActionView??=new(source);
                _compatEffectiveActionOwners.Remove(obj.RenderIdentity);
            }
        }
        else {_rslEffectiveActionsByRenderIdentity[obj.RenderIdentity]=actions;_compatEffectiveActionOwners.Add(obj.RenderIdentity);}
    }
    private bool TryGetEffectiveActions(IEnbRenderObject obj,[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<EnbCurrentRetailRslEffectiveAction>? actions)
    {
        if(_buildingSceneOwner is {} source&&source.Definition.RenderIdentity==obj.RenderIdentity&&source.EffectiveActionEpoch==_effectiveActionEpoch)
        {actions=source.EffectiveActions;return actions is not null;}
        return _rslEffectiveActionsByRenderIdentity.TryGetValue(obj.RenderIdentity,out actions);
    }
}
