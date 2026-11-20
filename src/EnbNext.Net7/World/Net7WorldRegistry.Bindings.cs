namespace EnbNext.Net7.World;

/// <summary>Source-side binding to the native first-match lookup. Read and
/// dispose only inside the session world boundary; never publish this mutable
/// domain reference to UI/render consumers.</summary>
public sealed class Net7WorldObjectBinding : IDisposable
{
    private Net7WorldRegistry? _registry;
    internal Net7WorldObjectBinding(Net7WorldRegistry registry,uint gameId,Net7WorldObject? target)
    {_registry=registry;GameId=gameId;Target=target;}
    public uint GameId {get;}
    public Net7WorldObject? Target {get;internal set;}
    public void Dispose(){var registry=Interlocked.Exchange(ref _registry,null);registry?.Unbind(this);Target=null;}
}

public sealed partial class Net7WorldRegistry
{
    private readonly Dictionary<uint,List<WeakReference<Net7WorldObjectBinding>>> _bindings=[];
    private readonly object _bindingGate=new();
    public Net7WorldObjectBinding BindFirst(uint gameId)
    {
        lock(_bindingGate)
        {
        var result=new Net7WorldObjectBinding(this,gameId,FindFirst(gameId));
        if(!_bindings.TryGetValue(gameId,out var listeners))_bindings.Add(gameId,listeners=[]);
        listeners.RemoveAll(static weak=>!weak.TryGetTarget(out _));
        listeners.Add(new(result));return result;
        }
    }
    internal void Unbind(Net7WorldObjectBinding binding)
    {
        lock(_bindingGate)
        {
        if(!_bindings.TryGetValue(binding.GameId,out var listeners))return;
        for(int i=listeners.Count-1;i>=0;i--)
            if(!listeners[i].TryGetTarget(out var target)||ReferenceEquals(target,binding))listeners.RemoveAt(i);
        if(listeners.Count==0)_bindings.Remove(binding.GameId);
        }
    }
    private void RefreshBindings(Net7WorldMembershipChange change)
    {
        lock(_bindingGate)
        {
        if(change.Kind==Net7WorldMembershipChangeKind.Reset)
        {
            foreach(var listeners in _bindings.Values)
                foreach(var weak in listeners)if(weak.TryGetTarget(out var binding))binding.Target=null;
            return;
        }
        uint id=change.Object!.GameId;
        if(!_bindings.TryGetValue(id,out var interested))return;
        var first=FindFirst(id);
        for(int i=interested.Count-1;i>=0;i--)
            if(interested[i].TryGetTarget(out var binding))binding.Target=first;
            else interested.RemoveAt(i);
        if(interested.Count==0)_bindings.Remove(id);
        }
    }
}
