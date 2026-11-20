using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed class FixedVisualPreparationState(ulong baseRevision,ulong fixedRevision)
    {
        internal readonly ulong BaseRevision=baseRevision,FixedRevision=fixedRevision;
        internal IReadOnlyList<LiveShipFixedVisualMaterialized>? Children;
    }
    private readonly bool _retainedFixedVisuals=Environment.GetEnvironmentVariable("ENB_LEGACY_FIXED_VISUAL_PREPARATION")!="1";
    private long _fixedVisualPreparationCount;
    private FixedVisualPreparationState? FixedVisualPreparation(IEnbRenderObject obj)
    {
        // 6FCB70 materializes the six fixed children on base/attachment/SubParts
        // changes. A render callback consumes those children; it is not another
        // aggregate factory invocation. Failed groups remain retryable.
        if(!_retainedFixedVisuals)return null;
        var owner=_buildingSceneOwner;
        if(owner is null || owner.Definition.RenderIdentity!=obj.RenderIdentity)return null;
        var prepared=owner.FixedVisuals;
        if(prepared is null || prepared.BaseRevision!=obj.BaseAssetMaterializationRevision ||
            prepared.FixedRevision!=obj.FixedShipVisualRevision)
            owner.FixedVisuals=prepared=new(obj.BaseAssetMaterializationRevision,obj.FixedShipVisualRevision);
        return prepared;
    }
}
