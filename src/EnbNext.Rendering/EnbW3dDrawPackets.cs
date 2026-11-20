using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Explicit HLOD choice supplied by the later global LOD allocator. It never
/// converts authored thresholds into a private distance rule.
/// </summary>
public readonly record struct EnbW3dHlodSelection(string Name, int LevelOrdinal);

/// <summary>
/// Final mode-zero hierarchy state-record overrides for one named hierarchy.
/// Current retail applies each matrix after authored/animated evaluation and
/// before descendants consume the record. RigidWorldTransform and
/// RenderObjectScale retain retail's separation between rigid hierarchy state
/// and mesh/pivot-translation scale. The dictionary may be updated while an
/// object graph is being assembled, but is read only during frame compile.
/// </summary>
public sealed class EnbW3dHierarchyState
{
    public string HierarchyName {get;private set;}
    public Matrix4x4 RigidWorldTransform {get;private set;}
    public float RenderObjectScale {get;private set;}
    public IReadOnlyDictionary<int,Matrix4x4> Transforms {get;private set;}
    public float? PendingHierarchyScale {get;set;}
    public float HierarchyTranslationScale=>PendingHierarchyScale??RenderObjectScale;
    public EnbW3dHierarchyState(string HierarchyName,Matrix4x4 RigidWorldTransform,float RenderObjectScale,
        IReadOnlyDictionary<int,Matrix4x4> Transforms)
    {this.HierarchyName=HierarchyName;this.RigidWorldTransform=RigidWorldTransform;this.RenderObjectScale=RenderObjectScale;this.Transforms=Transforms;}
    public void Update(string name,Matrix4x4 world,float scale,IReadOnlyDictionary<int,Matrix4x4> transforms,float? pending=null)
    {HierarchyName=name;RigidWorldTransform=world;RenderObjectScale=scale;Transforms=transforms;PendingHierarchyScale=pending;}
}

public enum EnbW3dDrawBlendClass
{
    Opaque,
    Blended,
    MixedOrUnspecified,
}

/// <summary>
/// Stable structural ordering key. It deliberately avoids randomized string
/// hashes and camera-distance policy. StaticSort carries the effective current-
/// retail level: normally the authored MeshHeader3 low byte, but also
/// reproduces the 0/10/15/20 shader fallback used when the modern loader requests
/// MeshGeometryClass::SORT. Earth & Beyond explicitly disables WW3D's separate
/// low-level SortingRenderer at startup, so BlendClass is still not authority for
/// inventing a generic camera-depth sort.
/// </summary>
public readonly record struct EnbW3dDrawOrderKey(
    EnbRenderPassKind RenderPass,
    EnbW3dDrawBlendClass BlendClass,
    EnbW3dStaticSortSemantics StaticSort,
    EnbW3dRenderResourceKey ResourceKey,
    int MeshOrdinal,
    int MaterialPassOrdinal,
    ulong RenderIdentity,
    int BindingOrdinal);

public readonly record struct EnbW3dWorldBounds(Vector3 Center, float Radius)
{
    public bool IsValid =>
        float.IsFinite(Center.X) && float.IsFinite(Center.Y) && float.IsFinite(Center.Z) &&
        float.IsFinite(Radius) && Radius >= 0f;
}

/// <summary>
/// One material pass ready for backend submission. This resolves the authored
/// uniform/per-vertex/per-triangle streams into Source.TriangleBindings, so a
/// concrete backend never has to guess how material, shader and texture IDs
/// pair for an individual triangle. SingleBindingSemantics remains a convenience
/// for the common all-uniform case.
/// </summary>
public sealed record EnbW3dDrawMaterialBinding(
    EnbW3dRenderMaterialPass Source,
    EnbW3dCompiledMaterialSemantics? SingleBindingSemantics);

/// <summary>
/// Backend-neutral queued task for one W3D mesh/material pass instance. Rigid
/// packets retain a world transform; influenced packets carry CPU-deformed
/// geometry and use identity world, matching current retail's deformation boundary.
/// Also freezes the current light environment into the packet because
/// retail deferred/sorted work snapshots lights at enqueue time. No GPU handle
/// or mutable gameplay object crosses this boundary.
/// </summary>
public sealed class EnbW3dDrawTask : IEquatable<EnbW3dDrawTask>
{
    public EnbW3dDrawTask(
    EnbW3dDrawOrderKey OrderKey,
    ulong RenderIdentity,
    ulong VisualGeneration,
    uint GameId,
    Matrix4x4 WorldTransform,
    EnbW3dWorldBounds Bounds,
    EnbW3dRenderMesh Mesh,
    EnbW3dDeformedGeometry? DeformedGeometry,
    EnbW3dDrawMaterialBinding Material,
    EnbW3dLightEnvironment LightEnvironment,
    long FrameTimeMilliseconds,
    string? HierarchyName,
    int PivotIndex,
    string RenderObjectName,
    string RenderInstancePath = "root")
    {
        _retainsPreparedState=DeformedGeometry is null&&Mesh is {CurrentRetailUsesMeshRenderer:true};
        _prepared=new(){OrderKey=OrderKey,RenderIdentity=RenderIdentity,VisualGeneration=VisualGeneration,GameId=GameId,
            Mesh=Mesh,Material=Material,HierarchyName=HierarchyName,PivotIndex=PivotIndex,RenderObjectName=RenderObjectName,RenderInstancePath=RenderInstancePath};
        _lightEnvironment=LightEnvironment;_frameTimeMilliseconds=FrameTimeMilliseconds;
        _frameStorage=new(new EnbW3dRigidFrameInputs{WorldTransform=WorldTransform,Bounds=Bounds,DeformedGeometry=DeformedGeometry,
            LightEnvironment=LightEnvironment,FrameTimeMilliseconds=FrameTimeMilliseconds});
    }
    // Called only when this physical owner's invocation slot is reused after the
    // preceding invocation drained. Queued sibling views have distinct slots.
    internal void UpdateGeneratedFrame(EnbW3dWorldBounds bounds,EnbW3dDeformedGeometry? geometry,
        EnbW3dLightEnvironment lights,long time,IReadOnlyList<Vector3>? viewPositions)
    {
        _frameStorage.Bounds=bounds;_frameStorage.DeformedGeometry=geometry;
        _frameStorage.CurrentRetailViewPositions=viewPositions;
        _lightEnvironment=lights;_frameTimeMilliseconds=time;
    }
    public EnbW3dDrawTask ForkTask()=>EnbW3dDrawTaskArena.Current?.Rent(this)??new(this);
    internal bool RetainedBeyondInvocation;
    internal EnbW3dDrawTask ReuseFrom(EnbW3dDrawTask source)
    {
        _prepared=source._prepared;_retainsPreparedState=source._retainsPreparedState;
        _frameStorage=source._frameStorage;_ownsFrame=false;source._ownsFrame=false;
        _lightEnvironment=source._lightEnvironment;_frameTimeMilliseconds=source._frameTimeMilliseconds;
        _meshInvocation=source._meshInvocation;RetainedBeyondInvocation=false;
        return this;
    }
    // A cache or UI query which outlives the drained invocation owns this task.
    // Its shared input block must also survive the invocation drain.
    public void RetainAfterDrain()
    {
        _frameStorage.Retained=true;
        if(RetainedBeyondInvocation)return;
        RetainedBeyondInvocation=true;
        _meshInvocation?.Geometry.RetainAfterDrain();
    }
    internal void ClearAfterDrain()
    {
        _prepared=null!;_lightEnvironment=null!;_meshInvocation=null;
        _frameStorage=null!;_ownsFrame=false;
    }
    public override bool Equals(object? other)=>other is EnbW3dDrawTask task&&Equals(task);

    // Prepared geometry/material identity is shared; every queued task owns its
    // mutable invocation inputs until the final flush.
    private bool _retainsPreparedState;
    private EnbW3dMeshDescriptor _prepared;
    private EnbW3dLightEnvironment _lightEnvironment=null!;
    private long _frameTimeMilliseconds;
    private FrameStorage _frameStorage;
    // One queued input block per mesh invocation, shared by its material passes.
    // A pass which changes pose/context obtains a distinct block from the arena.
    internal sealed class FrameStorage(EnbW3dRigidFrameInputs value)
    {
        internal bool Retained;
        public Matrix4x4 WorldTransform=value.WorldTransform;
        public EnbW3dWorldBounds Bounds=value.Bounds;
        public EnbW3dDeformedGeometry? DeformedGeometry=value.DeformedGeometry;


        public IReadOnlyList<Vector3>? CurrentRetailViewPositions=value.CurrentRetailViewPositions;
        public EnbCurrentRetailRslEffectPassSnapshot? CurrentRetailRslPassContext=value.CurrentRetailRslPassContext;
        public float? CurrentRetailPltDayNightPhase=value.CurrentRetailPltDayNightPhase;
        public float CurrentRetailMeshModelScale=value.CurrentRetailMeshModelScale;
        public Vector2? CurrentRetailStage0UvOffset=value.CurrentRetailStage0UvOffset;
        public EnbW3dEffectFrameInputs? Effects=value.Effects;
        public EnbW3dUnscaledFrameTransform? Unscaled=value.Unscaled;
        public void CopyFrom(FrameStorage source)
        {
            WorldTransform=source.WorldTransform;Bounds=source.Bounds;DeformedGeometry=source.DeformedGeometry;
            CurrentRetailViewPositions=source.CurrentRetailViewPositions;CurrentRetailRslPassContext=source.CurrentRetailRslPassContext;
            CurrentRetailPltDayNightPhase=source.CurrentRetailPltDayNightPhase;CurrentRetailMeshModelScale=source.CurrentRetailMeshModelScale;
            CurrentRetailStage0UvOffset=source.CurrentRetailStage0UvOffset;Effects=source.Effects;Unscaled=source.Unscaled;
        }
        public void Reset()
        {DeformedGeometry=null;CurrentRetailViewPositions=null;CurrentRetailRslPassContext=null;Effects=null;Unscaled=null;}
        public EnbW3dRigidFrameInputs Snapshot()=>new()
        {
            WorldTransform=WorldTransform,
            Bounds=Bounds,
            DeformedGeometry=DeformedGeometry,


            CurrentRetailViewPositions=CurrentRetailViewPositions,
            CurrentRetailRslPassContext=CurrentRetailRslPassContext,
            CurrentRetailPltDayNightPhase=CurrentRetailPltDayNightPhase,
            CurrentRetailMeshModelScale=CurrentRetailMeshModelScale,
            CurrentRetailStage0UvOffset=CurrentRetailStage0UvOffset,
            Effects=Effects,
            Unscaled=Unscaled,
        };
    }
    // Binding a material pass shares its invocation inputs until an actual change.
    private EnbCurrentRetailMeshInvocation? _meshInvocation;
    private bool _ownsFrame=true;
    private FrameStorage OwnFrame()
    {
        if(!_ownsFrame)
        {
            var storage=EnbW3dDrawTaskArena.Current?.RentFrame(_frameStorage)??new FrameStorage(new EnbW3dRigidFrameInputs());
            if(EnbW3dDrawTaskArena.Current is null)storage.CopyFrom(_frameStorage);
            _frameStorage=storage;_ownsFrame=true;
        }
        return _frameStorage;
    }
    private EnbW3dRigidFrameInputs _frame=>_frameStorage.Snapshot() with {LightEnvironment=_lightEnvironment,FrameTimeMilliseconds=_frameTimeMilliseconds};
    private EnbW3dDrawTask(EnbW3dDrawTask source)
    {
        _prepared=source._prepared;
        _retainsPreparedState=source._retainsPreparedState;
        _frameStorage=source._frameStorage;_ownsFrame=false;source._ownsFrame=false;
        _lightEnvironment=source._lightEnvironment;
        _frameTimeMilliseconds=source._frameTimeMilliseconds;
        _meshInvocation=source._meshInvocation;
    }
    public bool Equals(EnbW3dDrawTask? other)=>ReferenceEquals(this,other)||
        other is not null && _prepared==other._prepared && _frame==other._frame &&
        ReferenceEquals(_meshInvocation,other._meshInvocation);
    public override int GetHashCode()=>HashCode.Combine(_prepared,_frame,_meshInvocation);
    public EnbW3dRigidFrameInputs FrameInputs => _frame with {CurrentRetailMeshInvocation=_meshInvocation};
    /// <summary>Consumer-owned binding for this immutable prepared definition.
    /// Frame copies share it; a changed definition starts with no binding.
    /// Consumers must check their context and resource lifetime before reuse.</summary>
    public object? GetPreparedConsumerState()=>_prepared.ConsumerState;
    public void SetPreparedConsumerState(object? value)=>_prepared.ConsumerState=value;
    public EnbW3dMeshOccurrenceIdentity? MeshOccurrence { get => _prepared.MeshOccurrence; internal set => _prepared.MeshOccurrence=value; }
    internal object PreparedIdentity => _prepared;
    internal EnbW3dMeshDescriptor PreparedState => _prepared;
    internal EnbW3dDrawTask WithPreparedState(EnbW3dMeshDescriptor state)
    {
        if(ReferenceEquals(_prepared,state))return this;
        var copy=ForkTask();
        copy._prepared=state;
        return copy;
    }
    internal EnbW3dDrawTask SnapshotFrame(Matrix4x4 world,EnbW3dWorldBounds bounds,
        EnbW3dDeformedGeometry? geometry,EnbW3dLightEnvironment lights,long time,float modelScale)
    {
        var copy=ForkTask();
        var frame=copy.OwnFrame();
        frame.WorldTransform=world;frame.Bounds=bounds;frame.DeformedGeometry=geometry;
        copy._lightEnvironment=lights;copy._frameTimeMilliseconds=time;frame.CurrentRetailMeshModelScale=modelScale;
        return copy;
    }

    // Additional material passes share this queued mesh invocation, not a world snapshot.
    internal EnbW3dDrawTask SnapshotFrameFrom(EnbW3dDrawTask invocation)
    {
        var copy=ForkTask();
        copy._frameStorage=invocation._frameStorage;copy._ownsFrame=false;invocation._ownsFrame=false;
        copy._lightEnvironment=invocation._lightEnvironment;
        copy._frameTimeMilliseconds=invocation._frameTimeMilliseconds;
        return copy;
    }

