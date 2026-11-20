using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using Size = EnbNext.Presentation.EnbUiSize;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldController
{
    private readonly Dictionary<uint,Task<EnbCurrentRetailStationInterior>> _stationRoomLoads=[];
    private CancellationTokenSource? _stationPreloadCancellation;
    private string? _stationPreloadSignature;

    private void ClearStationPreloads()
    {
        _stationPreloadCancellation?.Cancel();
        _stationPreloadCancellation?.Dispose();
        _stationPreloadCancellation=null;
        _stationPreloadSignature=null;
        _stationRoomLoads.Clear();
    }

    private void PrepareStationRooms(EnbSceneStationDefinition definition)
    {
        string signature=definition.DefinitionValue+":"+string.Join(";",definition.Rooms.OrderBy(r=>r.RoomType).Select(r=>$"{r.RoomType}:{r.Flags}"));
        if(_stationPreloadSignature==signature || _retailAssetsAccessor() is not {} assets)return;
        ClearStationPreloads();
        _stationPreloadSignature=signature;
        var token=(_stationPreloadCancellation=new()).Token;
        Task prior=Task.CompletedTask;
        foreach(var room in definition.Rooms.OrderBy(r=>r.RoomType))
        {
            if(room.RoomType==_stationRoom && _stationLoadedRoom is {} loaded)
            { _stationRoomLoads[room.RoomType]=Task.FromResult(loaded);continue; }
            var preceding=prior;
            var task=Task.Run(async ()=>
            {
                try { await preceding.ConfigureAwait(false); } catch { /* Other rooms can still load. */ }
                token.ThrowIfCancellationRequested();
                var interior=EnbCurrentRetailStationInterior.Load(assets,definition,room.RoomType);
                _w3dWorld.PrepareStationRoom(interior,assets,token,definition);
                _w3dWorld.PrepareStationPeople(definition,room.RoomType,assets,token);
                token.ThrowIfCancellationRequested();
                return interior;
            },token);
            _stationRoomLoads[room.RoomType]=task;
            // Observe failed speculative loads; crossing still reports their error.
            _=task.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            prior=task;
        }
    }
}
