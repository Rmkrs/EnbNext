using System.Numerics;
using EnbNext.Rendering;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Net7.World;

public readonly record struct Net7SpatialSample(
    uint ClientTime,
    Vector3 Position,
    Quaternion Orientation,
    Vector3 Velocity,
    float Speed,
    Vector3 Rotation)
{
    /// <summary>
    /// Exact sampled transform column 0 when the spatial provider owns a basis
    /// directly. Movement compares these raw float bits rather than reconstructing
    /// them from a quaternion after the fact.
    /// </summary>
    public Vector3 OrientationColumn0 { get; init; }
    public Matrix4x4? ProviderTransform { get; init; }
    public float BankHeadingTarget { get; init; }
}

internal interface INet7SpatialModel
{
    // Spatial +18 defaults to zero (70F2D0). Keyframes and transitions
    // select retained command state without sampling/advancing another pose.
    float BankHeadingTarget => 0;
    INet7SpatialModel Resolve(uint clientTime) => this;
    bool TrySample(Net7WorldState world, uint clientTime, Net7SpatialSamplingContext context, out Net7SpatialSample sample);
    bool TrySampleVelocity(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 velocity)
    {
        bool success=TrySample(world,time,context,out var sample);velocity=sample.Velocity;return success;
    }
    bool TrySampleRotation(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 rotation)
    {
        bool success=TrySample(world,time,context,out var sample);rotation=sample.Rotation;return success;
    }
}

internal sealed class Net7SpatialSamplingContext(uint? currentClientTime=null, bool prediction=true)
{
    public uint? CurrentClientTime {get;private set;}=currentClientTime;
    internal void BeginFrame(uint time)
    {
        if(Depth!=0)throw new InvalidOperationException("A spatial sample is still active.");
        CurrentClientTime=time;
    }
    public bool Prediction {get;}=prediction;
    // 00714410 follows parent providers without a fixed graph-depth limit.
    // Retain cycle detection separately from admission of acyclic chains.

    private readonly HashSet<uint> _activeObjects = [];

    public int Depth => _activeObjects.Count;

    public bool TryEnter(uint gameId)
    {
        if (!_activeObjects.Add(gameId))
            return false;
        return true;
    }

    public void Exit(uint gameId) => _activeObjects.Remove(gameId);

    public bool Contains(uint gameId) => _activeObjects.Contains(gameId);
}

internal static class Net7SpatialTime
{
    public static int SignedDelta(uint later, uint earlier) => unchecked((int)(later - earlier));

    public static bool AtOrAfter(uint value, uint boundary) => SignedDelta(value, boundary) >= 0;

    public static uint Add(uint value, uint milliseconds) => unchecked(value + milliseconds);
}

internal static class Net7SpatialMath
{
    public const float PhysicsEpsilon = 0.000001f;
}

internal sealed class Net7FixedSpatialModel:INet7SpatialModel
{
    private readonly Matrix4x4 _transform;
    private readonly Quaternion _orientation;
    public Net7FixedSpatialModel(Vector3 position,Quaternion orientation,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        //714B50/714D50 retain8C2550's matrix;714C00 returns it verbatim.
        _transform=EnbRetailCameraQuaternionBasis.Create(orientation,precision);
        _transform.Translation=position;
        _orientation=EnbRetailQuaternionConversions.ExtractOrientation(_transform,precision);
    }
    public bool TrySample(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Net7SpatialSample sample)
    {
        sample=new(time,_transform.Translation,_orientation,Vector3.Zero,0,Vector3.Zero)
        {OrientationColumn0=new(_transform.M11,_transform.M12,_transform.M13),ProviderTransform=_transform};
        return true;
    }
}

internal sealed class Net7SimpleSpatialModel(
    uint stateTime,
    Vector3 position,
    Quaternion orientation,
    Vector3 velocity,
    EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24) : INet7SpatialModel
{
    private readonly Quaternion _orientation = orientation;

    public bool TrySample(Net7WorldState world, uint clientTime, Net7SpatialSamplingContext context, out Net7SpatialSample sample)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R elapsed=(R)(double)Net7SpatialTime.SignedDelta(clientTime,stateTime);
        //70F3BD: FILD retains signed32; X/Y products spill, Z product does not.
        float x=(float)(elapsed*velocity.X),y=(float)(elapsed*velocity.Y);
        Vector3 sampledPosition=new((float)((R)x+position.X),(float)((R)y+position.Y),(float)(elapsed*velocity.Z+position.Z));
        sample = new Net7SpatialSample(
            clientTime,
            sampledPosition,
            _orientation,
            velocity,
            (float)R.Sqrt((R)(float)((R)velocity.X*velocity.X+(R)velocity.Y*velocity.Y+(R)velocity.Z*velocity.Z)),
            Vector3.Zero)
        {
            OrientationColumn0 = Net7Basis3.FromQuaternion(_orientation).Column0,
        };
        return true;
    }
}

