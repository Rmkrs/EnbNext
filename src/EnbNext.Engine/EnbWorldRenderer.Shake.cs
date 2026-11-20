using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly EnbShakeRuntime _sceneShake = new();
    private ulong? _shakeOwner;
    private ulong _shakeForceRevision, _shakeDamageRevision;
    private ulong _shakeWarpSequence;
    private uint _shakeUpdated;
    private Vector3 _frameShake;
    private sealed record InterfaceShake(Vector3 Value, int Width, int Height);
    private InterfaceShake _interfaceShake = new(Vector3.Zero, 0, 0);
    private EnbWorldRenderer? _interfaceShakeConsumer;
    private readonly EnbOpenGlScannerFramebuffer _interfaceShakeFramebuffer = new();

    public void AttachInterfaceShakeConsumer(EnbWorldRenderer consumer) => _interfaceShakeConsumer = consumer;

    private void PublishInterfaceShake(Vector3 value, int width, int height)
    {
        var sample = new InterfaceShake(value, width, height);
        if (sample == Volatile.Read(ref _interfaceShake)) return;
        Volatile.Write(ref _interfaceShake, sample);
        _interfaceShakeConsumer?.RequestFrame(); // Includes the final zero sample.
    }

    private void RenderWithInterfaceShake(EnbGlApi gl, int fb, int width, int height, Action<int> render)
    {
        var sample = Volatile.Read(ref (MapWorldRenderer ?? this)._interfaceShake);
        var offset = sample.Width == width && sample.Height == height
            ? EnbShakeRuntime.InterfacePixelOffset(sample.Value, width, height) : (0, 0);
        if (offset == (0, 0)) { render(fb); return; }
        // Seed from the shifted destination and restore there: only UI moves,
        // while destination-dependent blends still see the correct world pixel.
        int scratch = _interfaceShakeFramebuffer.Begin(gl, fb, offset.Item1, offset.Item2, width, height);
        try { render(scratch); }
        finally
        {
            _interfaceShakeFramebuffer.End(gl, fb, offset.Item1, offset.Item2);
            gl.Viewport(0, 0, width, height);
        }
    }

    private void PauseShakeFrame(EnbNext.Presentation.IEnbSceneFrame snapshot, uint time)
    {
        // 742B60 normalizes Aux flags every frame, but calls +14 only on the
        // active view. 6E3710/6E3B60 use frame delta, so inactive time does not
        // advance retained impulses or queue inactive Aux hits for re-entry.
        var owner = SnapshotObjects(snapshot).FirstOrDefault(o => o.IsLocalShip);
        if (_shakeOwner != owner?.RenderIdentity) _sceneShake.Clear();
        _shakeOwner = owner?.RenderIdentity;
        var shake = owner?.ShipPresentation?.Shake;
        _shakeForceRevision = shake?.ForceRevision ?? 0;
        _shakeDamageRevision = shake?.DamageRevision ?? 0;
        var warp = owner?.ShipPresentation?.PrivateWarpChanges ?? [];
        _shakeWarpSequence = warp.Length == 0 ? 0 : warp[^1].Sequence;
        _shakeUpdated = time;
        _frameShake = Vector3.Zero;
    }

    private LiveFrame PrepareShakeFrame(LiveFrame frame, int width, int height)
    {
        // Apply the ordered feed before this pre-render callback. No pose work is
        // needed here; the local-owner link changes only on source mutations.
        bool owned=ApplyOwnedSceneChanges(frame);
        var owner = owned?_localSceneOwner?.Definition:
            SnapshotObjects(frame.Snapshot).FirstOrDefault(o => o.IsLocalShip);
        var pose=owned&&!frame.Snapshot.RenderPoses.IsDefault&&_localScenePoseSlot>=0?frame.Snapshot.RenderPoses[_localScenePoseSlot]:default;
        var presentation=owned?_localSceneOwner?.Presentation(pose):owner?.ShipPresentation;
        var shake = presentation?.Shake;
        // SampleLivePresentationFrame refreshes cockpit vitals at the current client clock.
        var vitals = frame.Snapshot.Hud.Cockpit?.Vitals ?? owner?.TargetVitals;
        var warpChanges = presentation?.PrivateWarpChanges ?? [];
        if (_shakeOwner != owner?.RenderIdentity)
        {
            _shakeOwner = owner?.RenderIdentity;
            _sceneShake.Clear();
            _shakeForceRevision = _shakeDamageRevision = 0;
            _shakeWarpSequence = warpChanges.Length > 1 ? warpChanges[^2].Sequence : 0;
            _shakeUpdated = frame.CurrentClientTime;
        }
        if (shake is not null)
        {
            if (shake.ForceRevision != _shakeForceRevision && shake.Force is {} force) _sceneShake.AddForce(force);
            if (shake.DamageRevision != _shakeDamageRevision && shake.Damage is {} damage)
                _sceneShake.AddDamage(damage, vitals?.MaximumShieldPower, vitals?.ShieldPercent);
            _shakeForceRevision = shake.ForceRevision;
            _shakeDamageRevision = shake.DamageRevision;
        }
        foreach (var change in warpChanges)
        {
            if (change.Sequence <= _shakeWarpSequence) continue;
            _shakeWarpSequence = change.Sequence;
            if (change.State == 2) _sceneShake.AddWarpStart();
        }
        _frameShake = _sceneShake.Advance(unchecked(frame.CurrentClientTime - _shakeUpdated), width, height);
        _shakeUpdated = frame.CurrentClientTime;
        PublishInterfaceShake(_frameShake, width, height);
        if (frame.Bridge is not {} bridge || _frameShake == Vector3.Zero) return frame;
        var cameras = bridge.Cameras.WithShake(_frameShake);
        return frame with
        {
            Bridge = bridge with { Cameras = cameras },
            Camera = EnbBridgeCameraComposition.ResolveCamera(cameras.WorldCamera, frame.Camera, frame.Camera.Projection.ClipRange)
        };
    }
}
