using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public readonly record struct EnbCurrentRetailParticleEmitterReceipt(
    int EmittedParticleCount,
    int LiveParticleCount,
    long StartTimeMilliseconds,
    long FrameTimeMilliseconds);

/// <summary>
/// Current-retail ParticleBufferClass sprite, connected-line and streak consumer,
/// including sprite atlases through16x16. Retained owners supply native births,
/// physical slots and property offsets; geometry is transient production GPU data.
/// </summary>
public static partial class EnbCurrentRetailParticleEmitterRuntime
{
    // Construction scratch never escapes. Published geometry is detached by
    // default; the production opt-in frame arena keeps each queued range alive
    // through final flush. Taking construction slots also permits nested builds.
    private static class ConstructionScratch<T>
    {
        [ThreadStatic] private static List<T>? available;
        internal readonly struct Lease(List<T> values) : IDisposable
        {
            internal List<T> Values => values;
            public void Dispose() { values.Clear(); available ??= values; }
        }
        internal static Lease Rent(int capacity)
        {
            var values = available;
            available = null;
            values ??= new List<T>(capacity);
            values.EnsureCapacity(capacity);
            return new(values);
        }
    }
    internal sealed class UniformTriangleMetadata(int count, Vector3 normal) : IReadOnlyList<EnbW3dRenderTriangleMetadata>
    {
        internal UniformTriangleMetadata Update(int nextCount,Vector3 nextNormal){count=nextCount;normal=nextNormal;return this;}
        public int Count => count;
        public EnbW3dRenderTriangleMetadata this[int index] => (uint)index < (uint)count
            ? new(0,normal,0) : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<EnbW3dRenderTriangleMetadata> GetEnumerator()
        { for(int i=0;i<count;i++)yield return new(0,normal,0); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private static EnbW3dNormalStream ParticleNormals(EnbCurrentRetailParticleEmitterDefinition definition,int count)
        => EnbW3dNormalStream.Zero(count);
    /// <summary>Persistent geometry preparation for one physical particle buffer.
    /// Definitions may be shared by buffers with different live counts; their
    /// count-dependent normals and atlas ranges must not evict one another.</summary>
    public sealed class GeometryOwner(EnbCurrentRetailParticleEmitterDefinition definition, string renderInstancePath="root")
    {
        public EnbCurrentRetailParticleEmitterDefinition Definition {get;}=definition;
        public string RenderInstancePath {get;}=renderInstancePath;
        private sealed class InvocationPool(EnbW3dDrawTaskArena arena)
        {
            internal readonly EnbW3dDrawTaskArena Arena=arena;
            internal readonly List<GeometryOwner> Slots=[];
            internal long Serial;
            internal int Cursor;
        }
        private readonly List<InvocationPool> pools=[];
        internal bool ownedInvocation;
        internal EnbW3dRenderMesh? mesh;
        internal EnbW3dDrawTask? task;
        internal GeometryOwner ForInvocation(EnbW3dDrawTaskArena current)
        {
            InvocationPool? pool=null;
            foreach(var item in pools)if(ReferenceEquals(item.Arena,current)){pool=item;break;}
            if(pool is null){pool=new(current);pools.Add(pool);}
            if(pool.Serial!=current.Invocation){pool.Serial=current.Invocation;pool.Cursor=0;}
            if(pool.Cursor==pool.Slots.Count)pool.Slots.Add(new(Definition,RenderInstancePath){ownedInvocation=true});
            return pool.Slots[pool.Cursor++];
        }
        private EnbW3dRenderTextureLocation? materialLocation;
        private string? materialName;
        private EnbResolvedTextureResource? materialTexture;
        private EnbW3dRenderMaterialPass? capacityMaterial;
        private EnbW3dCompiledMaterialSemantics capacitySemantics;
        private TriangleRange? range;
        private sealed class TriangleRange(EnbW3dRenderTriangleBinding[] storage):IReadOnlyList<EnbW3dRenderTriangleBinding>
        {
            public int Count {get;set;}
            public EnbW3dRenderTriangleBinding this[int index] => (uint)index<(uint)Count?storage[index]:throw new ArgumentOutOfRangeException(nameof(index));
            public IEnumerator<EnbW3dRenderTriangleBinding> GetEnumerator(){for(int i=0;i<Count;i++)yield return storage[i];}
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
        }
        internal bool BuildOwnedMaterial(EnbW3dRenderTextureLocation? location,string? name,EnbResolvedTextureResource? texture,
            int count,out EnbW3dRenderMaterialPass pass,out EnbW3dCompiledMaterialSemantics semantics,out string? diagnostic)
        {
            bool same=Equals(materialLocation,location)&&materialName==name&&ReferenceEquals(materialTexture,texture);
            if(capacityMaterial is null||!same||capacityMaterial.TriangleBindings.Count<count)
            {
                int capacity=same&&capacityMaterial is not null?Math.Max(count,capacityMaterial.TriangleBindings.Count*2):count;
                if(!TryBuildMaterialCore(Definition,location,capacity,out var compiled,out semantics,out diagnostic,name,texture))
                {pass=null!;return false;}
                capacityMaterial=compiled;capacitySemantics=semantics;materialLocation=location;materialName=name;materialTexture=texture;
                range=new((EnbW3dRenderTriangleBinding[])compiled.TriangleBindings);
                material=compiled.Copy().SetTriangleBindings(range);materialSemantics=semantics;
                binding=null;task=null;mesh=null;
            }
            range!.Count=count;pass=material!;semantics=capacitySemantics;diagnostic=null;return true;
        }

        private readonly SpriteTopology _topology=new();
        private LineGeometryStorage? line;
        internal LineGeometryStorage Line=>line??=new();
        private EnbW3dRenderMaterialPass? material;
        private EnbW3dCompiledMaterialSemantics materialSemantics;
        private EnbW3dRenderMaterialPass? bindingSource;
        private IReadOnlyList<Vector2>? bindingUv;
        internal readonly EnbW3dUvOwner Uv=new();
        private EnbW3dDrawMaterialBinding? binding;
        internal bool TryMaterial(EnbW3dRenderTextureLocation? location,string? name,EnbResolvedTextureResource? texture,
            int count,out EnbW3dRenderMaterialPass pass,out EnbW3dCompiledMaterialSemantics semantics)
        {
            if(material is not null && material.TriangleBindings.Count==count && Equals(materialLocation,location) &&
                materialName==name && ReferenceEquals(materialTexture,texture))
            {pass=material;semantics=materialSemantics;return true;}
            pass=null!;semantics=default;return false;
        }
        internal void PublishMaterial(EnbW3dRenderTextureLocation? location,string? name,EnbResolvedTextureResource? texture,
            EnbW3dRenderMaterialPass pass,EnbW3dCompiledMaterialSemantics semantics)
        {materialLocation=location;materialName=name;materialTexture=texture;material=pass;materialSemantics=semantics;}
        internal EnbW3dDrawMaterialBinding Bind(EnbW3dRenderMaterialPass pass,EnbW3dCompiledMaterialSemantics semantics,IReadOnlyList<Vector2> uv)
        {
            if(binding is not null && ReferenceEquals(bindingSource,pass))
            {
                if(ReferenceEquals(bindingUv,uv))return binding;
                if(ownedInvocation)
                {
                    if(binding.Source.TextureStages.Count!=0)
                    {
                        var stage=binding.Source.TextureStages[0];
                        IReadOnlyList<Vector2> coordinates=uv is Vector2[] array?System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(array):uv;
                        stage.RuntimeTexCoords=coordinates;stage.EffectiveRuntimeTexCoords=coordinates;
                    }
                    bindingUv=uv;return binding;
                }
            }
            binding=BindParticleMaterial(pass,semantics,uv);bindingSource=pass;bindingUv=uv;return binding;
        }
        internal EnbW3dNormalStream Normals(int count)=>EnbW3dNormalStream.Zero(count);
        internal EnbW3dGeneratedTopology Topology(int count,int corners,bool fixedAtlas,out Vector2[]? uv)
            =>_topology.Get(count,corners,fixedAtlas,out uv);
    }
    internal sealed class SpriteTopology
    {
        // Publish the range and atlas together: other views may still consume
        // the previous immutable range while this buffer changes live count.
        private sealed record Publication(int Count, int Corners, bool FixedAtlas,
            EnbW3dGeneratedTopology Topology, Vector2[]? Coordinates);
        private Publication? published;
        private int storageCorners;
        private uint[] storage=[];
        private EnbW3dGeneratedTopology? last;
        private Vector2[] fixedCoordinates=[];
        internal EnbW3dGeneratedTopology Get(int count,int corners,bool fixedAtlas,out Vector2[]? coordinates)
        {
            var current=Volatile.Read(ref published);
            if(current is not null && current.Count==count && current.Corners==corners && current.FixedAtlas==fixedAtlas)
            {
                coordinates=current.Coordinates;
                return current.Topology;
            }
            lock(this)
            {
                if(storageCorners!=corners)
                {
                    storage=[]; last=null; fixedCoordinates=[];
                    storageCorners=corners;
                }
                int perParticle=(corners-2)*3;
                int needed=checked(count*perParticle);
                if(needed>storage.Length)
                {
                    int capacity=Math.Max(count,storage.Length/perParticle*2);
                    var next=new uint[checked(capacity*perParticle)];
                    for(int i=0;i<capacity;i++)
                    {
                        int index=i*perParticle;uint first=(uint)(i*corners);
                        next[index]=first;next[index+1]=first+1;next[index+2]=first+2;
                        if(corners==4){next[index+3]=first+2;next[index+4]=first+3;next[index+5]=first;}
                    }
                    storage=next;last=null;
                }
                if(last?.Indices.Count!=needed)last=new(storage,needed,checked(count*corners));
                coordinates=null;
                if(fixedAtlas)
                {
                    // FrameMode zero masks every sampled frame to zero. These
                    // coordinates belong to the topology, not the current pose.
                    // Replace on size changes; never overwrite a queued view.
                    int vertices=checked(count*corners);
                    if(fixedCoordinates.Length!=vertices)
                    {
                        var next=new Vector2[vertices];
                        var pattern=corners==4?s_quadUv:s_uv;
                        for(int i=0;i<vertices;i++)next[i]=pattern[i%corners];
                        fixedCoordinates=next;
                    }
                    coordinates=fixedCoordinates;
                }
                Volatile.Write(ref published,new Publication(count,corners,fixedAtlas,last,coordinates));
                return last;
            }
        }
    }
    internal sealed class CompiledProperties(EnbCurrentRetailParticleEmitterDefinition definition)
    {
        public readonly long Lifetime=EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(definition.LifetimeSeconds);
        private EnbRetailParticleRotationCurve? rotation;
        public EnbRetailParticleRotationCurve Rotation=>rotation??=new(definition.Rotation.Keys,Lifetime);
        public readonly EnbRetailParticleColorCurve.Compiled Color=new(definition.Color.Keys,EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(definition.LifetimeSeconds));
        public readonly EnbRetailParticleScalarCurve.Compiled Opacity=new(definition.Opacity.Keys,EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(definition.LifetimeSeconds));
        public readonly EnbRetailParticleScalarCurve.Compiled Size=new(definition.Size.Keys,EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(definition.LifetimeSeconds));
        public readonly EnbRetailParticleScalarCurve.Compiled Frame=new(definition.Frame.Keys,EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(definition.LifetimeSeconds));
        public readonly EnbRetailParticleScalarCurve.Compiled? Blur=definition.BlurTime is {} blur?new(blur.Keys,EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(definition.LifetimeSeconds)):null;
    }
    // Definitions own immutable decoded property keys. Compile their native
    // millisecond times/slopes once, as 0095EF40/0095F550 do at construction.

    public static bool SupportsEmissionSpace(EnbCurrentRetailParticleEmitterDefinition d, bool retainedWorldBirths = false)
        => float.IsFinite(d.InitialEmissionTimeSeconds) &&
           (d.InitialEmissionTimeSeconds <= 0 || d.LocalSpace || retainedWorldBirths);
    private const uint PointGroupPrimaryGradientModulate = 0x0000_0400u;
    private const uint PrimaryGradientMask = 0x0000_1C00u;
    private const uint CullMask = 0x0008_0000u;
    private const int MaximumRuntimeParticles = 1_000_000;
    private const float MillisecondsPerSecond = 1000f;
    private static readonly Vector2[] s_uv =
    [
        new(0.5f, 0f),
        new(0f, 0.866f),
        new(1f, 0.866f),
    ];

    //00957370 initializes mode-1 UVs and0,1,2 /2,3,0 indices.
    private static readonly Vector2[] s_quadUv = [new(0,0),new(0,1),new(1,1),new(1,0)];

    public static bool TryBuildPacket(
        EnbW3dRenderResourceKey ownerResourceKey,
        ulong renderIdentity,
        ulong visualGeneration,
        IEnbParticleRenderOwner instance,
        EnbResolvedRenderCamera camera,
        EnbW3dLightEnvironment lightEnvironment,
        uint randomSeedSalt,
        long startTimeMilliseconds,
        long frameTimeMilliseconds,
        out EnbW3dDrawTask? packet,
        out EnbCurrentRetailParticleEmitterReceipt receipt,
        out string? diagnostic,
        float particleSizeScale = 1f,
        Func<long, Matrix4x4>? birthWorld = null,
        long? emissionStopMilliseconds = null,
        IReadOnlyList<long>? retainedBirths = null,
        IReadOnlyList<EnbCurrentRetailParticleBirth>? worldBirths = null,
        long? activeEmissionRun = null,
        Func<long, Vector3>? inheritedVelocity = null,
        EnbRetailParticleMotionHistory? motionHistory = null,
        int internalDetailLevel = 0,bool currentRetailFactoryScale=false,
        EnbRetailParticleReplayHistory? randomHistory=null,bool cullToCamera=false,
        EnbW3dTransientGeometryArena.Frame? frameStreams=null,GeometryOwner? geometryOwner=null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(lightEnvironment);
        packet = null;
        receipt = default;
        diagnostic = null;
        if (internalDetailLevel is < 0 or > 16)
        {
            diagnostic = "Particle internal detail level must be between 0 and 16.";
            return false;
        }

        EnbCurrentRetailParticleEmitterDefinition definition = instance.Definition;
        if (!SupportsEmissionSpace(definition, worldBirths is not null || randomHistory is not null))
        { diagnostic = "Particle prewarm requires retained world births or local space."; return false; }
        if (frameTimeMilliseconds < startTimeMilliseconds || startTimeMilliseconds < 0)
        {
            diagnostic = "ParticleEmitter frame time precedes its admitted start time.";
            return false;
        }
        bool streak = definition.RenderMode is 3 or 4;
        bool line = definition.RenderMode == 2 && (worldBirths is not null || randomHistory is not null) &&
            (SupportsJetLine(definition) || SupportsEngineTrail(definition));
        if ((definition.RenderMode > 1 && !line && !streak) ||
            definition.CreationRandomizer.ClassId is not (0 or 1 or 2 or 3) ||
            definition.VelocityRandomizer.ClassId is not (0 or 1 or 2 or 3))
        {
            diagnostic = $"ParticleEmitter '{definition.Name}' has unsupported render/randomizer state or lacks retained line births (render={definition.RenderMode}, creationRandomizer={definition.CreationRandomizer.ClassId}, velocityRandomizer={definition.VelocityRandomizer.ClassId}).";
            return false;
        }
        if (!float.IsFinite(particleSizeScale) || !currentRetailFactoryScale && particleSizeScale <= 0)
        { diagnostic = "Particle buffer scale must be finite and positive."; return false; }
        if (definition.RuntimeBurstSize > MaximumRuntimeParticles ||
            !float.IsFinite(definition.EmissionRatePerSecond) ||
            !float.IsFinite(definition.LifetimeSeconds) || definition.LifetimeSeconds <= 0f ||
            (double)definition.LifetimeSeconds * MillisecondsPerSecond >= long.MaxValue ||
            definition.MaximumEmissions < 0)
        {
            diagnostic = $"ParticleEmitter '{definition.Name}' has invalid burst/rate/lifetime/maximum-emission state.";
            return false;
        }
        if (!float.IsFinite(definition.VelocityInheritanceFactor) ||
            (!definition.LocalSpace && definition.VelocityInheritanceFactor != 0f && worldBirths is null && inheritedVelocity is null))
        {
            diagnostic = $"ParticleEmitter '{definition.Name}' requires finite inheritance and retained birth velocity.";
            return false;
        }

        uint emissionIntervalMilliseconds = EnbRetailParticleEmissionArithmetic.Interval(definition.EmissionRatePerSecond);
        if (emissionIntervalMilliseconds == 0 && retainedBirths is null && worldBirths is null && (randomHistory is null || definition.MaximumEmissions==0))
        {
            diagnostic = $"ParticleEmitter '{definition.Name}' has a sub-millisecond emission interval outside this finite runtime.";
            return false;
        }

        long elapsedMilliseconds = Math.Max(0, Math.Min(frameTimeMilliseconds, emissionStopMilliseconds ?? frameTimeMilliseconds) - startTimeMilliseconds);
        long initialMilliseconds = EnbRetailParticleEmissionArithmetic.InitialMilliseconds(definition.InitialEmissionTimeSeconds);
        elapsedMilliseconds += initialMilliseconds;
        long emissionEvents = elapsedMilliseconds == 0 ? 0 : emissionIntervalMilliseconds == 0
            ? ((long)definition.MaximumEmissions+definition.RuntimeBurstSize-1)/definition.RuntimeBurstSize
            : (elapsedMilliseconds - 1) / emissionIntervalMilliseconds;
        long totalEmitted;
        try
        {
            totalEmitted = checked(emissionEvents * definition.RuntimeBurstSize);
        }
        catch (OverflowException)
        {
            diagnostic = $"ParticleEmitter '{definition.Name}' emission count overflowed its finite runtime.";
            return false;
        }
        if (definition.MaximumEmissions != 0)
            totalEmitted = Math.Min(totalEmitted, definition.MaximumEmissions);
        if (retainedBirths is not null) totalEmitted = retainedBirths.Count;
        if (worldBirths is not null) totalEmitted = worldBirths.Count;

        long receiptEmitted=totalEmitted;
        if(randomHistory is not null)
        {
            var suppliedBirths=worldBirths;
            worldBirths=randomHistory.Prepare(totalEmitted,frameTimeMilliseconds,ordinal=>
            {
                if(suppliedBirths is not null)return suppliedBirths[(int)ordinal];
                long birth=retainedBirths is null ? checked(startTimeMilliseconds+((ordinal/definition.RuntimeBurstSize+1)*emissionIntervalMilliseconds)-initialMilliseconds)
                    : retainedBirths[(int)ordinal];
                var world=birthWorld?.Invoke(birth)??instance.WorldTransform;
                System.Numerics.Matrix4x4.Decompose(world,out var scale,out _,out _);
                return new(birth,ordinal,world)
                {NativeBirthScale=scale.X,NativeBirthRotation=EnbCurrentRetailParticleBirthTransform.ExtractOrientation(world),
                    InheritedVelocityPerMillisecond=inheritedVelocity?.Invoke(birth)??Vector3.Zero};
            });
            totalEmitted=worldBirths.Count;
            motionHistory=randomHistory.Motion;
        }

        double bufferExpression = definition.RuntimeBurstSize * (double)definition.EmissionRatePerSecond *
                                  (definition.LifetimeSeconds + 1.0);
        // 0097D377..381 bounds finite emitters by their total allowance before
        // allocating the buffer. Keep arithmetic wide rather than reproducing
        // retail's signed low-word overflow for extremely long lifetimes.
        if (definition.MaximumEmissions > 0) bufferExpression=Math.Min(bufferExpression,definition.MaximumEmissions);
        if (!double.IsFinite(bufferExpression) || bufferExpression > MaximumRuntimeParticles)
        {
            diagnostic = $"ParticleEmitter '{definition.Name}' requires an invalid or excessive particle buffer ({bufferExpression:R}).";
            return false;
        }
        int bufferCapacity = Math.Max(2, (int)bufferExpression);
        long firstBufferedParticle = Math.Max(0, totalEmitted - bufferCapacity);

        long lifetimeMilliseconds = EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(definition.LifetimeSeconds);
        motionHistory?.Expire(frameTimeMilliseconds,lifetimeMilliseconds);
        // Retained buffers have already advanced every physical slot. Native
        // 00961D10 bounds scan precedes detail admission; property evaluation
        // is pure once position and random offsets are stored. Do not compile
        // color/rotation/size for slots that cannot reach the draw consumer.
        using var positionScratch = ConstructionScratch<Vector3>.Rent(Math.Min(bufferCapacity,(int)Math.Min(totalEmitted,int.MaxValue)));
        List<Vector3>? retainedPositions=null;
        if(!streak && worldBirths is not null)
        {
            retainedPositions=positionScratch.Values;
            for(long ordinal=firstBufferedParticle;ordinal<totalEmitted;ordinal++)
            {
                var birth=worldBirths[(int)ordinal];long age=frameTimeMilliseconds-birth.Time;
                if(age<0||age>=lifetimeMilliseconds)continue;
                Vector3? position=birth.NativeBufferPosition;
                if(position is null && birth is {SampledPosition:not null,SampledVelocityPerMillisecond:not null} &&
                    motionHistory is not null && motionHistory.TryGetState(birth.Time,birth.Ordinal,age,out var motion))
                    position=motion.Position;
                if(position is not {} stored || birth.NativePropertyOffsets is null ||
                    !IsFinite(stored) || internalDetailLevel!=0 && birth.NativeBufferSlot is not >=0)
                {retainedPositions=null;break;}
                retainedPositions.Add(stored);
            }
            if(cullToCamera && retainedPositions is {Count:>0})
            {
                var bounds=EnbRetailParticleBufferBounds.FromActivePositions(retainedPositions,
                    EnbRetailParticleBufferBounds.MaximumSize(definition.Size,lifetimeMilliseconds,particleSizeScale))
                    .WorldSphere(definition.LocalSpace?instance.WorldTransform:Matrix4x4.Identity);
                if(!EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds))
                {
                    receipt=new(receiptEmitted>int.MaxValue?int.MaxValue:(int)receiptEmitted,retainedPositions.Count,startTimeMilliseconds,frameTimeMilliseconds);
                    return true;
                }
            }
        }
        using var liveScratch = ConstructionScratch<ParticleState>.Rent(Math.Min(bufferCapacity, (int)Math.Min(totalEmitted, int.MaxValue)));
        var live = liveScratch.Values;
        CompiledProperties? properties=null;
        for (long particleOrdinal = firstBufferedParticle; particleOrdinal < totalEmitted; particleOrdinal++)
        {
            // Borrow one complete physical slot, then evaluate it. Re-reading
            // the adapter would rebuild this large value and its property offsets.
            EnbCurrentRetailParticleBirth? nativeBirth = worldBirths is null ? null : worldBirths[(int)particleOrdinal];
            long eventOrdinal = particleOrdinal / definition.RuntimeBurstSize;
            long birthTime = retainedBirths is null ? checked(startTimeMilliseconds + ((eventOrdinal + 1) * emissionIntervalMilliseconds) - initialMilliseconds) : retainedBirths[(int)particleOrdinal];
            if (nativeBirth is {} born) birthTime = born.Time;
            long ageMilliseconds = frameTimeMilliseconds - birthTime;
            if (ageMilliseconds < 0 || ageMilliseconds >= lifetimeMilliseconds)
                continue;
            if(retainedPositions is not null && internalDetailLevel!=0 &&
                !EnbParticleBufferDetailBudgetSemantics.IncludesPhysicalSlot(nativeBirth!.Value.NativeBufferSlot!.Value,internalDetailLevel))continue;

            Matrix4x4 bornWorld = birthWorld is null || definition.LocalSpace ? instance.WorldTransform : birthWorld(birthTime);
            long randomOrdinal = particleOrdinal;
            if (nativeBirth is {} birth)
            {
                if (!definition.LocalSpace) bornWorld = birth.World;
                randomOrdinal = birth.Ordinal;
            }
            Vector3? retainedVelocity = nativeBirth?.LocalVelocityPerSecond;
            if (!TryEvaluateParticle(instance, ref properties, bornWorld, randomSeedSalt, randomOrdinal, ageMilliseconds, particleSizeScale, out ParticleState state, out diagnostic, retainedVelocity, nativeBirth,motionHistory))
                return false;
            if (!definition.LocalSpace && worldBirths is not null && nativeBirth?.NativeBirthRotation is null && nativeBirth?.NativeBufferPosition is null && nativeBirth?.SampledPosition is null)
                state = state with { Center = state.Center + nativeBirth!.Value.InheritedVelocityPerMillisecond * ageMilliseconds };
            else if (!definition.LocalSpace && worldBirths is null && inheritedVelocity is not null)
                state = state with { Center = state.Center + inheritedVelocity(birthTime) * ageMilliseconds };
            if (internalDetailLevel != 0 && nativeBirth?.NativeBufferSlot is not >= 0)
            {
                diagnostic = "Particle detail selection requires retained physical buffer slots.";
                return false;
            }
            live.Add(state with {
                PhysicalSlot = nativeBirth?.NativeBufferSlot,
                EmissionRun = nativeBirth?.EmissionRun ?? 0 });
        }

        receipt = new EnbCurrentRetailParticleEmitterReceipt(
            receiptEmitted > int.MaxValue ? int.MaxValue : (int)receiptEmitted,
            retainedPositions?.Count??live.Count,
            startTimeMilliseconds,
            frameTimeMilliseconds);
        if(cullToCamera && retainedPositions is null && !streak && live.Count!=0)
        {
            var boundPositions=positionScratch.Values;
            boundPositions.Clear();
            foreach(var state in live)boundPositions.Add(definition.LocalSpace?state.LocalCenter??state.Center:state.Center);
            var bounds=EnbRetailParticleBufferBounds.FromActivePositions(
                boundPositions,
                EnbRetailParticleBufferBounds.MaximumSize(definition.Size,lifetimeMilliseconds,particleSizeScale))
                .WorldSphere(definition.LocalSpace?instance.WorldTransform:Matrix4x4.Identity);
            if(!EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds))return true;
        }
        if (internalDetailLevel != 0)
            live.RemoveAll(p => !EnbParticleBufferDetailBudgetSemantics.IncludesPhysicalSlot(p.PhysicalSlot!.Value, internalDetailLevel));
        if (live.Count == 0)
            return true;