internal sealed class Net7TransitionSpatialModel(
    INet7SpatialModel start,
    INet7SpatialModel end,
    uint startTime,
    uint durationMilliseconds) : INet7SpatialModel
{
    public float BankHeadingTarget => end.BankHeadingTarget;
    private readonly uint _endTime = Net7SpatialTime.Add(startTime, durationMilliseconds);
    public string IntervalAudit => $"evolving[{startTime}..{_endTime}]";
    public INet7SpatialModel Resolve(uint time)
    {
        if(time>=_endTime)return end;
        start=start.Resolve(time);
        return this;
    }

    public bool TrySampleVelocity(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 velocity)
    {
        if(time>=_endTime)return end.TrySampleVelocity(world,time,context,out velocity);
        if(time<=startTime)return start.TrySampleVelocity(world,time,context,out velocity);
        if(!start.TrySampleVelocity(world,time,context,out var first)||!end.TrySampleVelocity(world,time,context,out var last))
        {velocity=default;return false;}
        velocity=Net7RetailEndpointInterpolation.Rotation(first,last,time,startTime,_endTime);return true;
    }

    public bool TrySampleRotation(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 rotation)
    {
        if(time>=_endTime)return end.TrySampleRotation(world,time,context,out rotation);
        if(time<=startTime)return start.TrySampleRotation(world,time,context,out rotation);
        if(!start.TrySampleRotation(world,time,context,out var first)||!end.TrySampleRotation(world,time,context,out var last))
        {rotation=default;return false;}
        rotation=Net7RetailEndpointInterpolation.Rotation(first,last,time,startTime,_endTime);return true;
    }

    public bool TrySample(Net7WorldState world, uint clientTime, Net7SpatialSamplingContext context, out Net7SpatialSample sample)
    {
        // The common retail transition uses raw unsigned time-boundary
        // comparisons. This is intentionally different from the simple state's
        // signed32 extrapolation delta. Around uint wrap, the transition therefore
        // selects start/end rather than treating the wrapped interval as a modern
        // modular interpolation window.
        // The controller resolves expired transitions before invoking712780.
        // Keep that collapse ahead of the sampler's start-boundary branch.
        if (clientTime >= _endTime)
            return end.TrySample(world, clientTime, context, out sample);

        if (clientTime <= startTime)
            return start.TrySample(world, clientTime, context, out sample);

        if (!start.TrySample(world, clientTime, context, out Net7SpatialSample startSample)
            || !end.TrySample(world, clientTime, context, out Net7SpatialSample endSample))
        {
            sample = default;
            return false;
        }

        sample = Net7RetailEndpointInterpolation.Sample(startSample,endSample,clientTime,startTime,_endTime,evolving:true);
        return true;
    }
}

// Retail 00712DB0/00712FA0 captures the two boundary samples. Blending
// trajectories at the current time instead can repeatedly brake a follower
// while the next remote packet still lies ahead of the presentation clock.
internal sealed class Net7EndpointTransitionSpatialModel(
    INet7SpatialModel start, INet7SpatialModel end,
    uint startTime, uint endTime,
    Net7SpatialSample startSample, Net7SpatialSample endSample) : INet7SpatialModel
{
    public float BankHeadingTarget => end.BankHeadingTarget;
    public string IntervalAudit => $"endpoints[{startTime}..{endTime}]/{startSample.Position}>{endSample.Position}";
    public INet7SpatialModel Resolve(uint time)
    {
        if(time>=endTime)return end;
        start=start.Resolve(time);
        return this;
    }
    public bool TrySampleVelocity(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 velocity)
    {
        if(time<=startTime)return start.TrySampleVelocity(world,time,context,out velocity);
        if(time>=endTime)return end.TrySampleVelocity(world,time,context,out velocity);
        velocity=Net7RetailEndpointInterpolation.Rotation(startSample.Velocity,endSample.Velocity,time,startTime,endTime);return true;
    }
    public bool TrySampleRotation(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 rotation)
    {
        if(time<=startTime)return start.TrySampleRotation(world,time,context,out rotation);
        if(time>=endTime)return end.TrySampleRotation(world,time,context,out rotation);
        rotation=Net7RetailEndpointInterpolation.Rotation(startSample.Rotation,endSample.Rotation,time,startTime,endTime);return true;
    }
    public bool TrySample(Net7WorldState world, uint clientTime, Net7SpatialSamplingContext context, out Net7SpatialSample sample)
    {
        if (clientTime <= startTime)
            return start.TrySample(world, clientTime, context, out sample);
        if (clientTime >= endTime)
            return end.TrySample(world, clientTime, context, out sample);
        sample = Net7RetailEndpointInterpolation.Sample(startSample,endSample,clientTime,startTime,endTime);
        return true;
    }
}

