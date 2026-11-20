using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Canonical EnbNext mesh topology at the backend handoff. Current retail can
/// submit both D3D8 triangle lists and triangle strips, but decoded W3D geometry
/// is authored as triangles and EnbNext already owns a flattened triangle index
/// stream. This therefore keeps one exact triangle-list representation instead of
/// preserving retail stripification as a compatibility requirement.
/// </summary>
public enum EnbW3dIndexedPrimitiveTopology : byte
{
    TriangleList = 0,
}

/// <summary>
/// Selects which immutable vertex stream a concrete backend uploads/binds.
/// Influenced meshes are already CPU-deformed and must be submitted with
/// identity world, matching the current-retail final mesh-render boundary.
/// </summary>
public enum EnbW3dSubmissionVertexSource : byte
{
    RigidMesh = 0,
    CpuDeformed = 1,
}

/// <summary>
/// One contiguous indexed triangle-list range with one effective material
/// binding. The fields map directly onto the semantic inputs of an indexed draw:
/// StartIndex, PrimitiveCount, MinVertexIndex and VertexCount. IndexCount is kept
/// explicitly so a backend never has to infer the triangle-list multiplier.
/// </summary>
public readonly record struct EnbW3dEffectiveSubmissionBinding
{
    private readonly EnbW3dTriangleMaterialState? state;
    public EnbW3dRenderVertexMaterialBinding? VertexMaterial
    {get=>state?.Key.Material;init=>state=new((state?.Key??default) with {Material=value});}
    public EnbW3dRenderShaderBinding? Shader
    {get=>state?.Key.Shader;init=>state=new((state?.Key??default) with {Shader=value});}
    public EnbW3dCompiledMaterialSemantics Semantics
    {get=>state!.Key.Semantics!.Value;init=>state=new((state?.Key??default) with {Semantics=value});}
    public EnbW3dSubmissionTextures Textures {get;init;}
    public bool UsesImplicitVertexMaterial {get;init;}
    public bool UsesImplicitShader {get;init;}
    public bool CurrentRetailNullMaterial {get;init;}
    internal EnbW3dEffectiveSubmissionBinding(EnbW3dRenderTriangleBinding source)
    {
        state=source.SharedState;
        Textures=new(source.Textures);
        UsesImplicitVertexMaterial=source.UsesImplicitVertexMaterial;
        UsesImplicitShader=source.UsesImplicitShader;
        CurrentRetailNullMaterial=source.CurrentRetailNullMaterial;
    }
    public EnbW3dEffectiveSubmissionBinding(EnbW3dRenderVertexMaterialBinding? VertexMaterial,
        EnbW3dRenderShaderBinding? Shader,ImmutableArray<EnbW3dRenderTextureBinding?> Textures,
        EnbW3dCompiledMaterialSemantics Semantics,bool UsesImplicitVertexMaterial,bool UsesImplicitShader)
    {
        state=new(new(VertexMaterial,Shader,Semantics));this.Textures=new(Textures);
        this.UsesImplicitVertexMaterial=UsesImplicitVertexMaterial;this.UsesImplicitShader=UsesImplicitShader;
    }
}

public sealed record EnbW3dIndexedSubmissionRun(
    int FirstTriangleOrdinal,
    int TriangleCount,
    int StartIndex,
    int IndexCount,
    uint MinVertexIndex,
    uint MaxVertexIndex,
    uint VertexCount,
    EnbW3dEffectiveSubmissionBinding Binding)
{
    public int PrimitiveCount => TriangleCount;
}

