using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Animated aggregate visibility controls emission, not buffer lifetime.
/// 0097F390 pauses/resumes without clearing interval remainder; 0097E330 updates
/// poses even while stopped. World-space births retain their original transforms;
/// local-space buffers use CurrentWorld on every draw.</summary>
public sealed class EnbCurrentRetailAnimatedParticleRuntime
{
    public sealed class Emitter
    {
        public List<long> Births { get; } = [];
        public Dictionary<long, Matrix4x4> Worlds { get; } = [];
        public List<EnbCurrentRetailParticleBirth> WorldBirths { get; } = [];
        public EnbRetailParticleMotionHistory MotionHistory {get;}=new();
        public long EmittedCount { get; internal set; }
        public Matrix4x4 CurrentWorld { get; internal set; } = Matrix4x4.Identity;
        public long EmissionRun { get; internal set; }
        public bool IsEmitting => Visible && !Completed;
        public bool HasCompletedFiniteRun => Completed;
        // 0095E250 -> 0097E180 checks emitter +D8 (started), not +E4
        // (finite allowance exhausted). A completed, visible emitter still
        // anchors its current line run until stopped or its owner is removed.
        public bool HasLineEndpoint => Visible;
        internal uint Remainder;
        internal bool Visible;
        internal Matrix4x4 Previous = Matrix4x4.Identity;
        internal int Remaining;
        internal bool Completed;
        internal EnbRetailParticleRandomOwner? Random;
        internal EnbRetailParticleBufferRing? Slots;
    }

    public IReadOnlyList<Emitter> Emitters => _emitters;
    private Emitter[] _emitters = [];
    private uint? _lastAge;
    private int _processedSamples;
    public uint LastAge => _lastAge ?? 0;
    public EnbCurrentRetailAnimatedParticleRuntime() { }
    public EnbCurrentRetailAnimatedParticleRuntime(IEnumerable<Emitter> emitters) => _emitters=emitters.ToArray();
    internal void ObserveOwnerClock(uint age) => _lastAge=age;

    public void Advance(EnbCurrentRetailRObjEffectAsset asset, EnbCurrentRetailParticlePoseHistory history, float scale,
        IReadOnlyDictionary<uint,EnbCurrentRetailRslAnimationSequenceRuntime.Sample>? animationSamples=null)
    {
        var particles=asset.AggregateParticles!;
        if (_emitters.Length==0) _emitters=particles.Select(p=>CreateEmitter(p.Definition)).ToArray();
        for (; _processedSamples<history.Samples.Count; _processedSamples++)
        {
            var sample=history.Samples[_processedSamples];
            if (_lastAge.HasValue && sample.Age<=_lastAge.Value) continue;
            var animation=asset.Animation;
            float frame=0;
            string? error=null;
            if(animationSamples is not null)
            {
                if(!animationSamples.TryGetValue(sample.Age,out var observed))
                    throw new InvalidDataException("Particle pose has no matching animation observation");
                animation=observed.BoundAnimation;frame=observed.Frame;
            }
            else if (animation is not null && !EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(animation,asset.AnimationFlags!,0,sample.Age,out frame,out error))
                throw new InvalidDataException(error);
            //0098EF60 scales child data and stored HTree translations separately;
            //009895B0/00989670 copy the rigid owner matrix directly into pivot0.
            var world=Matrix4x4.CreateFromQuaternion(sample.Rotation)*Matrix4x4.CreateTranslation(sample.Position);
            if (!EnbW3dHierarchyPose.TryEvaluate(asset.Resource!.Hierarchies.Single(),world,animation,frame,null,out var pose,out error,storedTranslationScale:scale))
                throw new InvalidDataException(error);
            uint delta=_lastAge.HasValue ? sample.Age-_lastAge.Value : 0;
            for (int i=0;i<particles.Count;i++)
            {
                var state=_emitters[i]; var particle=particles[i];
                AdvanceEmitter(state,particle.Definition,sample.Age,delta,pose.WorldTransforms[particle.PivotIndex],pose.Visibility[particle.PivotIndex],scale,!_lastAge.HasValue);
            }
            _lastAge=sample.Age;
        }
        history.RetainLatestSample();
        _processedSamples=history.Samples.Count;
    }
    public static Emitter CreateEmitter(EnbNext.Assets.EnbCurrentRetailParticleEmitterDefinition definition,
        EnbRetailParticleRandomStreams? randomStreams=null,EnbRetailParticleRandomOwner? randomOwner=null)
        => new() {Remaining=definition.MaximumEmissions,Remainder=EnbRetailParticleEmissionArithmetic.InitialMilliseconds(definition.InitialEmissionTimeSeconds),
            Random=randomOwner??new(definition,randomStreams), Slots=new(checked((int)EnbRetailParticleEmissionArithmetic.Capacity(definition)),
                EnbRetailParticleEmissionArithmetic.Milliseconds(definition.LifetimeSeconds),0)};