internal readonly struct Net7Basis3
{
    public Net7Basis3(
        float m00, float m01, float m02,
        float m10, float m11, float m12,
        float m20, float m21, float m22)
    {
        M00 = m00; M01 = m01; M02 = m02;
        M10 = m10; M11 = m11; M12 = m12;
        M20 = m20; M21 = m21; M22 = m22;
    }

    public float M00 { get; }
    public float M01 { get; }
    public float M02 { get; }
    public float M10 { get; }
    public float M11 { get; }
    public float M12 { get; }
    public float M20 { get; }
    public float M21 { get; }
    public float M22 { get; }

    public Vector3 Column0 => new(M00, M10, M20);
    public Vector3 Column1 => new(M01, M11, M21);
    public Vector3 Column2 => new(M02, M12, M22);

    public static Net7Basis3 Identity => new(
        1, 0, 0,
        0, 1, 0,
        0, 0, 1);

    public static Net7Basis3 FromQuaternion(Quaternion value)
    {
        Quaternion q = value;
        float xx = q.X * q.X;
        float yy = q.Y * q.Y;
        float zz = q.Z * q.Z;
        float xy = q.X * q.Y;
        float xz = q.X * q.Z;
        float yz = q.Y * q.Z;
        float xw = q.X * q.W;
        float yw = q.Y * q.W;
        float zw = q.Z * q.W;
        return new Net7Basis3(
            1f - (2f * (yy + zz)), 2f * (xy - zw), 2f * (xz + yw),
            2f * (xy + zw), 1f - (2f * (xx + zz)), 2f * (yz - xw),
            2f * (xz - yw), 2f * (yz + xw), 1f - (2f * (xx + yy)));
    }

    public Vector3 TransformLocalVector(Vector3 local)
        => (Column0 * local.X) + (Column1 * local.Y) + (Column2 * local.Z);

    public Net7Basis3 RotateRows01(float angle)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        //710BBB..710C39 retains FSIN, but spills FCOS to float.
        R s = R.Sine((R)angle);
        R c = (float)R.Cosine((R)angle);
        return new Net7Basis3(
            (float)((M00 * c) - (M10 * s)), (float)((M01 * c) - (M11 * s)), (float)((M02 * c) - (M12 * s)),
            (float)((M10 * c) + (M00 * s)), (float)((M11 * c) + (M01 * s)), (float)((M12 * c) + (M02 * s)),
            M20, M21, M22);
    }

    public Net7Basis3 RotateColumns12(float angle)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        //710C68..710CD4 spills FSIN and retains FCOS.
        R s = (float)R.Sine((R)angle);
        R c = R.Cosine((R)angle);
        return new Net7Basis3(
            M00, (float)((M02 * s) + (M01 * c)), (float)((M02 * c) - (M01 * s)),
            M10, (float)((M12 * s) + (M11 * c)), (float)((M12 * c) - (M11 * s)),
            M20, (float)((M22 * s) + (M21 * c)), (float)((M22 * c) - (M21 * s)));
    }

    public Net7Basis3 RotateColumns02(float angle)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        //710D13..710D7F and the tilt branch use the same retained FCOS.
        R s = (float)R.Sine((R)angle);
        R c = R.Cosine((R)angle);
        return new Net7Basis3(
            (float)((M00 * c) - (M02 * s)), M01, (float)((M00 * s) + (M02 * c)),
            (float)((M10 * c) - (M12 * s)), M11, (float)((M10 * s) + (M12 * c)),
            (float)((M20 * c) - (M22 * s)), M21, (float)((M20 * s) + (M22 * c)));
    }
}

internal sealed class Net7AdvancedKeyframeSpatialModel : INet7SpatialModel
{
    //70FFE0 selects cached lower keyframe+8 before primary+4;711980 reads+54.
    public float BankHeadingTarget { get { lock(_sampleCacheGate)return (_sampleCache??_initial).TurnTarget; } }
    private const float AccelerationDifferenceEpsilon = 0.0001f;
    private const float MaximumTilt = 1.2217304706573486f;
    private static readonly float DriftDamping = MathF.Pow(0.5f, 0.1f);
    private static readonly float RotationalDampingSquared = DriftDamping * DriftDamping;

    private readonly Keyframe _initial;
    private readonly object _sampleCacheGate = new();
    private Keyframe? _sampleCache;
    private Keyframe? _upperSampleCache;
    private Keyframe? _primaryUpperCache;

    public Net7AdvancedKeyframeSpatialModel(Net7AdvancedPositionUpdate update, uint clientTimestamp)
    {
        ArgumentNullException.ThrowIfNull(update);
        _initial = Keyframe.FromUpdate(update, clientTimestamp);
    }

    private Net7AdvancedKeyframeSpatialModel(Keyframe initial) => _initial = initial;

    public Net7AdvancedKeyframeSpatialModel CloneModel() => new(_initial.Clone());

    // 710260 clones the primary command keyframe, never the prediction cache.
    // 7102B0/710410 then advance and edit that clone. Units here are per ms.
    internal Net7AdvancedKeyframeSpatialModel WithControls(uint time, float? turn, float? tilt,
        float? targetSpeed, float? acceleration, float? autoLevelRate)
    {
        uint lowerTime = unchecked(time - unchecked(time - _initial.Timestamp) % 100u);
        Keyframe frame;
        lock (_sampleCacheGate)
            frame = _initial.Clone();
        AdvanceOrRewind(frame, lowerTime);
        if (targetSpeed is float speed && acceleration is float accel)
        {
            frame.TargetSpeed = speed * 100f;
            float difference = frame.TargetSpeed - frame.CurrentSpeed;
            frame.AccelerationActive = MathF.Abs(difference) >= 0.0001f;
            frame.AccelerationIncrement = frame.AccelerationActive ? (difference < 0 ? -accel : accel) * 10000f : 0;
        }
        if (turn is float yaw)
        {
            frame.TurnStep = yaw * 100f;
            frame.TurnTarget = frame.TurnStep < 0 ? -1000f : 1000f;
            frame.TurnActive = true;
        }
        if (tilt is float pitch)
        {
            frame.TiltStep = pitch * 100f;
            frame.TiltTarget = frame.TiltStep < 0 ? -MaximumTilt : MaximumTilt;
            frame.TiltActive = true;
        }
        if (autoLevelRate is float level)
        {
            frame.TiltStep = (frame.TiltCurrent < 0 ? level : -level) * 100f;
            frame.TiltTarget = 0;
            frame.TiltActive = true;
        }
        return new(frame);
    }

    public bool TrySample(Net7WorldState world, uint clientTime, Net7SpatialSamplingContext context, out Net7SpatialSample sample)
    {
        lock (_sampleCacheGate)
        {
            //70F630 retains both prediction keyframes. An earlier query replaces
            //the lower clone; the upper clone survives until its timestamp differs.
            if(context.Prediction && (_sampleCache is null || _sampleCache.Timestamp>clientTime))
                _sampleCache=_initial.Clone();
            var lower=context.Prediction ? _sampleCache! : _initial;
            uint remainder=unchecked(clientTime-lower.Timestamp)%100u;
            uint lowerTime=unchecked(clientTime-remainder);
            AdvanceOrRewind(lower, lowerTime);
            if (remainder == 0)
            {
                var raw=lower.ToRawSample(clientTime);
                sample=Net7RetailKeyframeInterpolation.Sample(raw,raw,clientTime,0);
                return true;
            }
            uint upperTime=unchecked(lowerTime+100u);
            ref Keyframe? upper = ref (context.Prediction ? ref _upperSampleCache : ref _primaryUpperCache);
            if(upper is null || upper.Timestamp!=upperTime)
            {
                upper=lower.Clone();
                AdvanceOrRewind(upper,upperTime);
            }
            //Construction precedes getters: basis repair must not retroactively
            //change the upper keyframe already used by another fractional query.
            Net7SpatialSample lowerSample=lower.ToRawSample(lowerTime);
            Net7SpatialSample upperSample=upper.ToRawSample(upperTime);
            sample=Net7RetailKeyframeInterpolation.Sample(lowerSample,upperSample,clientTime,remainder);
            return true;
        }
    }

