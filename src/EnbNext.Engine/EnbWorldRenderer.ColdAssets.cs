using EnbNext.Assets;
using EnbNext.Presentation;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private readonly object _coldAssetsGate=new();
    private EnbRetailBaseAssetResolver? _coldResolver;
    private EnbBaseAssetPredecode? _coldAssets;
    public void PrepareColdAssets(IEnbSceneFrame snapshot)
    {
        if(_baseAssetResolverAccessor() is not {} resolver)return;
        lock(_coldAssetsGate)
        {
            if(!ReferenceEquals(resolver,_coldResolver))
            {
                _coldAssets?.Dispose();_coldResolver=resolver;
                _coldAssets=new(selector=>resolver.Resolve(selector));
            }
            foreach(var obj in snapshot.Objects)_coldAssets!.Request(obj.BaseAsset);
        }
    }
    public void FinishColdAssets()
    {
        lock(_coldAssetsGate){_coldAssets?.Dispose();_coldAssets=null;_coldResolver=null;}
    }
    private bool TryGetColdAsset(EnbRetailBaseAssetResolver resolver,int selector,out EnbBaseAssetResolution result)
    {
        lock(_coldAssetsGate)
            if(ReferenceEquals(resolver,_coldResolver) && _coldAssets is {} cold && cold.TryGetCompleted(selector,out result))return true;
        result=null!;return false;
    }
}
