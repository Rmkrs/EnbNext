namespace EnbNext.Presentation;

/// <summary>5BFB00 construction and 5BFF50 retained resource membership.</summary>
public sealed class EnbHarvestResourceList
{
    private readonly List<EnbSceneHarvestableResourceSnapshot> _items=[];
    public IReadOnlyList<EnbSceneHarvestableResourceSnapshot> Items=>_items;
    public void Reset(IEnumerable<EnbSceneHarvestableResourceSnapshot> resources)
    {
        _items.Clear();
        _items.AddRange(resources.OrderBy(item=>item.Slot));
    }
    public void Update(int aggregateState,IReadOnlyList<int> slotStates,
        IReadOnlyList<EnbSceneHarvestableResourceSnapshot> resources)
    {
        if(aggregateState<=1)return;
        for(int slot=0;slot<slotStates.Count;slot++)
        {
            if(slotStates[slot]<=1)continue;
            int index=_items.FindIndex(item=>item.Slot==slot);
            var replacement=resources.FirstOrDefault(item=>item.Slot==slot);
            if(replacement is null)
            {if(index>=0)_items.RemoveAt(index);}
            else if(index>=0)_items[index]=replacement;
            else _items.Add(replacement);
        }
    }
}