/// <summary>
/// Immutable backend-facing submission plan for one draw packet. It owns no
/// GPU object and chooses no graphics API. A concrete backend consumes the
/// packet's already-closed world/material/light state plus these exact indexed
/// ranges and decides only resource realization and command encoding.
/// </summary>
public readonly record struct EnbW3dIndexedSubmissionPlan(
    EnbW3dDrawTask Packet,
    EnbW3dSubmissionVertexSource VertexSource,
    EnbW3dIndexedPrimitiveTopology PrimitiveTopology,
    ImmutableArray<EnbW3dIndexedSubmissionRun> Runs)
{
    internal long TopologyRevision {get;init;}=Packet?.Mesh?.TopologyRevision??0;
    public Matrix4x4 WorldTransform => Packet.WorldTransform;
    public EnbW3dNPatchGeometry? NPatchGeometry { get; init; }
    public IReadOnlyList<uint> Indices => NPatchGeometry is {} patch ? patch.Indices : Packet.Mesh.Indices;
    public IReadOnlyList<Vector3> Positions =>
        NPatchGeometry is {} patch ? patch.Positions : Packet.CurrentRetailViewPositions ?? (Packet.DeformedGeometry is EnbW3dDeformedGeometry deformed
            ? deformed.Positions
            : Packet.Mesh.Positions);
    public IReadOnlyList<Vector3> Normals =>
        NPatchGeometry is {} patch ? patch.Normals : Packet.DeformedGeometry is EnbW3dDeformedGeometry deformed
            ? deformed.Normals
            : Packet.Mesh.Normals;
}

/// <summary>
/// Converts the draw-packet contract into concrete indexed submission
/// ranges. It preserves authored triangle order and only coalesces adjacent
/// triangles whose fully resolved effective binding is equal. Non-adjacent
/// triangles are never reordered merely to reduce state changes.
/// </summary>
// Producer-owned immutable topology. Published frames retain their prefix;
// capacity growth never rewrites an array used by an older/queued frame.
internal sealed class EnbW3dGeneratedTopology(uint[] storage, int indexCount, int vertexCount)
{
    internal readonly uint[] Storage = storage;
    internal readonly int VertexCount = vertexCount;
    internal readonly IReadOnlyList<uint> Indices = new ArraySegment<uint>(storage,0,indexCount);
    internal bool Describes(EnbW3dRenderMesh mesh) => ReferenceEquals(Indices,mesh.Indices) &&
        mesh.Positions.Count==VertexCount && mesh.TriangleMetadata.Count==Indices.Count/3;
    internal static bool Same(EnbW3dDrawTask previous,EnbW3dDrawTask current)
    {
        var a=previous.Mesh.GeneratedTopology; var b=current.Mesh.GeneratedTopology;
        if(a is null || b is null || !ReferenceEquals(a.Storage,b.Storage) ||
            a.VertexCount!=b.VertexCount || a.Indices.Count!=b.Indices.Count ||
            !a.Describes(previous.Mesh) || !b.Describes(current.Mesh) ||
            previous.Mesh.Normals.Count!=current.Mesh.Normals.Count)return false;
        var left=previous.Material.Source.TriangleBindings;var right=current.Material.Source.TriangleBindings;
        return ReferenceEquals(left,right) ||
            left is ArraySegment<EnbW3dRenderTriangleBinding> x &&
            right is ArraySegment<EnbW3dRenderTriangleBinding> y &&
            ReferenceEquals(x.Array,y.Array) && x.Offset==y.Offset && x.Count==y.Count;
    }
}

public static class EnbW3dIndexedSubmissionCompiler
{
    /// <summary>Only immutable rigid geometry/material streams can share compiled runs.
    /// Pose, lighting, mapper time and per-frame UV offsets remain on the new packet.</summary>
    public static bool CanReuseGeometry(EnbW3dIndexedSubmissionPlan previous, EnbW3dDrawTask packet)
        => previous.Packet.DeformedGeometry is null && packet.DeformedGeometry is null &&
           previous.Packet.Mesh.Positions is not EnbW3dFrameStream<Vector3> &&
           packet.Mesh.Positions is not EnbW3dFrameStream<Vector3> &&
           ReferenceEquals(previous.Packet.Mesh,packet.Mesh) &&
           ReferenceEquals(previous.Packet.Material.Source,packet.Material.Source) &&
           CanReuseTopology(previous, packet);

