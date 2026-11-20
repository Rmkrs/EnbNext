using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;

namespace EnbNext.Net7;

public sealed partial class Net7Session
{
    private readonly SemaphoreSlim _flightGate = new(1, 1);
    private Net7FlightInput _flightInput;
    private Net7FlightInput _flightSent;
    private ulong _flightHandoff;
    private uint _flightGeneration;
    private ulong? _incapacitationReleaseHandoff;

    private bool IsLocalShipIncapacitated()
        => World.LocalShipObjectId is uint id &&
            Net7WarpStatus.Read(World.Objects.FindFirst(id)?.AuxData, Gameplay.PresentationMode).Incapacitated;

    /// <summary>00581670: the first cockpit update that observes IsIncapacitated
    /// (latch +13D, cleared once it reads false) queues Move 4, Turn 0 and
    /// Tilt 0 and zeroes the local thrust/steering inputs. It is the only
    /// incapacitation effect on flight; later input is gated only by
    /// LockSpeed/LockOrient. The latch is cockpit-owned, so a new sector's
    /// cockpit observes an incapacitated ship afresh.</summary>
    internal async Task ReconcileIncapacitatedFlightAsync(CancellationToken cancellationToken)
    {
        Task<bool> Send(Func<Task<bool>> send)
        {
            lock (_worldStateSync) return send();
        }
        lock (_worldStateSync)
        {
            if (!IsLocalShipIncapacitated()) { _incapacitationReleaseHandoff = null; return; }
            if (_incapacitationReleaseHandoff == World.PresentationHandoffVersion || World.LocalShipObjectId is not uint id ||
                World.IsTransitioning || !_transportState.GameplayForwardingActive) return;
            _incapacitationReleaseHandoff = World.PresentationHandoffVersion;
            World.ApplyLocalFlightControls(id, _clock.GetMilliseconds(), 0, 0, 0);
            ResetFlightInput();
        }
        await Send(() => SendMoveAsync(4, cancellationToken)).ConfigureAwait(false);
        await Send(() => SendSteeringAsync(0x0012, 0, cancellationToken)).ConfigureAwait(false);
        await Send(() => SendSteeringAsync(0x0013, 0, cancellationToken)).ConfigureAwait(false);
    }

    private void ResetFlightInput()
    {
        _flightGeneration++;
        _flightInput = _flightSent = default;
    }

    /// <summary>Frame-level intent. Prediction is immediate; only changed wire
    /// controls are sent, with the existing independent proxy axis throttles.</summary>
    public Task<bool> UpdateFlightInputAsync(Net7FlightInput input, CancellationToken cancellationToken = default)
        => UpdateFlightInputCoreAsync(input, null, cancellationToken);

    internal Task<bool> UpdateRecordedFlightInputAsync(Net7FlightInput input, uint recordedClientTime,
        CancellationToken cancellationToken)
    {
        if (!_recordedReplay) throw new InvalidOperationException("Recorded control times require a replay session.");
        return UpdateFlightInputCoreAsync(input, recordedClientTime, cancellationToken);
    }

    private async Task<bool> UpdateFlightInputCoreAsync(Net7FlightInput input, uint? recordedClientTime,
        CancellationToken cancellationToken)
    {
        input.Validate();
        await _flightGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            uint generation;
            uint ship;
            ulong handoff;
            lock (_worldStateSync)
            {
                if (_flightHandoff != World.PresentationHandoffVersion || World.IsTransitioning || !_transportState.GameplayForwardingActive)
                {
                    _flightHandoff = World.PresentationHandoffVersion;
                    ResetFlightInput();
                    return false;
                }
                if (World.LocalShipObjectId is not uint id) return false;
                // LockOrient producers neither send nor apply the local
                // steering input (0057D6C0 guards both in 005806E0/006B58A0).
                if (World.LocalShipLockOrientActive)
                    input = input with { Horizontal = _flightInput.Horizontal, Vertical = _flightInput.Vertical };
                generation = _flightGeneration;
                ship = id;
                handoff = _flightHandoff;
                if (input != _flightInput)
                {
                    uint appliedTime=recordedClientTime ?? _clock.GetMilliseconds();
                    if (!_recordedReplay && _wireCapture?.IsActive == true)
                        _wireCapture.Mark("replay-flight-applied-v2", System.Text.Json.JsonSerializer.Serialize(
                            new Net7RecordedFlightInput(input, appliedTime)));
                    World.ApplyLocalFlightControls(id, appliedTime,
                        input.Horizontal != _flightInput.Horizontal ? input.Horizontal : null,
                        input.Vertical != _flightInput.Vertical ? input.Vertical : null,
                        input.Thrust != _flightInput.Thrust ? input.Thrust : null);
                    _flightInput = input;
                }
            }
            bool CurrentScope() => generation == _flightGeneration && handoff == World.PresentationHandoffVersion &&
                World.LocalShipObjectId == ship && !World.IsTransitioning && _transportState.GameplayForwardingActive;
            Task<bool> SendScoped(Func<Task<bool>> send)
            {
                lock (_worldStateSync) return CurrentScope() ? send() : Task.FromResult(false);
            }
            if (input.Horizontal != _flightSent.Horizontal && await SendScoped(() => SendHorizontalSteeringAsync(input.Horizontal, cancellationToken)).ConfigureAwait(false))
            {
                lock (_worldStateSync) { if (!CurrentScope()) return false; _flightSent = _flightSent with { Horizontal = input.Horizontal }; }
            }
            if (input.Vertical != _flightSent.Vertical && await SendScoped(() => SendVerticalSteeringAsync(input.Vertical, cancellationToken)).ConfigureAwait(false))
            {
                lock (_worldStateSync) { if (!CurrentScope()) return false; _flightSent = _flightSent with { Vertical = input.Vertical }; }
            }
            if (input.MoveType != _flightSent.MoveType && await SendScoped(() => SendMoveAsync(input.MoveType, cancellationToken)).ConfigureAwait(false))
            {
                lock (_worldStateSync) { if (!CurrentScope()) return false; _flightSent = _flightSent with { Thrust = input.Thrust, ThrustLocked = input.ThrustLocked }; }
            }
            return true;
        }
        finally { _flightGate.Release(); }
    }

    /// <summary>Samples a coherent frame under the same boundary as packet
    /// materialization, without rebuilding assets or sending network traffic.</summary>
    public T ReadPresentationFrame<T>(Func<Net7WorldState, uint, T> sample)
    {
        lock (_worldStateSync) return sample(World, _clock.GetMilliseconds());
    }
}