    internal void RetainDefinition() { _retainsPreparedState=true;RetainAfterDrain(); }
    public EnbW3dDrawOrderKey OrderKey
    {
        get => _prepared.OrderKey;
        set
        {
            if (!EnbPreparedPacketTransitions<EnbW3dDrawOrderKey>.Same(_prepared.OrderKey, value))
                _prepared = EnbPreparedPacketTransitions<EnbW3dDrawOrderKey>.Set(_prepared, _retainsPreparedState, 0, value, static (state, input) => state with { OrderKey = input });
        }
    }
    public ulong RenderIdentity
    {
        get => _prepared.RenderIdentity;
        set
        {
            if (!EnbPreparedPacketTransitions<ulong>.Same(_prepared.RenderIdentity, value))
                _prepared = EnbPreparedPacketTransitions<ulong>.Set(_prepared, _retainsPreparedState, 1, value, static (state, input) => state with { RenderIdentity = input });
        }
    }
    public ulong VisualGeneration
    {
        get => _prepared.VisualGeneration;
        set
        {
            if (!EnbPreparedPacketTransitions<ulong>.Same(_prepared.VisualGeneration, value))
                _prepared = EnbPreparedPacketTransitions<ulong>.Set(_prepared, _retainsPreparedState, 2, value, static (state, input) => state with { VisualGeneration = input });
        }
    }
    public uint GameId
    {
        get => _prepared.GameId;
        set
        {
            if (!EnbPreparedPacketTransitions<uint>.Same(_prepared.GameId, value))
                _prepared = EnbPreparedPacketTransitions<uint>.Set(_prepared, _retainsPreparedState, 3, value, static (state, input) => state with { GameId = input });
        }
    }
    public Matrix4x4 WorldTransform { get => _frameStorage.WorldTransform; set { OwnFrame().WorldTransform=value; } }
    public EnbW3dWorldBounds Bounds { get => _frameStorage.Bounds; set { OwnFrame().Bounds=value; } }
    public EnbW3dRenderMesh Mesh
    {
        get => _prepared.Mesh;
        set
        {
            if (!EnbPreparedPacketTransitions<EnbW3dRenderMesh>.Same(_prepared.Mesh, value))
                _prepared = EnbPreparedPacketTransitions<EnbW3dRenderMesh>.Set(_prepared, _retainsPreparedState, 4, value, static (state, input) => state with { Mesh = input });
        }
    }
    public EnbW3dDeformedGeometry? DeformedGeometry { get => _frameStorage.DeformedGeometry; set { if(!ReferenceEquals(_frameStorage.DeformedGeometry,value)) OwnFrame().DeformedGeometry=value; } }
    public EnbW3dDrawMaterialBinding Material
    {
        get => _prepared.Material;
        set
        {
            if (!EnbPreparedPacketTransitions<EnbW3dDrawMaterialBinding>.Same(_prepared.Material, value))
                _prepared = EnbPreparedPacketTransitions<EnbW3dDrawMaterialBinding>.Set(_prepared, _retainsPreparedState, 5, value, static (state, input) => state with { Material = input });
        }
    }
    public EnbW3dLightEnvironment LightEnvironment { get => _lightEnvironment; set => _lightEnvironment=value; }
    public long FrameTimeMilliseconds { get => _frameTimeMilliseconds; set => _frameTimeMilliseconds=value; }
    public string? HierarchyName
    {
        get => _prepared.HierarchyName;
        set
        {
            if (!EnbPreparedPacketTransitions<string?>.Same(_prepared.HierarchyName, value))
                _prepared = EnbPreparedPacketTransitions<string?>.Set(_prepared, _retainsPreparedState, 6, value, static (state, input) => state with { HierarchyName = input });
        }
    }
    public int PivotIndex
    {
        get => _prepared.PivotIndex;
        set
        {
            if (!EnbPreparedPacketTransitions<int>.Same(_prepared.PivotIndex, value))
                _prepared = EnbPreparedPacketTransitions<int>.Set(_prepared, _retainsPreparedState, 7, value, static (state, input) => state with { PivotIndex = input });
        }
    }
    public string RenderObjectName
    {
        get => _prepared.RenderObjectName;
        set
        {
            if (!EnbPreparedPacketTransitions<string>.Same(_prepared.RenderObjectName, value))
                _prepared = EnbPreparedPacketTransitions<string>.Set(_prepared, _retainsPreparedState, 8, value, static (state, input) => state with { RenderObjectName = input });
        }
    }

    /// <summary>009BEB20 emits connected-line vertices in saved view space and
    /// submits with identity world/view. Keep the exact GPU stream separately
    /// from world-space geometry used by scene bounds and triangle ordering.</summary>
    public IReadOnlyList<Vector3>? CurrentRetailViewPositions { get => _frameStorage.CurrentRetailViewPositions; set { if(!ReferenceEquals(_frameStorage.CurrentRetailViewPositions,value)) OwnFrame().CurrentRetailViewPositions=value; } }
    /// <summary>Transient particle geometry with a retained buffer owner. GPU
    /// storage may be streamed; material compatibility is checked separately.</summary>
    public bool CurrentRetailParticleBuffer
    {
        get => _prepared.CurrentRetailParticleBuffer;
        set
        {
            if (!EnbPreparedPacketTransitions<bool>.Same(_prepared.CurrentRetailParticleBuffer, value))
                _prepared = EnbPreparedPacketTransitions<bool>.Set(_prepared, _retainsPreparedState, 9, value, static (state, input) => state with { CurrentRetailParticleBuffer = input });
        }
    }
    public EnbRetailSpecialMaterialDraw? SpecialMaterial
    {
        get => _prepared.SpecialMaterial;
        set
        {
            if (!EnbPreparedPacketTransitions<EnbRetailSpecialMaterialDraw?>.Same(_prepared.SpecialMaterial, value))
                _prepared = EnbPreparedPacketTransitions<EnbRetailSpecialMaterialDraw?>.Set(_prepared, _retainsPreparedState, 10, value, static (state, input) => state with { SpecialMaterial = input });
        }
    }
    public EnbRetailSpecialMaterialProjection? SpecialMaterialProjection
    {
        get => _prepared.SpecialMaterialProjection;
        set
        {
            if (!EnbPreparedPacketTransitions<EnbRetailSpecialMaterialProjection?>.Same(_prepared.SpecialMaterialProjection, value))
                _prepared = EnbPreparedPacketTransitions<EnbRetailSpecialMaterialProjection?>.Set(_prepared, _retainsPreparedState, 11, value, static (state, input) => state with { SpecialMaterialProjection = input });
        }
    }
    /// <summary>00460FF0 parent scope.0093F3ED's static-sort path does not
    /// retain it; immediate meshes consume it during their native enqueue.</summary>
    public EnbCurrentRetailRslEffectPassSnapshot? CurrentRetailRslPassContext { get => _frameStorage.CurrentRetailRslPassContext; set { if(!ReferenceEquals(_frameStorage.CurrentRetailRslPassContext,value)) OwnFrame().CurrentRetailRslPassContext=value; } }
    public EnbCurrentRetailMeshInvocation? CurrentRetailMeshInvocation { get => _meshInvocation; set => _meshInvocation=value; }
    /// <summary>A separately attached material pass, not a base descriptor
    /// selected by its ordinal or resource name.</summary>
    public uint? CurrentRetailAdditionalMeshPassFlags
    {
        get => _prepared.CurrentRetailAdditionalMeshPassFlags;
        set
        {
            if (!EnbPreparedPacketTransitions<uint?>.Same(_prepared.CurrentRetailAdditionalMeshPassFlags, value))
                _prepared = EnbPreparedPacketTransitions<uint?>.Set(_prepared, _retainsPreparedState, 12, value, static (state, input) => state with { CurrentRetailAdditionalMeshPassFlags = input });
        }
    }
    /// <summary>00474B30 streams sea topology/UVs while retaining swatch materials.</summary>
    public bool CurrentRetailTerrainSea
    {
        get => _prepared.CurrentRetailTerrainSea;
        set
        {
            if (!EnbPreparedPacketTransitions<bool>.Same(_prepared.CurrentRetailTerrainSea, value))
                _prepared = EnbPreparedPacketTransitions<bool>.Set(_prepared, _retainsPreparedState, 13, value, static (state, input) => state with { CurrentRetailTerrainSea = input });
        }
    }
    /// <summary>00466F60 displacement: streamed topology and shader clip-Z fog.</summary>
    public bool CurrentRetailTerrainDisplacement
    {
        get => _prepared.CurrentRetailTerrainDisplacement;
        set
        {
            if (!EnbPreparedPacketTransitions<bool>.Same(_prepared.CurrentRetailTerrainDisplacement, value))
                _prepared = EnbPreparedPacketTransitions<bool>.Set(_prepared, _retainsPreparedState, 14, value, static (state, input) => state with { CurrentRetailTerrainDisplacement = input });
        }
    }
    /// <summary>Legacy device render state47; positive values separate coplanar shadows toward the camera.</summary>
    public uint CurrentRetailDepthBias
    {
        get => _prepared.CurrentRetailDepthBias;
        set
        {
            if (!EnbPreparedPacketTransitions<uint>.Same(_prepared.CurrentRetailDepthBias, value))
                _prepared = EnbPreparedPacketTransitions<uint>.Set(_prepared, _retainsPreparedState, 15, value, static (state, input) => state with { CurrentRetailDepthBias = input });
        }
    }
    /// <summary>0097AF40's decal queue, flushed after the mesh containers.
    /// The key identifies one mesh owner; that queue prepends mesh owners.</summary>
    public string? CurrentRetailProjectedDecalOwner
    {
        get => _prepared.CurrentRetailProjectedDecalOwner;
        set
        {
            if (!EnbPreparedPacketTransitions<string?>.Same(_prepared.CurrentRetailProjectedDecalOwner, value))
                _prepared = EnbPreparedPacketTransitions<string?>.Set(_prepared, _retainsPreparedState, 16, value, static (state, input) => state with { CurrentRetailProjectedDecalOwner = input });
        }
    }

    /// <summary>
    /// Optional current-retail MainView AdjustDayNight phase. This is runtime
    /// scene state, not authored W3D material data. This applies it only to
    /// resolved plt_dn* bindings whose authored mapper is the proven linear
    /// family. Null retains the W3D's authored mapper behavior.
    /// </summary>
    public float? CurrentRetailPltDayNightPhase { get => _frameStorage.CurrentRetailPltDayNightPhase; set
    {
        // An appearance-only copy often carries the same optional phase. Do not
        // duplicate the complete pose payload unless its actual words change.
        var previous=_frameStorage.CurrentRetailPltDayNightPhase;
        if(previous.HasValue!=value.HasValue || (value.HasValue &&
            BitConverter.SingleToInt32Bits(previous!.Value)!=BitConverter.SingleToInt32Bits(value.Value)))
            OwnFrame().CurrentRetailPltDayNightPhase=value;
    } }

    /// <summary>Stable composed instance identity within the owning object.
    /// Repeated engines/weapons may share resource and binding ordinals but
    /// must not overwrite each other's instance materials or mapper state.</summary>
    public string RenderInstancePath
    {
        get => _prepared.RenderInstancePath;
        set
        {
            if (!EnbPreparedPacketTransitions<string>.Same(_prepared.RenderInstancePath, value))
                _prepared = EnbPreparedPacketTransitions<string>.Set(_prepared, _retainsPreparedState, 17, value, static (state, input) => state with { RenderInstancePath = input });
        }
    }

    // Applied packet revision, separate from the RenderObj/animation generation.
    public int CurrentRetailColorizationRevision
    {
        get => _prepared.CurrentRetailColorizationRevision;
        set
        {
            if (!EnbPreparedPacketTransitions<int>.Same(_prepared.CurrentRetailColorizationRevision, value))
                _prepared = EnbPreparedPacketTransitions<int>.Set(_prepared, _retainsPreparedState, 18, value, static (state, input) => state with { CurrentRetailColorizationRevision = input });
        }
    }
    public int CurrentRetailNameDecalRevision
    {
        get => _prepared.CurrentRetailNameDecalRevision;
        set
        {
            if (!EnbPreparedPacketTransitions<int>.Same(_prepared.CurrentRetailNameDecalRevision, value))
                _prepared = EnbPreparedPacketTransitions<int>.Set(_prepared, _retainsPreparedState, 19, value, static (state, input) => state with { CurrentRetailNameDecalRevision = input });
        }
    }
    public int CurrentRetailDecalRevision
    {
        get => _prepared.CurrentRetailDecalRevision;
        set
        {
            if (!EnbPreparedPacketTransitions<int>.Same(_prepared.CurrentRetailDecalRevision, value))
                _prepared = EnbPreparedPacketTransitions<int>.Set(_prepared, _retainsPreparedState, 20, value, static (state, input) => state with { CurrentRetailDecalRevision = input });
        }
    }

    /// <summary>
    /// Current-retail Create.Scale ownership represented without mutating the
    /// immutable compiled mesh. Retail applies this scalar to MeshModel geometry
    /// before deferred presentation; camera-aligned/oriented submission later
    /// replaces the RenderObj matrix basis entirely and therefore cannot recover
    /// this scale from WorldTransform.
    /// </summary>
    public float CurrentRetailMeshModelScale { get => _frameStorage.CurrentRetailMeshModelScale; set { OwnFrame().CurrentRetailMeshModelScale=value; } }
    // Zero MeshModel scale destroys the basis in our immutable-geometry matrix
    // representation. Retain the actual RenderObj matrix for normal submission.
    public Matrix4x4? CurrentRetailUnscaledWorldTransform { get => _frameStorage.Unscaled?.Transform; set { if(value is {} transform)OwnFrame().Unscaled=new(transform); else if(_frameStorage.Unscaled is not null)OwnFrame().Unscaled=null; } }

    /// <summary>UVController runtime offset for material zero/stage zero (retail
    /// 004A13A0). Unlike authored scrolling, this replaces the linear offset.
    /// The UI gauge controller owns it; other materials retain their clocks.</summary>
    public Vector2? CurrentRetailStage0UvOffset { get => _frameStorage.CurrentRetailStage0UvOffset; set { OwnFrame().CurrentRetailStage0UvOffset=value; } }
    /// <summary>PassEffect's ship-relative projector, followed by its shared swatch mapper.</summary>
    public Matrix4x4? CurrentRetailEffectProjection
    {
        get => _frameStorage.Effects?.CurrentRetailEffectProjection;
        set
        {
            if(_frameStorage.Effects?.CurrentRetailEffectProjection != value)
                OwnFrame().Effects = (_frameStorage.Effects ?? EnbW3dEffectFrameInputs.Empty) with { CurrentRetailEffectProjection = value };
        }
    }
    // Retained material mapper objects can outlive any individual GPU owner.
    public EnbW3dSharedMaterialMappers? CurrentRetailSharedMaterialMappers
    {
        get => _prepared.CurrentRetailSharedMaterialMappers;
        set
        {
            if (!EnbPreparedPacketTransitions<EnbW3dSharedMaterialMappers?>.Same(_prepared.CurrentRetailSharedMaterialMappers, value))
                _prepared = EnbPreparedPacketTransitions<EnbW3dSharedMaterialMappers?>.Set(_prepared, _retainsPreparedState, 22, value, static (state, input) => state with { CurrentRetailSharedMaterialMappers = input });
        }
    }

    /// <summary>The retained loaded table after instance material writes, in
    /// loader order including unused entries and duplicate pointers. Null uses
    /// the immutable mesh's original table; descriptor membership is separate.</summary>
    public IReadOnlyList<EnbW3dRenderVertexMaterial>? CurrentRetailLoadedMaterials
    {
        get => _prepared.CurrentRetailLoadedMaterials;
        set
        {
            if (!EnbPreparedPacketTransitions<IReadOnlyList<EnbW3dRenderVertexMaterial>?>.Same(_prepared.CurrentRetailLoadedMaterials, value))
                _prepared = EnbPreparedPacketTransitions<IReadOnlyList<EnbW3dRenderVertexMaterial>?>.Set(_prepared, _retainsPreparedState, 23, value, static (state, input) => state with { CurrentRetailLoadedMaterials = input });
        }
    }
    /// <summary>Replacement texture table. Null retains the loader's table;
    /// generated unregistered TextureClass owners are retained directly.</summary>
    public IReadOnlyList<EnbCurrentRetailTextureObjectState>? CurrentRetailLoadedTextures
    {
        get => _prepared.CurrentRetailLoadedTextures;
        set
        {
            if (!EnbPreparedPacketTransitions<IReadOnlyList<EnbCurrentRetailTextureObjectState>?>.Same(_prepared.CurrentRetailLoadedTextures, value))
                _prepared = EnbPreparedPacketTransitions<IReadOnlyList<EnbCurrentRetailTextureObjectState>?>.Set(_prepared, _retainsPreparedState, 24, value, static (state, input) => state with { CurrentRetailLoadedTextures = input });
        }
    }

