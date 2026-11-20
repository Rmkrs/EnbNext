using System.Collections.Concurrent;

namespace EnbNext.Net7.World;

public enum Net7WorldRenderChangeKind { Reset, Add, Definition, Remove }
public readonly record struct Net7WorldRenderChange(ulong Revision,
    Net7WorldRenderChangeKind Kind, Net7WorldObject? Object, Net7WorldObject? After);

/// <summary>Source mutations in registry order. Read/project under the world's
/// mutation boundary; mutable source references never cross into rendering.</summary>
public sealed class Net7WorldRenderSubscription : IDisposable
{
    private readonly ConcurrentQueue<Net7WorldRenderChange> _pending=new();
    private int _disposed;
    internal bool IsDisposed=>Volatile.Read(ref _disposed)!=0;
    internal void Publish(Net7WorldRenderChange change)
    {
        if(IsDisposed)return;
        _pending.Enqueue(change);
        if(IsDisposed)_pending.Clear();
    }
    public bool TryRead(out Net7WorldRenderChange change)=>_pending.TryDequeue(out change);
    public void Dispose(){Interlocked.Exchange(ref _disposed,1);_pending.Clear();}
}

public sealed partial class Net7WorldRegistry
{
    private readonly List<WeakReference<Net7WorldRenderSubscription>> _renderSubscribers=[];
    private ulong _renderRevision;
    public Net7WorldRenderSubscription SubscribeRenderChanges()
    {
        var subscription=new Net7WorldRenderSubscription();
        subscription.Publish(new(_renderRevision,Net7WorldRenderChangeKind.Reset,null,null));
        Net7WorldObject? previous=null;
        foreach(int bucket in _bucketOrder)
            for(Node? node=_buckets[bucket];node is not null;node=node.Next)
            {
                subscription.Publish(new(_renderRevision,Net7WorldRenderChangeKind.Add,node.Value,previous));
                previous=node.Value;
            }
        _renderSubscribers.Add(new(subscription));
        return subscription;
    }
    private void PublishRenderMembership(Net7WorldMembershipChange change)
    {
        if(change.Kind==Net7WorldMembershipChangeKind.Add)
            change.Object!.RenderMutationSink=PublishRenderDefinition;
        else if(change.Kind==Net7WorldMembershipChangeKind.Remove)
            change.Object!.RenderMutationSink=null;
        DeliverRenderChange(new(++_renderRevision,change.Kind switch
        {
            Net7WorldMembershipChangeKind.Reset=>Net7WorldRenderChangeKind.Reset,
            Net7WorldMembershipChangeKind.Add=>Net7WorldRenderChangeKind.Add,
            Net7WorldMembershipChangeKind.Remove=>Net7WorldRenderChangeKind.Remove,
            _=>throw new ArgumentOutOfRangeException(nameof(change))
        },change.Object,change.After));
    }
    private void PublishRenderDefinition(Net7WorldObject obj)
        =>DeliverRenderChange(new(++_renderRevision,Net7WorldRenderChangeKind.Definition,obj,null));
    private void DeliverRenderChange(Net7WorldRenderChange change)
    {
        for(int i=_renderSubscribers.Count-1;i>=0;i--)
            if(_renderSubscribers[i].TryGetTarget(out var subscriber)&&!subscriber.IsDisposed)
                subscriber.Publish(change);
            else _renderSubscribers.RemoveAt(i);
    }
}