    private static void AdvanceOrRewind(Keyframe frame, uint targetBoundary)
    {
        // AdvanceSpatialKeyframeToTime @ 0x00711520 uses raw uint ordering.
        // Its backwards path is deliberately narrow: stationary/accelerating/
        // drifting keyframes retain the packet transform rather than inventing
        // a rewind. This matters for zero-speed remote structures that still
        // carry angular controls while the kind-8 provider samples in the past.
        if (targetBoundary < frame.Timestamp)
        {
            // 00711546..: rewind only when drift^2 <= 1e-6 (00AE7928), |acceleration|
            // <= 1e-6 and both speeds >= 1e-6 (00AE7950); written in that positive
            // form so a NaN operand also keeps the packet transform, as the x87
            // compares do.
            if (!(frame.Drift.LengthSquared() <= Net7SpatialMath.PhysicsEpsilon &&
                  MathF.Abs(frame.AccelerationIncrement) <= Net7SpatialMath.PhysicsEpsilon &&
                  Net7SpatialMath.PhysicsEpsilon <= MathF.Abs(frame.CurrentSpeed) &&
                  Net7SpatialMath.PhysicsEpsilon <= MathF.Abs(frame.TargetSpeed)))
            {
                return;
            }

            while (targetBoundary < frame.Timestamp)
                frame.RewindOneTick();
        }
        else if (frame.Timestamp < targetBoundary)
        {
            do
            {
                frame.AdvanceOneTick();
            }
            while (frame.Timestamp < targetBoundary);
        }
    }

    private sealed class Keyframe
    {
        public uint Timestamp;
        public Net7Basis3 Basis;
        public Vector3 Position;
        public float CurrentSpeed;
        public float TargetSpeed;
        public bool AccelerationActive;
        public float AccelerationIncrement;
        public Vector3 Drift;
        public bool DriftActive;
        public float DriftFactor;
        public bool TurnActive;
        public float TurnCurrent;
        public float TurnStep;
        public float TurnTarget;
        public bool TurnDampingActive;
        public float TurnDampingValue;
        public float TurnDampingFactor;
        public bool BankActive;
        public float BankValue;
        public float BankDamping;
        public bool YawActive;
        public float YawValue;
        public float YawDamping;
        public bool TiltActive;
        public float TiltCurrent;
        public float TiltStep;
        public float TiltTarget;

        public static Keyframe FromUpdate(Net7AdvancedPositionUpdate update, uint timestamp)
        {
            //710530 constructs the stored matrix through8D2C10, not8C25C0.
            var matrix=EnbNext.Rendering.EnbRetailQuaternionConversions.QuaternionBasis(update.Orientation);
            Net7Basis3 basis=new(matrix.M11,matrix.M21,matrix.M31,matrix.M12,matrix.M22,matrix.M32,matrix.M13,matrix.M23,matrix.M33);
            float currentSpeed = update.CurrentSpeed * 100f;
            float targetSpeed = update.SetSpeed * 100f;
            float speedDifference = targetSpeed - currentSpeed;
            //7105E0..710609 compares the scaled per-tick speed difference.
            bool accelerationActive = MathF.Abs(speedDifference) >= AccelerationDifferenceEpsilon;
            float accelerationIncrement = 0f;
            if (accelerationActive)
            {
                float increment = update.Acceleration * 10000f;
                accelerationIncrement = speedDifference < 0f ? -increment : increment;
            }

            Vector3 drift = update.ImpartedVelocity * 100f;
            bool driftActive = MathF.Abs(drift.X) >= Net7SpatialMath.PhysicsEpsilon
                || MathF.Abs(drift.Y) >= Net7SpatialMath.PhysicsEpsilon
                || MathF.Abs(drift.Z) >= Net7SpatialMath.PhysicsEpsilon;

            float tiltCurrent = MathF.Acos(Math.Clamp(basis.M22, -1f, 1f));
            if (basis.M20 > 0f)
                tiltCurrent = -tiltCurrent;
            float tiltStep = update.RotY * 100f;
            float tiltTarget = update.DesiredY;
            if (MathF.Abs(tiltTarget) > MathF.PI)
                tiltTarget = tiltStep >= 0f ? MathF.PI : -MathF.PI;

            float turnStep = update.RotZ * 100f;
            float turnCurrent = MathF.Atan2(basis.M10, basis.M00);
            float turnTarget = update.DesiredZ;
            if (MathF.Abs(turnTarget) > (3f * MathF.PI))
                turnTarget = turnStep < 0f ? -MathF.Abs(turnTarget) : MathF.Abs(turnTarget);

            float spin = update.ImpartedSpin * 100f;
            float roll = update.ImpartedRoll * 100f;
            float pitch = update.ImpartedPitch * 100f;
            //7106FC..7107AA copies the retained drift factor into the impulse
            //damping fields. Without drift it is zero, even for active impulses.
            float impulseDamping = driftActive ? DriftDamping : 0f;
            float squaredImpulseDamping = driftActive ? RotationalDampingSquared : 0f;

            return new Keyframe
            {
                Timestamp = timestamp,
                Basis = basis,
                Position = update.Position,
                CurrentSpeed = currentSpeed,
                TargetSpeed = targetSpeed,
                AccelerationActive = accelerationActive,
                AccelerationIncrement = accelerationIncrement,
                Drift = drift,
                DriftActive = driftActive,
                DriftFactor = driftActive ? DriftDamping : 0f,
                TurnCurrent = turnCurrent,
                TurnStep = turnStep,
                TurnTarget = turnTarget,
                TurnActive = MathF.Abs(turnStep) > Net7SpatialMath.PhysicsEpsilon,
                TurnDampingActive = MathF.Abs(spin) > Net7SpatialMath.PhysicsEpsilon,
                TurnDampingValue = spin,
                TurnDampingFactor = MathF.Abs(spin) > Net7SpatialMath.PhysicsEpsilon ? impulseDamping : 0f,
                BankActive = MathF.Abs(roll) > Net7SpatialMath.PhysicsEpsilon,
                BankValue = roll,
                BankDamping = MathF.Abs(roll) > Net7SpatialMath.PhysicsEpsilon ? squaredImpulseDamping : 0f,
                YawActive = MathF.Abs(pitch) > Net7SpatialMath.PhysicsEpsilon,
                YawValue = pitch,
                YawDamping = MathF.Abs(pitch) > Net7SpatialMath.PhysicsEpsilon ? squaredImpulseDamping : 0f,
                TiltCurrent = tiltCurrent,
                TiltStep = tiltStep,
                TiltTarget = tiltTarget,
                TiltActive = MathF.Abs(tiltStep) > Net7SpatialMath.PhysicsEpsilon,
            };
        }

