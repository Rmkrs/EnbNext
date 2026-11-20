using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Retained emitter/buffer scene lifetime and the separate bounds, callback and render clocks.</summary>
public sealed class EnbRetailParticleSceneLifetime
{
    internal static bool UseUncachedBirthSnapshots { get; set; }
    internal static bool VerifyBirthSnapshots { get; set; }
    private readonly EnbCurrentRetailParticleEmitterDefinition _definition;
    private readonly EnbRetailParticleBufferRing _buffer;
    public EnbCurrentRetailAnimatedParticleRuntime.Emitter Emitter { get; }
    public EnbRetailParticleMotionHistory MotionHistory => Emitter.MotionHistory;
    public bool SceneAttached { get; private set; }
    public bool Started { get; private set; }
    public bool BufferAttached { get; private set; }
    public bool Destroyed { get; private set; }
    public bool BoundsDirty { get; private set; } = true;
    public bool Hidden { get; private set; }
    public bool AnimationHidden { get; private set; }
    public event Action<bool>? SceneAttachmentChanged;
    //95C310 tests destroyedBC and active D4/D8, not the pending birth queue.
    public bool CanRetire => Destroyed && _buffer.CurrentState.ExistingCount == 0 && _buffer.CurrentState.NewCount == 0;
    public EnbRetailParticleBufferRing.State BufferState => _buffer.CurrentState;
    private IReadOnlyList<EnbCurrentRetailParticleBirth>? _liveBirthSnapshot;
    private ulong _liveBirthRevision, _propertyRevision;
    private EnbRetailParticleBufferProperties? _snapshotProperties;
    private BorrowedBirthList? _borrowedBirths;
    /// <summary>Immediate render/query access to retained physical slots. This
    /// view must not escape the synchronous consumer or span a buffer update.
    /// Use LiveBirths when an immutable publication is required.</summary>
    public IReadOnlyList<EnbCurrentRetailParticleBirth> BorrowLiveBirths()
        => _borrowedBirths ??= new(this);