        if (streak)
            return EnbCurrentRetailStreakRuntime.TryBuildPacket(ownerResourceKey,renderIdentity,visualGeneration,
                instance.GameId,instance.EmitterResourceName,instance.BindingOrdinal,definition,
                live.Select(p=>new EnbCurrentRetailStreakSample(p.Center,p.Tail,p.Size,p.Color,
                    EnbCurrentRetailStreakRuntime.TailColor(definition,p.Color)) {Frame=p.FloatFrame}).ToArray(),
                camera,frameTimeMilliseconds,out packet,out diagnostic,instance.TextureLocation,instance.RuntimeTextureName,instance.RetainedTexture,geometryOwner?.RenderInstancePath??"root");

        if(geometryOwner is not null && !ReferenceEquals(geometryOwner.Definition,definition))
            throw new ArgumentException("Particle geometry owner belongs to another definition.",nameof(geometryOwner));
        if(frameStreams is not null && EnbW3dDrawTaskArena.Current is {} taskArena)
            geometryOwner=geometryOwner?.ForInvocation(taskArena);
        if (line)
        {
            // 0095E295..0095E36D appends the live emitter position using the
            // newest retained colour, but only within the current emission run.
            if (activeEmissionRun == live[^1].EmissionRun)
                live.Add(live[^1] with { Center = instance.WorldTransform.Translation });
            return TryBuildConnectedLinePacket(ownerResourceKey, renderIdentity, visualGeneration,
                instance, camera, lightEnvironment, frameTimeMilliseconds, live, out packet, out diagnostic, particleSizeScale, startTimeMilliseconds, geometryOwner, frameStreams);
        }

