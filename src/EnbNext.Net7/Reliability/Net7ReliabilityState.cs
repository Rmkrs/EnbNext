namespace EnbNext.Net7.Reliability;

public enum Net7ReliabilityEntryState
{
    Absent = 0,
    Pending = 1,
    RecoveryOutstanding = 2,
    Abandoned = 3,
    Processed = 4,
}

public readonly record struct Net7ReliabilityEntrySnapshot(
    uint Sequence,
    Net7ReliabilityEntryState State,
    byte Variant,
    ushort Opcode,
    int PayloadLength);

public sealed record Net7ReliabilitySnapshot(
    uint Cursor,
    uint LastFirstSeen,
    bool BadDecodeRetryMarker,
    int ReassemblyRemaining,
    int ReassemblyOriginalRemaining,
    uint ReassemblyStartSequence,
    bool RestartRequested,
    uint LastRestartTick,
    bool ServerGateCachingEnabled,
    bool GateTransitionActive,
    bool GateAcknowledgementArm,
    bool GateProcessingPaused,
    bool GateSequenceScanMode,
    bool GateAcknowledgementLatch,
    uint GateBoundarySequence,
    bool OppositeSideLoginSeen,
    bool PostGateTransitionByte,
    uint SavedClientTime,
    IReadOnlyList<Net7ReliabilityEntrySnapshot> Entries);

public enum Net7DecodedPacketDisposition
{
    Unhandled = 0,
    Consumed = 1,
}