    public static void AdvanceEmitter(Emitter state, EnbNext.Assets.EnbCurrentRetailParticleEmitterDefinition definition,
        uint age, uint delta, Matrix4x4 current, bool visible, float scale, bool initial = false,
        Vector3? localVelocityPerSecond = null,Action<EnbCurrentRetailParticleBirth>? enqueue=null,bool bufferCallback=false)
    {
        void Queue(EnbCurrentRetailParticleBirth birth)
        {
            var sampled=state.Random!.Sample(birth,definition);
            if(enqueue is not null)enqueue(sampled);
            else state.Slots!.Enqueue(sampled);
        }
        void UpdateSlots()
        {
            if(enqueue is not null)return;
            state.Slots!.Advance(age);
            state.WorldBirths.Clear();
            state.WorldBirths.AddRange(state.Slots.LiveParticles.Select(p=>p.Birth with
            {NativeBufferSlot=p.Slot,NativePropertyOffsets=state.Random!.Properties.AtSlot(p.Slot)}));
        }
        if(localVelocityPerSecond is {} velocity &&
            (!float.IsFinite(velocity.X)||!float.IsFinite(velocity.Y)||!float.IsFinite(velocity.Z)))
            throw new InvalidDataException("Particle birth velocity must be finite");
        long lifetime=EnbRetailParticleEmissionArithmetic.Milliseconds(definition.LifetimeSeconds);
        // Expired births cannot contribute to any later render view.
        foreach(var expired in state.Births.Where(b=>(long)age-b>=lifetime)) state.Worlds.Remove(expired);
        state.Births.RemoveAll(b=>(long)age-b>=lifetime);
        state.WorldBirths.RemoveAll(b=>(long)age-b.Time>=lifetime);
        state.CurrentWorld=current;
        // Native restart snapshots the current transform before emission.
        if (visible && !state.Visible)
        {
            state.Previous=current;
            //0097F46F increments AL; birth records and buffer+2B4 retain that byte.
            state.EmissionRun = unchecked((byte)(state.EmissionRun + 1));
            // 0097F446–0097F45D restores the finite allowance only when
            // a completed emitter starts again. Partial runs resume.
            if(state.Completed) {state.Remaining=definition.MaximumEmissions;state.Completed=false;}
        }
        //95E990 ->97E330 drains authored remainder even with a zero frame
        //delta. A visibility setter/initial pose observation is not that call.
        if (visible && !state.Completed && (bufferCallback || delta>0 || (definition.LocalSpace && initial && state.Remainder>0)))
        {
            uint interval=EnbRetailParticleEmissionArithmetic.Interval(definition.EmissionRatePerSecond);
            delta = EnbRetailParticleEmissionArithmetic.ReduceFrame(delta, interval,
                EnbRetailParticleEmissionArithmetic.Capacity(definition), definition.RuntimeBurstSize);
            if (interval == 0)
            {
                //0097E598/0097EBE5 reduces a nonzero elapsed frame to1.
                // The interval then never reduces the remainder. A finite
                // allowance terminates the native loop; an unlimited emitter
                // has no terminating branch and must be contained locally.
                if (definition.MaximumEmissions <= 0)
                    throw new InvalidDataException("Zero-interval continuous particle emission has no terminating retail path");
                uint remainder = unchecked(state.Remainder + (delta > 0 ? 1u : 0u));
                if (remainder > 0)
                {
                    using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
                    long birth = (long)age - remainder;
                    float fraction = definition.LocalSpace ? 1 : (float)((EnbRetailLineArithmetic.Register)1.0 - (double)remainder);
                    var nativeBeforeRotation = EnbCurrentRetailParticleBirthTransform.ExtractOrientation(state.Previous);
                    var nativeBeforePosition = state.Previous.Translation;
                    var nativeRotation = EnbCurrentRetailParticleBirthTransform.ExtractOrientation(current);
                    var nativePosition = current.Translation;
                    var birthQuaternion = definition.LocalSpace ? nativeRotation :
                        EnbCurrentRetailParticleBirthTransform.Interpolate(nativeBeforeRotation, nativeRotation, fraction);
                    var birthRotation = definition.LocalSpace ? Matrix4x4.CreateFromQuaternion(nativeRotation) :
                        EnbCurrentRetailParticleBirthTransform.Create(birthQuaternion);
                    float Coordinate(float before, float after) => (float)(((EnbRetailLineArithmetic.Register)after - before) * fraction + before);
                    var translation = definition.LocalSpace ? nativePosition : new Vector3(Coordinate(nativeBeforePosition.X, nativePosition.X),
                        Coordinate(nativeBeforePosition.Y, nativePosition.Y), Coordinate(nativeBeforePosition.Z, nativePosition.Z));
                    var pose = Matrix4x4.CreateScale(scale) * birthRotation * Matrix4x4.CreateTranslation(translation);
                    var nativeInherited = definition.LocalSpace ? Vector3.Zero : (nativePosition - nativeBeforePosition) * definition.VelocityInheritanceFactor;
                    int nativeEmitted = state.Remaining;
                    EnbRetailParticleAdmission.ValidateWork((long)state.Births.Count + nativeEmitted);
                    if (nativeEmitted > 0)
                    {
                        bool live=(long)age-birth<lifetime;
                        if(live)state.Worlds.TryAdd(birth, pose);
                        for (int j = 0; j < nativeEmitted; j++)
                        {
                            var queued=new EnbCurrentRetailParticleBirth(birth, checked(state.EmittedCount + j), pose)
                            { EmissionRun = state.EmissionRun, InheritedVelocityPerMillisecond = nativeInherited,
                                LocalVelocityPerSecond = localVelocityPerSecond,
                                NativeBirthRotation = definition.LocalSpace ? null : birthQuaternion, NativeBirthScale = scale };
                            Queue(queued);
                            if(live)state.Births.Add(birth);
                        }
                    }
                    state.EmittedCount = checked(state.EmittedCount + nativeEmitted);
                    state.Remaining = 0; state.Completed = true;
                }
                state.Remainder = remainder;
                if(!bufferCallback || !definition.LocalSpace)state.Previous = current;
                state.Visible = visible;
                UpdateSlots();
                return;
            }
            uint elapsed=unchecked(state.Remainder+delta);
            long events=elapsed<=interval ? 0 : (elapsed-1)/interval;
            long burst=definition.RuntimeBurstSize;
            if(definition.MaximumEmissions>0) events=Math.Min(events,(state.Remaining+burst-1)/burst);
            long emitted=events*burst;
            if(definition.MaximumEmissions>0) emitted=Math.Min(emitted,state.Remaining);
            EnbRetailParticleAdmission.ValidateWork(state.Births.Count + emitted);
            var beforeRotation = EnbCurrentRetailParticleBirthTransform.ExtractOrientation(state.Previous);
            var beforePosition = state.Previous.Translation;
            var rotation = EnbCurrentRetailParticleBirthTransform.ExtractOrientation(current);
            var position = current.Translation;
            using var nativeArithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            float reciprocalDelta = (float)((EnbRetailLineArithmetic.Register)1d / (double)delta);
            float nativeFraction = (float)((EnbRetailLineArithmetic.Register)1d - (EnbRetailLineArithmetic.Register)(double)elapsed * reciprocalDelta);
            float nativeFractionStep = (float)((EnbRetailLineArithmetic.Register)(double)interval * reciprocalDelta);
            // 0097E623–0097E688: inherit the attachment's translation
            // delta, after its hierarchy and owner transforms. Local
            // buffers ignore inheritance (0097ECB0).
            float inheritedFactor = (float)((EnbRetailLineArithmetic.Register)reciprocalDelta * definition.VelocityInheritanceFactor);
            float InheritedCoordinate(float before, float after) => (float)(((EnbRetailLineArithmetic.Register)after - before) * inheritedFactor);
            var inherited=definition.LocalSpace || definition.VelocityInheritanceFactor==0 ? Vector3.Zero :
                new Vector3(InheritedCoordinate(beforePosition.X,position.X),InheritedCoordinate(beforePosition.Y,position.Y),InheritedCoordinate(beforePosition.Z,position.Z));
            // A long frame may span many lifetimes. Advance the event
            // ordinal/allowance for all events, retaining only live births.
            for(long e=0;e<events;e++)
            {
                nativeFraction = (float)((EnbRetailLineArithmetic.Register)nativeFraction + nativeFractionStep);
                long remainder=(long)elapsed-(e+1)*interval;
                long birth=(long)age-remainder;
                bool live=(long)age-birth<lifetime;
                float t=definition.LocalSpace ? 1 : nativeFraction;
                var birthQuaternion = definition.LocalSpace ? rotation : EnbCurrentRetailParticleBirthTransform.Interpolate(beforeRotation,rotation,t);
                var birthRotation = definition.LocalSpace ? Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(beforeRotation,rotation,t)) :
                    EnbCurrentRetailParticleBirthTransform.Create(birthQuaternion);
                float Coordinate(float before, float after) => (float)(((EnbRetailLineArithmetic.Register)after - before) * t + before);
                var birthWorld=Matrix4x4.CreateScale(scale)*birthRotation*
                    Matrix4x4.CreateTranslation(new Vector3(Coordinate(beforePosition.X,position.X),
                        Coordinate(beforePosition.Y,position.Y),Coordinate(beforePosition.Z,position.Z)));
                if(live)state.Worlds.Add(birth,birthWorld);
                // 0097E530/0097EB80 truncate the final burst to the
                // remaining particle allowance, not the event count.
                for(long j=0;j<Math.Min(burst,emitted-e*burst);j++)
                {
                    var queued=new EnbCurrentRetailParticleBirth(birth,state.EmittedCount+e*burst+j,birthWorld) {
                        EmissionRun=state.EmissionRun, InheritedVelocityPerMillisecond=inherited,
                        LocalVelocityPerSecond=localVelocityPerSecond, NativeBirthRotation=definition.LocalSpace ? null : birthQuaternion,
                        NativeBirthScale=scale};
                    Queue(queued);
                    if(live)state.Births.Add(birth);
                }
            }
            state.Remainder=unchecked(elapsed-(uint)events*interval);
            state.EmittedCount=checked(state.EmittedCount+emitted);
            if(definition.MaximumEmissions>0)
            {state.Remaining-=(int)emitted;state.Completed=state.Remaining==0;}
        }
        //97E330's local branch returns without publishing BC..D4.
        if(!bufferCallback || !definition.LocalSpace)state.Previous=current;
        state.Visible=visible;
        UpdateSlots();
    }
}