    /// <summary>Material-reference owners for expanded descriptors. Each indexed
    /// run selects its own material's mapper pair (0097A230/0092E720). Callers
    /// retain this reference-keyed table with the model, independently of GPU
    /// uploads; the uniform swatch pair remains the single-material form.</summary>
    public IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>? CurrentRetailMaterialMappers
    {
        get => _prepared.CurrentRetailMaterialMappers;
        set
        {
            if (!EnbPreparedPacketTransitions<IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>?>.Same(_prepared.CurrentRetailMaterialMappers, value))
                _prepared = EnbPreparedPacketTransitions<IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>?>.Set(_prepared, _retainsPreparedState, 25, value, static (state, input) => state with { CurrentRetailMaterialMappers = input });
        }
    }

    public EnbW3dMapperRuntimeEvaluation? CurrentRetailEffectMapper
    {
        get => _frameStorage.Effects?.CurrentRetailEffectMapper;
        set
        {
            if(_frameStorage.Effects?.CurrentRetailEffectMapper != value)
                OwnFrame().Effects = (_frameStorage.Effects ?? EnbW3dEffectFrameInputs.Empty) with { CurrentRetailEffectMapper = value };
        }
    }
    public Matrix4x4? CurrentRetailEffectProjection1
    {
        get => _frameStorage.Effects?.CurrentRetailEffectProjection1;
        set
        {
            if(_frameStorage.Effects?.CurrentRetailEffectProjection1 != value)
                OwnFrame().Effects = (_frameStorage.Effects ?? EnbW3dEffectFrameInputs.Empty) with { CurrentRetailEffectProjection1 = value };
        }
    }
    public EnbW3dMapperRuntimeEvaluation? CurrentRetailEffectMapper1
    {
        get => _frameStorage.Effects?.CurrentRetailEffectMapper1;
        set
        {
            if(_frameStorage.Effects?.CurrentRetailEffectMapper1 != value)
                OwnFrame().Effects = (_frameStorage.Effects ?? EnbW3dEffectFrameInputs.Empty) with { CurrentRetailEffectMapper1 = value };
        }
    }
    /// <summary>RObjEffect ResetMappers owns an epoch distinct from its source ship.</summary>
    public uint? CurrentRetailEffectMapperStartMilliseconds
    {
        get => _prepared.CurrentRetailEffectMapperStartMilliseconds;
        set
        {
            if (!EnbPreparedPacketTransitions<uint?>.Same(_prepared.CurrentRetailEffectMapperStartMilliseconds, value))
                _prepared = EnbPreparedPacketTransitions<uint?>.Set(_prepared, _retainsPreparedState, 29, value, static (state, input) => state with { CurrentRetailEffectMapperStartMilliseconds = input });
        }
    }