        public Keyframe Clone() => (Keyframe)MemberwiseClone();

        public Net7SpatialSample ToRawSample(uint clientTime)
        {
            Matrix4x4 input=new(Basis.M00,Basis.M10,Basis.M20,0,Basis.M01,Basis.M11,Basis.M21,0,
                Basis.M02,Basis.M12,Basis.M22,0,Position.X,Position.Y,Position.Z,1);
            var sample=Net7RetailKeyframeSample.Raw(input,CurrentSpeed,Drift,TiltStep,TurnStep,clientTime,out var repaired);
            Basis=new(repaired.M11,repaired.M21,repaired.M31,repaired.M12,repaired.M22,repaired.M32,repaired.M13,repaired.M23,repaired.M33);
            return sample;
        }

        public void AdvanceOneTick()
        {
            Position += Basis.Column0 * CurrentSpeed;

            if (AccelerationActive)
            {
                float speed = CurrentSpeed + AccelerationIncrement;
                if (AccelerationIncrement <= 0f)
                {
                    if (speed <= TargetSpeed)
                        speed = TargetSpeed;
                }
                else if (speed >= TargetSpeed)
                {
                    speed = TargetSpeed;
                }
                CurrentSpeed = speed;
            }

            if (DriftActive)
            {
                Drift *= DriftFactor;
                if (MathF.Abs(Drift.X) >= Net7SpatialMath.PhysicsEpsilon
                    || MathF.Abs(Drift.Y) >= Net7SpatialMath.PhysicsEpsilon
                    || MathF.Abs(Drift.Z) >= Net7SpatialMath.PhysicsEpsilon)
                {
                    Position += Drift;
                }
                else
                {
                    DriftActive = false;
                }
            }

            AdvanceTurn();
            AdvanceBank();
            AdvanceYaw();
            AdvanceTilt();
            Timestamp = unchecked(Timestamp + 100u);
        }

        private void AdvanceTurn()
        {
            if (TurnActive)
            {
                float remaining = TurnTarget - TurnCurrent;
                if (MathF.Abs(remaining) < MathF.Abs(TurnStep))
                    TurnStep = remaining;
                if (MathF.Abs(TurnStep) < Net7SpatialMath.PhysicsEpsilon)
                    TurnActive = false;
            }

            if (TurnDampingActive)
            {
                TurnDampingValue *= TurnDampingFactor;
                if (MathF.Abs(TurnDampingValue) < Net7SpatialMath.PhysicsEpsilon)
                    TurnDampingActive = false;
            }

            if (TurnActive || TurnDampingActive)
            {
                float angle = TurnDampingValue + TurnStep;
                Basis = Basis.RotateRows01(angle);
                TurnCurrent += angle;
            }
        }

        private void AdvanceBank()
        {
            if (!BankActive)
                return;
            BankValue = (BankDamping * BankValue) - (Basis.M21 * 0.00048828125f);
            Basis = Basis.RotateColumns12(BankValue);
            if (MathF.Abs(BankValue) < Net7SpatialMath.PhysicsEpsilon)
                BankActive = false;
        }

        private void AdvanceYaw()
        {
            if (!YawActive)
                return;
            YawValue *= YawDamping;
            Basis = Basis.RotateColumns02(YawValue);
            if (MathF.Abs(YawValue) < Net7SpatialMath.PhysicsEpsilon)
                YawActive = false;
        }

