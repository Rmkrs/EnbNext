namespace EnbNext.Presentation;

/// <summary>596770 / 596130 name-keyed presentation members, separate from wire slot indices.</summary>
public sealed class EnbRetainedGroupMembers
{
    private readonly List<EnbSceneGroupMember> _items=[];
    private static long _nextLifetime;
    public IReadOnlyList<EnbSceneGroupMember> Items=>_items;
    public string? Update(int state,bool forced,IReadOnlyList<EnbSceneGroupMember> incoming,bool station=false,Action<uint>? memberChanged=null)
    {
        if(forced?state==0:state<2)return null;
        int previousCount=_items.Count;
        foreach(var member in _items.Where(member=>!incoming.Any(candidate=>candidate.Name==member.Name)).ToArray())
        {
            _items.Remove(member);
            memberChanged?.Invoke(member.GameId); // 596920 -> 597920; old retained ID.
        }
        foreach(var member in incoming)
        {
            if(member.Name.Length==0)break;
            int index=_items.FindIndex(existing=>existing.Name==member.Name);
            if(index>=0)_items[index]=_items[index] with {GameId=member.GameId};
            else if(_items.Count<5)
            {
                _items.Add(member with {PresentationLifetime=Interlocked.Increment(ref _nextLifetime)});
                memberChanged?.Invoke(member.GameId); // 596130 emits only for a newly constructed name.
            }
        }
        // 596770 ends in 598370: after identity reconciliation, formation
        // positions are refreshed by raw Aux ordinal, independently of names.
        for(int i=0;!station && i<_items.Count && i<incoming.Count;i++)
            _items[i]=_items[i] with {Position=incoming[i].Position};
        return _items.Count==previousCount?null:_items.Count==5?"Group_Max":
            _items.Count==0?"Group_Empty":_items.Count<previousCount?"Group_Decrease":"Group_Increase";
    }
}