    // Skinning changes positions and normals, not indices or material runs.
    // This deliberately does not authorize retaining the previous GPU vertex stream.
    internal static bool CanReuseTopology(EnbW3dIndexedSubmissionPlan previous, EnbW3dDrawTask packet)
        => previous.TopologyRevision==packet.Mesh.TopologyRevision && ((ReferenceEquals(previous.Packet.Mesh, packet.Mesh) &&
             ReferenceEquals(previous.Packet.Material.Source, packet.Material.Source)) ||
            EnbW3dGeneratedTopology.Same(previous.Packet,packet)) &&
           ReferenceEquals(previous.Packet.CurrentRetailMaterialMappers, packet.CurrentRetailMaterialMappers) &&
           Equals(previous.Packet.CurrentRetailSharedMaterialMappers, packet.CurrentRetailSharedMaterialMappers) &&
           previous.Packet.OrderKey == packet.OrderKey &&
           previous.Packet.RenderInstancePath == packet.RenderInstancePath &&
           previous.Packet.VisualGeneration == packet.VisualGeneration &&
           previous.Packet.RenderObjectName == packet.RenderObjectName;

    public static bool TryCompile(
        EnbW3dDrawTask packet,
        out EnbW3dIndexedSubmissionPlan plan,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(packet);
        plan = default!;
        diagnostic = null;

        EnbW3dRenderMesh mesh = packet.Mesh;
        EnbW3dRenderMaterialPass pass = packet.Material.Source;

        if (mesh.Indices.Count % 3 != 0)
        {
            diagnostic = $"Mesh '{mesh.Name}' has {mesh.Indices.Count} flattened indices; R6A triangle-list submission requires a multiple of three.";
            return false;
        }

        int triangleCount = mesh.Indices.Count / 3;
        if (mesh.TriangleMetadata.Count != triangleCount)
        {
            diagnostic = $"Mesh '{mesh.Name}' has {triangleCount} indexed triangles but {mesh.TriangleMetadata.Count} triangle metadata records.";
            return false;
        }

        if (pass.TriangleBindings.Count != triangleCount)
        {
            diagnostic = $"Mesh '{mesh.Name}' pass {pass.Ordinal} has {pass.TriangleBindings.Count} effective triangle bindings for {triangleCount} indexed triangles.";
            return false;
        }

        EnbW3dSubmissionVertexSource vertexSource;
        int positionCount;
        int normalCount;
        if (packet.DeformedGeometry is EnbW3dDeformedGeometry deformed)
        {
            vertexSource = EnbW3dSubmissionVertexSource.CpuDeformed;
            if (!packet.WorldTransform.Equals(Matrix4x4.Identity))
            {
                diagnostic = $"Mesh '{mesh.Name}' carries CPU-deformed geometry with a non-identity world transform.";
                return false;
            }

            if (deformed.Positions.Count != mesh.Positions.Count)
            {
                diagnostic = $"Mesh '{mesh.Name}' carries {deformed.Positions.Count} deformed positions for {mesh.Positions.Count} source vertices.";
                return false;
            }

            if (deformed.Normals.Count != mesh.Normals.Count)
            {
                diagnostic = $"Mesh '{mesh.Name}' carries {deformed.Normals.Count} deformed normals for {mesh.Normals.Count} source normals.";
                return false;
            }

            positionCount = deformed.Positions.Count;
            normalCount = deformed.Normals.Count;
        }
        else
        {
            vertexSource = mesh.Positions is EnbW3dFrameStream<Vector3>
                ?EnbW3dSubmissionVertexSource.CpuDeformed:EnbW3dSubmissionVertexSource.RigidMesh;
            if(vertexSource==EnbW3dSubmissionVertexSource.CpuDeformed && packet.WorldTransform!=Matrix4x4.Identity)
            {diagnostic="Borrowed generated vertices require an identity world transform.";return false;}
            positionCount = mesh.Positions.Count;
            normalCount = mesh.Normals.Count;
        }

        if(packet.CurrentRetailViewPositions is {} viewPositions &&
            (viewPositions.Count!=positionCount || viewPositions.Any(p=>!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z))))
        {
            diagnostic=$"Mesh '{mesh.Name}' has an invalid current-retail view-space vertex stream.";
            return false;
        }
        if (normalCount != 0 && normalCount != positionCount)
        {
            diagnostic = $"Mesh '{mesh.Name}' has {normalCount} normals for {positionCount} submission vertices.";
            return false;
        }