    public EnbW3dDrawTask SetOrderKey(EnbW3dDrawOrderKey value){OrderKey=value;return this;}
    public EnbW3dDrawTask SetRenderIdentity(ulong value){RenderIdentity=value;return this;}
    public EnbW3dDrawTask SetVisualGeneration(ulong value){VisualGeneration=value;return this;}
    public EnbW3dDrawTask SetGameId(uint value){GameId=value;return this;}
    public EnbW3dDrawTask SetWorldTransform(Matrix4x4 value){WorldTransform=value;return this;}
    public EnbW3dDrawTask SetBounds(EnbW3dWorldBounds value){Bounds=value;return this;}
    public EnbW3dDrawTask SetMesh(EnbW3dRenderMesh value){Mesh=value;return this;}
    public EnbW3dDrawTask SetDeformedGeometry(EnbW3dDeformedGeometry? value){DeformedGeometry=value;return this;}
    public EnbW3dDrawTask SetMaterial(EnbW3dDrawMaterialBinding value){Material=value;return this;}
    public EnbW3dDrawTask SetLightEnvironment(EnbW3dLightEnvironment value){LightEnvironment=value;return this;}
    public EnbW3dDrawTask SetFrameTimeMilliseconds(long value){FrameTimeMilliseconds=value;return this;}
    public EnbW3dDrawTask SetHierarchyName(string? value){HierarchyName=value;return this;}
    public EnbW3dDrawTask SetPivotIndex(int value){PivotIndex=value;return this;}
    public EnbW3dDrawTask SetRenderObjectName(string value){RenderObjectName=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailViewPositions(IReadOnlyList<Vector3>? value){CurrentRetailViewPositions=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailParticleBuffer(bool value){CurrentRetailParticleBuffer=value;return this;}
    public EnbW3dDrawTask SetSpecialMaterial(EnbRetailSpecialMaterialDraw? value){SpecialMaterial=value;return this;}
    public EnbW3dDrawTask SetSpecialMaterialProjection(EnbRetailSpecialMaterialProjection? value){SpecialMaterialProjection=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailRslPassContext(EnbCurrentRetailRslEffectPassSnapshot? value){CurrentRetailRslPassContext=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailMeshInvocation(EnbCurrentRetailMeshInvocation? value){CurrentRetailMeshInvocation=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailAdditionalMeshPassFlags(uint? value){CurrentRetailAdditionalMeshPassFlags=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailTerrainSea(bool value){CurrentRetailTerrainSea=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailTerrainDisplacement(bool value){CurrentRetailTerrainDisplacement=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailDepthBias(uint value){CurrentRetailDepthBias=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailProjectedDecalOwner(string? value){CurrentRetailProjectedDecalOwner=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailPltDayNightPhase(float? value){CurrentRetailPltDayNightPhase=value;return this;}
    public EnbW3dDrawTask SetRenderInstancePath(string value){RenderInstancePath=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailColorizationRevision(int value){CurrentRetailColorizationRevision=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailNameDecalRevision(int value){CurrentRetailNameDecalRevision=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailDecalRevision(int value){CurrentRetailDecalRevision=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailMeshModelScale(float value){CurrentRetailMeshModelScale=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailUnscaledWorldTransform(Matrix4x4? value){CurrentRetailUnscaledWorldTransform=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailStage0UvOffset(Vector2? value){CurrentRetailStage0UvOffset=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailEffectProjection(Matrix4x4? value){CurrentRetailEffectProjection=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailSharedMaterialMappers(EnbW3dSharedMaterialMappers? value){CurrentRetailSharedMaterialMappers=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailLoadedMaterials(IReadOnlyList<EnbW3dRenderVertexMaterial>? value){CurrentRetailLoadedMaterials=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailLoadedTextures(IReadOnlyList<EnbCurrentRetailTextureObjectState>? value){CurrentRetailLoadedTextures=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailMaterialMappers(IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>? value){CurrentRetailMaterialMappers=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailEffectMapper(EnbW3dMapperRuntimeEvaluation? value){CurrentRetailEffectMapper=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailEffectProjection1(Matrix4x4? value){CurrentRetailEffectProjection1=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailEffectMapper1(EnbW3dMapperRuntimeEvaluation? value){CurrentRetailEffectMapper1=value;return this;}
    public EnbW3dDrawTask SetCurrentRetailEffectMapperStartMilliseconds(uint? value){CurrentRetailEffectMapperStartMilliseconds=value;return this;}
}

/// <summary>Per-view value inputs, independent of persistent packet identity.
/// Includes deformation/context references so generated and skin work stays live.</summary>
public readonly record struct EnbW3dRigidFrameInputs
{
    public EnbW3dRigidFrameInputs() { }
    internal EnbW3dEffectFrameInputs? Effects { get; init; }
    public Matrix4x4 WorldTransform { get; init; }
    public EnbW3dWorldBounds Bounds { get; init; }
    public EnbW3dDeformedGeometry? DeformedGeometry { get; init; }
    public EnbW3dLightEnvironment LightEnvironment { get; init; } = default!;
    public long FrameTimeMilliseconds { get; init; }
    public IReadOnlyList<Vector3>? CurrentRetailViewPositions { get; init; }
    public EnbCurrentRetailRslEffectPassSnapshot? CurrentRetailRslPassContext { get; init; }
    public EnbCurrentRetailMeshInvocation? CurrentRetailMeshInvocation { get; init; }
    public float? CurrentRetailPltDayNightPhase { get; init; }
    public float CurrentRetailMeshModelScale { get; init; } = 1f;
    // Only a zero factory scale needs a second matrix. Ordinary tasks retain
    // their single world matrix; immutable copies share this rare payload.
    private readonly EnbW3dUnscaledFrameTransform? _unscaled;
    internal EnbW3dUnscaledFrameTransform? Unscaled {get=>_unscaled;init=>_unscaled=value;}
    public Matrix4x4? CurrentRetailUnscaledWorldTransform
    {
        get => _unscaled?.Transform;
        init => _unscaled = value is {} transform ? new(transform) : null;
    }
    public Vector2? CurrentRetailStage0UvOffset { get; init; }
}

internal sealed record EnbW3dUnscaledFrameTransform(Matrix4x4 Transform);

internal sealed record EnbW3dEffectFrameInputs
{
    internal static readonly EnbW3dEffectFrameInputs Empty=new();
    public Matrix4x4? CurrentRetailEffectProjection { get; init; }
    public Matrix4x4? CurrentRetailEffectProjection1 { get; init; }
    public EnbW3dMapperRuntimeEvaluation? CurrentRetailEffectMapper { get; init; }
    public EnbW3dMapperRuntimeEvaluation? CurrentRetailEffectMapper1 { get; init; }
}

internal sealed record EnbW3dMeshDescriptor
{
    public EnbW3dDrawOrderKey OrderKey { get; init; } = default!;
    public ulong RenderIdentity { get; init; } = default!;
    public ulong VisualGeneration { get; init; } = default!;
    public uint GameId { get; init; } = default!;
    public EnbW3dRenderMesh Mesh { get; init; } = default!;
    public EnbW3dDrawMaterialBinding Material { get; init; } = default!;
    public string? HierarchyName { get; init; } = default!;
    public int PivotIndex { get; init; } = default!;
    public string RenderObjectName { get; init; } = default!;
    public bool CurrentRetailParticleBuffer { get; init; } = default!;
    public EnbRetailSpecialMaterialDraw? SpecialMaterial { get; init; } = default!;
    public EnbRetailSpecialMaterialProjection? SpecialMaterialProjection { get; init; } = default!;
    public uint? CurrentRetailAdditionalMeshPassFlags { get; init; } = default!;
    public bool CurrentRetailTerrainSea { get; init; } = default!;
    public bool CurrentRetailTerrainDisplacement { get; init; } = default!;
    public uint CurrentRetailDepthBias { get; init; } = default!;
    public string? CurrentRetailProjectedDecalOwner { get; init; } = default!;
    public string RenderInstancePath { get; init; } = "root";
    public int CurrentRetailColorizationRevision { get; init; } = default!;
    public int CurrentRetailNameDecalRevision { get; init; } = default!;
    public int CurrentRetailDecalRevision { get; init; } = default!;
    public EnbW3dSharedMaterialMappers? CurrentRetailSharedMaterialMappers { get; init; } = default!;
    public IReadOnlyList<EnbW3dRenderVertexMaterial>? CurrentRetailLoadedMaterials { get; init; } = default!;
    public IReadOnlyList<EnbCurrentRetailTextureObjectState>? CurrentRetailLoadedTextures { get; init; } = default!;
    public IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>? CurrentRetailMaterialMappers { get; init; } = default!;
    public uint? CurrentRetailEffectMapperStartMilliseconds { get; init; } = default!;
    internal EnbW3dMeshOccurrenceIdentity? MeshOccurrence;
    internal EnbPreparedPacketTransitionCache.Owner? TransitionOwner;
    public EnbW3dMeshDescriptor() { }
    private EnbW3dMeshDescriptor(EnbW3dMeshDescriptor original)
    {
        MeshOccurrence=original.MeshOccurrence;
        OrderKey=original.OrderKey;
        RenderIdentity=original.RenderIdentity;
        VisualGeneration=original.VisualGeneration;
        GameId=original.GameId;
        Mesh=original.Mesh;
        Material=original.Material;
        HierarchyName=original.HierarchyName;
        PivotIndex=original.PivotIndex;
        RenderObjectName=original.RenderObjectName;
        CurrentRetailParticleBuffer=original.CurrentRetailParticleBuffer;
        SpecialMaterial=original.SpecialMaterial;
        SpecialMaterialProjection=original.SpecialMaterialProjection;
        CurrentRetailAdditionalMeshPassFlags=original.CurrentRetailAdditionalMeshPassFlags;
        CurrentRetailTerrainSea=original.CurrentRetailTerrainSea;
        CurrentRetailTerrainDisplacement=original.CurrentRetailTerrainDisplacement;
        CurrentRetailDepthBias=original.CurrentRetailDepthBias;
        CurrentRetailProjectedDecalOwner=original.CurrentRetailProjectedDecalOwner;
        RenderInstancePath=original.RenderInstancePath;
        CurrentRetailColorizationRevision=original.CurrentRetailColorizationRevision;
        CurrentRetailNameDecalRevision=original.CurrentRetailNameDecalRevision;
        CurrentRetailDecalRevision=original.CurrentRetailDecalRevision;
        CurrentRetailSharedMaterialMappers=original.CurrentRetailSharedMaterialMappers;
        CurrentRetailLoadedMaterials=original.CurrentRetailLoadedMaterials;
        CurrentRetailLoadedTextures=original.CurrentRetailLoadedTextures;
        CurrentRetailMaterialMappers=original.CurrentRetailMaterialMappers;
        CurrentRetailEffectMapperStartMilliseconds=original.CurrentRetailEffectMapperStartMilliseconds;
        // Original-definition consumer bindings are unusable by this clone.
        // Leave them empty instead of retaining the retired source through them.
        // A copied descriptor is a different cache key. Never copy its outgoing
        // transition cache back into the child and retain the previous owner.
    }
    private sealed class ConsumerBinding(EnbW3dMeshDescriptor definition,object value)
    {internal readonly EnbW3dMeshDescriptor Definition=definition;internal readonly object Value=value;}
    private ConsumerBinding? _consumer;
    internal object? ConsumerState
    {
        get{var binding=Volatile.Read(ref _consumer);return ReferenceEquals(binding?.Definition,this)?binding.Value:null;}
        set=>Volatile.Write(ref _consumer,value is null?null:new ConsumerBinding(this,value));
    }
    private ConsumerBinding? _meshPreparation;
    internal object? MeshPreparation
    {
        get { var binding=Volatile.Read(ref _meshPreparation); return ReferenceEquals(binding?.Definition,this)?binding.Value:null; }
        set => Volatile.Write(ref _meshPreparation,value is null?null:new ConsumerBinding(this,value));
    }
    private ConsumerBinding? _queryProjection;
    internal EnbSubmittedProjectionBinding? QueryProjection
    {
        get { var binding=Volatile.Read(ref _queryProjection); return ReferenceEquals(binding?.Definition,this)?binding.Value as EnbSubmittedProjectionBinding:null; }
        set => Volatile.Write(ref _queryProjection,value is null?null:new ConsumerBinding(this,value));
    }
    // Consumer bookkeeping is deliberately outside descriptor value identity.
    // A descriptor clone starts without these slots; the definition check prevents it
    // inheriting another definition's backend state.
    public bool Equals(EnbW3dMeshDescriptor? other)=>ReferenceEquals(this,other) || other is not null &&
        EqualityComparer<EnbW3dDrawOrderKey>.Default.Equals(OrderKey,other.OrderKey) &&
        EqualityComparer<ulong>.Default.Equals(RenderIdentity,other.RenderIdentity) &&
        EqualityComparer<ulong>.Default.Equals(VisualGeneration,other.VisualGeneration) &&
        EqualityComparer<uint>.Default.Equals(GameId,other.GameId) &&
        EqualityComparer<EnbW3dRenderMesh>.Default.Equals(Mesh,other.Mesh) &&
        EqualityComparer<EnbW3dDrawMaterialBinding>.Default.Equals(Material,other.Material) &&
        EqualityComparer<string?>.Default.Equals(HierarchyName,other.HierarchyName) &&
        EqualityComparer<int>.Default.Equals(PivotIndex,other.PivotIndex) &&
        EqualityComparer<string>.Default.Equals(RenderObjectName,other.RenderObjectName) &&
        EqualityComparer<bool>.Default.Equals(CurrentRetailParticleBuffer,other.CurrentRetailParticleBuffer) &&
        EqualityComparer<EnbRetailSpecialMaterialDraw?>.Default.Equals(SpecialMaterial,other.SpecialMaterial) &&
        EqualityComparer<EnbRetailSpecialMaterialProjection?>.Default.Equals(SpecialMaterialProjection,other.SpecialMaterialProjection) &&
        EqualityComparer<uint?>.Default.Equals(CurrentRetailAdditionalMeshPassFlags,other.CurrentRetailAdditionalMeshPassFlags) &&
        EqualityComparer<bool>.Default.Equals(CurrentRetailTerrainSea,other.CurrentRetailTerrainSea) &&
        EqualityComparer<bool>.Default.Equals(CurrentRetailTerrainDisplacement,other.CurrentRetailTerrainDisplacement) &&
        EqualityComparer<uint>.Default.Equals(CurrentRetailDepthBias,other.CurrentRetailDepthBias) &&
        EqualityComparer<string?>.Default.Equals(CurrentRetailProjectedDecalOwner,other.CurrentRetailProjectedDecalOwner) &&
        EqualityComparer<string>.Default.Equals(RenderInstancePath,other.RenderInstancePath) &&
        EqualityComparer<int>.Default.Equals(CurrentRetailColorizationRevision,other.CurrentRetailColorizationRevision) &&
        EqualityComparer<int>.Default.Equals(CurrentRetailNameDecalRevision,other.CurrentRetailNameDecalRevision) &&
        EqualityComparer<int>.Default.Equals(CurrentRetailDecalRevision,other.CurrentRetailDecalRevision) &&
        EqualityComparer<EnbW3dSharedMaterialMappers?>.Default.Equals(CurrentRetailSharedMaterialMappers,other.CurrentRetailSharedMaterialMappers) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderVertexMaterial>?>.Default.Equals(CurrentRetailLoadedMaterials,other.CurrentRetailLoadedMaterials) &&
        EqualityComparer<IReadOnlyList<EnbCurrentRetailTextureObjectState>?>.Default.Equals(CurrentRetailLoadedTextures,other.CurrentRetailLoadedTextures) &&
        EqualityComparer<IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>?>.Default.Equals(CurrentRetailMaterialMappers,other.CurrentRetailMaterialMappers) &&
        EqualityComparer<uint?>.Default.Equals(CurrentRetailEffectMapperStartMilliseconds,other.CurrentRetailEffectMapperStartMilliseconds);
    public override int GetHashCode()
    {
        var hash=new HashCode();
        hash.Add(OrderKey);
        hash.Add(RenderIdentity);
        hash.Add(VisualGeneration);
        hash.Add(GameId);
        hash.Add(Mesh);
        hash.Add(Material);
        hash.Add(HierarchyName);
        hash.Add(PivotIndex);
        hash.Add(RenderObjectName);
        hash.Add(CurrentRetailParticleBuffer);
        hash.Add(SpecialMaterial);
        hash.Add(SpecialMaterialProjection);
        hash.Add(CurrentRetailAdditionalMeshPassFlags);
        hash.Add(CurrentRetailTerrainSea);
        hash.Add(CurrentRetailTerrainDisplacement);
        hash.Add(CurrentRetailDepthBias);
        hash.Add(CurrentRetailProjectedDecalOwner);
        hash.Add(RenderInstancePath);
        hash.Add(CurrentRetailColorizationRevision);
        hash.Add(CurrentRetailNameDecalRevision);
        hash.Add(CurrentRetailDecalRevision);
        hash.Add(CurrentRetailSharedMaterialMappers);
        hash.Add(CurrentRetailLoadedMaterials);
        hash.Add(CurrentRetailLoadedTextures);
        hash.Add(CurrentRetailMaterialMappers);
        hash.Add(CurrentRetailEffectMapperStartMilliseconds);
        return hash.ToHashCode();
    }

}

// Keep only the last input per property. The descriptor owns its transitions,
// so retirement releases the chain without dependent-handle fixed-point scans.
internal static class EnbPreparedPacketTransitions<T>
{
    public static bool Same(T a,T b)=>typeof(T).IsValueType || typeof(T)==typeof(string)
        ? EqualityComparer<T>.Default.Equals(a,b) : ReferenceEquals(a,b);
    public static EnbW3dMeshDescriptor Set(EnbW3dMeshDescriptor state,bool retain,int slot,T input,
        Func<EnbW3dMeshDescriptor,T,EnbW3dMeshDescriptor> apply)
        =>retain?EnbPreparedPacketTransitionCache.Set(state,slot,input,apply):apply(state,input);
}

// Owner-held transitions preserve last-input and identity semantics.
// The last transition per slot has exactly the same replacement semantics.
internal static class EnbPreparedPacketTransitionCache
{
    internal sealed class Owner
    {
        internal readonly object Gate=new();
        internal ImmutableDictionary<int,object> Published=ImmutableDictionary<int,object>.Empty;
    }
    private sealed record Transition<T>(T Input,EnbW3dMeshDescriptor State);
    internal static EnbW3dMeshDescriptor Set<T>(EnbW3dMeshDescriptor state,int slot,T input,
        Func<EnbW3dMeshDescriptor,T,EnbW3dMeshDescriptor> apply)
    {
        var owner=Volatile.Read(ref state.TransitionOwner);
        if(owner is null)
        {
            var created=new Owner();
            owner=Interlocked.CompareExchange(ref state.TransitionOwner,created,null)??created;
        }
        var entries=Volatile.Read(ref owner.Published);
        if(entries.TryGetValue(slot,out var entry)&&entry is Transition<T> previous&&EnbPreparedPacketTransitions<T>.Same(previous.Input,input))return previous.State;
        lock(owner.Gate)
        {
            entries=Volatile.Read(ref owner.Published);
            if(entries.TryGetValue(slot,out entry)&&entry is Transition<T> current&&EnbPreparedPacketTransitions<T>.Same(current.Input,input))return current.State;
            var changed=apply(state,input);
            Volatile.Write(ref owner.Published,entries.SetItem(slot,new Transition<T>(input,changed)));
            return changed;
        }
    }
}

public sealed record EnbW3dDazzleDrawInstance(EnbDazzleDefinition Definition, Matrix4x4 World)
{
    public string HlodName { get; init; } = "";
    public int LevelOrdinal { get; init; }
    public int MemberOrdinal { get; init; }
    public EnbRetailDazzleHistoryState? History { get; init; }
    //0096BAB0 changes +E8 without scaling the RenderObj transform/direction.
    public float? CurrentRetailDazzleScale { get; init; }
    public float DazzleInstanceScale { get; init; } = 1;
    public bool HasConfiguredDazzleScale { get; init; }
}

public sealed record EnbW3dParticleEmitterDrawInstance(
    string RenderObjectName,
    string EmitterResourceName,
    uint GameId,
    EnbRenderPassKind RenderPass,
    EnbCurrentRetailParticleEmitterDefinition Definition,
    EnbW3dRenderTextureLocation? TextureLocation,
    Matrix4x4 WorldTransform,
    string HierarchyName,
    int PivotIndex,
    int BindingOrdinal) : IEnbParticleRenderOwner
{
    public string? RuntimeTextureName { get; init; }
    public EnbResolvedTextureResource? RetainedTexture { get; init; }
}

/// <summary>
/// Current-retail hierarchy evaluator. Retail assigns pivot 0 directly
/// to the owning object transform, then evaluates pivots 1..N-1 as
/// parentWorld * local in its column-vector convention. System.Numerics uses
/// row vectors, so the equivalent operation is local * parentWorld.
/// </summary>
public static class EnbW3dHierarchyPose
{

    public static bool TryEvaluate(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 objectWorldTransform,
        out Matrix4x4[] worldTransforms,
        out string? diagnostic)
    {
        if (!TryEvaluate(hierarchy, objectWorldTransform, animation: null, frame: 0f, out EnbW3dHierarchyPoseOwner pose, out diagnostic))
        {
            worldTransforms = [];
            return false;
        }

        worldTransforms = pose.WorldTransforms;
        return true;
    }

    public static bool TryEvaluate(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 objectWorldTransform,
        EnbW3dRenderHierarchyAnimation? animation,
        float frame,
        out EnbW3dHierarchyPoseOwner pose,
        out string? diagnostic)
        => TryEvaluate(
            hierarchy,
            objectWorldTransform,
            animation,
            frame,
            postWorldTransforms: null,
            out pose,
            out diagnostic);

    /// <summary>
    /// Evaluates one hierarchy and applies optional per-record post transforms
    /// after the record's authored/animated result and before descendants read
    /// it. This is the generic state-record seam used by current-retail HTree
    /// mode-0 overrides.
    /// </summary>
    public static bool TryEvaluate(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 objectWorldTransform,
        EnbW3dRenderHierarchyAnimation? animation,
        float frame,
        IReadOnlyDictionary<int, Matrix4x4>? postWorldTransforms,
        out EnbW3dHierarchyPoseOwner pose,
        out string? diagnostic,
        float? storedTranslationScale=null, EnbW3dHierarchyPoseOwner? storage=null)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        pose = default!;
        diagnostic = null;

        if (!IsFinite(objectWorldTransform))
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' received a non-finite object transform.";
            return false;
        }

        if (hierarchy.Pivots.Count == 0)
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' contains no pivots.";
            return false;
        }

        if (!hierarchy.Pivots[0].IsRoot)
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' pivot 0 is not an authored root.";
            return false;
        }
        if (!TryValidatePostWorldTransforms(hierarchy, postWorldTransforms, out diagnostic))
            return false;

        if (animation is not null)
        {
            // Retail 00989670 (Anim_Update) and 00989BC0 (Combo_Update) apply an HAnim
            // by pivot index with no hierarchy-name test. Installed RSL and W3D data
            // never pair an HAnim with another hierarchy (EnbInstalledHAnimSampleCorpusTests,
            // EnbInstalledRslQualifiedAnimationTests); this branch is corpus-unreached.
            if (!string.Equals(animation.HierarchyName, hierarchy.Name, StringComparison.OrdinalIgnoreCase))
            {
                diagnostic = $"Hierarchy animation '{animation.Name}' targets '{animation.HierarchyName}', not hierarchy '{hierarchy.Name}'.";
                return false;
            }

            if (!float.IsFinite(frame) || frame <= int.MinValue)
            {
                diagnostic = $"Hierarchy animation '{animation.Name}' frame {frame} is not a finite samplable frame.";
                return false;
            }
        }

        storage?.Prepare(hierarchy.Pivots.Count);
        var world = storage?.World ?? new EnbW3dHierarchyPoseOwner.Writer<Matrix4x4>(new Matrix4x4[hierarchy.Pivots.Count]);
        var visibility = storage?.VisibilityWriter ?? new EnbW3dHierarchyPoseOwner.Writer<bool>(new bool[hierarchy.Pivots.Count]);

        // Current retail writes pivot 0 directly from the owning object and
        // starts the animated-pivot loop at index 1. Any channel targeting
        // runtime pivot 0 is therefore not applied by this path.
        Matrix4x4 rootWorld = objectWorldTransform;
        if (postWorldTransforms is not null && postWorldTransforms.TryGetValue(0, out Matrix4x4 rootPostTransform))
            rootWorld = rootPostTransform * rootWorld;
        if (!IsFinite(rootWorld))
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' pivot 0 produced a non-finite post-transform result.";
            return false;
        }
        world.Add(rootWorld);
        visibility.Add(true);

        for (int index = 1; index < hierarchy.Pivots.Count; index++)
        {
            EnbW3dRenderPivot pivot = hierarchy.Pivots[index];
            if (pivot.ParentIndex >= (uint)index)
            {
                diagnostic = $"Hierarchy '{hierarchy.Name}' pivot {index} references parent {pivot.ParentIndex}; current retail hierarchy evaluation requires an earlier pivot.";
                return false;
            }

            Matrix4x4 resolved = storedTranslationScale is {} nativeScale
                ? EnbRetailParticleHierarchyTransform.Compose(
                    EnbRetailParticleHierarchyTransform.ScaleTranslation(pivot.LocalTransform,nativeScale),world[(int)pivot.ParentIndex])
                : EnbRetailParticleHierarchyTransform.Compose(pivot.LocalTransform,world[(int)pivot.ParentIndex]);
            bool visible = true;
            if (animation is not null)
            {
                if (!EnbW3dAnimationSampler.TrySamplePivot(animation, index, frame, out EnbW3dPivotAnimationSample sample, out diagnostic))
                    return false;

                // Retail first resolves the authored pivot, then adds the
                // animation translation in that pivot's local basis and
                // composes the sampled quaternion onto the same basis. In
                // System.Numerics row-vector convention this is the reversed
                // R * T * authoredLocal * parentWorld product.
                resolved=EnbRetailParticleHierarchyTransform.ApplyAnimation(resolved,sample.Translation,sample.Rotation,
                    hierarchy.CurrentRetailAnimationTranslationScale*(storedTranslationScale??1f));
                visible = sample.Visible;
            }

            if (postWorldTransforms is not null && postWorldTransforms.TryGetValue(index, out Matrix4x4 postTransform))
            {
                resolved = EnbRetailParticleHierarchyTransform.Compose(postTransform,resolved);
                visible = true; //009C5120 is followed by a native record visibility write.
            }

            if (!IsFinite(resolved))
            {
                diagnostic = $"Hierarchy '{hierarchy.Name}' pivot {index} produced a non-finite world transform.";
                return false;
            }

            world.Add(resolved);
            visibility.Add(visible);
        }

        pose = storage is null ? new EnbW3dHierarchyPoseOwner(
            world.MoveToArray(),visibility.MoveToArray(),animation?.Name,animation is null?null:frame)
            :storage.Complete(animation?.Name,animation is null?null:frame);
        return true;
    }

    /// <summary>
    /// Current-retail multi-HAnim hierarchy evaluation used by RenderObj
    /// aggregate mode 4. Retail gathers every active Animation state's HAnim
    /// in active-list order, normalizes ordinary global weights, blends
    /// translation linearly, incrementally blends quaternions in that same
    /// order, and ORs visibility across all contributing HAnims. Pivot 0 is
    /// still written directly from the owning object transform.
    /// </summary>
    public static bool TryEvaluate(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 objectWorldTransform,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,
        out EnbW3dHierarchyPoseOwner pose,
        out string? diagnostic)
        => TryEvaluate(
            hierarchy,
            objectWorldTransform,
            animations,
            postWorldTransforms: null,
            out pose,
            out diagnostic);

    /// <summary>
    /// Multi-HAnim equivalent of the per-record post-transform evaluator.
    /// </summary>
    public static bool TryEvaluate(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 objectWorldTransform,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,
        IReadOnlyDictionary<int, Matrix4x4>? postWorldTransforms,
        out EnbW3dHierarchyPoseOwner pose,
        out string? diagnostic,
        float? storedTranslationScale=null, EnbW3dHierarchyPoseOwner? storage=null)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(animations);
        pose = default!;
        diagnostic = null;

        if (animations.Count == 0)
            return TryEvaluate(
                hierarchy,
                objectWorldTransform,
                animation: null,
                frame: 0f,
                postWorldTransforms: postWorldTransforms,
                out pose,
                out diagnostic,
                storedTranslationScale,storage);

        if (!IsFinite(objectWorldTransform))
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' received a non-finite object transform.";
            return false;
        }
        if (hierarchy.Pivots.Count == 0 || !hierarchy.Pivots[0].IsRoot)
        {
            diagnostic = hierarchy.Pivots.Count == 0
                ? $"Hierarchy '{hierarchy.Name}' contains no pivots."
                : $"Hierarchy '{hierarchy.Name}' pivot 0 is not an authored root.";
            return false;
        }
        if (!TryValidatePostWorldTransforms(hierarchy, postWorldTransforms, out diagnostic))
            return false;

        float totalWeight = 0f;
        // Only normalized weights are temporary. Clip/frame inputs are immutable
        // for this evaluation and need no copied rich sample array.
        Span<float> weights=animations.Count<=32?stackalloc float[animations.Count]:new float[animations.Count];
        for (int index = 0; index < animations.Count; index++)
        {
            EnbW3dResolvedWeightedAnimationSample entry = animations[index];
            // See the single-HAnim evaluator: 00989BC0 has no hierarchy-name test; corpus-unreached.
            if (!string.Equals(entry.Animation.HierarchyName, hierarchy.Name, StringComparison.OrdinalIgnoreCase))
            {
                diagnostic = $"Hierarchy animation '{entry.Animation.Name}' targets '{entry.Animation.HierarchyName}', not hierarchy '{hierarchy.Name}'.";
                return false;
            }
            if (!float.IsFinite(entry.Frame) || entry.Frame <= int.MinValue)
            {
                diagnostic = $"Hierarchy animation '{entry.Animation.Name}' frame {entry.Frame} is not a finite samplable frame.";
                return false;
            }
            if (!float.IsFinite(entry.Weight) || entry.Weight < 0f)
            {
                diagnostic = $"Hierarchy animation '{entry.Animation.Name}' has invalid blend weight {entry.Weight}.";
                return false;
            }
            totalWeight += entry.Weight;
            weights[index] = entry.Weight;
        }

        if (!float.IsFinite(totalWeight))
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' received a non-finite current-retail HAnim aggregate weight.";
            return false;
        }

        // Retail's ordinary aggregate normalizer leaves an all-zero aggregate
        // untouched. Transform samplers skip zero-weight entries, while the
        // later visibility pass still ORs visibility across every HAnim.
        EnbCurrentRetailAnimationAggregateNormalizer.NormalizeUnmasked(weights);

        storage?.Prepare(hierarchy.Pivots.Count);
        var world = storage?.World ?? new EnbW3dHierarchyPoseOwner.Writer<Matrix4x4>(new Matrix4x4[hierarchy.Pivots.Count]);
        var visibility = storage?.VisibilityWriter ?? new EnbW3dHierarchyPoseOwner.Writer<bool>(new bool[hierarchy.Pivots.Count]);
        Matrix4x4 rootWorld = objectWorldTransform;
        if (postWorldTransforms is not null && postWorldTransforms.TryGetValue(0, out Matrix4x4 rootPostTransform))
            rootWorld = rootPostTransform * rootWorld;
        if (!IsFinite(rootWorld))
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' pivot 0 produced a non-finite post-transform result.";
            return false;
        }
        world.Add(rootWorld);
        visibility.Add(true);

        for (int pivotIndex = 1; pivotIndex < hierarchy.Pivots.Count; pivotIndex++)
        {
            EnbW3dRenderPivot pivot = hierarchy.Pivots[pivotIndex];
            if (pivot.ParentIndex >= (uint)pivotIndex)
            {
                diagnostic = $"Hierarchy '{hierarchy.Name}' pivot {pivotIndex} references parent {pivot.ParentIndex}; current retail hierarchy evaluation requires an earlier pivot.";
                return false;
            }

            var accumulated=new EnbCurrentRetailHierarchyAnimationAccumulator();
            bool visible = false;

            for(int entryIndex=0;entryIndex<animations.Count;entryIndex++)
            {
                var entry=animations[entryIndex];
                float weight=weights[entryIndex];
                if (!EnbW3dAnimationSampler.TrySamplePivot(
                        entry.Animation,
                        pivotIndex,
                        entry.Frame,
                        out EnbW3dPivotAnimationSample sample,
                        out diagnostic))
                {
                    return false;
                }

                visible |= sample.Visible;

                if (weight <= 0f)
                    continue;

                accumulated.AddTranslation(sample.Translation,weight,hierarchy.CurrentRetailAnimationTranslationScale*(storedTranslationScale??1f));
                accumulated.AddRotation(sample.Rotation,weight,EnbW3dAnimationSampler.InterpolateQuaternionRetail);
            }

            Matrix4x4 authored=EnbRetailParticleHierarchyTransform.Compose(storedTranslationScale is {} nativeScale
                ? EnbRetailParticleHierarchyTransform.ScaleTranslation(pivot.LocalTransform,nativeScale)
                : pivot.LocalTransform,world[(int)pivot.ParentIndex]);
            Matrix4x4 resolved=accumulated.Apply(authored);
            if (postWorldTransforms is not null && postWorldTransforms.TryGetValue(pivotIndex, out Matrix4x4 postTransform))
            {
                resolved = EnbRetailParticleHierarchyTransform.Compose(postTransform,resolved);
                visible = true; //00989BC0 forces visibility after a captured override.
            }
            if (!IsFinite(resolved))
            {
                diagnostic = $"Hierarchy '{hierarchy.Name}' pivot {pivotIndex} produced a non-finite blended world transform.";
                return false;
            }

            world.Add(resolved);
            visibility.Add(visible);
        }

        pose = storage is null ? new EnbW3dHierarchyPoseOwner(
            world.MoveToArray(),visibility.MoveToArray(),
            animations.Count==1?animations[0].Animation.Name:string.Join(" + ",animations.Select(static entry=>entry.Animation.Name)),
            animations.Count==1?animations[0].Frame:null)
            :storage.Complete(storage.AnimationName,animations.Count==1?animations[0].Frame:null);
        return true;
    }

    private static bool TryValidatePostWorldTransforms(
        EnbW3dRenderHierarchy hierarchy,
        IReadOnlyDictionary<int, Matrix4x4>? postWorldTransforms,
        out string? diagnostic)
    {
        diagnostic = null;
        if (postWorldTransforms is null)
            return true;

        foreach ((int pivotIndex, Matrix4x4 transform) in postWorldTransforms)
        {
            if (pivotIndex < 0 || pivotIndex >= hierarchy.Pivots.Count)
            {
                diagnostic = $"Hierarchy '{hierarchy.Name}' post-transform pivot {pivotIndex} is outside 0..{hierarchy.Pivots.Count - 1}.";
                return false;
            }
            if (!IsFinite(transform))
            {
                diagnostic = $"Hierarchy '{hierarchy.Name}' post-transform pivot {pivotIndex} is non-finite.";
                return false;
            }
        }

        return true;
    }

    internal static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

/// <summary>
/// Compiles one immutable scene object + immutable W3D resource into stable
/// rigid or CPU-skinned draw packets. This owns HLOD selection; consumes only
/// explicit HLOD and animation choices and never invents LOD/animation policy.
/// </summary>
public static partial class EnbW3dDrawPacketCompiler
{
    private sealed class ResourceMeshBindings
    {
        public readonly Dictionary<EnbW3dRenderMesh, ImmutableArray<(EnbW3dRenderHierarchyModel Model, EnbW3dRenderHierarchyModelNode Node)>> Nodes = new(ReferenceEqualityComparer.Instance);
        // Both keys are selected from this immutable resource below. Their
        // lifetime is already bounded by the outer weak resource entry; nested
        // weak tables add dependent GC handles without shortening that lifetime.
        private readonly Dictionary<EnbW3dRenderHlod, Dictionary<EnbW3dRenderHlodLevel, HlodMemberBindings>> _levels = new(ReferenceEqualityComparer.Instance);
        public HlodMemberBindings GetLevel(EnbW3dRenderResource resource, EnbW3dRenderHlod hlod, EnbW3dRenderHlodLevel level)
        {
            lock (_levels)
            {
                if (!_levels.TryGetValue(hlod, out var levels))
                    _levels.Add(hlod, levels = new(ReferenceEqualityComparer.Instance));
                if (!levels.TryGetValue(level, out var bindings))
                    levels.Add(level, bindings = new(resource, hlod, level));
                return bindings;
            }
        }

        public ResourceMeshBindings(EnbW3dRenderResource resource)
        {
            foreach (var mesh in resource.Meshes)
            {
                var nodes = ImmutableArray.CreateBuilder<(EnbW3dRenderHierarchyModel, EnbW3dRenderHierarchyModelNode)>();
                foreach (var model in resource.HierarchyModels)
                    foreach (var node in model.Nodes)
                        if (EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh, node.RenderObjectName))
                            nodes.Add((model, node));
                Nodes[mesh] = nodes.ToImmutable();
            }
        }
    }

    private sealed class HlodMemberBindings
    {
        public readonly ImmutableArray<EnbW3dRenderHlodMember> Members;
        public readonly ImmutableArray<(EnbW3dRenderHlodMember Member, EnbW3dExternalRenderObject External, int Ordinal)> ExternalMembers;
        public readonly HashSet<string> Names;
        public readonly Dictionary<EnbW3dRenderMesh, ImmutableArray<EnbW3dRenderHlodMember>> MeshMembers = new(ReferenceEqualityComparer.Instance);
        public readonly bool NoOpOnly;
        public HlodMemberBindings(EnbW3dRenderResource resource, EnbW3dRenderHlod hlod, EnbW3dRenderHlodLevel level)
        {
            Members = [..level.Members, ..hlod.Aggregate?.Members ?? []];
            var selectedEmitterMembers = ImmutableArray.CreateBuilder<(EnbW3dRenderHlodMember Member, EnbW3dExternalRenderObject External, int Ordinal)>();
            for (int memberOrdinal = 0; memberOrdinal < Members.Length; memberOrdinal++)
            {
                EnbW3dRenderHlodMember member = Members[memberOrdinal];
                EnbW3dExternalRenderObject? external = resource.ExternalRenderObjects.FirstOrDefault(candidate =>
                    (candidate.DazzleDefinition is not null || candidate.SpecialMaterialDefinition is not null &&
                        !resource.CompositeRenderChildren.Any(child=>child.Name.Equals(candidate.Name,StringComparison.OrdinalIgnoreCase)) ||
                        (candidate.Kind == EnbW3dExternalRenderObjectKind.ParticleEmitter && candidate.ParticleEmitterDefinition is not null && candidate.HasReadyParticleTexture)) &&
                    string.Equals(candidate.Name, member.Name, StringComparison.OrdinalIgnoreCase));
                if (external is not null)
                    selectedEmitterMembers.Add((member, external, memberOrdinal));
            }

            ExternalMembers=selectedEmitterMembers.ToImmutable();
            Names = new(Members.Select(static member => member.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var mesh in resource.Meshes)
                MeshMembers.Add(mesh, [..Members.Where(member => EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh, member.Name))]);
            NoOpOnly = hlod.UsesRetainedPivotIndices && Names.Count == 0 || Names.Count != 0 &&
                Names.All(name => EnbCurrentRetailRenderObjectSemantics.IsNoOpDrawRenderObject(resource, name) ||
                    resource.CompositeRenderChildren.Any(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase)));
        }
    }

    // Animation wrappers may change every invocation without replacing the
    // authored mesh/model definitions. Resolve names once for those definitions.
    // The weak mesh-list owner bounds retention; keep only the current variant.
    private sealed class AuthoredBindingOwner
    {
        private sealed record Publication(object Models,object Hlods,object External,object Children,ResourceMeshBindings Bindings)
        {
            public bool Matches(EnbW3dRenderResource resource)=>ReferenceEquals(Models,resource.HierarchyModels) &&
                ReferenceEquals(Hlods,resource.HierarchicalLods) && ReferenceEquals(External,resource.ExternalRenderObjects) &&
                ReferenceEquals(Children,resource.CompositeRenderChildren);
        }
        private readonly object _gate=new();
        private Publication? _published;
        public ResourceMeshBindings Get(EnbW3dRenderResource resource)
        {
            var state=Volatile.Read(ref _published);
            if(state is not null && state.Matches(resource))return state.Bindings;
            lock(_gate)
            {
                state=Volatile.Read(ref _published);
                if(state is not null && state.Matches(resource))return state.Bindings;
                state=new(resource.HierarchyModels,resource.HierarchicalLods,resource.ExternalRenderObjects,
                    resource.CompositeRenderChildren,new(resource));
                Volatile.Write(ref _published,state);
                return state.Bindings;
            }
        }
    }
    private static readonly ConditionalWeakTable<IReadOnlyList<EnbW3dRenderMesh>, AuthoredBindingOwner> MeshBindings = new();

    public static bool TryBuild(
        IEnbRenderObject sceneObject,
        EnbW3dRenderResource resource,
        long frameTimeMilliseconds,
        EnbRenderPassKind renderPass,
        EnbW3dHlodSelection? hlodSelection,
        EnbW3dLightEnvironment lightEnvironment,
        out EnbW3dDrawInvocation drawList,
        out string? diagnostic)
        => TryBuild(
            sceneObject,
            resource,
            frameTimeMilliseconds,
            renderPass,
            hlodSelection,
            lightEnvironment,
            animationSample: null,
            out drawList,
            out diagnostic);

    public static bool TryBuild(
        IEnbRenderObject sceneObject,
        EnbW3dRenderResource resource,
        long frameTimeMilliseconds,
        EnbRenderPassKind renderPass,
        EnbW3dHlodSelection? hlodSelection,
        EnbW3dLightEnvironment lightEnvironment,
        IReadOnlyList<EnbW3dWeightedAnimationSample> animationBlend,
        out EnbW3dDrawInvocation drawList,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(animationBlend);
        ArgumentNullException.ThrowIfNull(sceneObject);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(lightEnvironment);
        drawList = default!;
        diagnostic = null;

        if (frameTimeMilliseconds < 0)
        {
            diagnostic = "W3D frame time cannot be negative.";
            return false;
        }
        if (sceneObject.Spatial?.Transform is not IEnbRenderTransform spatial)
        {
            diagnostic = $"Scene object {sceneObject.GameId} has no spatial transform.";
            return false;
        }
        if (!TryBuildObjectTransform(sceneObject.Scale, spatial, out Matrix4x4 objectWorld, out diagnostic))
            return false;

        TryBuildObjectTransform(1, spatial, out var specialRigidWorld, out _);
        return TryBuildAtWorldTransformCore(
            sceneObject,
            resource,
            frameTimeMilliseconds,
            renderPass,
            hlodSelection,
            lightEnvironment,
            animationBlend,
            hierarchyPostTransforms: null,
            objectWorld,
            out drawList,
            out diagnostic, new(specialRigidWorld,sceneObject.Scale));
    }

    public static bool TryBuild(
        IEnbRenderObject sceneObject,
        EnbW3dRenderResource resource,
        long frameTimeMilliseconds,
        EnbRenderPassKind renderPass,
        EnbW3dHlodSelection? hlodSelection,
        EnbW3dLightEnvironment lightEnvironment,
        IReadOnlyList<EnbW3dWeightedAnimationSample> animationBlend,
        EnbW3dHierarchyState hierarchyPostTransforms,
        out EnbW3dDrawInvocation drawList,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(hierarchyPostTransforms);
        ArgumentNullException.ThrowIfNull(animationBlend);
        ArgumentNullException.ThrowIfNull(sceneObject);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(lightEnvironment);
        drawList = default!;
        diagnostic = null;

        if (frameTimeMilliseconds < 0)
        {
            diagnostic = "W3D frame time cannot be negative.";
            return false;
        }
        if (sceneObject.Spatial?.Transform is not IEnbRenderTransform spatial)
        {
            diagnostic = $"Scene object {sceneObject.GameId} has no spatial transform.";
            return false;
        }
        if (!TryBuildObjectTransform(sceneObject.Scale, spatial, out Matrix4x4 objectWorld, out diagnostic))
            return false;

        TryBuildObjectTransform(1, spatial, out var specialRigidWorld, out _);
        return TryBuildAtWorldTransformCore(
            sceneObject,
            resource,
            frameTimeMilliseconds,
            renderPass,
            hlodSelection,
            lightEnvironment,
            animationBlend,
            hierarchyPostTransforms,
            objectWorld,
            out drawList,
            out diagnostic, new(specialRigidWorld,sceneObject.Scale));
    }

    public static bool TryBuild(
        IEnbRenderObject sceneObject,
        EnbW3dRenderResource resource,
        long frameTimeMilliseconds,
        EnbRenderPassKind renderPass,
        EnbW3dHlodSelection? hlodSelection,
        EnbW3dLightEnvironment lightEnvironment,
        EnbW3dAnimationSample? animationSample,
        out EnbW3dDrawInvocation drawList,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(sceneObject);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(lightEnvironment);
        drawList = default!;
        diagnostic = null;

        if (frameTimeMilliseconds < 0)
        {
            diagnostic = "W3D frame time cannot be negative.";
            return false;
        }

        if (sceneObject.Spatial?.Transform is not IEnbRenderTransform spatial)
        {
            diagnostic = $"Scene object {sceneObject.GameId} has no spatial transform.";
            return false;
        }

        if (!TryBuildObjectTransform(sceneObject.Scale, spatial, out Matrix4x4 objectWorld, out diagnostic))
            return false;

        TryBuildObjectTransform(1, spatial, out var specialRigidWorld, out _);
        return TryBuildAtWorldTransformCore(
            sceneObject,
            resource,
            frameTimeMilliseconds,
            renderPass,
            hlodSelection,
            lightEnvironment,
            animationSample is EnbW3dAnimationSample sample
                ? new EnbW3dWeightedAnimationSample[] { new(sample.Name, sample.Frame, 1f) }
                : null,
            hierarchyPostTransforms: null,
            objectWorld,
            out drawList,
            out diagnostic, new(specialRigidWorld,sceneObject.Scale));
    }

    /// <summary>
    /// Compiles an already-attached current-retail RenderObj at an explicit
    /// world transform. Aggregate 0x0600 attachment semantics obtain this
    /// matrix from the owning hierarchy bone instead of reusing the top-level
    /// scene object's transform.
    /// </summary>
    public static bool TryBuildAtWorldTransform(
        IEnbRenderObject sceneObject,
        EnbW3dRenderResource resource,
        Matrix4x4 objectWorld,
        long frameTimeMilliseconds,
        EnbRenderPassKind renderPass,
        EnbW3dHlodSelection? hlodSelection,
        EnbW3dLightEnvironment lightEnvironment,
        out EnbW3dDrawInvocation drawList,
        out string? diagnostic, EnbRetailSpecialMaterialTransform? specialTransform = null)
        => TryBuildAtWorldTransform(
            sceneObject,
            resource,
            objectWorld,
            frameTimeMilliseconds,
            renderPass,
            hlodSelection,
            lightEnvironment,
            animationSample: null,
            out drawList,
            out diagnostic, specialTransform);

    public static bool TryBuildAtWorldTransform(
        IEnbRenderObject sceneObject,
        EnbW3dRenderResource resource,
        Matrix4x4 objectWorld,
        long frameTimeMilliseconds,
        EnbRenderPassKind renderPass,
        EnbW3dHlodSelection? hlodSelection,
        EnbW3dLightEnvironment lightEnvironment,
        IReadOnlyList<EnbW3dWeightedAnimationSample> animationBlend,
        out EnbW3dDrawInvocation drawList,
        out string? diagnostic, EnbRetailSpecialMaterialTransform? specialTransform = null,
        EnbW3dHierarchyPoseOwner? skinParentPose = null,float? renderScale=null)
    {
        ArgumentNullException.ThrowIfNull(animationBlend);
        ArgumentNullException.ThrowIfNull(sceneObject);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(lightEnvironment);
        drawList = default!;
        diagnostic = null;

        if (frameTimeMilliseconds < 0)
        {
            diagnostic = "W3D frame time cannot be negative.";
            return false;
        }
        if (!IsFinite(objectWorld))
        {
            diagnostic = "Attached W3D RenderObj received a non-finite world matrix.";
            return false;
        }

        return TryBuildAtWorldTransformCore(
            sceneObject,
            resource,
            frameTimeMilliseconds,
            renderPass,
            hlodSelection,
            lightEnvironment,
            animationBlend,
            hierarchyPostTransforms: null,
            objectWorld,
            out drawList,
            out diagnostic, specialTransform, skinParentPose:skinParentPose,renderScale:renderScale);
    }

    public static bool TryBuildAtWorldTransform(
        IEnbRenderObject sceneObject,
        EnbW3dRenderResource resource,
        Matrix4x4 objectWorld,
        long frameTimeMilliseconds,
        EnbRenderPassKind renderPass,
        EnbW3dHlodSelection? hlodSelection,
        EnbW3dLightEnvironment lightEnvironment,
        IReadOnlyList<EnbW3dWeightedAnimationSample> animationBlend,
        EnbW3dHierarchyState hierarchyPostTransforms,
        out EnbW3dDrawInvocation drawList,
        out string? diagnostic, EnbRetailSpecialMaterialTransform? specialTransform = null,
        EnbW3dHierarchyPoseOwner? skinParentPose = null,float? renderScale=null)
    {
        ArgumentNullException.ThrowIfNull(hierarchyPostTransforms);
        ArgumentNullException.ThrowIfNull(animationBlend);
        ArgumentNullException.ThrowIfNull(sceneObject);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(lightEnvironment);
        drawList = default!;
        diagnostic = null;

        if (frameTimeMilliseconds < 0)
        {
            diagnostic = "W3D frame time cannot be negative.";
            return false;
        }
        if (!IsFinite(objectWorld))
        {
            diagnostic = "Attached W3D RenderObj received a non-finite world matrix.";
            return false;
        }

        return TryBuildAtWorldTransformCore(
            sceneObject,
            resource,
            frameTimeMilliseconds,
            renderPass,
            hlodSelection,
            lightEnvironment,
            animationBlend,
            hierarchyPostTransforms,
            objectWorld,
            out drawList,
            out diagnostic, specialTransform, skinParentPose:skinParentPose,renderScale:renderScale);
    }

    public static bool TryBuildAtWorldTransform(
        IEnbRenderObject sceneObject,
        EnbW3dRenderResource resource,
        Matrix4x4 objectWorld,
        long frameTimeMilliseconds,
        EnbRenderPassKind renderPass,
        EnbW3dHlodSelection? hlodSelection,
        EnbW3dLightEnvironment lightEnvironment,
        EnbW3dAnimationSample? animationSample,
        out EnbW3dDrawInvocation drawList,
        out string? diagnostic, EnbRetailSpecialMaterialTransform? specialTransform = null)
    {
        ArgumentNullException.ThrowIfNull(sceneObject);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(lightEnvironment);
        drawList = default!;
        diagnostic = null;

        if (frameTimeMilliseconds < 0)
        {
            diagnostic = "W3D frame time cannot be negative.";
            return false;
        }
        if (!IsFinite(objectWorld))
        {
            diagnostic = "Attached W3D RenderObj received a non-finite world matrix.";
            return false;
        }

        return TryBuildAtWorldTransformCore(
            sceneObject,
            resource,
            frameTimeMilliseconds,
            renderPass,
            hlodSelection,
            lightEnvironment,
            animationSample is EnbW3dAnimationSample sample
                ? new EnbW3dWeightedAnimationSample[] { new(sample.Name, sample.Frame, 1f) }
                : null,
            hierarchyPostTransforms: null,
            objectWorld,
            out drawList,
            out diagnostic, specialTransform);
    }

    public static bool TryCaptureProjectedDecalReceivers(IEnbRenderObject obj,
        EnbW3dRenderResource resource, Matrix4x4 world, long time, EnbW3dHlodSelection? selection,
        EnbW3dLightEnvironment lights, IReadOnlyList<EnbW3dWeightedAnimationSample> animations,
        EnbW3dHierarchyState? transforms, out EnbW3dDrawInvocation list, out string? diagnostic,
        EnbW3dHierarchyPoseOwner? skinParentPose = null,float? renderScale=null)
        => TryBuildAtWorldTransformCore(obj, resource, time, EnbRenderPassKind.OrdinaryWorld, selection,
            lights, animations, transforms, world, out list, out diagnostic, captureProjectedDecals: true,skinParentPose:skinParentPose,renderScale:renderScale);

    public static bool TryBuildPrepared(IEnbRenderObject owner, EnbW3dRenderResource resource,
        Matrix4x4? suppliedWorld, long time, EnbRenderPassKind pass, EnbW3dHlodSelection? selection,
        EnbW3dLightEnvironment lights, IReadOnlyList<EnbW3dWeightedAnimationSample> animations,
        EnbW3dHierarchyState? overrides, EnbW3dMeshOccurrencePreparation preparation,
        out EnbW3dDrawInvocation list, out string? diagnostic, EnbW3dHierarchyPoseOwner? skinParentPose = null,
        EnbRetailSpecialMaterialTransform? specialTransform = null, float? renderScale = null)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(lights); ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(preparation);
        list = default!; diagnostic = null;
        if (time < 0) { diagnostic = "W3D frame time cannot be negative."; return false; }
        float scale=renderScale??owner.Scale;
        Matrix4x4 world;
        EnbRetailSpecialMaterialTransform? special = specialTransform;
        if (suppliedWorld is {} attached)
        {
            world = attached;
            if (!IsFinite(world)) { diagnostic = "Attached W3D RenderObj received a non-finite world matrix."; return false; }
        }
        else
        {
            if (owner.Spatial?.Transform is not IEnbRenderTransform spatial)
            { diagnostic = $"Scene object {owner.GameId} has no spatial transform."; return false; }
            if (!TryBuildObjectTransform(scale, spatial, out world, out diagnostic)) return false;
            TryBuildObjectTransform(1, spatial, out var rigid, out _);
            special ??= new(rigid, scale);
        }
        // Retain the last evaluated rigid layout only while all pose inputs
        // remain identical. Animation sampling and post-transform mutation are
        // dependencies, not reasons to rebuild an unchanged hierarchy.
        bool retainLayout=preparation.CanRetainLayout(resource,specialTransform is null&&skinParentPose is null&&scale!=0&&
            (overrides is null||overrides.RenderObjectScale!=0));
        if(retainLayout&&preparation.TrySnapshotLayout(owner,world,pass,selection,lights,time,animations,overrides,out list,scale))return true;
        bool? preparedResult=scale!=0 && overrides?.RenderObjectScale!=0 && special?.Scale!=0
            ?TryBuildPreparedTraversal(owner,resource,world,time,pass,selection,lights,animations,overrides,preparation,skinParentPose,out list,out diagnostic,scale)
            :null;
        bool ready=preparedResult??TryBuildAtWorldTransformCore(owner, resource, time, pass, selection, lights, animations,
            overrides, world, out list, out diagnostic, special, skinParentPose: skinParentPose, preparation: preparation, renderScale:scale);
        if(ready&&retainLayout)preparation.StoreLayout(owner,world,pass,list,animations,overrides,scale);
        else preparation.InvalidateLayout();
        return ready;
    }

    private static bool TryBuildAtWorldTransformCore(
        IEnbRenderObject sceneObject,
        EnbW3dRenderResource resource,
        long frameTimeMilliseconds,
        EnbRenderPassKind renderPass,
        EnbW3dHlodSelection? hlodSelection,
        EnbW3dLightEnvironment lightEnvironment,
        IReadOnlyList<EnbW3dWeightedAnimationSample>? animationBlend,
        EnbW3dHierarchyState? hierarchyPostTransforms,
        Matrix4x4 objectWorld,
        out EnbW3dDrawInvocation drawList,
        out string? diagnostic, EnbRetailSpecialMaterialTransform? specialTransform = null,
        bool captureProjectedDecals = false, EnbW3dHierarchyPoseOwner? skinParentPose = null,
        EnbW3dMeshOccurrencePreparation? preparation = null, float? renderScale = null)
    {
        drawList = default!;
        diagnostic = null;

        using var preparationFrame = preparation?.Begin(resource);
        var selectedAnimations = preparation?.FrameAnimations ?? new List<EnbW3dResolvedWeightedAnimationSample>();
        if (animationBlend is not null)
        {
            foreach (EnbW3dWeightedAnimationSample requestedAnimation in animationBlend)
            {
                EnbW3dRenderHierarchyAnimation[] matches = resource.Animations
                    .Where(candidate => string.Equals(candidate.Name, requestedAnimation.Name, StringComparison.OrdinalIgnoreCase) &&
                        (requestedAnimation.HierarchyName is null ||
                         string.Equals(candidate.HierarchyName, requestedAnimation.HierarchyName, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (matches.Length != 1)
                {
                    diagnostic = matches.Length == 0
                        ? $"W3D animation '{requestedAnimation.Name}' was not found in '{resource.LogicalFileName}'."
                        : $"W3D animation '{requestedAnimation.Name}' is ambiguous in '{resource.LogicalFileName}' ({matches.Length} matches).";
                    return false;
                }

                EnbW3dRenderHierarchyAnimation selected = matches[0];
                if (!float.IsFinite(requestedAnimation.Frame) || requestedAnimation.Frame <= int.MinValue)
                {
                    diagnostic = $"W3D animation '{requestedAnimation.Name}' frame {requestedAnimation.Frame} is not a finite samplable frame.";
                    return false;
                }
                if (!float.IsFinite(requestedAnimation.Weight) || requestedAnimation.Weight < 0f)
                {
                    diagnostic = $"W3D animation '{requestedAnimation.Name}' has invalid blend weight {requestedAnimation.Weight}.";
                    return false;
                }

                selectedAnimations.Add(new EnbW3dResolvedWeightedAnimationSample(
                    selected,
                    requestedAnimation.Frame,
                    requestedAnimation.Weight));
            }
        }

        EnbW3dRenderHlod? selectedHlod = null;
        IReadOnlyList<EnbW3dRenderHlodMember>? selectedMembers = null;
        HashSet<string>? selectedNames = null;
        HlodMemberBindings? selectedBindings = null;
        var resourceMeshBindings = MeshBindings.GetValue(resource.Meshes, static _ => new AuthoredBindingOwner()).Get(resource);
        bool selectedLevelIsRetailNoOpOnly = false;
        if (hlodSelection is EnbW3dHlodSelection selection)
        {
            EnbW3dRenderHlod? hlod = resource.HierarchicalLods.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, selection.Name, StringComparison.OrdinalIgnoreCase));
            if (hlod is null)
            {
                diagnostic = $"W3D HLOD '{selection.Name}' was not found in '{resource.LogicalFileName}'.";
                return false;
            }

            EnbW3dRenderHlodLevel? level = hlod.Levels.FirstOrDefault(candidate => candidate.Ordinal == selection.LevelOrdinal);
            if (level is null)
            {
                diagnostic = $"W3D HLOD '{selection.Name}' has no level ordinal {selection.LevelOrdinal}.";
                return false;
            }

            selectedHlod = hlod;
            // Current retail attaches 0x0705 aggregate children independently
            // of the selected ordinary HLOD level. They remain drawable for
            // every selected level, while 0x0706 proxy entries are query-only
            // and never enter the render-child list.
            selectedBindings = resourceMeshBindings.GetLevel(resource, hlod, level);
            selectedMembers = selectedBindings.Members;
            selectedNames = selectedBindings.Names;

            // Current retail may attach proven non-mesh RenderObjs whose draw
            // virtual is a no-op. R6Z2 established the built-in NULL object;
            // R6Z5 adds structurally bound ParticleEmitter prototypes. A level
            // composed entirely of such children is intentionally empty rather
            // than a missing-mesh error. Unknown non-mesh names stay fail-closed.
            selectedLevelIsRetailNoOpOnly = selectedBindings.NoOpOnly;
        }

        var hierarchyPoses = preparation?.FramePoses ?? new Dictionary<string, EnbW3dHierarchyPoseOwner>(StringComparer.OrdinalIgnoreCase);
        var invocation=EnbW3dDrawInvocation.Create(resource.Key,sceneObject.RenderIdentity,sceneObject.VisualGeneration,
            frameTimeMilliseconds,hlodSelection,animationBlend is {Count:1}?new EnbW3dAnimationSample(animationBlend[0].Name,animationBlend[0].Frame):null);
        var packets=invocation.Packets;
        var particleEmitters=invocation.ParticleEmitters;
        var dazzles=invocation.Dazzles;
        int bindingOrdinal = 0;
        var decalOccurrences = new Dictionary<(int Mesh,int Pivot,string Name),int>();
        bool selectedDrawableCandidate = false;

        if (selectedHlod is not null && selectedMembers is not null)
        {
            var selectedEmitterMembers = selectedBindings!.ExternalMembers;
            if (selectedEmitterMembers.Length != 0)
            {
                selectedDrawableCandidate = true;
                EnbW3dRenderHierarchy? hierarchy = resource.Hierarchies.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, selectedHlod.HierarchyName, StringComparison.OrdinalIgnoreCase));
                if (hierarchy is null)
                {
                    diagnostic = $"W3D HLOD '{selectedHlod.Name}' requires missing hierarchy '{selectedHlod.HierarchyName}'.";
                    return false;
                }
                if (!TryResolveHierarchyPose(
                        hierarchy,
                        objectWorld,
                        selectedAnimations,
                        hierarchyPostTransforms,
                        hierarchyPoses,
                        out EnbW3dHierarchyPoseOwner pose,
                        out diagnostic, preparation))
                {
                    return false;
                }

                // All dazzle children in this selected HLOD use the same rigid
                // hierarchy inputs. Evaluate once for this compilation, without
                // retaining animation or transform state across frames.
                EnbW3dHierarchyPoseOwner? dazzleRigidPose = null;
                foreach ((EnbW3dRenderHlodMember member, EnbW3dExternalRenderObject external, int memberOrdinal) in selectedEmitterMembers)
                {
                    if (member.ReferenceIndex >= (uint)pose.WorldTransforms.Length)
                    {
                        diagnostic = $"W3D HLOD '{selectedHlod.Name}' ParticleEmitter member '{member.Name}' references hierarchy state record {member.ReferenceIndex}, outside '{hierarchy.Name}' ({pose.WorldTransforms.Length} records).";
                        return false;
                    }

                    int pivotIndex = (int)member.ReferenceIndex;
                    if (!captureProjectedDecals && !pose.Visibility[pivotIndex])
                        continue;

                    if(external.SpecialMaterialDefinition is {} special)
                    {
                        AddSpecial(special,pose.WorldTransforms[pivotIndex],hierarchy.Name,pivotIndex,memberOrdinal);
                        continue;
                    }
                    if (external.DazzleDefinition is {} dazzle)
                    {
                        var dazzleWorld=pose.WorldTransforms[pivotIndex];
                        //96BAB0 scales the dazzle's stored size, not its basis.
                        //The shared mesh pose above includes RenderObjectScale;
                        //96E270 must receive the rigid child record instead.
                        if(hierarchyPostTransforms is {} dazzlePost &&
                            dazzlePost.HierarchyName.Equals(hierarchy.Name,StringComparison.OrdinalIgnoreCase))
                        {
                            if (dazzleRigidPose is null)
                            {
                                var animations=selectedAnimations.Where(a=>a.Animation.HierarchyName.Equals(hierarchy.Name,StringComparison.OrdinalIgnoreCase)).ToArray();
                                if(!EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(hierarchy,dazzlePost.RigidWorldTransform,
                                    dazzlePost.HierarchyTranslationScale,animations,dazzlePost.Transforms,out dazzleRigidPose,out diagnostic))return false;
                            }
                            dazzleWorld=dazzleRigidPose.WorldTransforms[pivotIndex];
                        }
                        dazzles.Add(new(dazzle, dazzleWorld)
                        {HlodName=selectedHlod.Name,LevelOrdinal=hlodSelection!.Value.LevelOrdinal,MemberOrdinal=memberOrdinal,DazzleInstanceScale=external.DazzleInstanceScale,HasConfiguredDazzleScale=external.HasConfiguredDazzleScale});
                        continue;
                    }
                    particleEmitters.Add(new EnbW3dParticleEmitterDrawInstance(
                        member.Name,
                        external.ResourceName,
                        sceneObject.GameId,
                        renderPass,
                        external.ParticleEmitterDefinition!,
                        external.ParticleTextureLocation!,
                        pose.WorldTransforms[pivotIndex],
                        hierarchy.Name,
                        pivotIndex,
                        memberOrdinal)
                    { RetainedTexture = external.ParticleRetainedTexture,
                      RuntimeTextureName = external.ParticleRetainedTexture?.LogicalName });
                }
            }
        }

        //A selected standalone 0x900 prototype renders at its owner transform;
        //0096ED00 constructs class32 directly, without an HLOD/pivot wrapper.
        if(selectedHlod is null && resource.HierarchyModels.Count == 0 && resource.HierarchicalLods.Count == 0)
        {
            foreach(var external in resource.ExternalRenderObjects)
                if(external.SpecialMaterialDefinition is {} special)AddSpecial(special,objectWorld,null,0,0);
            for(int ordinal=0;ordinal<resource.ExternalRenderObjects.Count;ordinal++)
                if(resource.ExternalRenderObjects[ordinal].DazzleDefinition is {} definition)
                {
                    selectedDrawableCandidate=true;
                    dazzles.Add(new(definition,objectWorld){MemberOrdinal=ordinal,DazzleInstanceScale=resource.ExternalRenderObjects[ordinal].DazzleInstanceScale,HasConfiguredDazzleScale=resource.ExternalRenderObjects[ordinal].HasConfiguredDazzleScale});
                }
        }

        if(selectedHlod is null)
            foreach(var model in resource.HierarchyModels)
                foreach(var node in model.Nodes)
                {
                    var definition=resource.ExternalRenderObjects.FirstOrDefault(e=>e.Name.Equals(node.RenderObjectName,StringComparison.OrdinalIgnoreCase))?.SpecialMaterialDefinition;
                    if(definition is null)continue;
                    var hierarchy=resource.Hierarchies.FirstOrDefault(h=>h.Name.Equals(model.HierarchyName,StringComparison.OrdinalIgnoreCase));
                    if(hierarchy is null || !TryResolveHierarchyPose(hierarchy,objectWorld,selectedAnimations,hierarchyPostTransforms,hierarchyPoses,out var pose,out diagnostic, preparation))return false;
                    if(node.PivotIndex>=pose.WorldTransforms.Length){diagnostic="Special-material HMODEL pivot is out of range.";return false;}
                    if(pose.Visibility[node.PivotIndex])AddSpecial(definition,pose.WorldTransforms[node.PivotIndex],hierarchy.Name,node.PivotIndex,node.Ordinal);
                }

        void AddSpecial(EnbCurrentRetailSpecialMaterialDefinition definition,Matrix4x4 world,string? hierarchy,int pivot,int occurrence)
        {
            selectedDrawableCandidate=true;
            if((definition.Flags&0x1000)!=0)return;
            // Class24's Scale virtual is a no-op. Scale belongs only to
            // hierarchy translations; preserve the actual rigid pivot record.
            var transform=hierarchyPostTransforms is {} post
                ? new EnbRetailSpecialMaterialTransform(post.RigidWorldTransform,post.HierarchyTranslationScale)
                : specialTransform;
            if(transform is {} factory)
            {
                world=factory.RigidWorld;
                if(hierarchy is not null)
                {
                    var h=resource.Hierarchies.Single(h=>h.Name.Equals(hierarchy,StringComparison.OrdinalIgnoreCase));
                    var animations=selectedAnimations.Where(a=>a.Animation.HierarchyName.Equals(hierarchy,StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(!EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(h,world,factory.Scale,animations,
                        hierarchyPostTransforms?.Transforms??new Dictionary<int,Matrix4x4>(),out var rigidPose,out var error))
                        throw new InvalidDataException(error);
                    world=rigidPose.WorldTransforms[pivot];
                }
            }
            var instance=new EnbRetailSpecialMaterialInstance(definition);
            foreach(var specialPacket in EnbRetailSpecialMaterialDrawPacket.Build(instance,resource.Key,world,lightEnvironment,
                frameTimeMilliseconds,sceneObject.RenderIdentity,sceneObject.VisualGeneration,sceneObject.GameId,out _))
                packets.Add((specialPacket).ForkTask().SetOrderKey(specialPacket.OrderKey with {RenderPass=renderPass}).SetHierarchyName(hierarchy).SetPivotIndex(pivot).SetRenderInstancePath($"special/{hierarchy}/{occurrence}").SetSpecialMaterial(specialPacket.SpecialMaterial! with {BindRetainedInstance=true,
                        RetainedOwnerPath=selectedHlod is null?$"special/{hierarchy}/{occurrence}":
                            $"special/{hierarchy}/hlod/{selectedHlod.Name}/{hlodSelection!.Value.LevelOrdinal}/{occurrence}"}));
        }

        foreach (EnbW3dRenderMesh mesh in resource.Meshes)
        {
            //9B6000 stores302/303/304 in one untyped array;98C7A0 attaches
            //all successfully created children to its single retained level.
            var drawBindings = resourceMeshBindings.Nodes[mesh];

            if (drawBindings.Length == 0)
            {
                if (selectedHlod is null)
                {
                    selectedDrawableCandidate = true;
                    //0093EFE0 asks the attached mesh's actual parent for +CC.
                    //A direct child uses that hierarchy, not its mount's one
                    //matrix and not an unrelated hierarchy in its source FILE.
                    EnbW3dDeformedGeometry? attachedSkin=null;
                    if(mesh.BoneIndices.Count!=0 && skinParentPose is not null &&
                        !TryDeformPrepared(mesh,skinParentPose,preparation,out attachedSkin,out diagnostic))return false;
                    if (!AppendMeshPackets(
                            sceneObject,
                            resource,
                            mesh,
                            attachedSkin is null?objectWorld:Matrix4x4.Identity,
                            objectWorld,
                            deformedGeometry: attachedSkin,
                            lightEnvironment,
                            renderPass,
                            frameTimeMilliseconds,
                            hierarchyName: null,
                            pivotIndex: -1,
                            renderObjectName: mesh.Name,
                            ref bindingOrdinal,
                            packets,
                            (renderScale??sceneObject.Scale),
                            out diagnostic, preparation, decalOccurrences))
                    {
                        return false;
                    }
                    continue;
                }

                var matchingMembers = selectedBindings!.MeshMembers[mesh];
                if (matchingMembers.Length == 0)
                    continue;
                selectedDrawableCandidate = true;

                EnbW3dRenderHierarchy? hierarchy = resource.Hierarchies.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, selectedHlod.HierarchyName, StringComparison.OrdinalIgnoreCase));
                if (hierarchy is null)
                {
                    diagnostic = $"W3D HLOD '{selectedHlod.Name}' requires missing hierarchy '{selectedHlod.HierarchyName}'.";
                    return false;
                }
                if (!TryResolveHierarchyPose(
                        hierarchy,
                        objectWorld,
                        selectedAnimations,
                        hierarchyPostTransforms,
                        hierarchyPoses,
                        out EnbW3dHierarchyPoseOwner pose,
                        out diagnostic, preparation))
                {
                    return false;
                }

                // Current retail materializes each selected HLOD member as a
                // child RenderObject and assigns it the hierarchy state record
                // selected by 0x0704 ReferenceIndex. A simple MeshClass has no
                // HMODEL node of its own, so this member record is its only
                // authored draw transform. Preserve multiple member instances
                // instead of collapsing them through the name filter.
                foreach (EnbW3dRenderHlodMember member in matchingMembers)
                {
                    if (member.ReferenceIndex >= (uint)pose.WorldTransforms.Length)
                    {
                        diagnostic = $"W3D HLOD '{selectedHlod.Name}' member '{member.Name}' references hierarchy state record {member.ReferenceIndex}, outside '{hierarchy.Name}' ({pose.WorldTransforms.Length} records).";
                        return false;
                    }

                    int pivotIndex = (int)member.ReferenceIndex;
                    if (!captureProjectedDecals && !pose.Visibility[pivotIndex])
                        continue;

                    Matrix4x4 memberWorld = pose.WorldTransforms[pivotIndex];
                    EnbW3dDeformedGeometry? memberGeometry = null;
                    // Mesh influence state also owns deformation for HLOD
                    // children. 0093EFE0 obtains the parent's hierarchy through
                    // virtual +CC, independently of HMODEL connection records.
                    if (mesh.BoneIndices.Count != 0 &&
                        !TryDeformPrepared(mesh,pose,preparation,out memberGeometry,out diagnostic))
                        return false;
                    if (!AppendMeshPackets(
                        sceneObject,
                        resource,
                        mesh,
                        memberGeometry is null ? memberWorld : Matrix4x4.Identity,
                        memberWorld,
                        memberGeometry,
                            lightEnvironment,
                            renderPass,
                            frameTimeMilliseconds,
                            hierarchy.Name,
                            pivotIndex,
                            member.Name,
                            ref bindingOrdinal,
                            packets,
                            hierarchyPostTransforms is {} memberScale && string.Equals(memberScale.HierarchyName,hierarchy.Name,StringComparison.OrdinalIgnoreCase)
                                ?memberScale.RenderObjectScale:(renderScale??sceneObject.Scale),
                            out diagnostic, preparation, decalOccurrences))
                    {
                        return false;
                    }
                }
                continue;
            }

            foreach ((EnbW3dRenderHierarchyModel model, EnbW3dRenderHierarchyModelNode node) in drawBindings)
            {
                if (selectedNames is not null &&
                    !selectedNames.Contains(node.RenderObjectName) &&
                    selectedBindings!.MeshMembers[mesh].IsEmpty)
                {
                    continue;
                }
                selectedDrawableCandidate = true;

                EnbW3dRenderHierarchy? hierarchy = resource.Hierarchies.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, model.HierarchyName, StringComparison.OrdinalIgnoreCase));
                if (hierarchy is null)
                {
                    diagnostic = $"Hierarchy model '{model.Name}' requires missing hierarchy '{model.HierarchyName}'.";
                    return false;
                }

                if (!TryResolveHierarchyPose(
                        hierarchy,
                        objectWorld,
                        selectedAnimations,
                        hierarchyPostTransforms,
                        hierarchyPoses,
                        out EnbW3dHierarchyPoseOwner pose,
                        out diagnostic, preparation))
                {
                    return false;
                }

                if (node.PivotIndex >= pose.WorldTransforms.Length)
                {
                    diagnostic = $"Hierarchy model '{model.Name}' node {node.Ordinal} references missing pivot {node.PivotIndex}.";
                    return false;
                }

                if (!captureProjectedDecals && !pose.Visibility[node.PivotIndex])
                    continue;

                Matrix4x4 packetWorld;
                Matrix4x4 boundsWorld;
                EnbW3dDeformedGeometry? deformedGeometry = null;

                // Current retail selects the deformation/final-submit path from
                // the mesh model's influence state (runtime flag 0x400), which
                // is set when the vertex-influence chunk is loaded. The HMODEL
                // 0x0302/0x0304 connection kind does not choose rigid versus
                // deformed rendering. In EnbNext the equivalent immutable
                // signal is the presence of the decoded per-vertex u16 array.
                bool hasVertexInfluences = mesh.BoneIndices.Count != 0;
                if (hasVertexInfluences)
                {
                    if (!TryDeformPrepared(mesh,pose,preparation,out deformedGeometry,out diagnostic))
                        return false;

                    // Retail submits already-deformed influenced vertices with
                    // an identity world matrix, but HMODEL still binds the child
                    // render object to its authored node pivot. Composite culling
                    // transforms the child's local bounds through that node-pivot
                    // state record, so draw and bounds transforms deliberately
                    // diverge here.
                    packetWorld = Matrix4x4.Identity;
                    boundsWorld = pose.WorldTransforms[node.PivotIndex];
                }
                else
                {
                    packetWorld = pose.WorldTransforms[node.PivotIndex];
                    boundsWorld = packetWorld;
                }

                if (!AppendMeshPackets(
                        sceneObject,
                        resource,
                        mesh,
                        packetWorld,
                        boundsWorld,
                        deformedGeometry,
                        lightEnvironment,
                        renderPass,
                        frameTimeMilliseconds,
                        hierarchy.Name,
                        node.PivotIndex,
                        node.RenderObjectName,
                        ref bindingOrdinal,
                        packets,
                        hierarchyPostTransforms is {} nodeScale && string.Equals(nodeScale.HierarchyName,hierarchy.Name,StringComparison.OrdinalIgnoreCase)
                            ?nodeScale.RenderObjectScale:(renderScale??sceneObject.Scale),
                        out diagnostic, preparation, decalOccurrences))
                {
                    return false;
                }
            }
        }

        if (hlodSelection is not null &&
            selectedNames is { Count: > 0 } &&
            !selectedDrawableCandidate &&
            !selectedLevelIsRetailNoOpOnly)
        {
            diagnostic = $"Selected W3D HLOD level produced no drawable mesh in '{resource.LogicalFileName}'.";
            return false;
        }

        var zeroScaleTransform = hierarchyPostTransforms is { RenderObjectScale: 0 } postZero
            ? new EnbRetailSpecialMaterialTransform(postZero.RigidWorldTransform, 0)
            : specialTransform is { Scale: 0 } specialZero ? specialZero : (EnbRetailSpecialMaterialTransform?)null;
        if (zeroScaleTransform is { } zeroFactory)
        {
            var rigidPoses = new Dictionary<string, EnbW3dHierarchyPoseOwner>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < packets.Count; i++)
            {
                var packet = packets[i];
                if (packet.SpecialMaterial is not null || packet.DeformedGeometry is not null) continue;
                var rigid = zeroFactory.RigidWorld;
                if (packet.HierarchyName is { } name)
                {
                    if (!rigidPoses.TryGetValue(name, out var pose))
                    {
                        var hierarchy = resource.Hierarchies.Single(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        var animations = selectedAnimations.Where(a => a.Animation.HierarchyName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (!EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(hierarchy, rigid, hierarchyPostTransforms?.HierarchyTranslationScale??0, animations,
                                hierarchyPostTransforms?.Transforms ?? new Dictionary<int, Matrix4x4>(), out pose, out diagnostic)) return false;
                        rigidPoses.Add(name, pose);
                    }
                    rigid = pose.WorldTransforms[packet.PivotIndex];
                }
                packets[i] = (packet).ForkTask().SetCurrentRetailUnscaledWorldTransform(rigid);
            }
        }

        drawList=invocation;
        return true;
    }

    private static bool TryDeformPrepared(EnbW3dRenderMesh mesh,EnbW3dHierarchyPoseOwner pose,
        EnbW3dMeshOccurrencePreparation? preparation,out EnbW3dDeformedGeometry geometry,out string? diagnostic)
        =>preparation is null?EnbW3dSkinDeformer.TryDeform(mesh,pose,out geometry,out diagnostic)
            :preparation.TryDeform(mesh,pose,out geometry,out diagnostic);

    private static bool TryResolveHierarchyPose(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 objectWorld,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> selectedAnimations,
        EnbW3dHierarchyState? hierarchyPostTransforms,
        Dictionary<string, EnbW3dHierarchyPoseOwner> hierarchyPoses,
        out EnbW3dHierarchyPoseOwner pose,
        out string? diagnostic, EnbW3dMeshOccurrencePreparation? preparation = null)
    {
        diagnostic = null;
        if (hierarchyPoses.TryGetValue(hierarchy.Name, out pose!))
            return true;

        // Prepared production occurrences retain both rigid and scaled pivot storage.
        // Standalone diagnostic compilation has no occurrence lifetime to retain it on,
        // but follows the same evaluator rather than a second LINQ/copy path.
        var owner=preparation?.HierarchyOwner(hierarchy)
            ??new EnbW3dMeshOccurrencePreparation.HierarchyDrawOwner(hierarchy);
        if(!owner.Update(preparation,objectWorld,selectedAnimations,hierarchyPostTransforms,out diagnostic))return false;
        pose=owner.Pose;hierarchyPoses.Add(hierarchy.Name,pose);return true;
    }


    private static bool AppendMeshPackets(
        IEnbRenderObject sceneObject,
        EnbW3dRenderResource resource,
        EnbW3dRenderMesh mesh,
        Matrix4x4 worldTransform,
        Matrix4x4 boundsTransform,
        EnbW3dDeformedGeometry? deformedGeometry,
        EnbW3dLightEnvironment lightEnvironment,
        EnbRenderPassKind renderPass,
        long frameTimeMilliseconds,
        string? hierarchyName,
        int pivotIndex,
        string renderObjectName,
        ref int bindingOrdinal,
        List<EnbW3dDrawTask> packets,
        float meshModelScale,
        out string? diagnostic, EnbW3dMeshOccurrencePreparation? preparation,
        Dictionary<(int Mesh,int Pivot,string Name),int> decalOccurrences)
    {
        diagnostic = null;
        if (!TryCreateBounds(
                mesh.BoundingSphereCenter,
                mesh.BoundingRadius,
                boundsTransform,
                out EnbW3dWorldBounds bounds))
        {
            diagnostic = $"Mesh '{mesh.Name}' produced invalid world culling bounds.";
            return false;
        }

        var occurrenceKey=(mesh.Ordinal,pivotIndex,renderObjectName);
        int repetition=decalOccurrences.GetValueOrDefault(occurrenceKey);
        decalOccurrences[occurrenceKey]=repetition+1;
        EnbW3dMeshOccurrenceIdentity? occurrence=null;
        EnbW3dDrawTask? meshFrame=null;
        foreach (EnbW3dRenderMaterialPass pass in mesh.MaterialPasses)
        {
            EnbW3dDrawBlendClass blendClass = ClassifyBlend(pass);
            var orderKey = new EnbW3dDrawOrderKey(
                renderPass,
                blendClass,
                mesh.StaticSort,
                resource.Key,
                mesh.Ordinal,
                pass.Ordinal,
                sceneObject.RenderIdentity,
                bindingOrdinal);

            if (preparation is not null)
            {
                var preparedPacket=preparation.Snapshot(sceneObject, orderKey, mesh,
                    OwnerCache.Get(ref pass.DerivedDrawMaterial, pass, static source => new EnbW3dDrawMaterialBinding(source, source.SingleBindingSemantics)),
                    hierarchyName, pivotIndex, renderObjectName, worldTransform, bounds, deformedGeometry,
                    lightEnvironment, frameTimeMilliseconds, meshModelScale,meshFrame);
                occurrence??=preparedPacket.MeshOccurrence is { } existing && existing.Repetition==repetition ? existing : new(repetition);
                preparedPacket.MeshOccurrence=occurrence;
                meshFrame??=preparedPacket;
                packets.Add(preparedPacket);
                bindingOrdinal++;
                continue;
            }
            packets.Add(new EnbW3dDrawTask(
                orderKey,
                sceneObject.RenderIdentity,
                sceneObject.VisualGeneration,
                sceneObject.GameId,
                worldTransform,
                bounds,
                mesh,
                deformedGeometry,
                OwnerCache.Get(ref pass.DerivedDrawMaterial,pass,static source=>new EnbW3dDrawMaterialBinding(source,source.SingleBindingSemantics)),
                lightEnvironment,
                frameTimeMilliseconds,
                hierarchyName,
                pivotIndex,
                renderObjectName)
            {
                CurrentRetailMeshModelScale = meshModelScale,
                MeshOccurrence = occurrence??=new(repetition),
            });
            bindingOrdinal++;
        }

        return true;
    }

    internal static EnbW3dDrawBlendClass ClassifyBlend(EnbW3dRenderMaterialPass pass)
        => OwnerCache.Get(ref pass.DerivedBlendClass, pass, static value => new EnbW3dDrawBlendClassBox(ClassifyBlendCore(value))).Value;

    private static EnbW3dDrawBlendClass ClassifyBlendCore(EnbW3dRenderMaterialPass pass)
    {
        if (pass.TriangleBindings.Count == 0)
        {
            return pass.SingleBindingSemantics is EnbW3dCompiledMaterialSemantics single
                ? single.FixedFunction.BlendEnabled ? EnbW3dDrawBlendClass.Blended : EnbW3dDrawBlendClass.Opaque
                : EnbW3dDrawBlendClass.MixedOrUnspecified;
        }

        if (pass.TriangleBindings[0].Semantics is not EnbW3dCompiledMaterialSemantics firstSemantics)
            return EnbW3dDrawBlendClass.MixedOrUnspecified;

        bool first = firstSemantics.FixedFunction.BlendEnabled;
        for (int index = 1; index < pass.TriangleBindings.Count; index++)
        {
            if (pass.TriangleBindings[index].Semantics is not EnbW3dCompiledMaterialSemantics semantics ||
                semantics.FixedFunction.BlendEnabled != first)
            {
                return EnbW3dDrawBlendClass.MixedOrUnspecified;
            }
        }
        return first ? EnbW3dDrawBlendClass.Blended : EnbW3dDrawBlendClass.Opaque;
    }

    private static bool TryBuildObjectTransform(
        float scale,
        IEnbRenderTransform spatial,
        out Matrix4x4 worldTransform,
        out string? diagnostic)
    {
        worldTransform = default;
        diagnostic = null;
        if (!float.IsFinite(scale) || !IsFinite(spatial.Position) || !IsFinite(spatial.Orientation))
        {
            diagnostic = "Scene object transform contains non-finite scale/position/orientation data.";
            return false;
        }

        if (!EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial,
                out Matrix4x4 rigidWorld,
                out diagnostic))
        {
            return false;
        }

        // Retail repairs the provider's Matrix3D before handing it to the
        // RenderObject. The BaseAsset scalar remains a separate uniform scale.
        worldTransform = Matrix4x4.CreateScale(scale) * rigidWorld;
        if (!IsFinite(worldTransform))
        {
            diagnostic = "Scene object transform produced a non-finite world matrix.";
            return false;
        }
        return true;
    }

    internal static bool TryCreateBounds(
        Vector3 localCenter,
        float localRadius,
        Matrix4x4 world,
        out EnbW3dWorldBounds bounds)
    {
        bounds = default;
        if (!IsFinite(localCenter) || !float.IsFinite(localRadius) || localRadius < 0f)
            return false;

        float xScale = new Vector3(world.M11, world.M12, world.M13).Length();
        float yScale = new Vector3(world.M21, world.M22, world.M23).Length();
        float zScale = new Vector3(world.M31, world.M32, world.M33).Length();
        float maxScale = MathF.Max(xScale, MathF.Max(yScale, zScale));
        Vector3 center = Vector3.Transform(localCenter, world);
        float radius = localRadius * maxScale;
        bounds = new EnbW3dWorldBounds(center, radius);
        return bounds.IsValid;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    internal static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

/// <summary>Reference box so a pass can own its blend classification (see OwnerCache).</summary>
internal sealed record EnbW3dDrawBlendClassBox(EnbW3dDrawBlendClass Value);
