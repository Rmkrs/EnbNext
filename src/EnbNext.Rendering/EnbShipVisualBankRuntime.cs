using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Retail type-0/1 ship visual bank, 006F3FA0 / 006F4610. Owns only
/// presentation state; returned snapshots must never become physics/camera anchors.</summary>
public sealed class EnbShipVisualBankRuntime
{
    private readonly Dictionary<ulong, State> _states = [];
    private readonly HashSet<ulong> _seen = [];

    public void Clear() => _states.Clear();

    public EnbSceneFrame ApplyFrame(EnbSceneFrame scene, double elapsedMilliseconds)
    {
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        _seen.Clear();
        var objects = scene.Objects;
        var renderPoses = scene.RenderPoses;
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = scene.MutableObject(i);
            _seen.Add(obj.RenderIdentity);
            // Missing IsOrganic is false in retail.
            if (obj.ObjectType > 1 || obj.ShipPresentation?.IsOrganic == true ||
                obj.Spatial?.Transform is not { } transform ||
                !float.IsFinite(transform.Rotation.Z)) continue;
            //6F2820 initializes +134/+144 on the client object.6F3FA0
            //retains them across model/composition and spatial updates; its
            //organic/type guards skip evaluation without destroying state.
            if (!_states.TryGetValue(obj.RenderIdentity, out var state))
            {
                state = new State(obj.ObjectType);
                _states[obj.RenderIdentity] = state;
            }
            else if (state.Time != scene.ClientTime)
            {
                var physicalMatrix=transform.ProviderTransform??EnbRetailCameraQuaternionBasis.Create(transform.Orientation);
                float target=EnbRetailShipBankTarget.Evaluate(obj.ObjectType,transform.Rotation.Z,transform.BankHeadingTarget,physicalMatrix);
                state.Bank.Advance(target, obj.IsLocalShip, elapsedMilliseconds);
            }
            state.Time = scene.ClientTime;
            Quaternion visual = transform.Orientation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, state.Bank.Angle);
            Matrix4x4 provider = transform.ProviderTransform ?? EnbRetailCameraQuaternionBasis.Create(transform.Orientation);
            provider.Translation = transform.Position;
            provider = EnbRetailShipBankTransform.Apply(provider, state.Bank.Angle);
            obj.ShipVisualBankRadians=state.Bank.Angle;
            var pose=obj.Pose;
            obj.Pose=pose with {Sample=pose.Sample with {Orientation=visual,ProviderTransform=provider}};
            renderPoses.Write(obj);
        }
        foreach (ulong id in _states.Keys.Where(id => !_seen.Contains(id)).ToArray()) _states.Remove(id);
        return scene;
    }

    private sealed class State(byte objectType)
    {
        public uint Time;
        public EnbShipVisualBank Bank { get; } = new(objectType);
    }
}

/// <summary>006F3FA0, evaluated on a fixed 60 Hz presentation
/// reference with fractional interpolation. The reference rate is a modern
/// presentation choice, not a recovered retail constant: it avoids multiplying
/// retail's per-update bank speed by monitor/UI callback frequency.</summary>
public sealed class EnbShipVisualBank(byte objectType=1)
{
    public const double ReferenceStepMilliseconds = 1000.0 / 60;
    private double _remainder;
    private float _stepAngle;
    private float _stepIncrement;
    public float Angle { get; private set; }
    public float Increment { get; private set; }

    public void Advance(float target, bool isLocalShip, double elapsedMilliseconds)
    {
        if (!float.IsFinite(target) || !double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        if (elapsedMilliseconds == 0) return;
        _remainder += elapsedMilliseconds;
        // Repeating 1000/Hz divisions must not leave a whole old-input tick
        // pending solely due to double rounding at a reference boundary.
        while (_remainder + 1e-9 >= ReferenceStepMilliseconds)
        {
            float before = _stepAngle;
            Step(target, isLocalShip, ref _stepAngle, ref _stepIncrement);
            _remainder = Math.Max(0, _remainder - ReferenceStepMilliseconds);
            if (before == _stepAngle && _stepIncrement == 0)
            {
                // A long pause at rest needs no unbounded catch-up loop.
                _remainder %= ReferenceStepMilliseconds;
                break;
            }
        }
        float nextAngle = _stepAngle, nextIncrement = _stepIncrement;
        Step(target, isLocalShip, ref nextAngle, ref nextIncrement);
        float alpha = (float)(_remainder / ReferenceStepMilliseconds);
        Angle = _stepAngle + (nextAngle - _stepAngle) * alpha;
        Increment = _stepIncrement + (nextIncrement - _stepIncrement) * alpha;
    }

    private void Step(float target, bool isLocalShip, ref float angle, ref float increment)
        =>EnbRetailShipBankStep.Advance(objectType,isLocalShip,target,(float)ReferenceStepMilliseconds,ref angle,ref increment);
}