        for (int triangleOrdinal = 0; triangleOrdinal < triangleCount; triangleOrdinal++)
        {
            EnbW3dRenderTriangleBinding binding = pass.TriangleBindings[triangleOrdinal];
            if (binding.TriangleOrdinal != triangleOrdinal)
            {
                diagnostic = $"Mesh '{mesh.Name}' pass {pass.Ordinal} binding {triangleOrdinal} names triangle {binding.TriangleOrdinal}.";
                return false;
            }

            if (binding.Semantics is null)
            {
                diagnostic = $"Mesh '{mesh.Name}' pass {pass.Ordinal} triangle {triangleOrdinal} has no compiled effective material semantics.";
                return false;
            }

            int firstIndexOffset = checked(triangleOrdinal * 3);
            uint firstVertexIndex = mesh.Indices[firstIndexOffset];
            if (firstVertexIndex > int.MaxValue || binding.MaterialVertexIndex != (int)firstVertexIndex)
            {
                diagnostic = $"Mesh '{mesh.Name}' pass {pass.Ordinal} triangle {triangleOrdinal} material provenance does not match its first indexed vertex.";
                return false;
            }

            for (int corner = 0; corner < 3; corner++)
            {
                uint vertexIndex = mesh.Indices[firstIndexOffset + corner];
                if (vertexIndex >= (uint)positionCount)
                {
                    diagnostic = $"Mesh '{mesh.Name}' triangle {triangleOrdinal} references vertex {vertexIndex} outside the {positionCount}-vertex submission stream.";
                    return false;
                }
            }
        }

        if (triangleCount == 0)
        {
            plan = new EnbW3dIndexedSubmissionPlan(
                packet,
                vertexSource,
                EnbW3dIndexedPrimitiveTopology.TriangleList,
                ImmutableArray<EnbW3dIndexedSubmissionRun>.Empty);
            return true;
        }

        var runs = ImmutableArray.CreateBuilder<EnbW3dIndexedSubmissionRun>();
        int runStart = 0;
        EnbW3dRenderTriangleBinding runBinding = pass.TriangleBindings[0];
        for (int triangleOrdinal = 1; triangleOrdinal <= triangleCount; triangleOrdinal++)
        {
            EnbW3dRenderTriangleBinding? nextBinding = triangleOrdinal < triangleCount
                ? pass.TriangleBindings[triangleOrdinal]
                : null;
            bool endsRun = triangleOrdinal == triangleCount ||
                           !HasSameEffectiveBinding(packet, runBinding, nextBinding!.Value);
            if (!endsRun)
                continue;

            AppendRun(mesh, runStart, triangleOrdinal - runStart, CreateEffectiveBinding(runBinding), runs);
            if (triangleOrdinal < triangleCount)
            {
                runStart = triangleOrdinal;
                runBinding = nextBinding!.Value;
            }
        }

