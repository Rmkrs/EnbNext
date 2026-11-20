using System.Collections.Generic;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // Visible occurrences already have their parent pointer. Query traversal
    // uses temporary child tasks attached directly to that parent, rather than
    // reconstructing an identity dictionary at each query publication.
    private sealed class ChildTaskChain : IReadOnlyList<LiveRootInstance>
    {
        internal RootTaskList? Collection;
        internal RootTaskList.Cell? First,Last;
        internal int CountValue;
        public int Count=>CountValue;
        public LiveRootInstance this[int index]
        {
            get
            {
                if((uint)index>=(uint)CountValue)throw new ArgumentOutOfRangeException(nameof(index));
                var cell=First!;while(index-->0)cell=cell.ChildNext!;return cell.Value;
            }
        }
        public IEnumerator<LiveRootInstance> GetEnumerator()
        {for(var cell=First;cell is not null;cell=cell.ChildNext)yield return cell.Value;}
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private sealed class QueryChildTaskSlot
    {
        internal QueryOccurrenceIndex? Owner;
        internal readonly RootTaskList Children=[];
    }
    private sealed class QueryOccurrenceIndex : IDisposable
    {
        private readonly EnbWorldRenderer _view;
        private readonly RootTaskList _parents=[];
        internal RootTaskList? RetainedTasks;
        internal QueryOccurrenceIndex(EnbWorldRenderer view){_view=view;}
        internal void Add(LiveRootInstance child)
        {
            if(RetainedTasks is not null)return;
            if(child.Parent is not {} parent)return;
            var slots=parent.QueryChildTasks;
            int depth=parent.QueryChildTaskDepth;
            QueryChildTaskSlot slot;
            if(depth!=0 && ReferenceEquals(slots[depth-1].Owner,this))slot=slots[depth-1];
            else
            {
                if(depth==slots.Count)slots.Add(new());
                slot=slots[depth];slot.Owner=this;parent.QueryChildTaskDepth++;
                _parents.Add(parent);
            }
            slot.Children.Add(child);
        }
        internal IReadOnlyList<LiveRootInstance> Children(LiveRootInstance parent)
        {
            if(RetainedTasks is not null)return RetainedTasks.ChildTasks(parent);
            int depth=parent.QueryChildTaskDepth;
            return depth!=0 && ReferenceEquals(parent.QueryChildTasks[depth-1].Owner,this)
                ?parent.QueryChildTasks[depth-1].Children:Array.Empty<LiveRootInstance>();
        }
        public void Dispose()
        {
            foreach(var parent in _parents)
            {
                var slot=parent.QueryChildTasks[parent.QueryChildTaskDepth-1];
                if(!ReferenceEquals(slot.Owner,this))throw new InvalidOperationException("Query child tasks must drain in scope order.");
                slot.Children.Clear();slot.Owner=null;parent.QueryChildTaskDepth--;
            }
            _parents.Clear();RetainedTasks=null;_view._queryOccurrenceIndices.Push(this);
        }
    }
    private readonly Stack<QueryOccurrenceIndex> _queryOccurrenceIndices=[];
    private QueryOccurrenceIndex IndexQueryOccurrences(RootTaskList roots)
    {
        var index=_queryOccurrenceIndices.TryPop(out var retained)?retained:new QueryOccurrenceIndex(this);
        if(roots.MaintainsChildTasks)index.RetainedTasks=roots;
        else foreach(var root in roots)index.Add(root);
        return index;
    }
}
