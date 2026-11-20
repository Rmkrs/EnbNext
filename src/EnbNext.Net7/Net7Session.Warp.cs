using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7;

public sealed partial class Net7Session
{
    /// <summary>Integrated cockpit engage/disengage. The same 009B command is
    /// used in retail's ordinary-space button path in idle and engaged states.</summary>
    public async Task<bool> RequestWarpAsync(uint shipId, IReadOnlyList<uint> route, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(route);
        byte[] payload;
        uint[] captured;
        ulong handoff;
        int generation;
        int? requestedState;
        lock (_worldStateSync)
        {
            if (World.LocalShipObjectId != shipId || World.IsTransitioning || !_transportState.GameplayForwardingActive)
                return false;
            var status = Net7WarpStatus.Read(World.Objects.FindFirst(shipId)?.AuxData, Gameplay.PresentationMode);
            if (status.CanTakeoff)
            {
                // 583250 planet branch queues event17 instead of a warp packet.
                Gameplay.BeginTakeoffCamera();
                NotifyPresentationStateChanged();
                return true;
            }
            if (!status.CanActivate) return false;
            requestedState = status.State;
            captured = route.ToArray();
            payload = Net7GameplayWire.BuildWarp(RequireStartSessionValue(), captured);
            handoff = World.PresentationHandoffVersion;
            generation = _gameplayGeneration;
        }
        bool Current() => generation == _gameplayGeneration && handoff == World.PresentationHandoffVersion &&
            World.LocalShipObjectId == shipId && !World.IsTransitioning && _transportState.GameplayForwardingActive &&
            Net7WarpStatus.Read(World.Objects.FindFirst(shipId)?.AuxData, Gameplay.PresentationMode) is { CanActivate: true } status &&
            status.State == requestedState;
        await _flightGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task snapshot;
            lock (_worldStateSync)
            {
                if (!Current()) return false;
                snapshot = TrySendPositionSnapshotAsync(cancellationToken);
            }
            await snapshot.ConfigureAwait(false);
            Task<bool> send;
            lock (_worldStateSync)
            {
                if (!Current()) return false;
                // 00583250 calls IsWarpPathReady (which captures/copies the
                // radar route) only for launch, never for engaged disengage.
                if (requestedState == 0) Gameplay.CaptureWarpRoute(captured);
                send = TrySendGameplayAsync(0x009B, payload, cancellationToken);
                // 00583396..005833AE: prediction reset, not three extra packets.
                // Do this before yielding to inbound movement, never after it.
                World.ApplyLocalFlightControls(shipId, _clock.GetMilliseconds(), 0, 0, 0);
                ResetFlightInput();
                _flightHandoff = handoff;
            }
            return await send.ConfigureAwait(false);
        }
        finally { _flightGate.Release(); }
    }

    public async Task<bool> CompleteTakeoffCameraAsync(ulong revision, CancellationToken cancellationToken = default)
    {
        if (Gameplay.TakeoffRevision != revision || World.IsTransitioning || Gameplay.PresentationMode != 1) return false;
        Gameplay.ClearTakeoffCamera();
        uint ship = RequireLocalAvatarObjectId();
        bool sent = await SendActionCoreAsync(Net7PlayerAction.CameraControllerSelfAction,ship,0,cancellationToken).ConfigureAwait(false);
        NotifyPresentationStateChanged();
        return sent;
    }
}