        int corners = definition.RenderMode == 1 ? 4 : 3;
        int trianglesPerParticle = corners - 2;
        var cornerUvs = definition.RenderMode == 1 ? s_quadUv : s_uv;
        if (!TryBuildMaterial(definition, instance.TextureLocation, live.Count * trianglesPerParticle, out EnbW3dRenderMaterialPass materialPass, out EnbW3dCompiledMaterialSemantics semantics, out diagnostic, instance.RuntimeTextureName,instance.RetainedTexture,geometryOwner))
            return false;

        int vertexCount = checked(live.Count * corners);
        var borrowedPositions=frameStreams?.RentPositions(vertexCount);
        var borrowedView=frameStreams?.RentPositions(vertexCount);
        var borrowedColors=frameStreams?.RentColors(vertexCount);
        var positionArray=borrowedPositions is null?new Vector3[vertexCount]:null;
        var viewArray=borrowedView is null?new Vector3[vertexCount]:null;
        var colorArray=borrowedColors is null?new EnbRenderColor32[vertexCount]:null;
        Span<Vector3> positions=borrowedPositions is not null?borrowedPositions.WriteSpan:positionArray;
        Span<Vector3> viewPositions=borrowedView is not null?borrowedView.WriteSpan:viewArray;
        Span<EnbRenderColor32> colors=borrowedColors is not null?borrowedColors.WriteSpan:colorArray;
        EnbRenderCameraMatrixCompiler.TryCreateWorldToCanonicalView(camera, out var view);
        var pointView = EnbRetailPointGroupTransform.Compose(Matrix4x4.Identity, view);
        var localPointView = EnbRetailPointGroupTransform.Compose(instance.WorldTransform, view);
        var normals = geometryOwner?.Normals(vertexCount)??ParticleNormals(definition,vertexCount);
        int atlasMode = (int)Math.Min(4u,definition.FrameMode);
        int atlasSide = 1 << atlasMode;
        Vector2[]? fixedUv;
        var topology=geometryOwner is not null?geometryOwner.Topology(live.Count,corners,atlasMode==0 && !geometryOwner.ownedInvocation,out fixedUv):
            OwnerCache.Get(ref EnbPreparedParticleEmitter.Of(definition).SpriteTopology,definition,static _=>new SpriteTopology()).Get(live.Count,corners,atlasMode==0,out fixedUv);
        bool retainedUv=geometryOwner is {ownedInvocation:true};
        IReadOnlyList<Vector2> uv;
        Span<Vector2> writableUv;
        if(retainedUv)
        {
            uv=geometryOwner!.Uv;
            writableUv=geometryOwner.Uv.Prepare(vertexCount,corners,atlasMode==0?cornerUvs:ReadOnlySpan<Vector2>.Empty);
        }
        else
        {
            var array=fixedUv??new Vector2[vertexCount];uv=array;writableUv=array;
        }
        var indices=topology.Indices;
        var triangles = new UniformTriangleMetadata(live.Count * trianglesPerParticle,-camera.Basis.Forward);
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        for (int particleIndex = 0; particleIndex < live.Count; particleIndex++)
        {
            ParticleState state = live[particleIndex];
            Vector3 viewCenter = state.LocalCenter is {} localCenter
                ? EnbRetailPointGroupTransform.Point(localCenter, localPointView)
                : EnbRetailPointGroupTransform.Point(state.Center, pointView);
            for (int corner = 0; corner < corners; corner++)
            {
                Vector3 authored = EnbRetailPointGroupExpansion.Corner((int)definition.RenderMode, state.OrientationIndex, corner);
                Vector3 position = state.Center +
                                   camera.Basis.Right * (authored.X * state.Size) +
                                   camera.Basis.Up * (authored.Y * state.Size);
                positions[particleIndex*corners+corner]=position;
                minimum = Vector3.Min(minimum, position);
                maximum = Vector3.Max(maximum, position);
                int vertex = (particleIndex * corners) + corner;
                viewPositions[vertex] = EnbRetailPointGroupExpansion.Expand(viewCenter, state.Size,
                    (int)definition.RenderMode, state.OrientationIndex, corner);
                colors[vertex] = state.Color;
                // 00957370 builds both triangle (00C069BC) and quad atlas
                // tables. Triangle V is .866 of the cell, not a full quad.
                if(atlasMode!=0 || !retainedUv && fixedUv is null)
                    writableUv[vertex] = (cornerUvs[corner] + new Vector2(state.Frame & (atlasSide - 1), state.Frame >> atlasMode)) / atlasSide;
            }
        }

