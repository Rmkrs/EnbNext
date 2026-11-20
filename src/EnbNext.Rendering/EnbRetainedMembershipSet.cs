using System.Collections;

namespace EnbNext.Rendering;

/// <summary>Persistent scene membership with a frame-local extension for
/// generated owners. Source mutations update the base; frames clear only their
/// extensions. Auxiliary views can use transient membership independently.</summary>
public sealed class EnbRetainedMembershipSet<T> : IReadOnlySet<T> where T:notnull
{
    private readonly HashSet<T> _persistent=[];
    private readonly HashSet<T> _temporary=[];
    private bool _usePersistent;
    public long PersistentMutations {get;private set;}
    public void Attach(T value) {if(_persistent.Add(value))PersistentMutations++;}
    public void Detach(T value) {if(_persistent.Remove(value))PersistentMutations++;}
    public void BeginFrame(bool usePersistent){_temporary.Clear();_usePersistent=usePersistent;}
    public void ClearFrame()=>_temporary.Clear();
    public IEnumerable<T> FrameExtensions=>_temporary;
    public void Add(T value){if(!_usePersistent || !_persistent.Contains(value))_temporary.Add(value);}
    public void UnionWith(IEnumerable<T> values){foreach(var value in values)Add(value);}
    public bool Contains(T value)=>(_usePersistent && _persistent.Contains(value)) || _temporary.Contains(value);
    public int Count=>(_usePersistent?_persistent.Count:0)+_temporary.Count;
    public IEnumerator<T> GetEnumerator()
    {
        if(_usePersistent)foreach(var value in _persistent)yield return value;
        foreach(var value in _temporary)yield return value;
    }
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    public bool IsSubsetOf(IEnumerable<T> other)=>new HashSet<T>(this).IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<T> other)=>other.All(Contains);
    public bool IsProperSupersetOf(IEnumerable<T> other)=>new HashSet<T>(this).IsProperSupersetOf(other);
    public bool IsProperSubsetOf(IEnumerable<T> other)=>new HashSet<T>(this).IsProperSubsetOf(other);
    public bool Overlaps(IEnumerable<T> other)=>other.Any(Contains);
    public bool SetEquals(IEnumerable<T> other)=>new HashSet<T>(this).SetEquals(other);
}