    private sealed class BorrowedBirthList(EnbRetailParticleSceneLifetime owner)
        : IReadOnlyList<EnbCurrentRetailParticleBirth>
    {
        public int Count => owner._buffer.LiveParticleCount;
        public EnbCurrentRetailParticleBirth this[int index]
        {
            get
            {
                var particle = owner._buffer.GetLiveParticle(index);
                return particle.Birth with { Time = particle.StoredTime, NativeBufferSlot = particle.Slot,
                    NativePropertyOffsets = owner.Emitter.Random!.Properties.AtSlot(particle.Slot) };
            }
        }
        public IEnumerator<EnbCurrentRetailParticleBirth> GetEnumerator()
        { for (int i = 0; i < Count; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public IReadOnlyList<EnbCurrentRetailParticleBirth> LiveBirths
    {
        get
        {
            var properties=Emitter.Random!.Properties;
            if(!UseUncachedBirthSnapshots && _liveBirthSnapshot is not null && _liveBirthRevision==_buffer.LiveRevision &&
                ReferenceEquals(properties,_snapshotProperties) && _propertyRevision==properties.Revision)
            {
                if(VerifyBirthSnapshots)
                {
                    if(_liveBirthSnapshot.Count!=_buffer.LiveParticleCount)throw new InvalidDataException("Cached particle count changed without publication");
                    for(int i=0;i<_liveBirthSnapshot.Count;i++)
                    {
                        var particle=_buffer.GetLiveParticle(i);
                        var expected=particle.Birth with {Time=particle.StoredTime,NativeBufferSlot=particle.Slot,
                            NativePropertyOffsets=properties.AtSlot(particle.Slot)};
                        if(_liveBirthSnapshot[i]!=expected)throw new InvalidDataException($"Cached particle slot {i} changed without publication: {_liveBirthSnapshot[i]} != {expected}");
                    }
                }
                return _liveBirthSnapshot;
            }
            var births=new EnbCurrentRetailParticleBirth[_buffer.LiveParticleCount];
            for(int i=0;i<births.Length;i++)
            {
                var particle=_buffer.GetLiveParticle(i);
                births[i]=particle.Birth with { Time=particle.StoredTime, NativeBufferSlot=particle.Slot,
                    NativePropertyOffsets=properties.AtSlot(particle.Slot) };
            }
            _liveBirthRevision=_buffer.LiveRevision;
            _snapshotProperties=properties;
            _propertyRevision=properties.Revision;
            // Bounds, visibility and rendering observe the same published
            // slots. Keep old snapshots immutable when the next update runs.
            return _liveBirthSnapshot=Array.AsReadOnly(births);
        }
    }

    public EnbRetailParticleSceneLifetime(EnbCurrentRetailParticleEmitterDefinition definition,uint initialClock,
        EnbRetailParticleRandomStreams? randomStreams=null,EnbRetailParticleRandomOwner? randomOwner=null,bool initiallyStarted=true)
    {
        _definition=definition;
        Emitter=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(definition,randomStreams,randomOwner);
        //0097D620 clone sets D8 started=1; primary97D180 starts it at0.
        Emitter.Visible=initiallyStarted;
        Started=initiallyStarted;
        _buffer=new(checked((int)EnbRetailParticleEmissionArithmetic.Capacity(definition)),
            EnbRetailParticleEmissionArithmetic.Milliseconds(definition.LifetimeSeconds),initialClock);
    }

    /// <summary>Attachment/visibility changes stop or restart emission without clearing retained particles or remainder.</summary>
    public void Prepare(uint clock,Matrix4x4 world,float scale,bool sceneAttached,bool started)
    {
        UpdateTransform(world);
        SetSceneAttached(sceneAttached);
        SetHidden(clock,world,scale,!started);
        Emitter.CurrentWorld=world;
    }

    /// <summary>Actual +58/+5C scene operations, separate from +184 flag setters.</summary>
    public void SetSceneAttached(bool attached)
    {
        attached &= !Destroyed;
        if(attached==SceneAttached)return;
        SceneAttached=attached;
        if(!attached)Emitter.Visible=Started=false;
        else if(BufferAttached)Emitter.Visible=Started=true;
        SceneAttachmentChanged?.Invoke(attached);
    }

    /// <summary>97D4F0/97F390 always reconcile: stopping is allowed while detached; starting requires scene attachment.</summary>
    public void SetHidden(uint clock,Matrix4x4 world,float scale,bool hidden)
    {
        Hidden=hidden;
        ReconcileStarted(clock,world,scale);
    }

    public void SetAnimationHidden(uint clock,Matrix4x4 world,float scale,bool hidden)
    {
        AnimationHidden=hidden;
        ReconcileStarted(clock,world,scale);
    }

    public void UpdateTransform(Matrix4x4 world)
    {
        //97DD90 forwards every local setter to buffer+50;941C20 always clears2000.
        if(_definition.LocalSpace)BoundsDirty=true;
        Emitter.CurrentWorld=world;
    }

    private void ReconcileStarted(uint clock,Matrix4x4 world,float scale)
    {
        if(Hidden || AnimationHidden || Destroyed)Emitter.Visible=Started=false;
        else if(SceneAttached && !Started)
        {
            EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(Emitter,_definition,clock,0,world,true,scale);
            Started=true;
        }
        UpdateTransform(world);
    }

    public void ReplayObjectSceneEvent(int stateId,EnbCurrentRetailRslObjectSceneEvent observation,
        bool wrapperAttached,uint clock,Matrix4x4 world,float scale)
    {
        if(observation.Kind is EnbCurrentRetailRslObjectSceneEventKind.Hidden or EnbCurrentRetailRslObjectSceneEventKind.BoneHidden)
        {
            if(observation.StateId==stateId)SetHidden(clock,world,scale,observation.Hidden);
            return;
        }
        if(!wrapperAttached)return;
        if(observation.PreviousStateId==stateId)SetSceneAttached(false);
        if(observation.StateId==stateId)SetSceneAttached(true);
    }

    /// <summary>0097DEF0 first attaches the buffer; 0095E990 later callbacks invalidate bounds and emit even when culled.</summary>
    public void Callback(uint clock,uint frameDelta,Matrix4x4 world,float scale,Vector3? localVelocityPerSecond=null)
    {
        //Single-owner callers retain their existing traversal entry point.
        //A shared scene dispatches the two registered objects separately.
        bool existingBuffer=BufferAttached;
        EmitterCallback(world);
        if(existingBuffer)BufferCallback(clock,frameDelta,world,scale,localVelocityPerSecond);
    }

    public bool EmitterCallback(Matrix4x4 world)
    {
        if(BufferAttached || !SceneAttached || !Started)return false;
        BufferAttached=true;
        //0097DF55..DFC6 snapshots the first attached pose without emitting.
        Emitter.Previous=world;
        Emitter.CurrentWorld=world;
        return true;
    }

    public void BufferCallback(uint clock,uint frameDelta,Matrix4x4 world,float scale,Vector3? localVelocityPerSecond=null)
    {
        if(!BufferAttached)return;
        BoundsDirty=true;
        //97D820 ->960CF0 clears buffer+214; no emitter virtual remains.
        if(Destroyed)return;
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(Emitter,_definition,clock,frameDelta,world,
            Started && !Destroyed,scale,localVelocityPerSecond:localVelocityPerSecond,enqueue:_buffer.Enqueue,bufferCallback:true);
    }

    //95E990 invalidates on every scene traversal, including a same-clock redraw.
    public void InvalidateBounds() => BoundsDirty=true;

    /// <summary>00463340/00960DB0 only update positions when the cached world bounds are invalid.</summary>
    public void EnsureBounds(uint clock)
    {
        if(!BufferAttached || !BoundsDirty)return;
        _buffer.Advance(clock);
        BoundsDirty=false;
    }

    /// <summary>0095D8E0 shares the buffer update clock with bounds: same-clock queued births wait.</summary>
    public void AdvanceForRender(uint clock)
    {
        if(BufferAttached)_buffer.Advance(clock);
    }

    public void Destroy(uint clock,Matrix4x4 world,float scale)
    {
        Destroyed=true;
        //97D820 ->960CF0 marks destruction and clears the emitter pointer;
        //it does not publish another matrix or invalidate cached buffer bounds.
        SetSceneAttached(false);
        Hidden=true;Emitter.Visible=Started=false;
    }
}