        //World buffers use identity model; retained local buffers use the
        //current model in the native composition before view-space expansion.
        IReadOnlyList<Vector3> worldStream=borrowedPositions is not null?borrowedPositions:
            System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(positionArray);
        IReadOnlyList<Vector3>? submittedViewPositions=(!definition.LocalSpace || live.All(p => p.LocalCenter is not null))
            ?borrowedView is not null?borrowedView:System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(viewArray):null;
        IReadOnlyList<EnbRenderColor32> colorStream=borrowedColors is not null?borrowedColors:
            System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(colorArray);
        packet = FinishParticleStreams(ownerResourceKey, renderIdentity, visualGeneration, instance,
            lightEnvironment, frameTimeMilliseconds, materialPass, semantics, worldStream, normals, colorStream, uv, indices, triangles, minimum, maximum, topology,submittedViewPositions,geometryOwner);
        return true;
    }

    private static EnbW3dDrawTask FinishParticleStreams(EnbW3dRenderResourceKey ownerResourceKey,
        ulong renderIdentity, ulong visualGeneration, IEnbParticleRenderOwner instance,
        EnbW3dLightEnvironment lightEnvironment, long frameTimeMilliseconds,
        EnbW3dRenderMaterialPass materialPass, EnbW3dCompiledMaterialSemantics semantics,
        IReadOnlyList<Vector3> positions, EnbW3dNormalStream normals,
        IReadOnlyList<EnbRenderColor32> colors, IReadOnlyList<Vector2> uv, IReadOnlyList<uint> indices, IReadOnlyList<EnbW3dRenderTriangleMetadata> triangles,
        Vector3 minimum, Vector3 maximum, EnbW3dGeneratedTopology? topology=null,IReadOnlyList<Vector3>? viewPositions=null,
        GeometryOwner? materialOwner=null,string? renderInstancePath=null)
    {
        var definition = instance.Definition;
        var materialBinding=materialOwner is not null?materialOwner.Bind(materialPass,semantics,uv):
            BindParticleMaterial(materialPass,semantics,uv);
        materialPass=materialBinding.Source;

        Vector3 center = (minimum + maximum) * 0.5f;
        float radius = 0f;
        foreach (Vector3 position in positions)
            radius = MathF.Max(radius, Vector3.Distance(center, position));

        bool retained=materialOwner is {ownedInvocation:true};
        var mesh = retained ? materialOwner!.mesh : null;
        mesh ??= new EnbW3dRenderMesh(
            Ordinal: int.MinValue + instance.BindingOrdinal,
            Name: $"{definition.Name}.ParticleBuffer",
            ContainerName: definition.Name,
            BoundingRadius: radius,
            Positions: positions,
            Normals: normals,
            BoneIndices: Array.Empty<ushort>(),
            Indices: indices,
            TriangleMetadata: triangles,
            VertexMaterials: Array.Empty<EnbW3dRenderVertexMaterial>(),
            MaterialPasses: new[] { materialPass })
        {
            BoundingBoxMin = minimum,
            BoundingBoxMax = maximum,
            BoundingSphereCenter = center,
            EffectiveColor1 = colors,
            GeneratedTopology = topology,
            CurrentRetailGeometrySortRequestOverride = instance.Definition.RenderMode!=2 || (instance.Definition.LinePropertyWords[0]&4)==0,
        };
        mesh.Ordinal=int.MinValue+instance.BindingOrdinal;
        if(!ReferenceEquals(mesh.Indices,indices))mesh.TopologyRevision++;
        mesh.Positions=positions;mesh.Normals=normals;mesh.Indices=indices;mesh.TriangleMetadata=triangles;
        mesh.BoundingRadius=radius;mesh.BoundingBoxMin=minimum;mesh.BoundingBoxMax=maximum;
        mesh.BoundingSphereCenter=center;mesh.EffectiveColor1=colors;mesh.GeneratedTopology=topology;
        if(retained){mesh.RetainsParticleFrame=true;materialOwner!.mesh=mesh;}
        var deformed = positions is EnbW3dFrameStream<Vector3>?null:new EnbW3dDeformedGeometry(mesh.Positions.ToImmutableArray(), EnbW3dNormalStream.Snapshot(mesh.Normals));
        //0095D8E0 selects the outer buffer bucket before dispatching any mode.
        //Line flag4 affects inner triangle sorting, not this scene queue.
        EnbW3dStaticSortSemantics staticSort = EnbW3dStaticSortSemantics.FromSingleShader(semantics.FixedFunction.PackedRetailState);
        var orderKey = new EnbW3dDrawOrderKey(
            instance.RenderPass,
            EnbW3dDrawBlendClass.Blended,
            staticSort,
            ownerResourceKey,
            mesh.Ordinal,
            0,
            renderIdentity,
            instance.BindingOrdinal);
        if(retained && materialOwner!.task is {} current && current.OrderKey==orderKey &&
            current.VisualGeneration==visualGeneration && current.GameId==instance.GameId &&
            current.HierarchyName==instance.HierarchyName && current.PivotIndex==instance.PivotIndex && current.RenderObjectName==instance.RenderObjectName)
        {
            current.UpdateGeneratedFrame(new EnbW3dWorldBounds(center,radius),deformed,lightEnvironment,frameTimeMilliseconds,viewPositions);
            return current;
        }
        var result=new EnbW3dDrawTask(
            orderKey,
            renderIdentity,
            visualGeneration,
            instance.GameId,
            Matrix4x4.Identity,
            new EnbW3dWorldBounds(center, radius),
            mesh,
            deformed,
            materialBinding,
            lightEnvironment,
            frameTimeMilliseconds,
            instance.HierarchyName,
            instance.PivotIndex,
            instance.RenderObjectName,renderInstancePath??materialOwner?.RenderInstancePath??"root") { CurrentRetailParticleBuffer = true,CurrentRetailViewPositions=viewPositions };
        if(retained)materialOwner!.task=result;
        return result;
    }

