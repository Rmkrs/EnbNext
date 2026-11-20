using System.Collections.Concurrent;

namespace EnbNext.Net7.World;

public enum Net7WorldMembershipChangeKind { Reset, Add, Remove }

/// <summary>Add follows After; a null predecessor inserts at the front.
/// This records actual registry node identity, including duplicate game IDs.</summary>
public readonly record struct Net7WorldMembershipChange(ulong Revision,
    Net7WorldMembershipChangeKind Kind, Net7WorldObject? Object, Net7WorldObject? After);

/// <summary>Ordered mutation delivery. Dispose releases pending object references.
/// Consumers of domain objects still use the session's world mutation boundary;</summary>
public sealed class Net7WorldMembershipSubscription : IDisposable
{
    private readonly ConcurrentQueue<Net7WorldMembershipChange> _pending = new();
    private int _disposed;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal void Publish(Net7WorldMembershipChange change)
    {
        if (IsDisposed) return;
        _pending.Enqueue(change);
        if (IsDisposed) _pending.Clear();
    }
    public bool TryRead(out Net7WorldMembershipChange change) => _pending.TryDequeue(out change);
    public void Dispose() { Interlocked.Exchange(ref _disposed, 1); _pending.Clear(); }
}

public sealed partial class Net7WorldRegistry
{
    private readonly List<WeakReference<Net7WorldMembershipSubscription>> _membershipSubscribers = [];

    /// <summary>Call under the same mutation boundary as registry operations.
    /// Bootstrap and later changes preserve 7274D0/48A120 registry traversal order.</summary>
    public Net7WorldMembershipSubscription SubscribeMembership()
    {
        var subscription = new Net7WorldMembershipSubscription();
        subscription.Publish(new(MembershipRevision, Net7WorldMembershipChangeKind.Reset, null, null));
        Net7WorldObject? previous = null;
        foreach (int bucket in _bucketOrder)
            for (Node? node = _buckets[bucket]; node is not null; node = node.Next)
            {
                subscription.Publish(new(MembershipRevision, Net7WorldMembershipChangeKind.Add, node.Value, previous));
                previous = node.Value;
            }
        _membershipSubscribers.Add(new(subscription));
        return subscription;
    }

    private void PublishMembership(Net7WorldMembershipChange change)
    {
        RefreshBindings(change);
        PublishRenderMembership(change);
        // Only mutations visit subscribers. Idle frames perform no registry
        // reconciliation, and the registry cannot keep an abandoned consumer alive.
        for (int i = _membershipSubscribers.Count - 1; i >= 0; i--)
            if (_membershipSubscribers[i].TryGetTarget(out var subscriber) && !subscriber.IsDisposed)
                subscriber.Publish(change);
            else _membershipSubscribers.RemoveAt(i);
    }
}