        private void AdvanceTilt()
        {
            if (!TiltActive)
                return;

            float remaining = TiltTarget - TiltCurrent;
            if ((remaining * TiltStep) < 0f || MathF.Abs(remaining) < Net7SpatialMath.PhysicsEpsilon)
                TiltStep = 0f;
            else if (MathF.Abs(remaining) < MathF.Abs(TiltStep))
                TiltStep = remaining;

            if (MathF.Abs(TiltCurrent) <= MaximumTilt)
            {
                float lower = -MaximumTilt - TiltCurrent;
                float upper = MaximumTilt - TiltCurrent;
                TiltStep = Math.Clamp(TiltStep, lower, upper);
            }
            else if ((TiltCurrent * TiltStep) > 0f)
            {
                TiltStep = 0f;
            }

            if (MathF.Abs(TiltStep) > Net7SpatialMath.PhysicsEpsilon)
            {
                Basis = Basis.RotateColumns02(TiltStep);
                TiltCurrent += TiltStep;
            }
            else
            {
                TiltActive = false;
            }
        }

        public void RewindOneTick()
        {
            Timestamp = unchecked(Timestamp - 100u);

            if (TiltActive)
            {
                Basis = Basis.RotateColumns02(-TiltStep);
                TiltCurrent -= TiltStep;
            }

            if (BankValue != 0f)
            {
                Basis = Basis.RotateColumns12(-BankValue);
                if (BankDamping != 0f)
                    BankValue = ((Basis.M21 * 0.00048828125f) + BankValue) / BankDamping;
            }

            if (YawValue != 0f)
            {
                Basis = Basis.RotateColumns02(-YawValue);
                if (YawDamping != 0f)
                    YawValue = (YawValue - (Basis.M20 * 0.00048828125f)) / YawDamping;
            }

            if (TurnActive || TurnDampingActive)
            {
                float angle = TurnDampingValue + TurnStep;
                Basis = Basis.RotateRows01(-angle);
                TurnCurrent -= angle;
                if (TurnDampingActive && TurnDampingFactor != 0f)
                    TurnDampingValue /= TurnDampingFactor;
            }

            if (DriftActive && DriftFactor != 0f)
            {
                Position -= Drift;
                Drift /= DriftFactor;
            }

            if (AccelerationActive)
                CurrentSpeed -= AccelerationIncrement;

            Position -= Basis.Column0 * CurrentSpeed;
        }
    }
}

internal readonly record struct Net7RemoteLagState(
    uint TransitionStartClientTime,
    int StartOffsetMilliseconds,
    int TargetOffsetMilliseconds,
    uint TransitionEndClientTime)
{
    public static Net7RemoteLagState CreateInitial(uint currentClientTime, uint updatePeriod)
    {
        int target = checked((int)Math.Min(updatePeriod, (uint)int.MaxValue));
        uint duration = checked((uint)Math.Min((ulong)updatePeriod * 2UL, uint.MaxValue));
        return new Net7RemoteLagState(currentClientTime, 0, target, Net7SpatialTime.Add(currentClientTime, duration));
    }

    public int GetEffectiveOffset(uint currentClientTime)
    {
        if (Net7SpatialTime.SignedDelta(currentClientTime, TransitionStartClientTime) <= 0)
            return StartOffsetMilliseconds;
        if (Net7SpatialTime.AtOrAfter(currentClientTime, TransitionEndClientTime))
            return TargetOffsetMilliseconds;

        int elapsed = Net7SpatialTime.SignedDelta(currentClientTime, TransitionStartClientTime);
        int duration = Net7SpatialTime.SignedDelta(TransitionEndClientTime, TransitionStartClientTime);
        if (duration <= 0)
            return TargetOffsetMilliseconds;
        long delta = (long)TargetOffsetMilliseconds - StartOffsetMilliseconds;
        return StartOffsetMilliseconds + (int)((delta * elapsed) / duration);
    }

    public Net7RemoteLagState Retarget(uint currentClientTime, uint newUpdatePeriod)
    {
        int target = checked((int)Math.Min(newUpdatePeriod, (uint)int.MaxValue));
        if (target == TargetOffsetMilliseconds)
            return this;
        int current = GetEffectiveOffset(currentClientTime);
        uint duration = (uint)Math.Min(2L * Math.Abs((long)target - current), uint.MaxValue);
        return new Net7RemoteLagState(currentClientTime, current, target, Net7SpatialTime.Add(currentClientTime, duration));
    }

    public Net7RemoteLagState AttachToFormation(uint currentClientTime)
    {
        // 00716220 drains the current delay to zero over that delay's duration.
        int current=GetEffectiveOffset(currentClientTime);
        return new(currentClientTime,current,0,Net7SpatialTime.Add(currentClientTime,(uint)Math.Max(0,current)));
    }
}

internal sealed class Net7RemoteAdvancedSpatialModel(
    INet7SpatialModel physical,
    Net7RemoteLagState lag) : INet7SpatialModel
{
    public INet7SpatialModel Physical { get; private set; } = physical;
    public float BankHeadingTarget => Physical.BankHeadingTarget;
    public Net7RemoteLagState Lag { get; } = lag;
    public INet7SpatialModel Resolve(uint time)
    {
        //716CA0 resolves the owned provider after subtracting its time offset.
        Physical=Physical.Resolve(unchecked(time-(uint)Lag.GetEffectiveOffset(time)));
        return this;
    }

    public int GetEffectiveOffset(uint currentClientTime) => Lag.GetEffectiveOffset(currentClientTime);

    public bool TrySamplePhysical(Net7WorldState world, uint physicalTime, Net7SpatialSamplingContext context, out Net7SpatialSample sample)
        => Physical.TrySample(world, physicalTime, context, out sample);

    public Net7RemoteAdvancedSpatialModel WithPhysical(INet7SpatialModel newPhysical)
        => new(newPhysical, Lag);

    public Net7RemoteAdvancedSpatialModel WithFormation(INet7SpatialModel newPhysical,uint currentClientTime)
        => new(newPhysical,Lag.AttachToFormation(currentClientTime));

    public Net7RemoteAdvancedSpatialModel WithPhysicalAndRetarget(
        INet7SpatialModel newPhysical,
        uint currentClientTime,
        uint updatePeriod)
        => new(newPhysical, Lag.Retarget(currentClientTime, updatePeriod));

    public bool TrySampleVelocity(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 velocity)
        =>Physical.TrySampleVelocity(world,unchecked(time-(uint)Lag.GetEffectiveOffset(time)),context,out velocity);
    public bool TrySampleRotation(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 rotation)
        =>Physical.TrySampleRotation(world,unchecked(time-(uint)Lag.GetEffectiveOffset(time)),context,out rotation);

    public bool TrySample(Net7WorldState world, uint clientTime, Net7SpatialSamplingContext context, out Net7SpatialSample sample)
    {
        int offset = Lag.GetEffectiveOffset(clientTime);
        uint physicalTime = unchecked(clientTime - (uint)offset);
        if (!Physical.TrySample(world, physicalTime, context, out Net7SpatialSample physicalSample))
        {
            sample = default;
            return false;
        }
        sample = physicalSample with { ClientTime = clientTime };
        return true;
    }
}