    private static EnbW3dDrawMaterialBinding BindParticleMaterial(EnbW3dRenderMaterialPass materialPass,
        EnbW3dCompiledMaterialSemantics semantics,IReadOnlyList<Vector2> uv)
    {
        // Bind an invocation-owned range or a detached packet stream. The owner
        // is not reused until all queued draws for its invocation drain.
        if(materialPass.TextureStages.Count!=0)
        {
            // Transfer the newly built stream once; downstream immutable lane
            // and upload plans can consume it without another vertex-array copy.
            IReadOnlyList<Vector2> coordinates=uv is Vector2[] array?System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(array):uv;
            var stage0 = (materialPass.TextureStages[0]).Copy().SetRuntimeTexCoords(coordinates).SetEffectiveRuntimeTexCoords(coordinates);
            materialPass = (materialPass).Copy().SetTextureStages(new[] { stage0 });
        }

        return new(materialPass,semantics);
    }

    private static Vector3 SampleRandomizer(EnbCurrentRetailParticleRandomizer randomizer, uint seed, uint boxStream, uint stream)
    {
        //Randomizer constructors clamp each authored dimension independently.
        static float Dimension(float value)=>value>0?value:0;
        var values = new Vector3(Dimension(randomizer.Values.X),Dimension(randomizer.Values.Y),Dimension(randomizer.Values.Z));
        if (randomizer.ClassId == 0)
            return Multiply(new(Signed(seed, boxStream), Signed(seed, boxStream + 1), Signed(seed, boxStream + 2)), values);
        if (randomizer.ClassId is 1 or 2)
        {
            // 00A15C70 solid ball; 00A15E80 normalizes the accepted vector for a sphere surface.
            Vector3 sample;
            do { sample = new(Signed(seed, stream++), Signed(seed, stream++), Signed(seed, stream++)); }
            while (sample.LengthSquared() is <= 0 or > 1);
            if (randomizer.ClassId == 2) sample = Vector3.Normalize(sample);
            return sample * MathF.Max(0, values.X);
        }
        // 00A16070: solid cylinder, X half extent and YZ disk radius.
        float y, z;
        do { y = Signed(seed, stream++); z = Signed(seed, stream++); } while (y * y + z * z > 1);
        return new(Signed(seed, boxStream) * MathF.Max(0, values.X),
            y * MathF.Max(0, values.Y), z * MathF.Max(0, values.Y));
    }
    // Both bounds and drawing consume the buffer's same current motion. Bounds
    // do not evaluate render-only color, opacity, rotation, atlas or tail curves.
    private static (Vector3 Position,Vector3 Velocity) RetainedMotion(
        EnbCurrentRetailParticleEmitterDefinition definition,EnbCurrentRetailParticleBirth birth,
        long ordinal,long age,float scale,EnbRetailParticleMotionHistory? history)
    {
        if(history is not null && history.TryGetState(birth.Time,ordinal,age,out var retained))return retained;
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var acceleration=EnbRetailParticleEmitterMotion.ConvertUnits(definition.AccelerationPerSecondSquared,1000000f);
        acceleration=new((float)((EnbRetailLineArithmetic.Register)acceleration.X*scale),
            (float)((EnbRetailLineArithmetic.Register)acceleration.Y*scale),
            (float)((EnbRetailLineArithmetic.Register)acceleration.Z*scale));
        return history is not null
            ?history.SampleState(birth.Time,ordinal,age,birth.SampledPosition!.Value,birth.SampledVelocityPerMillisecond!.Value,acceleration)
            :EnbRetailParticleKinematics.Initialize(birth.SampledPosition!.Value,birth.SampledVelocityPerMillisecond!.Value,acceleration,age);
    }

