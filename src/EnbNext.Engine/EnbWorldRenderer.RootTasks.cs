using System.Collections;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    // Ordered per-view tasks. Owner chains allow a scene callback to unlink only
    // its own tasks; the global chain preserves the existing remove/append order.
    private sealed class RootTaskChain
    {
        public RootTaskList? Collection;
        public RootTaskList.Cell? First,Last;
        public int Count;
    }
    private readonly RootTaskList _frameRootTasks=new();
    private sealed class RootTaskList : IList<LiveRootInstance>,IReadOnlyList<LiveRootInstance>,IList,IDisposable
    {
        internal sealed class Cell
        {
            public LiveRootInstance Value=null!;
            public Cell? Previous,Next,OwnerPrevious,OwnerNext;
            public RootTaskChain? Owner;
            public RslWrapperOwner? RslAttachment;
            public (ulong Identity,ulong Generation)? CompatibilityRsl;
            public long Ordinal;
            public RootTaskList? Collection;
            public Cell? ValuePrevious,ValueNext;
            public Cell? ChildPrevious,ChildNext;
            public ChildTaskChain? ChildChain;
        }
        private Stack<Cell>? free;
        private RootTaskChain? unowned;
        private LiveRootInstance[] dense=[];
        private int denseCount;
        private int DenseCount=>denseCount;
        private LiveRootInstance DenseAt(int index){CheckDenseIndex(index);return dense[index];}
        private void CheckDenseIndex(int index){if((uint)index>=(uint)denseCount)throw new ArgumentOutOfRangeException(nameof(index));}
        private void GrowDense(){if(denseCount==dense.Length)Array.Resize(ref dense,Math.Max(4,denseCount*2));}
        private void DenseAdd(LiveRootInstance item){GrowDense();dense[denseCount++]=item;}
        private int DenseIndexOf(LiveRootInstance item)=>Array.IndexOf(dense,item,0,denseCount);
        private void DenseClear(){Array.Clear(dense,0,denseCount);denseCount=0;}
        private void DenseInsert(int index,LiveRootInstance item)
        {
            if((uint)index>(uint)denseCount)throw new ArgumentOutOfRangeException(nameof(index));
            GrowDense();Array.Copy(dense,index,dense,index+1,denseCount-index);dense[index]=item;denseCount++;
        }
        private void DenseRemoveRange(int index,int length)
        {
            if(index<0||length<0||index>denseCount-length)throw new ArgumentOutOfRangeException();
            Array.Copy(dense,index+length,dense,index,denseCount-index-length);
            Array.Clear(dense,denseCount-length,length);denseCount-=length;
        }
        private int DenseRemoveAll(Predicate<LiveRootInstance> predicate)
        {
            int keep=0,original=denseCount;
            for(int i=0;i<original;i++)if(!predicate(dense[i]))dense[keep++]=dense[i];
            Array.Clear(dense,keep,original-keep);denseCount=keep;return original-keep;
        }
        private EnbWorldRenderer? view;
        private Cell? first,last,cursor;
        private int count,cursorIndex=-1;
        private long ordinal;
        private bool active;
        public RootTaskList Rent(EnbWorldRenderer owner)
        {
            if(active)return new RootTaskList().Rent(owner);
            active=true;view=owner;free??=new();unowned??=new();return this;
        }
        public bool IsOwnedBy(EnbWorldRenderer owner)=>ReferenceEquals(view,owner);
        internal bool MaintainsChildTasks=>view?._ownerLocalShipChildTasks==true;
        internal IReadOnlyList<LiveRootInstance> ChildTasks(LiveRootInstance parent)
        {
            if(parent.QueuedChildChains is {} chains)foreach(var chain in chains)
                if(ReferenceEquals(chain.Collection,this))return chain;
            return Array.Empty<LiveRootInstance>();
        }
        public int Count=>view is null?denseCount:count;
        public long OwnerRemovalVisits {get;private set;}
        public bool IsReadOnly=>false;
        bool IList.IsFixedSize=>false;
        bool ICollection.IsSynchronized=>false;
        object ICollection.SyncRoot=>this;
        object? IList.this[int index]{get=>this[index];set=>this[index]=(LiveRootInstance)value!;}
        int IList.Add(object? value){int index=Count;Add((LiveRootInstance)value!);return index;}
        bool IList.Contains(object? value)=>value is LiveRootInstance item&&Contains(item);
        int IList.IndexOf(object? value)=>value is LiveRootInstance item?IndexOf(item):-1;
        void IList.Insert(int index,object? value)=>Insert(index,(LiveRootInstance)value!);
        void IList.Remove(object? value){if(value is LiveRootInstance item)Remove(item);}
        void ICollection.CopyTo(Array array,int index){foreach(var item in this)array.SetValue(item,index++);}
        private Cell At(int index)
        {
            if((uint)index>=(uint)count)throw new ArgumentOutOfRangeException(nameof(index));
            int distance=cursor is null?int.MaxValue:Math.Abs(index-cursorIndex);
            if(index<distance){cursor=first;cursorIndex=0;distance=index;}
            if(count-1-index<distance){cursor=last;cursorIndex=count-1;}
            while(cursorIndex<index){cursor=cursor!.Next;cursorIndex++;}
            while(cursorIndex>index){cursor=cursor!.Previous;cursorIndex--;}
            return cursor!;
        }
        public LiveRootInstance this[int index]
        {
            get=>view is null?DenseAt(index):At(index).Value;
            set {if(view is null){CheckDenseIndex(index);dense[index]=value;}else throw new NotSupportedException("Replace tasks by explicit remove/append.");}
        }
        public void Add(LiveRootInstance item)
        {
            if(view is null){DenseAdd(item);return;}
            SceneOwner? source=view._buildingSceneOwner??item.PreparedOwner?.SceneSource;
            for(var parent=item.Parent;source is null&&parent is not null;parent=parent.Parent)source=parent.PreparedOwner?.SceneSource;
            if(source is not null&&item.Object.RenderIdentity==source.Definition.RenderIdentity)
                item.PreparedSceneVisibility=source.Visibility;
            view.BindProjectedDecalRoot(item,source is not null&&item.Object.RenderIdentity==source.RenderIdentity?source:null);
            var chain=source?.RootTasks??unowned!;
            if(chain.Collection is not null&&!ReferenceEquals(chain.Collection,this))chain=unowned!;
            chain.Collection=this;chain.Count++;
            var cell=free!.Count==0?new Cell():free!.Pop();
            cell.Value=item;cell.Ordinal=++ordinal;cell.Previous=last;cell.Next=null;cell.Owner=chain;
            cell.OwnerPrevious=chain.Last;cell.OwnerNext=null;
            if(view._ownerLocalShipChildTasks)
            {
                cell.Collection=this;cell.ValuePrevious=null;cell.ValueNext=item.QueuedTaskHead;
                if(cell.ValueNext is {} nextValue)nextValue.ValuePrevious=cell;
                item.QueuedTaskHead=cell;
            }
            if(source is not null && item.IsRsl && item.Object.RenderIdentity==source.Definition.RenderIdentity &&
                item.Object.RenderObjectGeneration==source.Definition.RenderObjectGeneration)
            {
                item.RslAttachment=source.Attachment;
                cell.RslAttachment=source.Attachment;
                if(cell.RslAttachment.TaskCount++==0)view.QueueWrapperAttachment(cell.RslAttachment);
            }
            else if(item.IsRsl&&view._ownedSceneFrame)
            {
                var key=(item.Object.RenderIdentity,item.Object.RenderObjectGeneration);
                if(view._effectRslRootOwners.TryGetValue(key.RenderIdentity,out var effect)&&effect.Object.RenderObjectGeneration==key.RenderObjectGeneration)
                {
                    item.RslAttachment=cell.RslAttachment=effect.Attachment;
                    if(effect.Attachment.TaskCount++==0)view.QueueWrapperAttachment(effect.Attachment);
                }
                else
                {
                    cell.CompatibilityRsl=key;
                    view._compatRslTaskMembers.TryGetValue(key,out int references);view._compatRslTaskMembers[key]=references+1;
                }
            }
            if(last is null)first=cell;else last.Next=cell;last=cell;
            if(chain.Last is null)chain.First=cell;else chain.Last.OwnerNext=cell;chain.Last=cell;
            count++;
            if(MaintainsChildTasks)LinkChild(cell);
        }
        public void AddRange(RootTaskList items)
        {
            if(ReferenceEquals(items,this)){AddRange((IEnumerable<LiveRootInstance>)ToArray());return;}
            foreach(var item in items)Add(item);
        }
        public void AddRange(IEnumerable<LiveRootInstance> items)
        {if(items is RootTaskList roots){AddRange(roots);return;}foreach(var item in items)Add(item);}
        private void Unlink(Cell cell)
        {
            UnlinkChild(cell);
            if(cell.Previous is {} previous)previous.Next=cell.Next;else first=cell.Next;
            if(cell.Next is {} next)next.Previous=cell.Previous;else last=cell.Previous;
            var chain=cell.Owner!;chain.Count--;
            if(cell.OwnerPrevious is {} op)op.OwnerNext=cell.OwnerNext;else chain.First=cell.OwnerNext;
            if(cell.OwnerNext is {} on)on.OwnerPrevious=cell.OwnerPrevious;else chain.Last=cell.OwnerPrevious;
            if(chain.First is null)chain.Collection=null;
            if(cell.RslAttachment is {} publication)
            {if(--publication.TaskCount==0)view!.QueueWrapperAttachment(publication);cell.RslAttachment=null;}
            if(cell.CompatibilityRsl is {} key)
            {
                int references=view!._compatRslTaskMembers[key];
                if(references==1)view._compatRslTaskMembers.Remove(key);else view._compatRslTaskMembers[key]=references-1;
                cell.CompatibilityRsl=null;
            }
            if(cell.Collection is not null)
            {
                if(cell.ValuePrevious is {} previousValue)previousValue.ValueNext=cell.ValueNext;
                else cell.Value.QueuedTaskHead=cell.ValueNext;
                if(cell.ValueNext is {} nextValue)nextValue.ValuePrevious=cell.ValuePrevious;
                cell.Collection=null;cell.ValuePrevious=null;cell.ValueNext=null;
            }
            cell.Value=null!;cell.Previous=null;cell.Next=null;cell.OwnerPrevious=null;cell.OwnerNext=null;cell.Owner=null;
            free!.Push(cell);count--;cursor=null;cursorIndex=-1;
        }
        internal static void Reparent(LiveRootInstance child)
        {
            child.ProjectedDecalFactory=null;
            for(var cell=child.QueuedTaskHead;cell is not null;cell=cell.ValueNext)
            {
                UnlinkChild(cell);
                if(cell.Collection?.MaintainsChildTasks==true)cell.Collection.LinkChild(cell);
            }
        }
        private void LinkChild(Cell cell)
        {
            if(cell.Value.Parent is not {} parent)return;
            var chains=parent.QueuedChildChains??=[];
            ChildTaskChain? chain=null,empty=null;
            foreach(var item in chains)
            {
                if(ReferenceEquals(item.Collection,this)){chain=item;break;}
                if(item.Collection is null)empty=item;
            }
            if(chain is null){chain=empty??new ChildTaskChain();if(empty is null)chains.Add(chain);chain.Collection=this;}
            cell.ChildChain=chain;chain.CountValue++;
            // Appends are O(1). Reparenting an already queued occurrence may
            // insert into the existing ordered child chain, preserving the
            // collection's exact occurrence order including duplicates.
            var previous=chain.Last;
            while(previous is not null && previous.Ordinal>cell.Ordinal)previous=previous.ChildPrevious;
            cell.ChildPrevious=previous;cell.ChildNext=previous is null?chain.First:previous.ChildNext;
            if(cell.ChildNext is {} next)next.ChildPrevious=cell;else chain.Last=cell;
            if(previous is null)chain.First=cell;else previous.ChildNext=cell;
        }
        private static void UnlinkChild(Cell cell)
        {
            if(cell.ChildChain is not {} chain)return;
            if(cell.ChildPrevious is {} previous)previous.ChildNext=cell.ChildNext;else chain.First=cell.ChildNext;
            if(cell.ChildNext is {} next)next.ChildPrevious=cell.ChildPrevious;else chain.Last=cell.ChildPrevious;
            if(--chain.CountValue==0)chain.Collection=null;
            cell.ChildChain=null;cell.ChildPrevious=null;cell.ChildNext=null;
        }
        public void MoveOwnedToEnd(SceneOwner source,RootTaskList current)
        {
            if(view is null || !ReferenceEquals(source.RootTasks.Collection,this))
                throw new InvalidOperationException("Callback task chain belongs to another view.");
            for(var cell=source.RootTasks.First;cell is not null;cell=cell.OwnerNext)
            {
                current.Add(cell.Value);
                if(cell.Previous is {} previous)previous.Next=cell.Next;else first=cell.Next;
                if(cell.Next is {} next)next.Previous=cell.Previous;else last=cell.Previous;
                UnlinkChild(cell);
                cell.Previous=last;cell.Next=null;cell.Ordinal=++ordinal;
                if(MaintainsChildTasks)LinkChild(cell);
                if(last is null)first=cell;else last.Next=cell;last=cell;
            }
            cursor=null;cursorIndex=-1;
        }
        public int RemoveOwned(SceneOwner source,Predicate<LiveRootInstance> predicate)
        {
            if(view is null)return RemoveAll(predicate);
            var owned=ReferenceEquals(source.RootTasks.Collection,this)?source.RootTasks.First:null;
            var other=unowned!.First;int removed=0;
            while(owned is not null||other is not null)
            {
                Cell cell;
                if(other is null||(owned is not null&&owned.Ordinal<other.Ordinal)){cell=owned!;owned=cell.OwnerNext;}
                else {cell=other;other=cell.OwnerNext;}
                OwnerRemovalVisits++;
                if(predicate(cell.Value)){Unlink(cell);removed++;}
            }
            return removed;
        }
        public int RemoveAll(Predicate<LiveRootInstance> predicate)
        {
            if(view is null)return DenseRemoveAll(predicate);
            int removed=0;
            for(var cell=first;cell is not null;){var next=cell.Next;if(predicate(cell.Value)){Unlink(cell);removed++;}cell=next;}
            return removed;
        }
        public void Clear()
        {
            if(view is null){DenseClear();return;}
            while(first is not null)Unlink(first);
            ordinal=0;
        }
        public void Dispose(){Clear();view=null;active=false;}
        public bool Contains(LiveRootInstance item)
        {
            if(view is null || !view._ownerLocalShipChildTasks)return IndexOf(item)>=0;
            // Actual queued cells belong to the occurrence. Duplicate tasks and
            // nested views retain independent links, without a global search.
            for(var cell=item.QueuedTaskHead;cell is not null;cell=cell.ValueNext)
                if(ReferenceEquals(cell.Collection,this))return true;
            return false;
        }
        public int IndexOf(LiveRootInstance item)
        {
            if(view is null)return DenseIndexOf(item);
            int i=0;for(var cell=first;cell is not null;cell=cell.Next,i++)if(ReferenceEquals(cell.Value,item))return i;return -1;
        }
        public bool Remove(LiveRootInstance item){int index=IndexOf(item);if(index<0)return false;RemoveAt(index);return true;}
        public void RemoveAt(int index){if(view is null)DenseRemoveRange(index,1);else Unlink(At(index));}
        public void RemoveRange(int index,int length){if(view is null)DenseRemoveRange(index,length);else for(int i=0;i<length;i++)RemoveAt(index);}
        public void Insert(int index,LiveRootInstance item){if(view is null)DenseInsert(index,item);else if(index==count)Add(item);else throw new NotSupportedException();}
        public void CopyTo(LiveRootInstance[] array,int index){foreach(var item in this)array[index++]=item;}
        public LiveRootInstance[] ToArray(){var result=new LiveRootInstance[Count];CopyTo(result,0);return result;}
        public Enumerator GetEnumerator()=>new(this);
        IEnumerator<LiveRootInstance> IEnumerable<LiveRootInstance>.GetEnumerator()=>GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
        public struct Enumerator : IEnumerator<LiveRootInstance>
        {
            private readonly RootTaskList owner;
            private Cell? next;
            private int index;
            internal Enumerator(RootTaskList owner){this.owner=owner;next=owner.first;index=-1;Current=null!;}
            public LiveRootInstance Current {get;private set;}
            object IEnumerator.Current=>Current;
            public bool MoveNext()
            {
                if(owner.view is null){if(++index>=owner.DenseCount)return false;Current=owner.DenseAt(index);return true;}
                if(next is null)return false;Current=next.Value;next=next.Next;return true;
            }
            public void Reset()=>throw new NotSupportedException();
            public void Dispose(){}
        }
    }
}
