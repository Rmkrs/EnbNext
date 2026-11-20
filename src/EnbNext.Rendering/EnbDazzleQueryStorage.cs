namespace EnbNext.Rendering;

// A view owns this storage; nested synchronous queries get distinct slots.
// No scene-root reference survives a completed query, including exceptions.
public sealed class EnbDazzleQueryStorage
{
    private sealed class Slot
    {
        internal readonly List<(EnbDazzleCollisionRoot Root,float Distance)> Candidates=[];
        internal EnbRetailDazzleSampleProjection? Projection;
    }
    private readonly List<Slot> _slots=[];
    private int _depth;
    internal Lease Acquire()
    {
        if(_depth==_slots.Count)_slots.Add(new());
        return new(this,_depth++);
    }
    internal readonly ref struct Lease
    {
        private readonly EnbDazzleQueryStorage _owner;
        private readonly int _index;
        internal Lease(EnbDazzleQueryStorage owner,int index){_owner=owner;_index=index;}
        internal List<(EnbDazzleCollisionRoot Root,float Distance)> Candidates=>_owner._slots[_index].Candidates;
        internal EnbRetailDazzleSampleProjection Projection(EnbResolvedRenderCamera camera)
        {
            var slot=_owner._slots[_index];
            if(slot.Projection is null)slot.Projection=new(camera);
            else slot.Projection.Bind(camera);
            return slot.Projection;
        }
        public void Dispose()
        {
            if(_owner._depth!=_index+1)throw new InvalidOperationException("Dazzle query leases must drain in order");
            Candidates.Clear();_owner._depth--;
        }
    }
}