    private static bool TryEvaluateParticle(
        IEnbParticleRenderOwner instance,
        ref CompiledProperties? properties,
        Matrix4x4 worldTransform,
        uint randomSeedSalt,
        long particleOrdinal,
        long ageMilliseconds,
        float bufferScale,
        out ParticleState state,
        out string? diagnostic,
        Vector3? retainedVelocity = null,
        EnbCurrentRetailParticleBirth? nativeBirth = null,
        EnbRetailParticleMotionHistory? motionHistory = null)
    {
        state = default;
        diagnostic = null;
        EnbCurrentRetailParticleEmitterDefinition definition = instance.Definition;
        // Direct synthetic packet callers may omit an owner. Production owners
        // always supply sampled vectors and native physical-slot properties.
        uint seed = nativeBirth?.NativePropertyOffsets is null ? StableSeed(instance, randomSeedSalt, particleOrdinal) : 0;
        Vector3 center;
        Vector3 bufferVelocity = nativeBirth?.NativeBufferVelocityPerMillisecond ?? Vector3.Zero;
        Vector3? retainedLocalCenter = null;
        if (nativeBirth?.NativeBufferPosition is {} bufferPosition)
        {
            retainedLocalCenter = definition.LocalSpace ? bufferPosition : null;
            center = definition.LocalSpace ? EnbRetailPointGroupTransform.Point(bufferPosition, worldTransform) : bufferPosition;
        }
        else if(nativeBirth is {SampledPosition:not null,SampledVelocityPerMillisecond:not null} sampledBirth)
        {
            var motion=RetainedMotion(definition,sampledBirth,particleOrdinal,ageMilliseconds,bufferScale,motionHistory);
            center=motion.Position;bufferVelocity=motion.Velocity;
            if(definition.LocalSpace)
            {retainedLocalCenter=center;center=EnbRetailPointGroupTransform.Point(center,worldTransform);}
        }
        else
        {
        // 0097E810 samples both randomizer objects through the same virtual operation.
        Vector3 localPosition = SampleRandomizer(definition.CreationRandomizer, seed, 1, 20);
        Vector3 localRandomVelocity = SampleRandomizer(definition.VelocityRandomizer, seed, 4, 64);
        Vector3 outward = localPosition.LengthSquared() > 1.0e-12f
            ? Vector3.Normalize(localPosition) * definition.OutwardVelocityPerSecond
            : Vector3.Zero;
        Vector3 velocityPerSecond = (retainedVelocity ?? definition.VelocityPerSecond) + localRandomVelocity + outward;
        center = Vector3.Transform(localPosition, worldTransform);
        Vector3 worldVelocityPerMillisecond =
            Vector3.TransformNormal(velocityPerSecond, worldTransform) / MillisecondsPerSecond;
        bool nativeMotion = nativeBirth is not null &&
            (definition.LocalSpace ? motionHistory is not null : nativeBirth.Value.NativeBirthRotation is not null);
        if (nativeMotion && nativeBirth is {} motionBirth)
        {
            using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            float nativeScale = motionBirth.NativeBirthScale;
            float Scale(float value) => (float)((EnbRetailLineArithmetic.Register)value * nativeScale);
            float Units(float value) => (float)((EnbRetailLineArithmetic.Register)value * .001f);
            Vector3 Scaled(Vector3 value) => new(Scale(value.X), Scale(value.Y), Scale(value.Z));
            Vector3 Converted(Vector3 value) => new(Units(value.X), Units(value.Y), Units(value.Z));
            //All four randomizer+10 virtuals clamp their scale to positive/zero;
            //stored emitter velocity, outward speed and buffer size stay signed.
            float RandomScale(float value)=>(float)((EnbRetailLineArithmetic.Register)(value>0?value:0)*(nativeScale>0?nativeScale:0));
            Vector3 RandomScaled(Vector3 value)=>new(RandomScale(value.X),RandomScale(value.Y),RandomScale(value.Z));
            //0097D33C scales the velocity randomizer before sampling;
            //0097DE70 then scales both randomizers and stored velocity.
            localPosition = SampleRandomizer(definition.CreationRandomizer with
                { Values = RandomScaled(definition.CreationRandomizer.Values) }, seed, 1, 20);
            localRandomVelocity = SampleRandomizer(definition.VelocityRandomizer with
                { Values = RandomScaled(Converted(definition.VelocityRandomizer.Values)) }, seed, 4, 64);
            var constantVelocity = Scaled(Converted(retainedVelocity ?? definition.VelocityPerSecond));
            float outwardAmount = Scale(Units(definition.OutwardVelocityPerSecond));
            EnbRetailLineArithmetic.Register outwardX = 0;
            float outwardY = 0, outwardZ = 0;
            if (outwardAmount != 0)
            {
                EnbRetailLineArithmetic.Register square = (EnbRetailLineArithmetic.Register)localPosition.Y * localPosition.Y +
                    (EnbRetailLineArithmetic.Register)localPosition.Z * localPosition.Z + (EnbRetailLineArithmetic.Register)localPosition.X * localPosition.X;
                if (square == 0) outwardX = outwardAmount;
                else
                {
                    var multiplier = EnbRetailLineCoalescing.ReciprocalSquareRoot((float)square) * outwardAmount;
                    outwardX = (EnbRetailLineArithmetic.Register)localPosition.X * multiplier;
                    outwardY = (float)((EnbRetailLineArithmetic.Register)localPosition.Y * multiplier);
                    outwardZ = (float)((EnbRetailLineArithmetic.Register)localPosition.Z * multiplier);
                }
            }
            var nativeVelocity = new Vector3(
                (float)((EnbRetailLineArithmetic.Register)(float)((EnbRetailLineArithmetic.Register)localRandomVelocity.X + outwardX) + constantVelocity.X),
                (float)((EnbRetailLineArithmetic.Register)(float)((EnbRetailLineArithmetic.Register)localRandomVelocity.Y + outwardY) + constantVelocity.Y),
                (float)((EnbRetailLineArithmetic.Register)(float)((EnbRetailLineArithmetic.Register)localRandomVelocity.Z + outwardZ) + constantVelocity.Z));
            if (definition.LocalSpace)
            {
                //0097ECB0 queues these vectors without the world birth
                //quaternion consumer.0097DD90 supplies the current model.
                center = localPosition;
                worldVelocityPerMillisecond = nativeVelocity;
            }
            else
            {
                var birthRotation = motionBirth.NativeBirthRotation!.Value;
                center = EnbCurrentRetailParticleBirthTransform.TransformBirthVector(birthRotation, localPosition, worldTransform.Translation, false);
                worldVelocityPerMillisecond = EnbCurrentRetailParticleBirthTransform.TransformBirthVector(birthRotation, nativeVelocity,
                    motionBirth.InheritedVelocityPerMillisecond, true);
            }
        }
        // Acceleration belongs to the separate world-space buffer, not the
        // rotating emitter (0095BA90 / 0095E8B0).
        Vector3 worldAccelerationPerMillisecondSquared = definition.AccelerationPerSecondSquared * bufferScale /
            (MillisecondsPerSecond * MillisecondsPerSecond);
        // 0097ECB0 leaves births local; 0097DD90 forwards the current transform
        // to the buffer. Its acceleration rotates with that buffer as well.
        if (definition.LocalSpace)
            worldAccelerationPerMillisecondSquared = Vector3.TransformNormal(definition.AccelerationPerSecondSquared, worldTransform) /
                (MillisecondsPerSecond * MillisecondsPerSecond);
        float age = ageMilliseconds;
        if(motionHistory is not null && nativeMotion && nativeBirth is {} retainedMotionBirth)
        {
            using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            float reciprocal=(float)((EnbRetailLineArithmetic.Register)1d/1000000d);
            float Acceleration(float value)=>(float)((EnbRetailLineArithmetic.Register)(float)((EnbRetailLineArithmetic.Register)value*reciprocal)*bufferScale);
            var a=definition.AccelerationPerSecondSquared;
            var acceleration=new Vector3(Acceleration(a.X),Acceleration(a.Y),Acceleration(a.Z));
            center=motionHistory.Sample(retainedMotionBirth.Time,particleOrdinal,ageMilliseconds,center,worldVelocityPerMillisecond,acceleration);
            if (definition.LocalSpace)
            {
                retainedLocalCenter = center;
                center = EnbRetailPointGroupTransform.Point(center, worldTransform);
            }
        }
        else center += (worldVelocityPerMillisecond * age) +
                      (worldAccelerationPerMillisecondSquared * (0.5f * age * age));
        }

        properties??=OwnerCache.Get(ref EnbPreparedParticleEmitter.Of(definition).PropertyCurves,definition,static d=>new CompiledProperties(d));
        long lifetimeMilliseconds=properties.Lifetime;
        var nativeProperties = nativeBirth?.NativePropertyOffsets;
        Vector3 rgb = nativeProperties is {} colorProperties
            ? properties.Color.Sample(ageMilliseconds, randomOffset:colorProperties.Color)
            : properties.Color.Sample(ageMilliseconds) + new Vector3(
            Signed(seed, 7) * definition.Color.Random.X,
            Signed(seed, 8) * definition.Color.Random.Y,
            Signed(seed, 9) * definition.Color.Random.Z);
        float opacity = nativeProperties is {} opacityProperties
            ? properties.Opacity.Sample(ageMilliseconds,randomOffset:opacityProperties.Opacity)
            : properties.Opacity.Sample(ageMilliseconds) + Signed(seed, 10) * definition.Opacity.Random;
        float size = properties.Size.Sample(ageMilliseconds,
            storageScale:bufferScale,randomOffset:nativeProperties?.Size ?? Signed(seed,11)*definition.Size.Random);
        if (!IsFinite(center) || !IsFinite(rgb) || !float.IsFinite(opacity) || !float.IsFinite(size))
        {
            diagnostic = $"ParticleEmitter '{definition.Name}' produced non-finite particle state.";
            return false;
        }

        // 0096148A/490/49F: multiply turns by 256, truncate, retain AL.
        // PointGroup 00957370 builds 256 entries at 00B11D38 radians/entry.
        // Negative fractional turns truncate toward zero before byte wrapping.
        byte orientationIndex;
        if (nativeProperties is {} orientationProperties)
            orientationIndex = properties.Rotation.SampleOrientation(ageMilliseconds,
                    orientationProperties.RotationRatePerMillisecond, orientationProperties.InitialOrientation);
        else if (definition.Rotation.Random == 0 && definition.InitialOrientationRandom == 0)
            orientationIndex = properties.Rotation.SampleOrientation(ageMilliseconds);
        else
        {
            float ageSeconds = ageMilliseconds / MillisecondsPerSecond;
            float turns = Signed(seed, 12) * definition.InitialOrientationRandom +
                          IntegrateScalar(definition.Rotation.Keys, ageSeconds) +
                          Signed(seed, 13) * definition.Rotation.Random * ageSeconds;
            orientationIndex = unchecked((byte)(long)Math.Truncate((double)turns * 256));
        }
        rgb = Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        opacity = Math.Clamp(opacity, 0f, 1f);
        size = MathF.Max(0f, size);
        float frameValue=properties.Frame.Sample(ageMilliseconds,
            randomOffset:nativeProperties?.Frame ?? Signed(seed,14)*definition.Frame.Random);
        Vector3 tail=center;
        if(definition.RenderMode is 3 or 4)
        {
            if(nativeBirth?.SampledPosition is null && nativeBirth?.NativeBufferPosition is null)
            { diagnostic="Streak draw requires retained native particle motion"; return false; }
            using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            float blur=properties.Blur is {} curve ? curve.Sample(ageMilliseconds,
                randomOffset:nativeProperties?.BlurTime ?? 0) : 0;
            float Tail(float p,float v)=>(float)((EnbRetailLineArithmetic.Register)p-(EnbRetailLineArithmetic.Register)(float)((EnbRetailLineArithmetic.Register)blur*v)*1000d);
            var head=retainedLocalCenter??center;
            tail=new(Tail(head.X,bufferVelocity.X),Tail(head.Y,bufferVelocity.Y),Tail(head.Z,bufferVelocity.Z));
            if(definition.LocalSpace)tail=EnbRetailPointGroupTransform.Point(tail,worldTransform);
        }
        state = new ParticleState(
            center,
            size,
            orientationIndex,
            new EnbRenderColor32(ToByte(rgb.X), ToByte(rgb.Y), ToByte(rgb.Z), ToByte(opacity)),
            ((int)frameValue) & ((1 << (2*(int)Math.Min(4u,definition.FrameMode)))-1))
        { LineColor = new(rgb, opacity), LocalCenter = retainedLocalCenter, Tail=tail,FloatFrame=frameValue };
        return true;
    }