        plan = new EnbW3dIndexedSubmissionPlan(
            packet,
            vertexSource,
            EnbW3dIndexedPrimitiveTopology.TriangleList,
            runs.ToImmutable());
        return true;
    }

    private static void AppendRun(
        EnbW3dRenderMesh mesh,
        int firstTriangleOrdinal,
        int triangleCount,
        EnbW3dEffectiveSubmissionBinding binding,
        ImmutableArray<EnbW3dIndexedSubmissionRun>.Builder runs)
    {
        int startIndex = checked(firstTriangleOrdinal * 3);
        int indexCount = checked(triangleCount * 3);
        uint minVertexIndex = uint.MaxValue;
        uint maxVertexIndex = 0;
        for (int indexOffset = startIndex; indexOffset < startIndex + indexCount; indexOffset++)
        {
            uint vertexIndex = mesh.Indices[indexOffset];
            minVertexIndex = Math.Min(minVertexIndex, vertexIndex);
            maxVertexIndex = Math.Max(maxVertexIndex, vertexIndex);
        }

        uint vertexCount = checked(maxVertexIndex - minVertexIndex + 1u);
        runs.Add(new EnbW3dIndexedSubmissionRun(
            firstTriangleOrdinal,
            triangleCount,
            startIndex,
            indexCount,
            minVertexIndex,
            maxVertexIndex,
            vertexCount,
            binding));
    }

    private static EnbW3dEffectiveSubmissionBinding CreateEffectiveBinding(EnbW3dRenderTriangleBinding source)
        => new(source);

    private static bool HasSameEffectiveBinding(
        EnbW3dDrawTask packet,
        EnbW3dRenderTriangleBinding left,
        EnbW3dRenderTriangleBinding right)
    {
        if (packet.CurrentRetailMaterialMappers is { } owners)
        {
            EnbW3dSharedMaterialMappers? Resolve(EnbW3dRenderTriangleBinding binding)
                => binding.VertexMaterial is { } material && owners.TryGetValue(material.Material, out var pair)
                    ? pair : packet.CurrentRetailSharedMaterialMappers;
            var leftMappers = Resolve(left);
            var rightMappers = Resolve(right);
            if (!ReferenceEquals(leftMappers?.Stage0, rightMappers?.Stage0) ||
                !ReferenceEquals(leftMappers?.Stage1, rightMappers?.Stage1)) return false;
        }
        if (left.CurrentRetailNullMaterial != right.CurrentRetailNullMaterial ||
            left.UsesImplicitVertexMaterial != right.UsesImplicitVertexMaterial ||
            left.UsesImplicitShader != right.UsesImplicitShader ||
            !Nullable.Equals(left.VertexMaterial, right.VertexMaterial) ||
            !Nullable.Equals(left.Shader, right.Shader) ||
            left.Semantics != right.Semantics ||
            left.Textures.Count != right.Textures.Count)
        {
            return false;
        }

        for (int stage = 0; stage < left.Textures.Count; stage++)
        {
            if (!Equals(left.Textures[stage], right.Textures[stage]))
                return false;
        }

        return true;
    }
}

