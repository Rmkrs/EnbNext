using EnbNext.Presentation;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private long _indexedEffectEndpointQueries;
    private IEnbRenderObject? FindCombatSceneObject(IEnbSceneFrame snapshot,uint? gameId,ulong? identity=null)
    {
        if(gameId is not {} id)return null;
        if(_ownedSceneFrame && ReferenceEquals(snapshot,_ownedFrameSnapshot))
        {
            _indexedEffectEndpointQueries++;
            var found=identity is {} owner?_ownedScene!.TryFind(owner,out var node):_ownedScene!.TryFindByGameId(id,out node);
            return found && node.Definition.GameId==id?node.State:null;
        }
        return SceneObjects(snapshot).FirstOrDefault(obj=>obj.GameId==id && (identity is null || obj.RenderIdentity==identity));
    }
}
