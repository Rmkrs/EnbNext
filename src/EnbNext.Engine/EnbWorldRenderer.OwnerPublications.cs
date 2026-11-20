using System.Collections;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    // Ordinary publications have stable slots. Only secondary/generated entries
    // use the dictionary adapter; a new frame never rebuilds ordinary membership.
    private sealed class OwnerPublicationMap<T> : IDictionary<ulong,T>, IReadOnlyDictionary<ulong,T> where T:class
    {
        internal sealed class Population { public int Count; }
        internal class Slot
        {
            private T? value;
            private Population? population;
            public T? Value
            {
                get=>value;
                set
                {
                    if(population is not null && (this.value is null)!=(value is null))population.Count+=value is null?-1:1;
                    this.value=value;
                }
            }
            public void Bind(Population? next)
            {
                if(value is not null){if(population is not null)population.Count--;if(next is not null)next.Count++;}
                population=next;
            }
        }
        private readonly Population population=new();
        private readonly Dictionary<ulong,Slot> owners=[];
        private readonly Dictionary<ulong,T> auxiliary=[];
        public long OwnedReads;
        public void Attach(ulong identity,Slot slot){owners.Add(identity,slot);slot.Bind(population);}
        public void Replace(ulong identity,Slot slot){if(owners.TryGetValue(identity,out var previous))previous.Bind(null);owners[identity]=slot;slot.Bind(population);}
        public void Detach(ulong identity){if(owners.Remove(identity,out var slot))slot.Bind(null);}
        public void BeginFrame()=>auxiliary.Clear();
        public T this[ulong key]
        {
            get=>TryGetValue(key,out var value)?value:throw new KeyNotFoundException();
            set {if(owners.TryGetValue(key,out var slot))slot.Value=value;else auxiliary[key]=value;}
        }
        public bool TryGetValue(ulong key,out T value)
        {
            if(owners.TryGetValue(key,out var slot)){OwnedReads++;value=slot.Value!;return value is not null;}
            return auxiliary.TryGetValue(key,out value!);
        }
        public bool ContainsKey(ulong key)=>TryGetValue(key,out _);
        public void Add(ulong key,T value){if(ContainsKey(key))throw new ArgumentException("Duplicate publication");this[key]=value;}
        public bool Remove(ulong key){if(owners.TryGetValue(key,out var slot)){bool had=slot.Value is not null;slot.Value=null;return had;}return auxiliary.Remove(key);}
        public int Count=>population.Count+auxiliary.Count;
        public ICollection<ulong> Keys=>this.Select(p=>p.Key).ToArray();
        public ICollection<T> Values=>this.Select(p=>p.Value).ToArray();
        IEnumerable<ulong> IReadOnlyDictionary<ulong,T>.Keys=>this.Select(p=>p.Key);
        IEnumerable<T> IReadOnlyDictionary<ulong,T>.Values=>this.Select(p=>p.Value);
        public bool IsReadOnly=>false;
        public void Clear(){foreach(var slot in owners.Values)slot.Value=null;auxiliary.Clear();}
        public void Add(KeyValuePair<ulong,T> item)=>Add(item.Key,item.Value);
        public bool Contains(KeyValuePair<ulong,T> item)=>TryGetValue(item.Key,out var value)&&EqualityComparer<T>.Default.Equals(value,item.Value);
        public bool Remove(KeyValuePair<ulong,T> item)=>Contains(item)&&Remove(item.Key);
        public void CopyTo(KeyValuePair<ulong,T>[] array,int index){foreach(var item in this)array[index++]=item;}
        public IEnumerator<KeyValuePair<ulong,T>> GetEnumerator()
        {foreach(var pair in owners)if(pair.Value.Value is {} value)yield return new(pair.Key,value);foreach(var pair in auxiliary)yield return pair;}
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private readonly OwnerPublicationMap<EnbRslObjectState> _ownedRslPublications=new(),_ownedRslQueryPublications=new();
    private readonly OwnerPublicationMap<EnbNext.Rendering.EnbCurrentRetailRslEffectPassLibrary> _ownedPassPublications=new();
    private bool TryGetFrameRslUpdate(EnbNext.Presentation.IEnbRenderObject obj,out EnbRslObjectState update)
    {
        if(_ownedSceneFrame&&_buildingSceneOwner is {} source&&source.Definition.RenderIdentity==obj.RenderIdentity)
        {
            if(ReferenceEquals(_frameRslObjectUpdates,_ownedRslPublications)){update=source.RslUpdate!;return update is not null;}
            if(ReferenceEquals(_frameRslObjectUpdates,_ownedRslQueryPublications)){update=source.QueryRslPublication.Value!;return update is not null;}
        }
        return _frameRslObjectUpdates.TryGetValue(obj.RenderIdentity,out update!);
    }
}