internal sealed class Net7ComponentSpatialModel : INet7SpatialModel
{
    private readonly EnbRetailLinePrecision _precision;
    private readonly uint _stateTime;
    private readonly Vector3 _position;
    private readonly Quaternion _orientation;
    private readonly Vector3 _velocityPerTick;
    private readonly float _decayPerTick;
    private readonly float _tractorSpeedPerTick;
    private readonly uint _tractorId;
    private readonly object _cacheGate=new();
    private ComponentTickState? _lowerCache;
    private ComponentTickState? _upperCache;
    private ComponentTickState? _primary;
    private ComponentTickState? _primaryUpper;

    public Net7ComponentSpatialModel(Net7ComponentPositionUpdate update, uint clientTimestamp,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        _precision=precision;
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        _stateTime = clientTimestamp;
        _position = update.Position;
        _orientation = EnbRetailQuaternionConversions.ExtractOrientation(EnbRetailQuaternionConversions.QuaternionBasis(update.Orientation,precision),precision);
        _velocityPerTick = new((float)((R)update.Velocity.X*.1f),(float)((R)update.Velocity.Y*.1f),(float)((R)update.Velocity.Z*.1f));
        _decayPerTick = (float)((R)update.ImpartedDecay * 0.1f);
        _tractorSpeedPerTick = (float)((R)update.TractorSpeed * 0.1f);
        _tractorId = update.TractorId;
    }

    public bool TrySample(Net7WorldState world, uint clientTime, Net7SpatialSamplingContext context, out Net7SpatialSample sample)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(_precision,true);
        lock(_cacheGate)
        {
            //70F630 uses the same retained lower/upper prediction slots as advanced
            //movement. Replaying old ticks would pull toward a newly moved target.
            _primary ??= new ComponentTickState(_position,_velocityPerTick,_stateTime);
            var lower=context.Prediction && _lowerCache is { } cached && cached.Timestamp<=clientTime
                ? cached : _primary.Value;
            uint remainder=unchecked(clientTime-lower.Timestamp)%100u;
            uint lowerTime=unchecked(clientTime-remainder);
            if(!AdvanceTo(world,context,ref lower,lowerTime)){sample=default;return false;}
            if(context.Prediction)_lowerCache=lower;else _primary=lower;
            ComponentTickState upper=lower;
            if(remainder!=0)
            {
                uint upperTime=unchecked(lowerTime+100u);
                ref ComponentTickState? upperSlot=ref(context.Prediction?ref _upperCache:ref _primaryUpper);
                upper=upperSlot is { } saved && saved.Timestamp==upperTime ? saved : lower;
                if(!AdvanceTo(world,context,ref upper,upperTime)){sample=default;return false;}
                upperSlot=upper;
            }
            if(!TryBuildSample(world,context,lower,lower.Timestamp,clientTime,out var first))
            {sample=default;return false;}
            if(remainder==0){sample=Net7RetailKeyframeInterpolation.Sample(first,first,clientTime,0,_precision);return true;}
            if(!TryBuildSample(world,context,upper,upper.Timestamp,clientTime,out var last))
            {sample=default;return false;}
            sample=Net7RetailKeyframeInterpolation.Sample(first,last,clientTime,remainder,_precision);
            return true;
        }
    }

    private bool AdvanceTo(Net7WorldState world,Net7SpatialSamplingContext context,ref ComponentTickState state,uint target)
    {
        //7100E0 compares unsigned timestamps and only advances; no rewind.
        while(state.Timestamp<target)
        {
            if(!AdvanceTick(world,context,ref state,state.Timestamp))return false;
            state.Timestamp=unchecked(state.Timestamp+100u);
        }
        return true;
    }
    private bool AdvanceTick(Net7WorldState world, Net7SpatialSamplingContext context, ref ComponentTickState state, uint tickTime)
    {
        if (!TryGetEffectiveVelocity(world, context, state, tickTime, out Vector3 effective))
            return false;
        state.Position=new((float)((R)state.Position.X+effective.X),(float)((R)state.Position.Y+effective.Y),(float)((R)state.Position.Z+effective.Z));
        state.Velocity=new((float)((R)state.Velocity.X*_decayPerTick),(float)((R)state.Velocity.Y*_decayPerTick),(float)((R)state.Velocity.Z*_decayPerTick));
        return true;
    }

    private bool TryBuildSample(
        Net7WorldState world,
        Net7SpatialSamplingContext context,
        ComponentTickState state,
        uint stateTime,
        uint clientTime,
        out Net7SpatialSample sample)
    {
        if (!TryGetEffectiveVelocity(world, context, state, stateTime, out Vector3 effective))
        {
            sample = default;
            return false;
        }
        sample = new Net7SpatialSample(
            clientTime,
            state.Position,
            _orientation,
            effective,
            (float)R.Sqrt((R)(float)((R)effective.X*effective.X+(R)effective.Y*effective.Y+(R)effective.Z*effective.Z)),
            Vector3.Zero)
        {
            OrientationColumn0 = Net7Basis3.FromQuaternion(_orientation).Column0,
        };
        return true;
    }

    private bool TryGetEffectiveVelocity(
        Net7WorldState world,
        Net7SpatialSamplingContext context,
        ComponentTickState state,
        uint tickTime,
        out Vector3 effective)
    {
        effective = state.Velocity;
        if (_tractorId == 0)
            return true;
        if (context.Contains(_tractorId))
            return false;

        Net7WorldObject? target = world.Objects.FindFirst(_tractorId);
        if (target?.Spatial?.Model is null)
            return true;
        if (!world.TrySampleObjectCore(_tractorId, tickTime, context, out Net7SpatialSample targetSample))
            return false;

        //7140E0 builds8C2780's view basis, then8C26C0 rotates (0,0,-speed).
        //Coincident targets take the retail basis branch; they do not suppress pull.
        var basis=EnbRetailViewBasis.BetweenPoints(state.Position,targetSample.Position);
        float z=-_tractorSpeedPerTick;
        float x=(float)((R)basis.M31*z+(R)basis.M21*0f+(R)basis.M11*0f);
        float y=(float)((R)basis.M32*z+(R)basis.M12*0f+(R)basis.M22*0f);
        float zz=(float)((R)basis.M33*z+(R)basis.M13*0f+(R)basis.M23*0f);
        effective=new((float)((R)x+state.Velocity.X),(float)((R)y+state.Velocity.Y),(float)((R)zz+state.Velocity.Z));
        return true;
    }

    private struct ComponentTickState(Vector3 position, Vector3 velocity,uint timestamp)
    {
        public Vector3 Position = position;
        public Vector3 Velocity = velocity;
        public uint Timestamp=timestamp;
    }
}