/// <summary>Immutable indexed topology, with optional bounded GPU-owner residency
/// across invisible frames. Changed material/mesh/generation recompiles.</summary>
public sealed class EnbW3dIndexedSubmissionCache
{
    private readonly Dictionary<(EnbW3dDrawOrderKey Order, string Instance), EnbW3dIndexedSubmissionPlan> _plans = [];
    private readonly HashSet<(EnbW3dDrawOrderKey Order, string Instance)> _used = [];
    private readonly Dictionary<(ulong Identity, ulong Generation), HashSet<(EnbW3dDrawOrderKey Order, string Instance)>> _ownerKeys = [];
    private readonly HashSet<(EnbW3dDrawOrderKey Order, string Instance)> _ephemeralKeys = [];
    private readonly List<(EnbW3dDrawOrderKey Order, string Instance)> _retire = [];
    private bool _indexedSweepEnabled;
    private bool _explicitResidencyEnabled;
    private readonly HashSet<(ulong Identity,ulong Generation)> _explicitResidents=[];
    private readonly HashSet<(ulong Identity,ulong Generation)> _nonResidentOwners=[];
    private bool HasOwnerIndex=>_indexedSweepEnabled||_explicitResidencyEnabled;
    public bool ExplicitResidencyEnabled
    {
        get=>_explicitResidencyEnabled;
        set {if(value==_explicitResidencyEnabled)return;_explicitResidencyEnabled=value;RebuildOwnerIndex();}
    }
    // Resource ownership changes call this once. Unchanged resident owners are
    // absent from EndFrame's candidate list, including while they are invisible.
    public void SetOwnerResident((ulong Identity,ulong Generation) owner,bool resident)
    {
        if(resident){_explicitResidents.Add(owner);_nonResidentOwners.Remove(owner);}
        else {_explicitResidents.Remove(owner);if(_ownerKeys.ContainsKey(owner))_nonResidentOwners.Add(owner);}
    }
    public void ClearResidentOwners()
    {
        foreach(var owner in _explicitResidents)if(_ownerKeys.ContainsKey(owner))_nonResidentOwners.Add(owner);
        _explicitResidents.Clear();
    }
    private void RebuildOwnerIndex()
    {
        _ownerKeys.Clear();_ephemeralKeys.Clear();_retire.Clear();_nonResidentOwners.Clear();
        if(HasOwnerIndex)foreach(var pair in _plans)IndexStoredPlan(pair.Key,pair.Value);
    }
    // Switching mechanics never changes the stored plans or their retention policy.
    public bool IndexedSweepEnabled
    {
        get => _indexedSweepEnabled;
        set
        {
            if (value == _indexedSweepEnabled) return;
            _indexedSweepEnabled = value;
            RebuildOwnerIndex();
        }
    }
    public int RetainedCount => _plans.Count;
    public int LastSweepCandidateCount { get; private set; }
    public int LastOwnerSweepCount {get;private set;}
    public int HitCount { get; private set; }
    public int MissCount { get; private set; }
    public void Clear() { _plans.Clear(); _used.Clear(); _ownerKeys.Clear(); _ephemeralKeys.Clear(); _retire.Clear(); _nonResidentOwners.Clear(); }
    public void BeginFrame() { _used.Clear(); HitCount = MissCount = 0; }
    public void EndFrame(IReadOnlySet<(ulong Identity, ulong Generation)>? residentOwners = null)
    {
        using var reuseSweep = EnbWorkReuseDiagnostics.Measure("index.sweep");
        int reuseBeforeCount = _plans.Count;
        if (EnbWorkReuseDiagnostics.Active)
        {
            using var inspection = EnbWorkReuseDiagnostics.Inspect();
            EnbWorkReuseDiagnostics.Add("index.retained", _plans.Count);
            EnbWorkReuseDiagnostics.Add("index.usedKeys", _used.Count);
            EnbWorkReuseDiagnostics.Add("index.unusedRetained", _plans.Keys.Count(k => !_used.Contains(k)));
        }
        LastSweepCandidateCount = 0;LastOwnerSweepCount=0;
        if (HasOwnerIndex)
        {
            _retire.Clear();
            foreach (var key in _ephemeralKeys)
            {
                LastSweepCandidateCount++;
                if (!_used.Contains(key)) _retire.Add(key);
            }
            if(_explicitResidencyEnabled)
            {
                foreach(var owner in _nonResidentOwners)
                {
                    LastOwnerSweepCount++;
                    foreach(var key in _ownerKeys[owner])
                    {LastSweepCandidateCount++;if(!_used.Contains(key))_retire.Add(key);}
                }
            }
            else foreach (var owner in _ownerKeys)
            {
                LastOwnerSweepCount++;
                if (residentOwners?.Contains(owner.Key) == true) continue;
                foreach (var key in owner.Value)
                {
                    LastSweepCandidateCount++;
                    if (!_used.Contains(key)) _retire.Add(key);
                }
            }
            foreach (var key in _retire)
            {
                UnindexStoredPlan(key, _plans[key]);
                _plans.Remove(key);
            }
            _retire.Clear();
        }
        else
        {
        LastSweepCandidateCount = _plans.Count;
        foreach (var key in _plans.Keys.Where(key => !_used.Contains(key) &&
                     (_plans[key].Packet.CurrentRetailEffectMapperStartMilliseconds.HasValue ||
                      residentOwners?.Contains((_plans[key].Packet.RenderIdentity, _plans[key].Packet.VisualGeneration)) != true)).ToArray())
            _plans.Remove(key);
        }
        EnbWorkReuseDiagnostics.Add("index.retired", reuseBeforeCount - _plans.Count);
    }

