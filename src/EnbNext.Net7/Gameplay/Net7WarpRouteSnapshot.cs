using System.Collections.Immutable;

namespace EnbNext.Net7.Gameplay;

/// <summary>
/// Detached radar route captured for a warp request, not confirmation of travel.
/// Current retail 0073A7F0 -> 007BD920 -> 008468C0 interprets 009C as a
/// zero-based vector index. The index need not increase monotonically.
/// </summary>
public sealed record Net7WarpRouteSnapshot(ImmutableArray<uint> ObjectIds, uint? ReportedIndex)
{
    /// <summary>Local ordering with target selection; never sent on the wire.</summary>
    public ulong CaptureSequence { get; init; }
    public uint? ReportedObjectId => ReportedIndex is uint index && index < (uint)ObjectIds.Length
        ? ObjectIds[(int)index] : null;
}
