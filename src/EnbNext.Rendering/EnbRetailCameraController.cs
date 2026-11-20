using EnbNext.Presentation;

namespace EnbNext.Rendering;

public enum EnbRetailCameraState
{
    Wait, Follow, Warp, Gate, Random, Landing, Dock, Arrival, Loot, Takeoff, Distance, Rotating
}

/// <summary>799E40 / 79B2B0. One coalesced native event per camera update.
/// Actions are presentation requests; the session retains protocol authority.</summary>
public sealed class EnbRetailCameraController
{
    private static readonly int[] Priority = [0,1,2,3,4,5,6,24,7,8,9,10,11,12,13,14,15,16,17,18,19,22,20,21,23,25];
    private uint _pending;
    private readonly uint[] _flags = new uint[12];
    private uint _waitStarted;
    private bool _waitArmed, _waitHadShip;
    public EnbRetailCameraState State { get; private set; }
    public uint Pending => _pending;

    public void Reset(uint now, bool hasShip, bool armInitialWait = true)
    {
        _pending = 0;
        Array.Clear(_flags);
        State = EnbRetailCameraState.Wait;
        _flags[0] = armInitialWait ? 1u : 0u; // 798D20 arms initial wait; 745750 scene reset does not.
        StartWait(now, hasShip);
    }

    public void Enqueue(int nativeEvent)
    {
        // x86 shift masks its count. Bits26..31 remain unconsumed by 799E40.
        if (nativeEvent is 0 or 1 or 12 or 13) _pending &= ~0x3003u;
        else if (nativeEvent is 2 or 3 or 4) _pending &= ~0x3cu;
        _pending |= 1u << nativeEvent;
    }

    public static bool Enabled(EnbRetailCameraState state, EnbSceneCameraStateSnapshot options) => state switch
    {
        EnbRetailCameraState.Random => false, // 79B4F0 deliberately disables state4.
        EnbRetailCameraState.Warp => options.WarpCameraEnabled,
        EnbRetailCameraState.Gate => options.GateCameraEnabled,
        EnbRetailCameraState.Landing or EnbRetailCameraState.Arrival or EnbRetailCameraState.Takeoff => options.LandCameraEnabled,
        EnbRetailCameraState.Dock => options.DockCameraEnabled,
        EnbRetailCameraState.Loot => options.LootCameraEnabled,
        _ => true
    };

    public EnbRetailCameraDecision Update(EnbSceneCameraStateSnapshot options, uint now, bool hasShip,
        bool flaggedObject = false, bool suppressWarp = false, bool lootTargetEligible = false)
    {
        var previous = State;
        int? consumed = null, action = null;
        bool smooth = false;
        foreach (int value in Priority)
            if ((_pending & (1u << value)) != 0)
            { _pending &= ~(1u << value); consumed = value; break; }

        bool Select(EnbRetailCameraState next)
        {
            if (State == next || !Enabled(next, options)) return false;
            State = next;
            return true;
        }
        void WaitArmed()
        {
            Select(EnbRetailCameraState.Wait);
            _flags[0] |= 1;
        }
        void Gated(EnbRetailCameraState next, int completion)
        {
            if (!flaggedObject && previous != next)
            {
                if (previous == EnbRetailCameraState.Wait) return;
                if (Select(next))
                {
                    if (next == EnbRetailCameraState.Dock) _flags[6] |= 1;
                    return;
                }
            }
            Select(EnbRetailCameraState.Wait);
            action = completion;
        }
        switch (consumed)
        {
            case 0: if (!Select(EnbRetailCameraState.Random)) WaitArmed(); break;
            case 1: Select(EnbRetailCameraState.Wait); break;
            case 2: if (!suppressWarp) Select(EnbRetailCameraState.Warp); break;
            case 3: if (State == EnbRetailCameraState.Warp) _flags[2] |= 1; break;
            case 4: if (State == EnbRetailCameraState.Warp) _flags[2] |= 2; break;
            case 5:
                if (State == EnbRetailCameraState.Warp) { Select(EnbRetailCameraState.Follow); smooth = true; }
                break;
            case 6: Gated(EnbRetailCameraState.Gate, 0x13); break;
            case 24: Select(EnbRetailCameraState.Wait); action = 0x13; break;
            case 7: case 14: case 19: case 22: case 25: Select(EnbRetailCameraState.Follow); break;
            case 8: Gated(EnbRetailCameraState.Landing, 8); break;
            case 9: Select(EnbRetailCameraState.Wait); action = 8; break;
            case 10: Gated(EnbRetailCameraState.Dock, 7); break;
            case 11: Select(EnbRetailCameraState.Wait); action = 7; break;
            case 12: if (Select(EnbRetailCameraState.Dock)) _flags[6] |= 2; else WaitArmed(); break;
            case 13: if (!Select(EnbRetailCameraState.Arrival)) WaitArmed(); break;
            case 15: if (!flaggedObject && hasShip && lootTargetEligible) Select(EnbRetailCameraState.Loot); break;
            case 16: Select(EnbRetailCameraState.Follow); smooth = true; break;
            case 17:
                if (State != EnbRetailCameraState.Wait && !Select(EnbRetailCameraState.Takeoff))
                { Select(EnbRetailCameraState.Wait); action = 0x1b; }
                break;
            case 18: Select(EnbRetailCameraState.Wait); action = 0x1b; break;
            case 20: if (State == EnbRetailCameraState.Follow) Select(EnbRetailCameraState.Distance); break;
            case 21:
                if (State == EnbRetailCameraState.Distance) { Select(EnbRetailCameraState.Follow); smooth = true; }
                break;
            case 23: Select(EnbRetailCameraState.Rotating); break;
        }
        if (previous != State)
        {
            if (previous == EnbRetailCameraState.Dock) _flags[6] = 0;
            if (State == EnbRetailCameraState.Wait) StartWait(now, hasShip);
        }
        if (State == EnbRetailCameraState.Wait)
        {
            if (!_waitArmed && (_flags[0] & 1) != 0) StartWait(now, hasShip);
            if (_waitHadShip)
            {
                if (!hasShip) _waitHadShip = false;
                else if (_waitArmed || unchecked(now - _waitStarted) > 15000) Enqueue(22);
            }
            else if (hasShip)
            {
                if (_waitArmed) Enqueue(22);
                else { _waitHadShip = true; _waitStarted = now; }
            }
        }
        return new(previous, State, consumed, action, smooth,
            previous == EnbRetailCameraState.Distance && previous != State);
    }

    private void StartWait(uint now, bool hasShip)
    {
        _waitStarted = now; _waitHadShip = hasShip; _waitArmed = (_flags[0] & 1) != 0;
        _flags[0] &= ~1u;
    }
}

public readonly record struct EnbRetailCameraDecision(EnbRetailCameraState Previous, EnbRetailCameraState State,
    int? Event, int? Action, bool ForceSmooth, bool InterruptEmote)
{
    public bool Changed => Previous != State;
}