internal sealed class Net7ParentRelativeSpatialModel(uint leaderId, Vector3 offset,
    EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24) : INet7SpatialModel
{
    public bool TrySampleVelocity(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 velocity)
        =>TryLeaderMotion(world,time,context,false,out velocity);
    public bool TrySampleRotation(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 rotation)
        =>TryLeaderMotion(world,time,context,true,out rotation);
    private bool TryLeaderMotion(Net7WorldState world,uint time,Net7SpatialSamplingContext context,bool rotation,out Vector3 value)
    {
        value=Vector3.Zero;
        if(context.Contains(leaderId))return false;
        var model=world.Objects.FindFirst(leaderId)?.Spatial?.Model;
        if(model is null)return true;
        if(!context.TryEnter(leaderId))return false;
        try
        {
            uint current=context.CurrentClientTime??time;
            return rotation?model.TrySampleRotation(world,current,context,out value):model.TrySampleVelocity(world,current,context,out value);
        }
        finally{context.Exit(leaderId);}
    }

    public bool TrySample(Net7WorldState world, uint clientTime, Net7SpatialSamplingContext context, out Net7SpatialSample sample)
    {
        if (context.Contains(leaderId))
        {
            sample = default;
            return false;
        }

        Net7WorldObject? leader = world.Objects.FindFirst(leaderId);
        if (leader?.Spatial?.Model is null)
        {
            sample = new Net7SpatialSample(clientTime, Vector3.Zero, Quaternion.Identity, Vector3.Zero, 0f, Vector3.Zero) { OrientationColumn0 = Vector3.UnitX };
            return true;
        }

        if (!world.TrySampleObjectCore(leaderId, clientTime, context, out Net7SpatialSample anchor))
        {
            sample = default;
            return false;
        }

        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        //716CA0/8C2550: sampled leader quaternion becomes a transform via8C25C0.
        var basis=anchor.ProviderTransform??EnbRetailCameraQuaternionBasis.Create(anchor.Orientation,precision);
        float x=(float)((R)basis.M11*offset.X+(R)basis.M31*offset.Z+(R)basis.M21*offset.Y);
        float y=(float)((R)basis.M32*offset.Z+(R)basis.M22*offset.Y+(R)basis.M12*offset.X);
        float z=(float)((R)basis.M33*offset.Z+(R)basis.M23*offset.Y+(R)basis.M13*offset.X);
        //714410 queries velocity/rotation at SClient+10BC, independently of pose time.
        uint currentTime=context.CurrentClientTime??clientTime;
        if(!context.TryEnter(leaderId)){sample=default;return false;}
        Vector3 velocity,rotation;
        try
        {
            if(!leader.Spatial.Model.TrySampleVelocity(world,currentTime,context,out velocity)
                ||!leader.Spatial.Model.TrySampleRotation(world,currentTime,context,out rotation))
            {sample=default;return false;}
        }
        finally{context.Exit(leaderId);}
        sample = anchor with
        {
            ClientTime = clientTime,
            ProviderTransform=null, //Outer formation provider rebuilds from the sampled quaternion.
            Position = new((float)((R)anchor.Position.X+x),(float)((R)anchor.Position.Y+y),(float)((R)anchor.Position.Z+z)),
            Orientation=EnbRetailQuaternionConversions.ExtractOrientation(basis,precision),
            OrientationColumn0=new(basis.M11,basis.M12,basis.M13),
            Velocity=velocity,
            Speed=(float)((R)velocity.Z*velocity.Z+(R)velocity.Y*velocity.Y+(R)velocity.X*velocity.X),
            Rotation=rotation,
        };
        return true;
    }
}
