using System.Collections.Immutable;

namespace EnbNext.Rendering;

/// <summary>
/// One current-retail scene-render submission after the application static-sort
/// queue has been resolved. Immediate packets retain traversal order. Deferred
/// packets follow after traversal in SortLevel 32..1 order while preserving FIFO
/// order inside each level.
/// </summary>
public sealed record EnbW3dFrameSubmissionOrder(
    EnbRenderPassKind RenderPass,
    ImmutableArray<EnbW3dDrawTask> Packets,
    int ImmediatePacketCount,
    int DeferredPacketCount)
{
    public int PacketCount => Packets.Length;
}

/// <summary>Invocation-owned static-sort task storage. Output remains detached;
/// draining clears only buckets activated by this invocation.</summary>
public sealed class EnbW3dFrameSubmissionWorkspace
{
    internal readonly List<EnbW3dDrawTask> Immediate=[];
    internal readonly List<EnbW3dDrawTask>?[] Deferred=new List<EnbW3dDrawTask>[EnbW3dStaticSortSemantics.MaximumDeferredLevel+1];
    internal readonly List<int> Active=[];
    private int inUse;
    internal void Begin()
    {
        if(Interlocked.Exchange(ref inUse,1)!=0)throw new InvalidOperationException("Submission workspace is already in use.");
    }
    internal List<EnbW3dDrawTask> Bucket(int level)
    {
        var bucket=Deferred[level]??=new();
        if(bucket.Count==0)Active.Add(level);
        return bucket;
    }
    internal void End()
    {
        Immediate.Clear();
        foreach(int level in Active)Deferred[level]!.Clear();
        Active.Clear();
        Volatile.Write(ref inUse,0);
    }
}

/// <summary>
/// Current-retail application static-sort queue realized as an explicit
/// frame submission order. This compiler owns no camera-depth sort and does not
/// enable WW3D's low-level SortingRenderer, which current Earth &amp; Beyond disables.
/// </summary>
public static class EnbW3dFrameSubmissionOrderCompiler
{
    public static bool TryCompile(
        EnbRenderPassKind renderPass,
        IReadOnlyList<EnbW3dDrawTask> traversalPackets,
        out EnbW3dFrameSubmissionOrder order,
        out string? diagnostic,
        EnbW3dFrameSubmissionWorkspace? workspace=null)
    {
        ArgumentNullException.ThrowIfNull(traversalPackets);
        workspace??=new();
        workspace.Begin();
        try{return TryCompileCore(renderPass,traversalPackets,workspace,out order,out diagnostic);}
        finally{workspace.End();}
    }
    private static bool TryCompileCore(EnbRenderPassKind renderPass,
        IReadOnlyList<EnbW3dDrawTask> traversalPackets,EnbW3dFrameSubmissionWorkspace workspace,
        out EnbW3dFrameSubmissionOrder order,out string? diagnostic)
    {
        order = default!;
        diagnostic = null;

        var immediate = workspace.Immediate;
        int deferredCount = 0;

        for (int ordinal = 0; ordinal < traversalPackets.Count; ordinal++)
        {
            EnbW3dDrawTask? packet = traversalPackets[ordinal];
            if (packet is null)
            {
                diagnostic = $"Frame traversal packet {ordinal} is null.";
                return false;
            }

            if (packet.OrderKey.RenderPass != renderPass)
            {
                diagnostic = $"Frame traversal packet {ordinal} belongs to {packet.OrderKey.RenderPass}, not requested pass {renderPass}.";
                return false;
            }

            EnbW3dStaticSortSemantics staticSort = packet.OrderKey.StaticSort;
            switch (staticSort.Disposition)
            {
                case EnbW3dStaticSortDisposition.Immediate:
                    if (staticSort.RuntimeLevel != 0)
                    {
                        diagnostic = $"Frame traversal packet {ordinal} has immediate disposition with nonzero SortLevel {staticSort.RuntimeLevel}.";
                        return false;
                    }

                    immediate.Add(packet);
                    break;

                case EnbW3dStaticSortDisposition.DeferredBucket:
                {
                    int level = staticSort.RuntimeLevel;
                    if (level < EnbW3dStaticSortSemantics.MinimumDeferredLevel ||
                        level > EnbW3dStaticSortSemantics.MaximumDeferredLevel)
                    {
                        diagnostic = $"Frame traversal packet {ordinal} has deferred disposition with out-of-range SortLevel {level}.";
                        return false;
                    }

                    List<EnbW3dDrawTask> bucket = workspace.Bucket(level);
                    bucket.Add(packet);
                    deferredCount++;
                    break;
                }

                case EnbW3dStaticSortDisposition.RejectedByCurrentRetailBucketRange:
                    // 0097D060 (static-sort enqueue) accepts only unsigned levels 1..32
                    // and otherwise returns without queueing: that object is simply
                    // not drawn. The rest of the frame is unaffected.
                    break;

                default:
                    diagnostic = $"Frame traversal packet {ordinal} carries unknown static-sort disposition {staticSort.Disposition}.";
                    return false;
            }
        }

        var ordered = ImmutableArray.CreateBuilder<EnbW3dDrawTask>(immediate.Count + deferredCount);
        ordered.AddRange(immediate);
        workspace.Active.Sort();
        for(int index=workspace.Active.Count-1;index>=0;index--)
            ordered.AddRange(workspace.Deferred[workspace.Active[index]]!);

        order = new EnbW3dFrameSubmissionOrder(
            renderPass,
            ordered.MoveToImmutable(),
            immediate.Count,
            deferredCount);
        return true;
    }
}
