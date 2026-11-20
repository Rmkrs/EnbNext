using EnbNext.Rendering;
namespace EnbNext.Rendering;
// Diagnostic fixtures create independent owners explicitly; production has no clone/publication adapter.
internal static class EffectOwnerFixture
{
    public static EnbCurrentRetailEffectOwner Copy(EnbCurrentRetailEffectOwner source,System.Action<EnbCurrentRetailEffectOwner> change)
    {
        var copy=new EnbCurrentRetailEffectOwner(source.Sequence,source.Definition,source.Root,source.BoundGameId,source.Active,source.ActualStartTick)
        {ActivationSequence=source.ActivationSequence,ConstructionTick=source.ConstructionTick,DurationMilliseconds=source.DurationMilliseconds,
            InitialDurationMilliseconds=source.InitialDurationMilliseconds,FactorySpeedup=source.FactorySpeedup,LinkKind=source.LinkKind,
            ParentSequence=source.ParentSequence,BoundRenderIdentity=source.BoundRenderIdentity,BoundToTarget=source.BoundToTarget};
        change(copy);return copy;
    }
}