    internal sealed class RetainedMaterial
    {
        public readonly object Gate = new();
        public MaterialPublication? Published;
    }
    // One immutable structural publication. The optional prefix view belongs
    // to this publication, so concurrent replacement cannot mix bindings.
    internal sealed class MaterialPublication(EnbW3dRenderMaterialPass capacityPass,
        EnbW3dCompiledMaterialSemantics semantics, EnbW3dRenderTextureLocation? location,
        string? runtimeName, EnbResolvedTextureResource? texture)
    {
        public readonly EnbW3dRenderMaterialPass CapacityPass = capacityPass;
        public readonly EnbW3dCompiledMaterialSemantics Semantics = semantics;
        public EnbW3dRenderMaterialPass? LastPrefix;
        public bool Matches(EnbW3dRenderTextureLocation? nextLocation, string? nextName,
            EnbResolvedTextureResource? nextTexture) => Equals(location,nextLocation) &&
            runtimeName == nextName && ReferenceEquals(texture,nextTexture);
    }

    internal static bool TryBuildMaterial(
        EnbCurrentRetailParticleEmitterDefinition definition, EnbW3dRenderTextureLocation? textureLocation,
        int triangleCount, out EnbW3dRenderMaterialPass pass, out EnbW3dCompiledMaterialSemantics semantics,
        out string? diagnostic, string? runtimeTextureName=null, EnbResolvedTextureResource? retainedTexture=null,
        GeometryOwner? geometryOwner=null)
    {
        if(geometryOwner is {ownedInvocation:true})
            return geometryOwner.BuildOwnedMaterial(textureLocation,runtimeTextureName,retainedTexture,triangleCount,out pass,out semantics,out diagnostic);
        if(geometryOwner is not null && geometryOwner.TryMaterial(textureLocation,runtimeTextureName,retainedTexture,
            triangleCount,out pass,out semantics)){diagnostic=null;return true;}
        // A visible count selects a prefix; it does not mutate the binding.
        // Synchronize capacity/binding replacement, not every visible task.
        var cache=OwnerCache.Get(ref EnbPreparedParticleEmitter.Of(definition).Material,definition,static _=>new RetainedMaterial());
        var state=Volatile.Read(ref cache.Published);
        if(state is null || !state.Matches(textureLocation,runtimeTextureName,retainedTexture) ||
            state.CapacityPass.TriangleBindings.Count<triangleCount)
        {
            lock(cache.Gate)
            {
                state=Volatile.Read(ref cache.Published);
                if(state is null || !state.Matches(textureLocation,runtimeTextureName,retainedTexture) ||
                    state.CapacityPass.TriangleBindings.Count<triangleCount)
                {
                    int capacity=triangleCount;
                    if(state is not null && state.Matches(textureLocation,runtimeTextureName,retainedTexture))
                        capacity=Math.Max(capacity,state.CapacityPass.TriangleBindings.Count*2);
                    if(!TryBuildMaterialCore(definition,textureLocation,capacity,out pass,out semantics,
                        out diagnostic,runtimeTextureName,retainedTexture))return false;
                    state=new(pass,semantics,textureLocation,runtimeTextureName,retainedTexture);
                    Volatile.Write(ref cache.Published,state);
                }
            }
        }
        var prefix=Volatile.Read(ref state.LastPrefix);
        if(prefix?.TriangleBindings.Count!=triangleCount)
        {
            prefix=(state.CapacityPass).Copy().SetTriangleBindings(new ArraySegment<EnbW3dRenderTriangleBinding>(
                (EnbW3dRenderTriangleBinding[])state.CapacityPass.TriangleBindings,0,triangleCount));
            Volatile.Write(ref state.LastPrefix,prefix);
        }
        pass=prefix;semantics=state.Semantics;diagnostic=null;
        geometryOwner?.PublishMaterial(textureLocation,runtimeTextureName,retainedTexture,pass,semantics);
        return true;
    }

