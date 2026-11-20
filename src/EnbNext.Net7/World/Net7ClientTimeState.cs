using System.Buffers.Binary;
using EnbNext.Net7;

namespace EnbNext.Net7.World;

/// <summary>
/// Clean representation of the client-owned timing state. The server
/// supplies T1/T2/T3; EnbNext stamps T4 locally and owns correction/drift.
/// </summary>
public sealed class Net7ClientTimeState
{
    private const int DriftDivisor = 32;
    private const int DriftMinimumSamplesForAdjustment = 65;
    private const double DriftAdjustmentThresholdMilliseconds = 50.0;

    private readonly Net7ProcessCompatibilityState _processCompatibilityState;
    private long _driftAccumulator;
    private int _driftSampleCount;

    public Net7ClientTimeState(Net7ProcessCompatibilityState? processCompatibilityState = null)
    {
        _processCompatibilityState = processCompatibilityState ?? new Net7ProcessCompatibilityState();
    }

    public long CorrectionMilliseconds { get; private set; }
    public long PatchedRttMilliseconds => _processCompatibilityState.PatchedRttMilliseconds;
    public long DriftAccumulator => _driftAccumulator;
    public int DriftSampleCount => _driftSampleCount;
    public int AdvancedRequestTimeCounter => _processCompatibilityState.AdvancedRequestTimeCounter;
    public int FormationRequestTimeCounter => _processCompatibilityState.FormationRequestTimeCounter;

    /// <summary>
    /// Apply retail 0x0034 Set_Client_Time. The current Net7Proxy-patched retail
    /// path uses patched RTT = -(T3-T2), while correction remains the pristine
    /// four-timestamp expression.
    /// </summary>
    public void ApplySetClientTime(ReadOnlySpan<byte> payload, uint clientReceivedTime)
    {
        if (payload.Length < 12)
            return; // safety rule: 008AB060 leaves missing timestamp bytes undefined; 00737960 consumes all three before updating state.

        uint t1 = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        uint t2 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        uint t3 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        uint t4 = clientReceivedTime;

        // The normal current retail-under-Net7Proxy path bends
        // only the RTT arm. Keep the raw signed millisecond result here; later
        // presentation can apply its own bounded duration representation.
        _processCompatibilityState.SetPatchedRttMilliseconds(-SignedDelta(t3, t2));

        long numerator = ((long)t2 + t3) - ((long)t1 + t4);
        CorrectionMilliseconds = numerator / 2;
        _driftAccumulator = 0;
        _driftSampleCount = 0;
    }

    public uint ConvertServerTimestamp(uint serverTimestamp)
    {
        // 00735280 and the positional handlers use signed duration bounds.
        long corrected = (long)serverTimestamp - CorrectionMilliseconds;
        return unchecked((uint)(int)Math.Clamp(corrected, int.MinValue, int.MaxValue));
    }

    /// <summary>
    /// Simple and Advanced updates contribute drift before object lookup.
    /// </summary>
    public uint ConvertAndObserveDrift(uint serverTimestamp, uint currentClientTime)
    {
        // 00733DC0 / 00734130 narrow the corrected timestamp as a signed
        // duration, saturating both ends before drift and provider mutation.
        uint converted = ConvertWithoutDrift(serverTimestamp);
        int error = unchecked((int)(currentClientTime - converted));

        _driftAccumulator = (_driftAccumulator - (_driftAccumulator / DriftDivisor)) + error;
        _driftSampleCount++;

        double average = _driftAccumulator / (double)DriftDivisor;
        if (Math.Abs(average) > DriftAdjustmentThresholdMilliseconds
            && _driftSampleCount >= DriftMinimumSamplesForAdjustment)
        {
            CorrectionMilliseconds -= (long)Math.Truncate(average);
            _driftAccumulator = 0;
            _driftSampleCount = 0;
        }

        return converted;
    }

    /// <summary>Component positional packets convert time but do not feed drift.</summary>
    public uint ConvertWithoutDrift(uint serverTimestamp) => ConvertServerTimestamp(serverTimestamp);

    public bool ObserveQualifyingLocalAdvancedUpdate()
        => _processCompatibilityState.ObserveQualifyingLocalAdvancedUpdate();

    public bool ObserveQualifyingLocalFormationUpdate()
        => _processCompatibilityState.ObserveQualifyingLocalFormationUpdate();

    /// <summary>
    /// Clears correction and drift, but deliberately preserves patched
    /// RTT and both 33-update Request_Time counters.
    /// </summary>
    public void ResetForServerHandoff()
    {
        CorrectionMilliseconds = 0;
        _driftAccumulator = 0;
        _driftSampleCount = 0;
    }

    public void ResetForCharacterSelection()
    {
        CorrectionMilliseconds = 0;
        _driftAccumulator = 0;
        _driftSampleCount = 0;
        // The RTT global and both Request_Time counters have no
        // character-selection reset in the current retail process. They remain
        // process-owned through Net7ProcessCompatibilityState.
    }

    private static long SignedDelta(uint later, uint earlier)
        => unchecked((int)(later - earlier));
}