    private void IndexStoredPlan((EnbW3dDrawOrderKey Order, string Instance) key, EnbW3dIndexedSubmissionPlan plan)
    {
        if (plan.Packet.CurrentRetailEffectMapperStartMilliseconds.HasValue) { _ephemeralKeys.Add(key); return; }
        var owner = (plan.Packet.RenderIdentity, plan.Packet.VisualGeneration);
        if (!_ownerKeys.TryGetValue(owner, out var keys)) _ownerKeys.Add(owner, keys = []);
        keys.Add(key);
        if(!_explicitResidents.Contains(owner))_nonResidentOwners.Add(owner);
    }

    private void UnindexStoredPlan((EnbW3dDrawOrderKey Order, string Instance) key, EnbW3dIndexedSubmissionPlan plan)
    {
        if (plan.Packet.CurrentRetailEffectMapperStartMilliseconds.HasValue) { _ephemeralKeys.Remove(key); return; }
        var owner = (plan.Packet.RenderIdentity, plan.Packet.VisualGeneration);
        var keys = _ownerKeys[owner];
        keys.Remove(key);
        if (keys.Count == 0){_ownerKeys.Remove(owner);_nonResidentOwners.Remove(owner);}
    }
    public bool TryCompile(EnbW3dDrawTask packet, out EnbW3dIndexedSubmissionPlan plan, out string? diagnostic)
    {
        using var reuseValidation = EnbWorkReuseDiagnostics.Measure("index.lookupValidateCompile");
        if (EnbWorkReuseDiagnostics.Active)
        {
            using var inspection = EnbWorkReuseDiagnostics.Inspect();
            EnbWorkReuseDiagnostics.ObservePacket("index.input", packet);
            EnbWorkReuseDiagnostics.Add("index.viewPositionsValidated", packet.CurrentRetailViewPositions?.Count ?? 0);
        }
        var key = (packet.OrderKey, packet.RenderInstancePath);
        if (_plans.TryGetValue(key, out var cached) &&
            EnbW3dIndexedSubmissionCompiler.CanReuseTopology(cached, packet) &&
            ValidFrameStreams(packet))
        {
            HitCount++;
            EnbWorkReuseDiagnostics.Add("index.topologyHit");
            _used.Add(key);
            plan = cached with { Packet = packet, VertexSource = packet.DeformedGeometry is null && packet.Mesh.Positions is not EnbW3dFrameStream<Vector3>
                ? EnbW3dSubmissionVertexSource.RigidMesh : EnbW3dSubmissionVertexSource.CpuDeformed };
            diagnostic = null;
            return true;
        }
        MissCount++;
        if (EnbWorkReuseDiagnostics.Enabled) EnbWorkReuseDiagnostics.Add(_plans.ContainsKey(key) ? "index.invalidated" : "index.newKey");
        if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet, out plan, out diagnostic)) return false;
        if (HasOwnerIndex && _plans.TryGetValue(key, out var replaced)) UnindexStoredPlan(key, replaced);
        packet.RetainAfterDrain();
        _plans[key] = plan;
        if (HasOwnerIndex) IndexStoredPlan(key, plan);
        _used.Add(key);
        return true;
    }

    private static bool ValidFrameStreams(EnbW3dDrawTask packet)
    {
        if(packet.Mesh.Positions is EnbW3dFrameStream<Vector3> && packet.WorldTransform!=Matrix4x4.Identity)return false;
        if (packet.DeformedGeometry is {} skin &&
            (packet.WorldTransform != Matrix4x4.Identity || skin.Positions.Count != packet.Mesh.Positions.Count ||
             skin.Normals.Count != packet.Mesh.Normals.Count)) return false;
        if(packet.CurrentRetailViewPositions is EnbW3dFrameStream<Vector3> borrowed)
        {
            if(borrowed.Count!=packet.Mesh.Positions.Count)return false;
            foreach(var p in borrowed.ReadSpan)
                if(!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z))return false;
            return true;
        }
        return packet.CurrentRetailViewPositions is not {} view ||
            view.Count == packet.Mesh.Positions.Count &&
            view.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z));
    }
}