    private static bool TryBuildMaterialCore(
        EnbCurrentRetailParticleEmitterDefinition definition,
        EnbW3dRenderTextureLocation? textureLocation,
        int triangleCount,
        out EnbW3dRenderMaterialPass pass,
        out EnbW3dCompiledMaterialSemantics semantics,
        out string? diagnostic, string? runtimeTextureName = null,EnbResolvedTextureResource? retainedTexture=null)
    {
        pass = default!;
        semantics = default;
        diagnostic = null;
        Span<byte> shaderBytes = stackalloc byte[16];
        if (definition.RequiresLegacyTextureShaderResolution)
        {
            diagnostic = "Legacy particle shader requires the asset texture resolver.";
            return false;
        }
        definition.Shader.CopyTo(shaderBytes);
        var shaderRecord = new EnbW3dRenderShaderRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(shaderBytes.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(shaderBytes.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(shaderBytes.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(shaderBytes.Slice(12, 4)));
        if (!EnbW3dMaterialCompiler.TryDecodeShader(shaderRecord, out EnbW3dFixedFunctionState authored, out diagnostic))
            return false;

        // PointGroupClass forces cull-none and, because ParticleBuffer supplies
        // a diffuse array, primary-gradient MODULATE before submission.
        uint effectivePacked = (authored.PackedRetailState & ~PrimaryGradientMask & ~CullMask) |
                               PointGroupPrimaryGradientModulate;
        if (definition.RenderMode == 2)
            effectivePacked = (authored.PackedRetailState & 0xfff7e7ffu) | 0x10400u; // 009BEB20 textured line submit
        // D3D8's unbound stage 0 passes diffuse through. An empty authored
        // texture is not a failed resource lookup (e.g. we_blob01_p2).
        bool textured = textureLocation is not null || runtimeTextureName is not null || retainedTexture is not null;
        if(!textured)effectivePacked &= ~0x10000u;
        var white = new EnbRenderPackedRgb8(255, 255, 255, 0);
        var material = new EnbW3dRenderVertexMaterial(
            0, $"{definition.Name}.ParticleMaterial", 0,
            white, white, white, new EnbRenderPackedRgb8(0, 0, 0, 0),
            1f, 1f, 0f, null, null);
        var prelit = new EnbW3dVertexLightingSemantics(
            LightingEnabled: false,
            AmbientSource: EnbW3dMaterialColorSource.Material,
            DiffuseSource: EnbW3dMaterialColorSource.Color1,
            SpecularSource: EnbW3dMaterialColorSource.Material,
            EmissiveSource: EnbW3dMaterialColorSource.Material);
        if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(
                effectivePacked, material, textured?1:0, prelit, out semantics, out diagnostic))
        {
            return false;
        }

        var texture = !textured ? null : new EnbW3dRenderTextureBinding(0, retainedTexture?.LogicalName ?? runtimeTextureName ?? definition.TextureFileName, textureLocation)
        {
            CurrentRetailResolvedTexture=retainedTexture,
            CurrentRetailTextureState=retainedTexture is null?null:EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(retainedTexture),
            ResolveLogicalNameAtRuntime = runtimeTextureName is not null,
            Sampling = EnbW3dTextureSamplingSemantics.RetailDefault,
            SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState,
        };
        var stage0 = new EnbW3dRenderTextureStage(
            0,
            texture is null ? [] : new[] { texture },
            Array.Empty<Vector2>(),
            EnbW3dRenderBindingCardinality.Uniform)
        {
            EffectiveTextureCoordinateSetIndex = 0,
            EffectiveRuntimeTexCoords = Array.Empty<Vector2>(),
        };
        var triangleBindings = new EnbW3dRenderTriangleBinding[triangleCount];
        var triangleStates = new EnbW3dTriangleBindingPool();
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            triangleBindings[triangle] = triangleStates.Create(
                triangle,
                definition.RenderMode == 1 ? (triangle / 2) * 4 + (triangle % 2) * 2 : triangle * 3,
                null,
                null,
                texture is null?[]:new EnbW3dRenderTextureBinding?[] { texture },
                semantics) with
            {
                UsesImplicitVertexMaterial = true,
                UsesImplicitShader = true,
            };
        }
        pass = new EnbW3dRenderMaterialPass(
            0,
            Array.Empty<EnbW3dRenderVertexMaterialBinding>(),
            EnbW3dRenderBindingCardinality.None,
            Array.Empty<EnbW3dRenderShaderBinding>(),
            EnbW3dRenderBindingCardinality.None,
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbRenderColor32>(),
            texture is null?[]:new[] { stage0 },
            triangleBindings,
            semantics);
        return true;
    }

    private static float IntegrateScalar(IReadOnlyList<EnbCurrentRetailParticleScalarKey> keys, float time)
    {
        if (time <= 0f)
            return 0f;
        float integral = 0f;
        float cursor = 0f;
        for (int i = 1; i < keys.Count && cursor < time; i++)
        {
            EnbCurrentRetailParticleScalarKey left = keys[i - 1];
            EnbCurrentRetailParticleScalarKey right = keys[i];
            float end = MathF.Min(time, right.TimeSeconds);
            if (end <= left.TimeSeconds)
                continue;
            float duration = end - left.TimeSeconds;
            float endValue = Lerp(
                left.Value,
                right.Value,
                duration / (right.TimeSeconds - left.TimeSeconds));
            integral += (left.Value + endValue) * 0.5f * duration;
            cursor = end;
        }
        if (time > keys[^1].TimeSeconds)
            integral += keys[^1].Value * (time - keys[^1].TimeSeconds);
        return integral;
    }

    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)(value * 255f), 0, 255);

    private static float Lerp(float left, float right, float amount)
        => left + ((right - left) * amount);

    private static Vector3 Multiply(Vector3 left, Vector3 right)
        => new(left.X * right.X, left.Y * right.Y, left.Z * right.Z);

    private static uint StableSeed(
        IEnbParticleRenderOwner instance,
        uint randomSeedSalt,
        long particleOrdinal)
    {
        uint hash = 2166136261u;
        foreach (char value in instance.EmitterResourceName)
        {
            hash ^= char.ToUpperInvariant(value);
            hash *= 16777619u;
        }
        hash ^= instance.GameId;
        hash *= 16777619u;
        hash ^= (uint)instance.BindingOrdinal;
        hash *= 16777619u;
        hash ^= randomSeedSalt;
        hash *= 16777619u;
        hash ^= (uint)particleOrdinal;
        hash *= 16777619u;
        hash ^= (uint)(particleOrdinal >> 32);
        return Mix(hash);
    }

    private static float Signed(uint seed, uint stream)
    {
        uint value = Mix(seed ^ (stream * 0x9E37_79B9u));
        return ((value >> 8) * (1f / 8_388_607.5f)) - 1f;
    }

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB_352Du;
        value ^= value >> 15;
        value *= 0x846C_A68Bu;
        value ^= value >> 16;
        return value;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct ParticleState(
        Vector3 Center,
        float Size,
        byte OrientationIndex,
        EnbRenderColor32 Color, int Frame)
    {
        public long EmissionRun { get; init; }
        public int? PhysicalSlot { get; init; }
        //0095DB30/008D36C0 preserve clamped float RGBA until009BEB20.
        public Vector4 LineColor { get; init; }
        public Vector3? LocalCenter { get; init; }
        public Vector3 Tail {get;init;}
        public float FloatFrame {get;init;}
    }
}
