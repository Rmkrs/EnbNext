using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>006FBEF0: state 1 prepares the effect handle; state 2 replaces it
/// with E710. Only the proven global exit callbacks release that handle.</summary>
public sealed class EnbWarpPlumeRuntime
{
    private readonly Dictionary<ulong,Owner> _owners=[];
    public void Clear()=>_owners.Clear();
    public void Update(IReadOnlyList<IEnbRenderObject> objects)
    {
        var retained=new HashSet<ulong>();
        foreach(var obj in objects)
        {
            if(obj.ObjectType!=1 || obj.ShipPresentation is not {} ship)continue;
            retained.Add(obj.RenderIdentity);
            if(!_owners.TryGetValue(obj.RenderIdentity,out var owner))
                _owners.Add(obj.RenderIdentity,owner=new(){Sequence=ship.WarpChanges.Length>1?ship.WarpChanges[^2].Sequence:0});
            foreach(var change in ship.WarpChanges)
            {
                if(change.Sequence<=owner.Sequence)continue;
                owner.Sequence=change.Sequence;
                if(change.State==1){owner.Prepared=true;owner.ActiveSequence=null;}
                else if(change.State==2 && owner.Prepared)owner.ActiveSequence=change.Sequence;
                else if(change.State is 3 or 7 or 8 or 9 or 11){owner.Prepared=false;owner.ActiveSequence=null;}
            }
        }
        foreach(var id in _owners.Keys.Where(id=>!retained.Contains(id)).ToArray())_owners.Remove(id);
    }
    public ulong? Get(ulong identity)=>_owners.TryGetValue(identity,out var owner)?owner.ActiveSequence:null;
    private sealed class Owner {public ulong Sequence;public bool Prepared;public ulong? ActiveSequence;}
}
