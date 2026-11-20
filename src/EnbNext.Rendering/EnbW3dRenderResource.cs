using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Stable identity for one installed W3D resource. The MIX archive location is
/// the resource-cache authority; renderer nodes may reference the same compiled
/// resource regardless of which BaseAsset selector requested it.
/// </summary>
public readonly record struct EnbW3dRenderResourceKey(
    string ResourceName,
    string ArchivePath,
    long DataOffset,
    long DataLength)
{
    public static EnbW3dRenderResourceKey From(EnbMixResourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new EnbW3dRenderResourceKey(
            location.Name,
            location.ArchivePath,
            location.DataOffset,
            location.DataLength);
    }
}

/// <summary>
/// Backend-independent, immutable W3D resource compiled from EnbNext.Assets.
/// It contains buffer-ready mesh streams and authored render bindings, but no
/// GPU object, backend handle, Net7 state, or frame-local state.
/// </summary>
/// <summary>Render-side owner of one prepared W3D resource (formerly a record).
/// Value equality, ToString and constructor match the former record exactly;
/// `with` is replaced by Copy().SetX(). Derived render data is owned in fields
/// outside equality and is not carried by Copy.</summary>
public sealed class EnbW3dRenderResource(
    EnbW3dRenderResourceKey Key,
    string LogicalFileName,
    IReadOnlyList<EnbW3dRenderMesh> Meshes,
    IReadOnlyList<EnbW3dRenderHierarchy> Hierarchies,
    IReadOnlyList<EnbW3dRenderHierarchyAnimation> Animations,
    IReadOnlyList<EnbW3dRenderHierarchyModel> HierarchyModels,
    IReadOnlyList<EnbW3dRenderHlod> HierarchicalLods) : IEquatable<EnbW3dRenderResource>
{
    public EnbW3dRenderResourceKey Key {get;set;}=Key;
    public string LogicalFileName {get;set;}=LogicalFileName;
    public IReadOnlyList<EnbW3dRenderMesh> Meshes {get;set;}=Meshes;
    public IReadOnlyList<EnbW3dRenderHierarchy> Hierarchies {get;set;}=Hierarchies;
    public IReadOnlyList<EnbW3dRenderHierarchyAnimation> Animations {get;set;}=Animations;
    public IReadOnlyList<EnbW3dRenderHierarchyModel> HierarchyModels {get;set;}=HierarchyModels;
    public IReadOnlyList<EnbW3dRenderHlod> HierarchicalLods {get;set;}=HierarchicalLods;
    /// <summary>Current-retail render-object prototypes resolved outside this W3D through
    /// the global WW3D asset manager's lazy &lt;name&gt;.w3d path. These are semantic
    /// render objects, not fabricated meshes.</summary>
    public IReadOnlyList<EnbCurrentRetailW3dCollectionDefinition> Collections {get;set;}=[];
    public IReadOnlyList<EnbCurrentRetailW3dLegacyLodDefinition> LegacyLods {get;set;}=[];
    public IReadOnlyList<EnbCurrentRetailParticleEmitterDefinition> ParticleEmitters {get;set;}=[];
    public IReadOnlyList<EnbCurrentRetailSphereDefinition> Spheres {get;set;}=[];
    public IReadOnlyList<EnbCurrentRetailRingDefinition> Rings {get;set;}=[];
    public IReadOnlyList<EnbCurrentRetailSpecialMaterialDefinition> SpecialMaterials {get;set;}=[];
    public IReadOnlyList<EnbW3dRenderCompositeChild> CompositeRenderChildren {get;set;}=[];
    public IReadOnlyList<EnbW3dExternalRenderObject> ExternalRenderObjects {get;set;}=Array.Empty<EnbW3dExternalRenderObject>();

    public EnbW3dRenderResource Copy(){var copy=(EnbW3dRenderResource)MemberwiseClone();copy.ResetDerivedData();return copy;}
    internal EnbW3dRigidOccurrence.Eligibility? DerivedRigidEligibility;
    private void ResetDerivedData(){DerivedRigidEligibility=null;}
    public EnbW3dRenderResource SetKey(EnbW3dRenderResourceKey value) {Key=value;return this;}
    public EnbW3dRenderResource SetLogicalFileName(string value) {LogicalFileName=value;return this;}
    public EnbW3dRenderResource SetMeshes(IReadOnlyList<EnbW3dRenderMesh> value) {Meshes=value;return this;}
    public EnbW3dRenderResource SetHierarchies(IReadOnlyList<EnbW3dRenderHierarchy> value) {Hierarchies=value;return this;}
    public EnbW3dRenderResource SetAnimations(IReadOnlyList<EnbW3dRenderHierarchyAnimation> value) {Animations=value;return this;}
    public EnbW3dRenderResource SetHierarchyModels(IReadOnlyList<EnbW3dRenderHierarchyModel> value) {HierarchyModels=value;return this;}
    public EnbW3dRenderResource SetHierarchicalLods(IReadOnlyList<EnbW3dRenderHlod> value) {HierarchicalLods=value;return this;}
    public EnbW3dRenderResource SetCollections(IReadOnlyList<EnbCurrentRetailW3dCollectionDefinition> value) {Collections=value;return this;}
    public EnbW3dRenderResource SetLegacyLods(IReadOnlyList<EnbCurrentRetailW3dLegacyLodDefinition> value) {LegacyLods=value;return this;}
    public EnbW3dRenderResource SetParticleEmitters(IReadOnlyList<EnbCurrentRetailParticleEmitterDefinition> value) {ParticleEmitters=value;return this;}
    public EnbW3dRenderResource SetSpheres(IReadOnlyList<EnbCurrentRetailSphereDefinition> value) {Spheres=value;return this;}
    public EnbW3dRenderResource SetRings(IReadOnlyList<EnbCurrentRetailRingDefinition> value) {Rings=value;return this;}
    public EnbW3dRenderResource SetSpecialMaterials(IReadOnlyList<EnbCurrentRetailSpecialMaterialDefinition> value) {SpecialMaterials=value;return this;}
    public EnbW3dRenderResource SetCompositeRenderChildren(IReadOnlyList<EnbW3dRenderCompositeChild> value) {CompositeRenderChildren=value;return this;}
    public EnbW3dRenderResource SetExternalRenderObjects(IReadOnlyList<EnbW3dExternalRenderObject> value) {ExternalRenderObjects=value;return this;}

    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbW3dRenderResource { ");
        text.Append("Key = ").Append((object?)Key);
        text.Append(", LogicalFileName = ").Append((object?)LogicalFileName);
        text.Append(", Meshes = ").Append((object?)Meshes);
        text.Append(", Hierarchies = ").Append((object?)Hierarchies);
        text.Append(", Animations = ").Append((object?)Animations);
        text.Append(", HierarchyModels = ").Append((object?)HierarchyModels);
        text.Append(", HierarchicalLods = ").Append((object?)HierarchicalLods);
        text.Append(", Collections = ").Append((object?)Collections);
        text.Append(", LegacyLods = ").Append((object?)LegacyLods);
        text.Append(", ParticleEmitters = ").Append((object?)ParticleEmitters);
        text.Append(", Spheres = ").Append((object?)Spheres);
        text.Append(", Rings = ").Append((object?)Rings);
        text.Append(", SpecialMaterials = ").Append((object?)SpecialMaterials);
        text.Append(", CompositeRenderChildren = ").Append((object?)CompositeRenderChildren);
        text.Append(", ExternalRenderObjects = ").Append((object?)ExternalRenderObjects);
        return text.Append(" }").ToString();
    }
    public override bool Equals(object? obj)=>Equals(obj as EnbW3dRenderResource);
    public bool Equals(EnbW3dRenderResource? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<EnbW3dRenderResourceKey>.Default.Equals(Key,other.Key) &&
        EqualityComparer<string>.Default.Equals(LogicalFileName,other.LogicalFileName) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderMesh>>.Default.Equals(Meshes,other.Meshes) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderHierarchy>>.Default.Equals(Hierarchies,other.Hierarchies) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderHierarchyAnimation>>.Default.Equals(Animations,other.Animations) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderHierarchyModel>>.Default.Equals(HierarchyModels,other.HierarchyModels) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderHlod>>.Default.Equals(HierarchicalLods,other.HierarchicalLods) &&
        EqualityComparer<IReadOnlyList<EnbCurrentRetailW3dCollectionDefinition>>.Default.Equals(Collections,other.Collections) &&
        EqualityComparer<IReadOnlyList<EnbCurrentRetailW3dLegacyLodDefinition>>.Default.Equals(LegacyLods,other.LegacyLods) &&
        EqualityComparer<IReadOnlyList<EnbCurrentRetailParticleEmitterDefinition>>.Default.Equals(ParticleEmitters,other.ParticleEmitters) &&
        EqualityComparer<IReadOnlyList<EnbCurrentRetailSphereDefinition>>.Default.Equals(Spheres,other.Spheres) &&
        EqualityComparer<IReadOnlyList<EnbCurrentRetailRingDefinition>>.Default.Equals(Rings,other.Rings) &&
        EqualityComparer<IReadOnlyList<EnbCurrentRetailSpecialMaterialDefinition>>.Default.Equals(SpecialMaterials,other.SpecialMaterials) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderCompositeChild>>.Default.Equals(CompositeRenderChildren,other.CompositeRenderChildren) &&
        EqualityComparer<IReadOnlyList<EnbW3dExternalRenderObject>>.Default.Equals(ExternalRenderObjects,other.ExternalRenderObjects);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbW3dRenderResource));
            hash=hash*-1521134295+EqualityComparer<EnbW3dRenderResourceKey>.Default.GetHashCode(Key!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(LogicalFileName!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderMesh>>.Default.GetHashCode(Meshes!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderHierarchy>>.Default.GetHashCode(Hierarchies!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderHierarchyAnimation>>.Default.GetHashCode(Animations!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderHierarchyModel>>.Default.GetHashCode(HierarchyModels!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderHlod>>.Default.GetHashCode(HierarchicalLods!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbCurrentRetailW3dCollectionDefinition>>.Default.GetHashCode(Collections!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbCurrentRetailW3dLegacyLodDefinition>>.Default.GetHashCode(LegacyLods!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbCurrentRetailParticleEmitterDefinition>>.Default.GetHashCode(ParticleEmitters!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbCurrentRetailSphereDefinition>>.Default.GetHashCode(Spheres!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbCurrentRetailRingDefinition>>.Default.GetHashCode(Rings!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbCurrentRetailSpecialMaterialDefinition>>.Default.GetHashCode(SpecialMaterials!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderCompositeChild>>.Default.GetHashCode(CompositeRenderChildren!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dExternalRenderObject>>.Default.GetHashCode(ExternalRenderObjects!);
            return hash;
        }
    }
    public static bool operator ==(EnbW3dRenderResource? left,EnbW3dRenderResource? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbW3dRenderResource? left,EnbW3dRenderResource? right)=>!(left==right);
}

public sealed record EnbW3dRenderCompositeChild(string Name,int RetailClassId,
    EnbCurrentRetailCollectionBounds ConstructorBounds,int ConstructorIntegerCost)
{
    public EnbW3dRenderResource? HlodResource {get;init;}
    public EnbW3dRenderHlod? Hlod {get;init;}
    public bool HasConfiguredHierarchy {get;init;}
    public IReadOnlyList<EnbW3dRenderCompositeChild>? CollectionChildren {get;init;}
    public Vector3? MeshMinimum {get;init;}
    public Vector3? MeshMaximum {get;init;}
    public float? DazzleDefinitionRadius {get;init;}
    public float DazzleInstanceScale {get;init;} = 1;
}

public enum EnbW3dExternalRenderObjectKind : byte
{
    ParticleEmitter = 1,
    Dazzle = 2,
    Box = 3,
    NullObject = 4,
    SpecialMaterial = 5,
}

public sealed record EnbW3dExternalRenderObject(
    string Name,
    EnbW3dExternalRenderObjectKind Kind,
    string ResourceName,
    string ArchiveName)
{
    /// <summary>
    /// Strictly decoded 0x0500 state for the current-retail ParticleEmitter
    /// prototype. Null keeps structurally known but unsupported external
    /// render objects out of the particle runtime.
    /// </summary>
    public EnbCurrentRetailParticleEmitterDefinition? ParticleEmitterDefinition { get; init; }

    public EnbW3dRenderTextureLocation? ParticleTextureLocation { get; init; }
    public EnbResolvedTextureResource? ParticleRetainedTexture { get; init; }
    public bool HasReadyParticleTexture => ParticleEmitterDefinition?.TextureFileName.Length == 0 ||
        ParticleTextureLocation is not null || ParticleRetainedTexture?.Image is not null;
    public EnbDazzleDefinition? DazzleDefinition { get; init; }
    public string? DazzleTypeName { get; init; }
    //0096BAB0 stores instance+E8;0096BCE0 copies it independently of definition.
    public float DazzleInstanceScale { get; init; } = 1;
    public bool HasConfiguredDazzleScale { get; init; }
    public EnbW3dBox? BoxDefinition { get; init; }
    public EnbCurrentRetailSpecialMaterialDefinition? SpecialMaterialDefinition { get; init; }
}

/// <summary>
/// Backend-neutral N-Patch request carried by a W3D mesh. Current retail maps
/// authored MeshHeader3.Attributes bit 0x20000000 to its internal MeshModel
/// request flag 0x00010000; version >= 0x40001 geometry type 0x20000 sets it
/// as well (009BABB6). Retail combines that request with runtime device and
/// mesh eligibility. EnbNext preserves the authored request and leaves the
/// backend/runtime eligibility decision explicit.
/// </summary>
public readonly record struct EnbW3dNPatchSemantics(bool Requested)
{
    public const uint AuthoredRequestAttributeMask = 0x2000_0000u;
    public const uint CurrentRetailRuntimeRequestFlagMask = 0x0001_0000u;

    public static EnbW3dNPatchSemantics FromAuthoredAttributes(uint attributes, uint version = 0)
        => new((attributes & AuthoredRequestAttributeMask) != 0 ||
            (version >= 0x40001 && (attributes & 0xff0000) == 0x20000));
}

/// <summary>
/// Current-retail disposition of the effective mesh SortLevel. MeshHeader3
/// supplies the initial low byte at MeshModel+0x1C, but proves that current
/// retail may replace an authored zero with a shader-derived 10/15/20 fallback.
/// Level 0 renders in ordinary traversal. Levels 1..32 enter the global static-
/// sort buckets, which flush after ordinary traversal from 32 down to 1 while
/// preserving insertion order within each bucket. Other non-zero signed byte
/// values reach the current enqueue call but are rejected by its finite bucket
/// range; that quirk is surfaced instead of normalized silently.
/// </summary>
public enum EnbW3dStaticSortDisposition
{
    Immediate,
    DeferredBucket,
    RejectedByCurrentRetailBucketRange,
}

/// <summary>
/// Proven current-retail shader classes used only by the mesh sort-level
/// post-load classifier at FUN_0093CE00. Friendly blend names are deliberately
/// not substituted for the finite numeric classes.
/// </summary>
public enum EnbW3dRetailShaderSortClass : byte
{
    Class0 = 0,
    Class1 = 1,
    Class2 = 2,
    Class3 = 3,
    Class4 = 4,
}

public enum EnbW3dStaticSortLevelSource
{
    AuthoredLowByte,
    CurrentRetailGeometrySortFallback,
    CurrentRetailSingleShader,
}

/// <summary>
/// Current retail's internal MeshGeometryClass::SORT ownership for the modern
/// MeshHeader3 + Shaders + MaterialPass path decoded by EnbNext. The request is
/// loader-derived from explicit pass-zero shaders; it is not an authored mesh
/// attribute. Earth & Beyond disables WW3D's low-level SortingRenderer at
/// startup, while leaving the shader-driven static-sort fallback enabled.
/// </summary>
public readonly record struct EnbW3dGeometrySortSemantics(
    bool Requested,
    uint PassZeroShaderClassMask,
    uint AllPassShaderClassMask)
{
    public const bool CurrentRetailLowLevelSortingRendererEnabled = false;
    public const bool CurrentRetailAutoStaticSortFallbackEnabled = true;

    public bool UsesCurrentRetailLowLevelSortingRenderer =>
        Requested && CurrentRetailLowLevelSortingRendererEnabled;
}

public readonly record struct EnbW3dStaticSortSemantics(
    uint AuthoredValue,
    sbyte RuntimeLevel,
    EnbW3dStaticSortDisposition Disposition,
    EnbW3dStaticSortLevelSource RuntimeLevelSource)
{
    public const int MinimumDeferredLevel = 1;
    public const int MaximumDeferredLevel = 32;

    /// <summary>0093CE90 for procedural render objects. Unlike the mesh
    /// class-mask fallback, a single class1 shader renders immediately.</summary>
    public static EnbW3dStaticSortSemantics FromSingleShader(uint packedRetailState)
    {
        sbyte level=EnbW3dRetailSortCompiler.ClassifyPackedShader(packedRetailState) switch
        {
            EnbW3dRetailShaderSortClass.Class0 or EnbW3dRetailShaderSortClass.Class1 => 0,
            EnbW3dRetailShaderSortClass.Class2 => 10,
            EnbW3dRetailShaderSortClass.Class3 => 15,
            _ => 20,
        };
        return FromRuntimeLevel(0,level,EnbW3dStaticSortLevelSource.CurrentRetailSingleShader);
    }

    public static EnbW3dStaticSortSemantics FromAuthoredValue(uint authoredValue)
        => FromRuntimeLevel(
            authoredValue,
            unchecked((sbyte)(byte)authoredValue),
            EnbW3dStaticSortLevelSource.AuthoredLowByte);

    /// <summary>
    /// Reproduces FUN_009BCC40 after the modern material-pass loader has decided
    /// whether MeshGeometryClass::SORT is requested. A non-zero authored low
    /// byte always wins. With authored level zero, current Earth & Beyond keeps
    /// the auto-static-sort policy enabled and synthesizes 0/10/15/20 from the
    /// finite shader-class masks.
    /// </summary>
    public static EnbW3dStaticSortSemantics ResolveCurrentRetail(
        uint authoredValue,
        EnbW3dGeometrySortSemantics geometrySort)
    {
        EnbW3dStaticSortSemantics authored = FromAuthoredValue(authoredValue);
        if (authored.RuntimeLevel != 0 ||
            !geometrySort.Requested ||
            !EnbW3dGeometrySortSemantics.CurrentRetailAutoStaticSortFallbackEnabled)
        {
            return authored;
        }

        sbyte runtimeLevel = geometrySort.PassZeroShaderClassMask == 0x0000_0001u
            ? (sbyte)0
            : geometrySort.AllPassShaderClassMask switch
            {
                0x0000_0003u => (sbyte)0,
                0x0000_0004u => (sbyte)10,
                0x0000_0008u => (sbyte)15,
                _ => (sbyte)20,
            };

        return FromRuntimeLevel(
            authoredValue,
            runtimeLevel,
            EnbW3dStaticSortLevelSource.CurrentRetailGeometrySortFallback);
    }

    private static EnbW3dStaticSortSemantics FromRuntimeLevel(
        uint authoredValue,
        sbyte runtimeLevel,
        EnbW3dStaticSortLevelSource runtimeLevelSource)
    {
        EnbW3dStaticSortDisposition disposition = runtimeLevel switch
        {
            0 => EnbW3dStaticSortDisposition.Immediate,
            >= MinimumDeferredLevel and <= MaximumDeferredLevel => EnbW3dStaticSortDisposition.DeferredBucket,
            _ => EnbW3dStaticSortDisposition.RejectedByCurrentRetailBucketRange,
        };
        return new EnbW3dStaticSortSemantics(authoredValue, runtimeLevel, disposition, runtimeLevelSource);
    }

    public bool IsDeferred => Disposition == EnbW3dStaticSortDisposition.DeferredBucket;

    /// <summary>
    /// Zero-based post-traversal bucket order used by current retail: runtime
    /// level 32 flushes first (ordinal 0) and level 1 last (ordinal 31). A frame
    /// aggregator must use a stable ordering operation so FIFO insertion order
    /// within one bucket is retained.
    /// </summary>
    public int? DeferredFlushOrdinal => IsDeferred ? MaximumDeferredLevel - RuntimeLevel : null;
}

/// <summary>
/// Finite current-retail classifier and geometry-sort ownership reducer. This
/// mirrors FUN_0093CE00, FUN_009BD140 and the modern ShaderIds loader branch in
/// FUN_009BBF80 without enabling the low-level SortingRenderer that the current
/// Earth & Beyond startup explicitly disables.
/// </summary>
public static class EnbW3dRetailSortCompiler
{
    public static EnbW3dRetailShaderSortClass ClassifyPackedShader(uint packedRetailState)
    {
        if ((packedRetailState & 0x0004_0000u) == 0 &&
            (packedRetailState & 0x0000_00E0u) == 0)
        {
            return EnbW3dRetailShaderSortClass.Class0;
        }

        if ((packedRetailState & 0x0004_0000u) != 0)
        {
            if ((packedRetailState & 0x0000_00E0u) == 0 ||
                ((packedRetailState & 0x0000_C000u) == 0x0000_8000u &&
                 (packedRetailState & 0x0000_00E0u) == 0x0000_00A0u))
            {
                return EnbW3dRetailShaderSortClass.Class1;
            }
        }

        if ((packedRetailState & 0x0000_C000u) == 0x0000_4000u)
        {
            if ((packedRetailState & 0x0000_00E0u) == 0x0000_0020u)
                return EnbW3dRetailShaderSortClass.Class2;
            if ((packedRetailState & 0x0000_00E0u) == 0x0000_0060u)
                return EnbW3dRetailShaderSortClass.Class3;
        }

        return EnbW3dRetailShaderSortClass.Class4;
    }

    public static EnbW3dGeometrySortSemantics CompileCurrentRetail(
        uint authoredSortLevel,
        IReadOnlyList<EnbW3dRenderMaterialPass> passes,
        bool? parseTimeGeometrySortRequest = null)
    {
        ArgumentNullException.ThrowIfNull(passes);

        EnbW3dRenderMaterialPass? passZero = passes.FirstOrDefault(static pass => pass.Ordinal == 0);
        uint passZeroMask = passZero is null ? 0u : GetShaderClassMask(passZero);
        uint allPassMask = 0;
        foreach (EnbW3dRenderMaterialPass pass in passes)
            allPassMask |= GetShaderClassMask(pass);

        bool authoredRuntimeLevelIsZero = (byte)authoredSortLevel == 0;
        bool requested = parseTimeGeometrySortRequest ??
            (authoredRuntimeLevelIsZero &&
             passZero is not null &&
             passZero.Shaders.Any(static shader =>
                 !shader.FixedFunction.AlphaTestEnabled &&
                 shader.FixedFunction.DestinationBlend != EnbRenderBlendFactor.Zero));

        return new EnbW3dGeometrySortSemantics(requested, passZeroMask, allPassMask);
    }

    private static uint GetShaderClassMask(EnbW3dRenderMaterialPass pass)
    {
        if (pass.Shaders.Count == 0)
            return 1u << (int)ClassifyPackedShader(EnbW3dMaterialCompiler.RetailW3dShaderBaseline);

        uint mask = 0;
        foreach (EnbW3dRenderShaderBinding shader in pass.Shaders)
            mask |= 1u << (int)ClassifyPackedShader(shader.FixedFunction.PackedRetailState);
        return mask;
    }
}

/// <summary>
/// Proven current-retail provenance of the runtime vertex-normal stream. Retail
/// allocates and zero-initializes one normal for every MeshHeader3 vertex before
/// any child chunk is visited. Authored 0x0003/0x0004 children overwrite that
/// array; absence does not trigger geometric normal generation.
/// </summary>
public enum EnbW3dRenderNormalSource
{
    Unspecified = 0,
    AuthoredNormalChunk = 1,
    RetailZeroInitializedMissingChunk = 2,
}

internal static class EnbCurrentRetailBuiltinRenderObjectSemantics
{
    // Current retail's global WW3D asset manager special-cases the literal
    // render-object name "NULL" and returns its built-in base RenderObj factory
    // instead of performing the ordinary named-asset hash lookup. HLOD members
    // are instantiated through that same manager path.
    // Current client virtuals prove a local AABB centered at the origin with
    // 0.1 extents, a local sphere radius of 0.1, and integer render cost 0.
    // HLOD bounds use the child AABB, while HLOD budgeting consumes the integer
    // cost (or CurrentRetailLevelCost's 1e-6 float fallback where appropriate).
    public const string NullRenderObjectName = "NULL";
    public const float NullBoundsExtent = 0.1f;
    public const int NullIntegerRenderCost = 0;

    public static bool IsNullRenderObject(string renderObjectName)
    {
        ArgumentNullException.ThrowIfNull(renderObjectName);
        return string.Equals(
            renderObjectName,
            NullRenderObjectName,
            StringComparison.OrdinalIgnoreCase);
    }

    public static Vector3 NullBoundingBoxMinimum { get; } =
        new(-NullBoundsExtent, -NullBoundsExtent, -NullBoundsExtent);

    public static Vector3 NullBoundingBoxMaximum { get; } =
        new(NullBoundsExtent, NullBoundsExtent, NullBoundsExtent);
}

internal static class EnbCurrentRetailRenderObjectSemantics
{
    public static bool TryGetHlodBoundingBox(
        EnbW3dRenderResource resource,
        string renderObjectName,
        out Vector3 minimum,
        out Vector3 maximum)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(renderObjectName);

        var composite=resource.CompositeRenderChildren.FirstOrDefault(value=>string.Equals(value.Name,renderObjectName,StringComparison.OrdinalIgnoreCase));
        if(composite is not null)
        { minimum=composite.ConstructorBounds.BoxCenter-composite.ConstructorBounds.BoxExtent;maximum=composite.ConstructorBounds.BoxCenter+composite.ConstructorBounds.BoxExtent;return true; }
        if (EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(renderObjectName))
        {
            minimum = EnbCurrentRetailBuiltinRenderObjectSemantics.NullBoundingBoxMinimum;
            maximum = EnbCurrentRetailBuiltinRenderObjectSemantics.NullBoundingBoxMaximum;
            return true;
        }

        if (TryGetExternal(resource, renderObjectName, out EnbW3dExternalRenderObject external) &&
            external.Kind == EnbW3dExternalRenderObjectKind.NullObject)
        {
            minimum=EnbCurrentRetailBuiltinRenderObjectSemantics.NullBoundingBoxMinimum;
            maximum=EnbCurrentRetailBuiltinRenderObjectSemantics.NullBoundingBoxMaximum;
            return true;
        }

        if (TryGetExternal(resource, renderObjectName, out external) &&
            external.Kind == EnbW3dExternalRenderObjectKind.ParticleEmitter)
        {
            // Current ParticleEmitter vtable +0xF8/+0xFC returns all-zero local
            // sphere/AABB. It still exists as an attached RenderObj.
            minimum = Vector3.Zero;
            maximum = Vector3.Zero;
            return true;
        }

        if (TryGetExternal(resource, renderObjectName, out external) && external.BoxDefinition is {} box)
        {
            // Both box classes return authored local center/extents at +0xFC.
            minimum = box.Center - box.Extents;
            maximum = box.Center + box.Extents;
            return true;
        }
        if (TryGetExternal(resource, renderObjectName, out external) && external.SpecialMaterialDefinition is {} special)
        {minimum=special.Minimum;maximum=special.Maximum;return true;}
        minimum = default;
        maximum = default;
        if (TryGetExternal(resource, renderObjectName, out external) && external.DazzleDefinition is {} dazzle)
        {
            minimum = new Vector3(-(dazzle.Radius*external.DazzleInstanceScale));
            maximum = new Vector3(dazzle.Radius*external.DazzleInstanceScale);
            return true;
        }
        return false;
    }

    public static bool TryGetIntegerRenderCost(
        EnbW3dRenderResource resource,
        string renderObjectName,
        out int cost)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(renderObjectName);

        var composite=resource.CompositeRenderChildren.FirstOrDefault(value=>string.Equals(value.Name,renderObjectName,StringComparison.OrdinalIgnoreCase));
        if(composite is not null){cost=composite.ConstructorIntegerCost;return true;}
        if (EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(renderObjectName))
        {
            cost = EnbCurrentRetailBuiltinRenderObjectSemantics.NullIntegerRenderCost;
            return true;
        }

        if (TryGetExternal(resource, renderObjectName, out EnbW3dExternalRenderObject external) &&
            external.Kind is EnbW3dExternalRenderObjectKind.ParticleEmitter or EnbW3dExternalRenderObjectKind.Dazzle or EnbW3dExternalRenderObjectKind.NullObject)
        {
            // Current ParticleEmitter +0x20 returns zero and inherits +0x114
            // 00942040, which maps zero to the established 1e-6 float fallback.
            cost = 0;
            return true;
        }

        if (TryGetExternal(resource, renderObjectName, out external) && external.BoxDefinition is {} box)
        {
            cost = box.IntegerRenderCost;
            return true;
        }
        if (TryGetExternal(resource, renderObjectName, out external) && external.SpecialMaterialDefinition is {} special)
        {cost=special.Members.Aggregate(0,(sum,member)=>unchecked(sum+(int)member.TriangleCount));return true;}
        cost = 0;
        return false;
    }

    public static bool IsNoOpDrawRenderObject(EnbW3dRenderResource resource, string renderObjectName)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(renderObjectName);

        if (EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(renderObjectName))
            return true;

        return TryGetExternal(resource, renderObjectName, out EnbW3dExternalRenderObject external) &&
               external.Kind is EnbW3dExternalRenderObjectKind.ParticleEmitter or EnbW3dExternalRenderObjectKind.Dazzle or EnbW3dExternalRenderObjectKind.Box or EnbW3dExternalRenderObjectKind.NullObject or EnbW3dExternalRenderObjectKind.SpecialMaterial;
    }

    private static bool TryGetExternal(
        EnbW3dRenderResource resource,
        string renderObjectName,
        out EnbW3dExternalRenderObject external)
    {
        foreach (EnbW3dExternalRenderObject candidate in resource.ExternalRenderObjects)
        {
            if (!string.Equals(candidate.Name, renderObjectName, StringComparison.OrdinalIgnoreCase))
                continue;
            external = candidate;
            return true;
        }

        external = default!;
        return false;
    }
}

public static class EnbW3dRenderObjectNameSemantics
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<EnbW3dRenderMesh>,
        Dictionary<string, EnbW3dRenderMesh>> FirstMeshBindings = new();

    /// <summary>Resolve retained query topology once per immutable mesh table.
    /// Both short and registered names preserve the first loader-order match.</summary>
    public static EnbW3dRenderMesh? FindFirstMesh(IReadOnlyList<EnbW3dRenderMesh> meshes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (meshes.Count == 0) return null;
        if (meshes.Count == 1) return MatchesMesh(meshes[0], name) ? meshes[0] : null;
        var bindings = FirstMeshBindings.GetValue(meshes, static source =>
        {
            var result = new Dictionary<string, EnbW3dRenderMesh>(StringComparer.OrdinalIgnoreCase);
            foreach (var mesh in source)
            {
                result.TryAdd(mesh.Name, mesh);
                if (!string.IsNullOrEmpty(mesh.ContainerName)) result.TryAdd(RegisteredMeshName(mesh), mesh);
            }
            return result;
        });
        return bindings.GetValueOrDefault(name);
    }

    /// <summary>009BAA73..009BAB59 stores ContainerName + '.' + Name when
    /// the header container is nonempty, otherwise Name alone.0093ED30/
    ///0092A700 exposes that stored string to prototype registration.</summary>
    public static string RegisteredMeshName(EnbW3dRenderMesh mesh)
        => string.IsNullOrEmpty(mesh.ContainerName) ? mesh.Name : mesh.ContainerName + "." + mesh.Name;

    public static bool MatchesMesh(EnbW3dRenderMesh mesh, string renderObjectName)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(renderObjectName);

        if (string.Equals(mesh.Name, renderObjectName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrEmpty(mesh.ContainerName))
            return false;

        int separator = mesh.ContainerName.Length;
        return renderObjectName.Length == separator + 1 + mesh.Name.Length &&
               renderObjectName[separator] == '.' &&
               renderObjectName.AsSpan(0, separator).Equals(mesh.ContainerName.AsSpan(), StringComparison.OrdinalIgnoreCase) &&
               renderObjectName.AsSpan(separator + 1).Equals(mesh.Name.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class EnbW3dRenderMesh(
    int Ordinal,
    string Name,
    string ContainerName,
    float BoundingRadius,
    IReadOnlyList<Vector3> Positions,
    IReadOnlyList<Vector3> Normals,
    IReadOnlyList<ushort> BoneIndices,
    IReadOnlyList<uint> Indices,
    IReadOnlyList<EnbW3dRenderTriangleMetadata> TriangleMetadata,
    IReadOnlyList<EnbW3dRenderVertexMaterial> VertexMaterials,
    IReadOnlyList<EnbW3dRenderMaterialPass> MaterialPasses) : IEquatable<EnbW3dRenderMesh>
{
    // Actual render state. Structural copies are explicit; owner updates mutate in place.
    public int Ordinal {get;set;}=Ordinal;
    public string Name {get;set;}=Name;
    public string ContainerName {get;set;}=ContainerName;
    public float BoundingRadius {get;set;}=BoundingRadius;
    public IReadOnlyList<Vector3> Positions {get;set;}=Positions;
    public IReadOnlyList<Vector3> Normals {get;set;}=Normals;
    public IReadOnlyList<ushort> BoneIndices {get;set;}=BoneIndices;
    public IReadOnlyList<uint> Indices {get;set;}=Indices;
    public IReadOnlyList<EnbW3dRenderTriangleMetadata> TriangleMetadata {get;set;}=TriangleMetadata;
    public IReadOnlyList<EnbW3dRenderVertexMaterial> VertexMaterials {get;set;}=VertexMaterials;
    public IReadOnlyList<EnbW3dRenderMaterialPass> MaterialPasses {get;set;}=MaterialPasses;
    /// <summary>Actual loaded MeshClass geometry enters0093F390's retained
    /// mesh renderer. Generated line/point/terrain streams have separate
    /// submission paths and must not acquire mesh-container ordering.</summary>
    public bool CurrentRetailUsesMeshRenderer { get; set; }
    internal bool RetainsParticleFrame {get;set;}
    internal long TopologyRevision {get;set;}
    internal EnbW3dGeneratedTopology? GeneratedTopology { get; set; }
    //009BAD80 case30 retains every loaded modern texture, including entries
    // not selected by a pass. These are authored acquisition definitions;
    // shared runtime texture identity and mutable sampling belong to the owner.
    public IReadOnlyList<EnbW3dRenderTextureDefinition> ModernTextureDefinitions { get; set; }
        = Array.Empty<EnbW3dRenderTextureDefinition>();
    public IReadOnlyList<EnbW3dTextureAcquisition> TextureAcquisitions { get; set; }
        = Array.Empty<EnbW3dTextureAcquisition>();
    public IReadOnlyList<string> CurrentRetailLoadedTextureNames { get; set; } = Array.Empty<string>();
    public IReadOnlyList<EnbResolvedTextureResource> CurrentRetailLoadedTextureResources { get; set; } = Array.Empty<EnbResolvedTextureResource>();
    public IReadOnlyList<EnbCurrentRetailTextureObjectState> CurrentRetailLoadedTextureStates { get; set; } = Array.Empty<EnbCurrentRetailTextureObjectState>();
    public IReadOnlyList<EnbW3dRenderVertexMaterial> CurrentRetailLoadedMaterials { get; set; } = Array.Empty<EnbW3dRenderVertexMaterial>();
    public IReadOnlyList<EnbW3dLoadedMaterialRuntime> CurrentRetailLoadedMaterialRuntime {get;set;}=[];
    public IReadOnlyList<string> LegacyTextureNames { get; set; } = Array.Empty<string>();
    public uint Attributes { get; set; }
    public uint GeometryVersion { get; set; }
    public uint AuthoredSortLevel { get; set; }
    public Vector3 BoundingBoxMin { get; set; }
    public Vector3 BoundingBoxMax { get; set; }
    public Vector3 BoundingSphereCenter { get; set; }
    public EnbW3dRenderNormalSource NormalSource { get; set; }
    public uint? AuthoredNormalChunkType { get; set; }
    public IReadOnlyList<uint>? AuthoredVertexJoinMap { get; set; }
    public IReadOnlyList<byte>? AuthoredAabTreePayload { get; set; }
    public EnbCurrentRetailMeshGeometryState? CurrentRetailLoadedGeometry { get; set; }
    // Decoded GPU vertices remain immutable. This records the uniform factor
    // already applied to the retained native model geometry used by collision.
    public float CurrentRetailLoadedGeometryScale { get; set; } = 1;

    /// <summary>
    /// Active MeshModel material-descriptor pass count consumed by current
    /// retail MeshClass virtual +0x20. The descriptor constructor starts at 1;
    /// modern MaterialInfo 0x0028 overwrites it with the authored PassCount.
    /// The renderer uses this together with triangle count for the exact mesh render-cost
    /// contribution used by the shared HLOD allocator.
    /// </summary>
    public int CurrentRetailRenderPassCount { get; set; } = 1;

    /// <summary>Runtime draw-state override for baked terrain shadow buffers (474B30 state47).</summary>
    public uint CurrentRetailDepthBias { get; set; }

    /// <summary>
    /// Current retail increments every loaded per-vertex influence index after
    /// mesh child dispatch when MeshHeader3.Version is older than 0x30000.
    /// Assets preserves the authored u16 values; BoneIndices contains the
    /// effective retail-runtime indices consumed by deformation.
    /// </summary>
    public bool HasLegacySyntheticRootInfluenceShift { get; set; }

    public EnbW3dNPatchSemantics NPatch => EnbW3dNPatchSemantics.FromAuthoredAttributes(Attributes, GeometryVersion);
    /// <summary>
    /// When non-null, the asset decoder observed current retail's parse-time
    /// MeshModel geometry-SORT latch directly. Legacy root 0x0015 requires this
    /// because the latch is evaluated before later optional texture children can
    /// rewrite the final packed shader.
    /// </summary>
    public bool? CurrentRetailGeometrySortRequestOverride { get; set; }

    // Loaded mesh/material descriptors are immutable. Replacement produces a
    // new mesh (including record clones); frame pose and mapper clocks do not
    // alter classification. Keep this outside record equality and weakly owned.
    private sealed record RetainedSort(EnbW3dGeometrySortSemantics Value);
    public EnbW3dGeometrySortSemantics GeometrySort => CurrentRetailUsesMeshRenderer
        ? OwnerCache.Get(ref _retainedSort, this, static mesh => new RetainedSort(mesh.CompileGeometrySort())).Value
        : CompileGeometrySort();

    // Derived data owned by this mesh (formerly ConditionalWeakTables keyed by mesh
    // identity). Same identity and lifetime as before. Copy() starts with none of it,
    // exactly as a copied mesh used to be a new weak-table key.
    private RetainedSort? _retainedSort;
    internal EnbW3dNPatchSeam[]? DerivedNPatchSeams;
    internal EnbW3dSkinDeformer.SkinBindings? DerivedSkinBindings;
    internal EnbW3dRenderMesh? DerivedModelSource;
    internal EnbRetailInfluenceContainment? DerivedInfluenceClassifier;
    internal EnbCurrentRetailMeshVisibility.LocalBox? DerivedVisibilityLocalBox;
    internal EnbCameraCollisionGeometry? DerivedStationCameraGeometry;
    internal EnbCameraCollisionGeometry? DerivedStationShipProxyGeometry;
    internal EnbRetailDazzleMeshRay? DerivedUiModelRay;
    private void ResetDerivedData()
    {
        _retainedSort = null; DerivedNPatchSeams = null; DerivedSkinBindings = null;
        DerivedModelSource = null; DerivedInfluenceClassifier = null; DerivedVisibilityLocalBox = null;
        DerivedStationCameraGeometry = null; DerivedStationShipProxyGeometry = null; DerivedUiModelRay = null;
    }
    private EnbW3dGeometrySortSemantics CompileGeometrySort() =>
        EnbW3dRetailSortCompiler.CompileCurrentRetail(
            AuthoredSortLevel,
            MaterialPasses,
            CurrentRetailGeometrySortRequestOverride);
    public EnbW3dStaticSortSemantics StaticSort =>
        EnbW3dStaticSortSemantics.ResolveCurrentRetail(AuthoredSortLevel, GeometrySort);
    public IReadOnlyList<EnbRenderColor32> EffectiveColor1 { get; set; } = Array.Empty<EnbRenderColor32>();
    public IReadOnlyList<EnbW3dRenderTextureCoordinateSet> RetailTextureCoordinateSets { get; set; }
        = Array.Empty<EnbW3dRenderTextureCoordinateSet>();
    public EnbW3dRetailShaderOptimizationKind RetailPostLoadShaderOptimization { get; set; }
        = EnbW3dRetailShaderOptimizationKind.GeneralPerShader;
    public IReadOnlyList<EnbW3dRenderSecondaryMaterialDescriptorChunk> SecondaryMaterialDescriptorChunks { get; set; }
        = Array.Empty<EnbW3dRenderSecondaryMaterialDescriptorChunk>();

    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbW3dRenderMesh { ");
        text.Append("Ordinal = ").Append((object?)Ordinal);
        text.Append(", Name = ").Append((object?)Name);
        text.Append(", ContainerName = ").Append((object?)ContainerName);
        text.Append(", BoundingRadius = ").Append((object?)BoundingRadius);
        text.Append(", Positions = ").Append((object?)Positions);
        text.Append(", Normals = ").Append((object?)Normals);
        text.Append(", BoneIndices = ").Append((object?)BoneIndices);
        text.Append(", Indices = ").Append((object?)Indices);
        text.Append(", TriangleMetadata = ").Append((object?)TriangleMetadata);
        text.Append(", VertexMaterials = ").Append((object?)VertexMaterials);
        text.Append(", MaterialPasses = ").Append((object?)MaterialPasses);
        text.Append(", CurrentRetailUsesMeshRenderer = ").Append((object?)CurrentRetailUsesMeshRenderer);
        text.Append(", ModernTextureDefinitions = ").Append((object?)ModernTextureDefinitions);
        text.Append(", TextureAcquisitions = ").Append((object?)TextureAcquisitions);
        text.Append(", CurrentRetailLoadedTextureNames = ").Append((object?)CurrentRetailLoadedTextureNames);
        text.Append(", CurrentRetailLoadedTextureResources = ").Append((object?)CurrentRetailLoadedTextureResources);
        text.Append(", CurrentRetailLoadedTextureStates = ").Append((object?)CurrentRetailLoadedTextureStates);
        text.Append(", CurrentRetailLoadedMaterials = ").Append((object?)CurrentRetailLoadedMaterials);
        text.Append(", CurrentRetailLoadedMaterialRuntime = ").Append((object?)CurrentRetailLoadedMaterialRuntime);
        text.Append(", LegacyTextureNames = ").Append((object?)LegacyTextureNames);
        text.Append(", Attributes = ").Append((object?)Attributes);
        text.Append(", GeometryVersion = ").Append((object?)GeometryVersion);
        text.Append(", AuthoredSortLevel = ").Append((object?)AuthoredSortLevel);
        text.Append(", BoundingBoxMin = ").Append((object?)BoundingBoxMin);
        text.Append(", BoundingBoxMax = ").Append((object?)BoundingBoxMax);
        text.Append(", BoundingSphereCenter = ").Append((object?)BoundingSphereCenter);
        text.Append(", NormalSource = ").Append((object?)NormalSource);
        text.Append(", AuthoredNormalChunkType = ").Append((object?)AuthoredNormalChunkType);
        text.Append(", AuthoredVertexJoinMap = ").Append((object?)AuthoredVertexJoinMap);
        text.Append(", AuthoredAabTreePayload = ").Append((object?)AuthoredAabTreePayload);
        text.Append(", CurrentRetailLoadedGeometry = ").Append((object?)CurrentRetailLoadedGeometry);
        text.Append(", CurrentRetailLoadedGeometryScale = ").Append((object?)CurrentRetailLoadedGeometryScale);
        text.Append(", CurrentRetailRenderPassCount = ").Append((object?)CurrentRetailRenderPassCount);
        text.Append(", CurrentRetailDepthBias = ").Append((object?)CurrentRetailDepthBias);
        text.Append(", HasLegacySyntheticRootInfluenceShift = ").Append((object?)HasLegacySyntheticRootInfluenceShift);
        text.Append(", NPatch = ").Append((object?)NPatch);
        text.Append(", CurrentRetailGeometrySortRequestOverride = ").Append((object?)CurrentRetailGeometrySortRequestOverride);
        text.Append(", GeometrySort = ").Append((object?)GeometrySort);
        text.Append(", StaticSort = ").Append((object?)StaticSort);
        text.Append(", EffectiveColor1 = ").Append((object?)EffectiveColor1);
        text.Append(", RetailTextureCoordinateSets = ").Append((object?)RetailTextureCoordinateSets);
        text.Append(", RetailPostLoadShaderOptimization = ").Append((object?)RetailPostLoadShaderOptimization);
        text.Append(", SecondaryMaterialDescriptorChunks = ").Append((object?)SecondaryMaterialDescriptorChunks);
        return text.Append(" }").ToString();
    }
    public EnbW3dRenderMesh Copy(){var copy=(EnbW3dRenderMesh)MemberwiseClone();copy.ResetDerivedData();return copy;}
    public EnbW3dRenderMesh SetOrdinal(int value) {Ordinal=value;return this;}
    public EnbW3dRenderMesh SetName(string value) {Name=value;return this;}
    public EnbW3dRenderMesh SetContainerName(string value) {ContainerName=value;return this;}
    public EnbW3dRenderMesh SetBoundingRadius(float value) {BoundingRadius=value;return this;}
    public EnbW3dRenderMesh SetPositions(IReadOnlyList<Vector3> value) {Positions=value;return this;}
    public EnbW3dRenderMesh SetNormals(IReadOnlyList<Vector3> value) {Normals=value;return this;}
    public EnbW3dRenderMesh SetBoneIndices(IReadOnlyList<ushort> value) {BoneIndices=value;return this;}
    public EnbW3dRenderMesh SetIndices(IReadOnlyList<uint> value) {Indices=value;return this;}
    public EnbW3dRenderMesh SetTriangleMetadata(IReadOnlyList<EnbW3dRenderTriangleMetadata> value) {TriangleMetadata=value;return this;}
    public EnbW3dRenderMesh SetVertexMaterials(IReadOnlyList<EnbW3dRenderVertexMaterial> value) {VertexMaterials=value;return this;}
    public EnbW3dRenderMesh SetMaterialPasses(IReadOnlyList<EnbW3dRenderMaterialPass> value) {MaterialPasses=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailUsesMeshRenderer(bool value) {CurrentRetailUsesMeshRenderer=value;return this;}
    internal EnbW3dRenderMesh SetGeneratedTopology(EnbW3dGeneratedTopology? value) {GeneratedTopology=value;return this;}
    public EnbW3dRenderMesh SetModernTextureDefinitions(IReadOnlyList<EnbW3dRenderTextureDefinition> value) {ModernTextureDefinitions=value;return this;}
    public EnbW3dRenderMesh SetTextureAcquisitions(IReadOnlyList<EnbW3dTextureAcquisition> value) {TextureAcquisitions=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailLoadedTextureNames(IReadOnlyList<string> value) {CurrentRetailLoadedTextureNames=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailLoadedTextureResources(IReadOnlyList<EnbResolvedTextureResource> value) {CurrentRetailLoadedTextureResources=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailLoadedTextureStates(IReadOnlyList<EnbCurrentRetailTextureObjectState> value) {CurrentRetailLoadedTextureStates=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailLoadedMaterials(IReadOnlyList<EnbW3dRenderVertexMaterial> value) {CurrentRetailLoadedMaterials=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailLoadedMaterialRuntime(IReadOnlyList<EnbW3dLoadedMaterialRuntime> value) {CurrentRetailLoadedMaterialRuntime=value;return this;}
    public EnbW3dRenderMesh SetLegacyTextureNames(IReadOnlyList<string> value) {LegacyTextureNames=value;return this;}
    public EnbW3dRenderMesh SetAttributes(uint value) {Attributes=value;return this;}
    public EnbW3dRenderMesh SetGeometryVersion(uint value) {GeometryVersion=value;return this;}
    public EnbW3dRenderMesh SetAuthoredSortLevel(uint value) {AuthoredSortLevel=value;return this;}
    public EnbW3dRenderMesh SetBoundingBoxMin(Vector3 value) {BoundingBoxMin=value;return this;}
    public EnbW3dRenderMesh SetBoundingBoxMax(Vector3 value) {BoundingBoxMax=value;return this;}
    public EnbW3dRenderMesh SetBoundingSphereCenter(Vector3 value) {BoundingSphereCenter=value;return this;}
    public EnbW3dRenderMesh SetNormalSource(EnbW3dRenderNormalSource value) {NormalSource=value;return this;}
    public EnbW3dRenderMesh SetAuthoredNormalChunkType(uint? value) {AuthoredNormalChunkType=value;return this;}
    public EnbW3dRenderMesh SetAuthoredVertexJoinMap(IReadOnlyList<uint>? value) {AuthoredVertexJoinMap=value;return this;}
    public EnbW3dRenderMesh SetAuthoredAabTreePayload(IReadOnlyList<byte>? value) {AuthoredAabTreePayload=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailLoadedGeometry(EnbCurrentRetailMeshGeometryState? value) {CurrentRetailLoadedGeometry=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailLoadedGeometryScale(float value) {CurrentRetailLoadedGeometryScale=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailRenderPassCount(int value) {CurrentRetailRenderPassCount=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailDepthBias(uint value) {CurrentRetailDepthBias=value;return this;}
    public EnbW3dRenderMesh SetHasLegacySyntheticRootInfluenceShift(bool value) {HasLegacySyntheticRootInfluenceShift=value;return this;}
    public EnbW3dRenderMesh SetCurrentRetailGeometrySortRequestOverride(bool? value) {CurrentRetailGeometrySortRequestOverride=value;return this;}
    public EnbW3dRenderMesh SetEffectiveColor1(IReadOnlyList<EnbRenderColor32> value) {EffectiveColor1=value;return this;}
    public EnbW3dRenderMesh SetRetailTextureCoordinateSets(IReadOnlyList<EnbW3dRenderTextureCoordinateSet> value) {RetailTextureCoordinateSets=value;return this;}
    public EnbW3dRenderMesh SetRetailPostLoadShaderOptimization(EnbW3dRetailShaderOptimizationKind value) {RetailPostLoadShaderOptimization=value;return this;}
    public EnbW3dRenderMesh SetSecondaryMaterialDescriptorChunks(IReadOnlyList<EnbW3dRenderSecondaryMaterialDescriptorChunk> value) {SecondaryMaterialDescriptorChunks=value;return this;}
    public bool Equals(EnbW3dRenderMesh? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<int>.Default.Equals(Ordinal,other.Ordinal) &&
        EqualityComparer<string>.Default.Equals(Name,other.Name) &&
        EqualityComparer<string>.Default.Equals(ContainerName,other.ContainerName) &&
        EqualityComparer<float>.Default.Equals(BoundingRadius,other.BoundingRadius) &&
        EqualityComparer<IReadOnlyList<Vector3>>.Default.Equals(Positions,other.Positions) &&
        EqualityComparer<IReadOnlyList<Vector3>>.Default.Equals(Normals,other.Normals) &&
        EqualityComparer<IReadOnlyList<ushort>>.Default.Equals(BoneIndices,other.BoneIndices) &&
        EqualityComparer<IReadOnlyList<uint>>.Default.Equals(Indices,other.Indices) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderTriangleMetadata>>.Default.Equals(TriangleMetadata,other.TriangleMetadata) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderVertexMaterial>>.Default.Equals(VertexMaterials,other.VertexMaterials) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderMaterialPass>>.Default.Equals(MaterialPasses,other.MaterialPasses) &&
        EqualityComparer<bool>.Default.Equals(CurrentRetailUsesMeshRenderer,other.CurrentRetailUsesMeshRenderer) &&
        EqualityComparer<EnbW3dGeneratedTopology?>.Default.Equals(GeneratedTopology,other.GeneratedTopology) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderTextureDefinition>>.Default.Equals(ModernTextureDefinitions,other.ModernTextureDefinitions) &&
        EqualityComparer<IReadOnlyList<EnbW3dTextureAcquisition>>.Default.Equals(TextureAcquisitions,other.TextureAcquisitions) &&
        EqualityComparer<IReadOnlyList<string>>.Default.Equals(CurrentRetailLoadedTextureNames,other.CurrentRetailLoadedTextureNames) &&
        EqualityComparer<IReadOnlyList<EnbResolvedTextureResource>>.Default.Equals(CurrentRetailLoadedTextureResources,other.CurrentRetailLoadedTextureResources) &&
        EqualityComparer<IReadOnlyList<EnbCurrentRetailTextureObjectState>>.Default.Equals(CurrentRetailLoadedTextureStates,other.CurrentRetailLoadedTextureStates) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderVertexMaterial>>.Default.Equals(CurrentRetailLoadedMaterials,other.CurrentRetailLoadedMaterials) &&
        EqualityComparer<IReadOnlyList<EnbW3dLoadedMaterialRuntime>>.Default.Equals(CurrentRetailLoadedMaterialRuntime,other.CurrentRetailLoadedMaterialRuntime) &&
        EqualityComparer<IReadOnlyList<string>>.Default.Equals(LegacyTextureNames,other.LegacyTextureNames) &&
        EqualityComparer<uint>.Default.Equals(Attributes,other.Attributes) &&
        EqualityComparer<uint>.Default.Equals(GeometryVersion,other.GeometryVersion) &&
        EqualityComparer<uint>.Default.Equals(AuthoredSortLevel,other.AuthoredSortLevel) &&
        EqualityComparer<Vector3>.Default.Equals(BoundingBoxMin,other.BoundingBoxMin) &&
        EqualityComparer<Vector3>.Default.Equals(BoundingBoxMax,other.BoundingBoxMax) &&
        EqualityComparer<Vector3>.Default.Equals(BoundingSphereCenter,other.BoundingSphereCenter) &&
        EqualityComparer<EnbW3dRenderNormalSource>.Default.Equals(NormalSource,other.NormalSource) &&
        EqualityComparer<uint?>.Default.Equals(AuthoredNormalChunkType,other.AuthoredNormalChunkType) &&
        EqualityComparer<IReadOnlyList<uint>?>.Default.Equals(AuthoredVertexJoinMap,other.AuthoredVertexJoinMap) &&
        EqualityComparer<IReadOnlyList<byte>?>.Default.Equals(AuthoredAabTreePayload,other.AuthoredAabTreePayload) &&
        EqualityComparer<EnbCurrentRetailMeshGeometryState?>.Default.Equals(CurrentRetailLoadedGeometry,other.CurrentRetailLoadedGeometry) &&
        EqualityComparer<float>.Default.Equals(CurrentRetailLoadedGeometryScale,other.CurrentRetailLoadedGeometryScale) &&
        EqualityComparer<int>.Default.Equals(CurrentRetailRenderPassCount,other.CurrentRetailRenderPassCount) &&
        EqualityComparer<uint>.Default.Equals(CurrentRetailDepthBias,other.CurrentRetailDepthBias) &&
        EqualityComparer<bool>.Default.Equals(HasLegacySyntheticRootInfluenceShift,other.HasLegacySyntheticRootInfluenceShift) &&
        EqualityComparer<bool?>.Default.Equals(CurrentRetailGeometrySortRequestOverride,other.CurrentRetailGeometrySortRequestOverride) &&
        EqualityComparer<IReadOnlyList<EnbRenderColor32>>.Default.Equals(EffectiveColor1,other.EffectiveColor1) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderTextureCoordinateSet>>.Default.Equals(RetailTextureCoordinateSets,other.RetailTextureCoordinateSets) &&
        EqualityComparer<EnbW3dRetailShaderOptimizationKind>.Default.Equals(RetailPostLoadShaderOptimization,other.RetailPostLoadShaderOptimization) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderSecondaryMaterialDescriptorChunk>>.Default.Equals(SecondaryMaterialDescriptorChunks,other.SecondaryMaterialDescriptorChunks);
    public override bool Equals(object? other)=>other is EnbW3dRenderMesh owner&&Equals(owner);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbW3dRenderMesh));
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(Ordinal!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(Name!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(ContainerName!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(BoundingRadius!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<Vector3>>.Default.GetHashCode(Positions!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<Vector3>>.Default.GetHashCode(Normals!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<ushort>>.Default.GetHashCode(BoneIndices!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<uint>>.Default.GetHashCode(Indices!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderTriangleMetadata>>.Default.GetHashCode(TriangleMetadata!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderVertexMaterial>>.Default.GetHashCode(VertexMaterials!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderMaterialPass>>.Default.GetHashCode(MaterialPasses!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(CurrentRetailUsesMeshRenderer!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dGeneratedTopology?>.Default.GetHashCode(GeneratedTopology!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderTextureDefinition>>.Default.GetHashCode(ModernTextureDefinitions!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dTextureAcquisition>>.Default.GetHashCode(TextureAcquisitions!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<string>>.Default.GetHashCode(CurrentRetailLoadedTextureNames!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbResolvedTextureResource>>.Default.GetHashCode(CurrentRetailLoadedTextureResources!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbCurrentRetailTextureObjectState>>.Default.GetHashCode(CurrentRetailLoadedTextureStates!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderVertexMaterial>>.Default.GetHashCode(CurrentRetailLoadedMaterials!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dLoadedMaterialRuntime>>.Default.GetHashCode(CurrentRetailLoadedMaterialRuntime!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<string>>.Default.GetHashCode(LegacyTextureNames!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(Attributes!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(GeometryVersion!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(AuthoredSortLevel!);
            hash=hash*-1521134295+EqualityComparer<Vector3>.Default.GetHashCode(BoundingBoxMin!);
            hash=hash*-1521134295+EqualityComparer<Vector3>.Default.GetHashCode(BoundingBoxMax!);
            hash=hash*-1521134295+EqualityComparer<Vector3>.Default.GetHashCode(BoundingSphereCenter!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dRenderNormalSource>.Default.GetHashCode(NormalSource!);
            hash=hash*-1521134295+EqualityComparer<uint?>.Default.GetHashCode(AuthoredNormalChunkType!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<uint>?>.Default.GetHashCode(AuthoredVertexJoinMap!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<byte>?>.Default.GetHashCode(AuthoredAabTreePayload!);
            hash=hash*-1521134295+EqualityComparer<EnbCurrentRetailMeshGeometryState?>.Default.GetHashCode(CurrentRetailLoadedGeometry!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(CurrentRetailLoadedGeometryScale!);
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(CurrentRetailRenderPassCount!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(CurrentRetailDepthBias!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(HasLegacySyntheticRootInfluenceShift!);
            hash=hash*-1521134295+EqualityComparer<bool?>.Default.GetHashCode(CurrentRetailGeometrySortRequestOverride!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbRenderColor32>>.Default.GetHashCode(EffectiveColor1!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderTextureCoordinateSet>>.Default.GetHashCode(RetailTextureCoordinateSets!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dRetailShaderOptimizationKind>.Default.GetHashCode(RetailPostLoadShaderOptimization!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderSecondaryMaterialDescriptorChunk>>.Default.GetHashCode(SecondaryMaterialDescriptorChunks!);
            return hash;
        }
    }
    public static bool operator ==(EnbW3dRenderMesh? left,EnbW3dRenderMesh? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbW3dRenderMesh? left,EnbW3dRenderMesh? right)=>!(left==right);
    public void Deconstruct(out int Ordinal,out string Name,out string ContainerName,out float BoundingRadius,out IReadOnlyList<Vector3> Positions,out IReadOnlyList<Vector3> Normals,out IReadOnlyList<ushort> BoneIndices,out IReadOnlyList<uint> Indices,out IReadOnlyList<EnbW3dRenderTriangleMetadata> TriangleMetadata,out IReadOnlyList<EnbW3dRenderVertexMaterial> VertexMaterials,out IReadOnlyList<EnbW3dRenderMaterialPass> MaterialPasses)
    {
        Ordinal=this.Ordinal;
        Name=this.Name;
        ContainerName=this.ContainerName;
        BoundingRadius=this.BoundingRadius;
        Positions=this.Positions;
        Normals=this.Normals;
        BoneIndices=this.BoneIndices;
        Indices=this.Indices;
        TriangleMetadata=this.TriangleMetadata;
        VertexMaterials=this.VertexMaterials;
        MaterialPasses=this.MaterialPasses;
    }
}


/// <summary>
/// One of the at-most-eight shared UV arrays created by current retail
/// FUN_00947490. Arrays are deduplicated by CRC32 only, in first-seen pass/stage
/// order, matching the current descriptor implementation.
/// </summary>
public sealed record EnbW3dRenderTextureCoordinateSet(
    int Index,
    uint RetailCrc32,
    IReadOnlyList<Vector2> Coordinates);

public sealed record EnbW3dRenderSecondaryMaterialDescriptorChunk(
    int MaterialPassOrdinal,
    int? TextureStageOrdinal,
    uint ChunkType,
    IReadOnlyList<byte> Payload);

public readonly record struct EnbW3dRenderTriangleMetadata(
    uint Attributes,
    Vector3 Normal,
    float Distance);

public readonly record struct EnbRenderPackedRgb8(byte R, byte G, byte B, byte Reserved);

public readonly record struct EnbRenderColor32(byte R, byte G, byte B, byte A);

/// <summary>
/// Raw mapper selector encoded in the current W3D vertex-material Attributes
/// word. Current retail FUN_0092F2A0 recognizes 0x01..0x14 except 0x05. The
/// numeric names are deliberate until the corresponding runtime matrix/apply
/// behavior is closed from the current binary.
/// </summary>
public enum EnbW3dMapperTypeSelector : byte
{
    None = 0x00,
    Mode01 = 0x01,
    Mode02 = 0x02,
    Mode03 = 0x03,
    Mode04 = 0x04,
    Reserved05 = 0x05,
    Mode06 = 0x06,
    Mode07 = 0x07,
    Mode08 = 0x08,
    Mode09 = 0x09,
    Mode0A = 0x0A,
    Mode0B = 0x0B,
    Mode0C = 0x0C,
    Mode0D = 0x0D,
    Mode0E = 0x0E,
    Mode0F = 0x0F,
    Mode10 = 0x10,
    Mode11 = 0x11,
    Mode12 = 0x12,
    Mode13 = 0x13,
    Mode14 = 0x14,
}

public static class EnbW3dMapperTypeSelectorExtensions
{
    /// <summary>
    /// Mapper virtual +1C consumed by 00948620 during mesh-buffer admission.
    /// Current retail predicates return constants; absent/uninstantiated mapper
    /// selectors contribute no normal requirement (0092F2A0).
    /// </summary>
    public static bool RequiresCurrentRetailNormals(this EnbW3dMapperTypeSelector selector)
        => selector is EnbW3dMapperTypeSelector.Mode01 or EnbW3dMapperTypeSelector.Mode02
            or EnbW3dMapperTypeSelector.Mode0C or EnbW3dMapperTypeSelector.Mode0D
            or EnbW3dMapperTypeSelector.Mode0E or EnbW3dMapperTypeSelector.Mode0F
            or EnbW3dMapperTypeSelector.Mode11 or EnbW3dMapperTypeSelector.Mode13
            or EnbW3dMapperTypeSelector.Mode14;

    public static bool IsInstantiatedByCurrentRetail(this EnbW3dMapperTypeSelector selector)
        => selector switch
        {
            EnbW3dMapperTypeSelector.Mode01 or
            EnbW3dMapperTypeSelector.Mode02 or
            EnbW3dMapperTypeSelector.Mode03 or
            EnbW3dMapperTypeSelector.Mode04 or
            EnbW3dMapperTypeSelector.Mode06 or
            EnbW3dMapperTypeSelector.Mode07 or
            EnbW3dMapperTypeSelector.Mode08 or
            EnbW3dMapperTypeSelector.Mode09 or
            EnbW3dMapperTypeSelector.Mode0A or
            EnbW3dMapperTypeSelector.Mode0B or
            EnbW3dMapperTypeSelector.Mode0C or
            EnbW3dMapperTypeSelector.Mode0D or
            EnbW3dMapperTypeSelector.Mode0E or
            EnbW3dMapperTypeSelector.Mode0F or
            EnbW3dMapperTypeSelector.Mode10 or
            EnbW3dMapperTypeSelector.Mode11 or
            EnbW3dMapperTypeSelector.Mode12 or
            EnbW3dMapperTypeSelector.Mode13 or
            EnbW3dMapperTypeSelector.Mode14 => true,
            _ => false,
        };
}

public sealed record EnbW3dRenderMapper(
    EnbW3dMapperTypeSelector TypeSelector,
    string Raw,
    float UScale,
    float VScale,
    float UPerSecond,
    float VPerSecond,
    float UOffset,
    float VOffset,
    bool ClampFix);

public sealed record EnbW3dRenderVertexMaterial(
    int Ordinal,
    string Name,
    uint Attributes,
    EnbRenderPackedRgb8 Ambient,
    EnbRenderPackedRgb8 Diffuse,
    EnbRenderPackedRgb8 Specular,
    EnbRenderPackedRgb8 Emissive,
    float Power,
    float Opacity,
    float UnknownFloat,
    EnbW3dRenderMapper? Mapper0,
    EnbW3dRenderMapper? Mapper1)
{
    /// <summary>
    /// Exact already-materialized current-retail float state for legacy root
    /// 0x0015 materials. Modern 0x002D materials remain byte-authored and leave
    /// this null. The override prevents legacy RGB products from being
    /// quantized back into a fictitious modern packed-RGB record.
    /// </summary>
    public EnbW3dRenderMaterialUniformOverride? CurrentRetailUniformOverride { get; init; }

    public bool IsLegacyRootMaterial { get; init; }
    public EnbW3dRetainedMapperOwner? CurrentRetailMapper0Owner { get; init; }
    public EnbW3dRetainedMapperOwner? CurrentRetailMapper1Owner { get; init; }
}

public readonly record struct EnbW3dRenderMaterialUniformOverride(
    Vector3 Ambient,
    Vector3 Diffuse,
    Vector3 Specular,
    Vector3 Emissive,
    float Power,
    float DiffuseAlpha);

public enum EnbW3dRenderBindingSourceKind : byte
{
    ModernAuthored = 0,
    LegacyRoot = 1,
    PackedRetailState = 2,
}

/// <summary>
/// Exact 16-byte authored W3D shader record represented as four little-endian
/// words. This additionally compiles this record into backend-independent
/// fixed-function semantics while retaining the authored bytes for provenance.
/// </summary>
public readonly record struct EnbW3dRenderShaderRecord(
    uint Word0,
    uint Word1,
    uint Word2,
    uint Word3)
{
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 16)
            throw new ArgumentException("A W3D shader destination must provide at least 16 bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), Word0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), Word1);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), Word2);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), Word3);
    }
    internal static EnbW3dRenderShaderRecord From(EnbW3dShader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ReadOnlySpan<byte> bytes = shader.Raw.Span;
        if (bytes.Length != 16)
            throw new InvalidDataException("A W3D shader record must contain exactly 16 bytes.");

        return new EnbW3dRenderShaderRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4)));
    }
}

public readonly record struct EnbW3dMaterialRuntimeCoordinates(int Stage0, int Stage1);

public readonly record struct EnbW3dRenderVertexMaterialBinding(
    uint SourceId,
    EnbW3dRenderVertexMaterial Material)
{
    public EnbW3dRenderBindingSourceKind SourceKind { get; init; }
        = EnbW3dRenderBindingSourceKind.ModernAuthored;
    public EnbW3dMaterialRuntimeCoordinates? CurrentRetailRuntimeCoordinates { get; init; }
    // A copied expanded descriptor can contain null pointers (00952080).
    // Keep the array slot, while triangle consumers receive a nullable binding.
    public bool IsUnbound { get; init; }
}

public readonly record struct EnbW3dRenderShaderBinding(
    uint SourceId,
    EnbW3dRenderShaderRecord Shader,
    EnbW3dFixedFunctionState FixedFunction)
{
    public EnbW3dRenderBindingSourceKind SourceKind { get; init; }
        = EnbW3dRenderBindingSourceKind.ModernAuthored;
}

public sealed record EnbW3dRenderTextureLocation(
    string Name,
    string ArchivePath,
    long DataOffset,
    long DataLength)
{
    internal static EnbW3dRenderTextureLocation From(EnbMixResourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new EnbW3dRenderTextureLocation(
            location.Name,
            location.ArchivePath,
            location.DataOffset,
            location.DataLength);
    }
}

public enum EnbRenderTextureAddressMode : byte
{
    Wrap = 1,
    Clamp = 3,
}

/// <summary>
/// Authored texture-object state recovered from W3D 0x0033 and the current
/// retail texture apply path. RetailCreationSelector intentionally remains
/// numeric because current binary behavior is proven while friendly names for
/// selector values 0/2/3/4 are not required by the renderer contract.
/// </summary>
public readonly record struct EnbW3dTextureSamplingSemantics(
    bool AuthoredInfoPresent,
    uint AuthoredAttributes,
    uint AuthoredWord1,
    uint AuthoredWord2,
    byte RetailCreationSelector,
    EnbRenderTextureAddressMode AddressU,
    EnbRenderTextureAddressMode AddressV,
    bool LegacyCapabilityFormatRequested)
{
    /// <summary>
    /// Exact runtime texture-object override applied after creation. The retail
    /// NAMEDECAL path writes LINEAR to both texture +0x58 and +0x5C regardless
    /// of the global TextureFilter option.
    /// </summary>
    public EnbRenderTextureMinMagFilter? CurrentRetailMinMagOverride { get; init; }
    public EnbRenderTextureMipFilter? CurrentRetailMipOverride { get; init; }

    public static EnbW3dTextureSamplingSemantics RetailDefault =>
        new(false, 0, 0, 0, 0, EnbRenderTextureAddressMode.Wrap, EnbRenderTextureAddressMode.Wrap, false);
}

public static class EnbW3dTextureSemanticCompiler
{

    public static EnbW3dTextureSamplingSemantics Compile(EnbW3dTextureInfo? info)
    {
        if (info is not EnbW3dTextureInfo authored)
            return EnbW3dTextureSamplingSemantics.RetailDefault;

        uint attributes = authored.Attributes;
        byte creationSelector = (attributes & 0x0000_0004u) != 0
            ? (byte)1
            : (byte)((attributes & 0x0000_00C0u) switch
            {
                0x0000_0040u => 2,
                0x0000_0080u => 3,
                0x0000_00C0u => 4,
                _ => 0,
            });

        return new EnbW3dTextureSamplingSemantics(
            true,
            attributes,
            authored.Word1,
            authored.Word2,
            creationSelector,
            (attributes & 0x0000_0008u) != 0 ? EnbRenderTextureAddressMode.Clamp : EnbRenderTextureAddressMode.Wrap,
            (attributes & 0x0000_0010u) != 0 ? EnbRenderTextureAddressMode.Clamp : EnbRenderTextureAddressMode.Wrap,
            (attributes & 0x0000_1000u) != 0);
    }
}

public readonly record struct EnbW3dTextureCreationCapabilities(bool RendererInitialized,
    bool BumpEnvironmentSupported, bool Format17Supported, bool Format18Supported, bool Format19Supported);

public readonly record struct EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics Sampling, uint RetailFormat)
{
    /// <summary>Resolved TextureClass+0E constructor input. Ordinary acquisition
    /// supplies true; supported bump formats clear it in0094B455.</summary>
    public bool CurrentRetailCompressionAllowed { get; init; } = true;
    /// <summary>Resolved0091AD40 metadata supplied by the owning package manager.</summary>
    public EnbCurrentRetailTextureThumbnail? CurrentRetailThumbnail { get; init; }
    /// <summary>Explicit TextureClass+8 input used when copying an existing texture.</summary>
    public uint? CurrentRetailCreationMipLevels { get; init; }
}

public sealed record EnbW3dRenderTextureDefinition(uint SourceId, string LogicalName, EnbW3dTextureInfo? Info);

public sealed record EnbW3dRenderTextureBinding(
    uint SourceId,
    string LogicalName,
    EnbW3dRenderTextureLocation? Location)
{
    private EnbW3dTextureSamplingSemantics _sampling = EnbW3dTextureSamplingSemantics.RetailDefault;
    public EnbCurrentRetailTextureObjectState? CurrentRetailTextureState { get; init; }
    //0048CB40's retained returned texture can outlive a later publication
    //of the same formatted registry key. Its image must travel with the binding.
    public EnbResolvedTextureResource? CurrentRetailResolvedTexture { get; init; }
    public EnbW3dTextureSamplingSemantics Sampling
    {
        get => CurrentRetailTextureState is {} texture ? _sampling with
        {
            RetailCreationSelector = texture.Sampling.RetailCreationSelector,
            AddressU = texture.Sampling.AddressU,
            AddressV = texture.Sampling.AddressV,
            CurrentRetailMinMagOverride = texture.Sampling.CurrentRetailMinMagOverride,
            CurrentRetailMipOverride = texture.Sampling.CurrentRetailMipOverride,
        } : _sampling;
        init => _sampling = value;
    }
    public EnbW3dRenderBindingSourceKind SourceKind { get; init; }
        = EnbW3dRenderBindingSourceKind.ModernAuthored;

    /// <summary>
    /// True when current-retail runtime mutation installed the logical resource
    /// after the immutable W3D was decoded. Such bindings have no authored MIX
    /// location but are resolved through the live texture resolver by name.
    /// </summary>
    public bool ResolveLogicalNameAtRuntime { get; init; }

    /// <summary>
    /// Current retail accepts 0xFFFFFFFF only in the expanded per-triangle
    /// texture-ID stream, where it means that triangle has no texture binding
    /// for the stage. The one-ID uniform path does not special-case -1.
    /// </summary>
    public bool IsUnbound => SourceId == uint.MaxValue;

    public bool IsMissing => !IsUnbound && Location is null && !ResolveLogicalNameAtRuntime;
}

public enum EnbW3dRenderBindingCardinality
{
    None,
    Uniform,
    PerVertex,
    PerTriangle,
}

public sealed record EnbW3dLoadedMaterialRuntime(EnbW3dRenderVertexMaterial Material,
    EnbW3dMaterialRuntimeCoordinates Coordinates,EnbW3dVertexLightingSemantics Lighting);


public sealed class EnbW3dRenderTextureStage(
    int Ordinal,
    IReadOnlyList<EnbW3dRenderTextureBinding> Textures,
    IReadOnlyList<Vector2> RuntimeTexCoords,
    EnbW3dRenderBindingCardinality BindingCardinality) : IEquatable<EnbW3dRenderTextureStage>
{
    // Actual render state. Structural copies are explicit; owner updates mutate in place.
    public int Ordinal {get;set;}=Ordinal;
    public IReadOnlyList<EnbW3dRenderTextureBinding> Textures {get;set;}=Textures;
    public IReadOnlyList<Vector2> RuntimeTexCoords {get;set;}=RuntimeTexCoords;
    public EnbW3dRenderBindingCardinality BindingCardinality {get;set;}=BindingCardinality;
    /// <summary>
    /// Descriptor UV-array slot written by current retail FUN_00947490. -1 means
    /// no authored primary coordinate chunk was assigned (or the finite eight-slot
    /// pool was exhausted).
    /// </summary>
    public int RetailDescriptorCoordinateSetIndex { get; set; } = -1;

    // Retained +74 slot, independent of the effective expanded texture array.
    public EnbW3dRenderTextureBinding? CurrentRetailUniformTexture { get; set; }

    /// <summary>
    /// Base texture-coordinate-set index installed on the effective material.
    /// Without a mapper FUN_00930910 emits it directly as D3DTSS_TEXCOORDINDEX;
    /// with a mapper the same value is passed to the mapper's apply program.
    /// FUN_009475E0 normalizes descriptor -1 to 0 before the special two-pass
    /// handoff can copy pass-1 stage-0's value into pass 0.
    /// </summary>
    public int EffectiveTextureCoordinateSetIndex { get; set; }

    /// <summary>
    /// Coordinates selected by EffectiveTextureCoordinateSetIndex when that
    /// shared retail slot exists. This is the backend-neutral equivalent of the
    /// current material's D3DTSS_TEXCOORDINDEX state.
    /// </summary>
    public IReadOnlyList<Vector2> EffectiveRuntimeTexCoords { get; set; } = Array.Empty<Vector2>();

    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbW3dRenderTextureStage { ");
        text.Append("Ordinal = ").Append((object?)Ordinal);
        text.Append(", Textures = ").Append((object?)Textures);
        text.Append(", RuntimeTexCoords = ").Append((object?)RuntimeTexCoords);
        text.Append(", BindingCardinality = ").Append((object?)BindingCardinality);
        text.Append(", RetailDescriptorCoordinateSetIndex = ").Append((object?)RetailDescriptorCoordinateSetIndex);
        text.Append(", CurrentRetailUniformTexture = ").Append((object?)CurrentRetailUniformTexture);
        text.Append(", EffectiveTextureCoordinateSetIndex = ").Append((object?)EffectiveTextureCoordinateSetIndex);
        text.Append(", EffectiveRuntimeTexCoords = ").Append((object?)EffectiveRuntimeTexCoords);
        return text.Append(" }").ToString();
    }
    public EnbW3dRenderTextureStage Copy()=>(EnbW3dRenderTextureStage)MemberwiseClone();
    public EnbW3dRenderTextureStage SetOrdinal(int value) {Ordinal=value;return this;}
    public EnbW3dRenderTextureStage SetTextures(IReadOnlyList<EnbW3dRenderTextureBinding> value) {Textures=value;return this;}
    public EnbW3dRenderTextureStage SetRuntimeTexCoords(IReadOnlyList<Vector2> value) {RuntimeTexCoords=value;return this;}
    public EnbW3dRenderTextureStage SetBindingCardinality(EnbW3dRenderBindingCardinality value) {BindingCardinality=value;return this;}
    public EnbW3dRenderTextureStage SetRetailDescriptorCoordinateSetIndex(int value) {RetailDescriptorCoordinateSetIndex=value;return this;}
    public EnbW3dRenderTextureStage SetCurrentRetailUniformTexture(EnbW3dRenderTextureBinding? value) {CurrentRetailUniformTexture=value;return this;}
    public EnbW3dRenderTextureStage SetEffectiveTextureCoordinateSetIndex(int value) {EffectiveTextureCoordinateSetIndex=value;return this;}
    public EnbW3dRenderTextureStage SetEffectiveRuntimeTexCoords(IReadOnlyList<Vector2> value) {EffectiveRuntimeTexCoords=value;return this;}
    public bool Equals(EnbW3dRenderTextureStage? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<int>.Default.Equals(Ordinal,other.Ordinal) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderTextureBinding>>.Default.Equals(Textures,other.Textures) &&
        EqualityComparer<IReadOnlyList<Vector2>>.Default.Equals(RuntimeTexCoords,other.RuntimeTexCoords) &&
        EqualityComparer<EnbW3dRenderBindingCardinality>.Default.Equals(BindingCardinality,other.BindingCardinality) &&
        EqualityComparer<int>.Default.Equals(RetailDescriptorCoordinateSetIndex,other.RetailDescriptorCoordinateSetIndex) &&
        EqualityComparer<EnbW3dRenderTextureBinding?>.Default.Equals(CurrentRetailUniformTexture,other.CurrentRetailUniformTexture) &&
        EqualityComparer<int>.Default.Equals(EffectiveTextureCoordinateSetIndex,other.EffectiveTextureCoordinateSetIndex) &&
        EqualityComparer<IReadOnlyList<Vector2>>.Default.Equals(EffectiveRuntimeTexCoords,other.EffectiveRuntimeTexCoords);
    public override bool Equals(object? other)=>other is EnbW3dRenderTextureStage owner&&Equals(owner);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbW3dRenderTextureStage));
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(Ordinal!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderTextureBinding>>.Default.GetHashCode(Textures!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<Vector2>>.Default.GetHashCode(RuntimeTexCoords!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dRenderBindingCardinality>.Default.GetHashCode(BindingCardinality!);
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(RetailDescriptorCoordinateSetIndex!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dRenderTextureBinding?>.Default.GetHashCode(CurrentRetailUniformTexture!);
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(EffectiveTextureCoordinateSetIndex!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<Vector2>>.Default.GetHashCode(EffectiveRuntimeTexCoords!);
            return hash;
        }
    }
    public static bool operator ==(EnbW3dRenderTextureStage? left,EnbW3dRenderTextureStage? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbW3dRenderTextureStage? left,EnbW3dRenderTextureStage? right)=>!(left==right);
    public void Deconstruct(out int Ordinal,out IReadOnlyList<EnbW3dRenderTextureBinding> Textures,out IReadOnlyList<Vector2> RuntimeTexCoords,out EnbW3dRenderBindingCardinality BindingCardinality)
    {
        Ordinal=this.Ordinal;
        Textures=this.Textures;
        RuntimeTexCoords=this.RuntimeTexCoords;
        BindingCardinality=this.BindingCardinality;
    }
}


/// <summary>
/// Material pass with authored tables and compact effective triangle bindings.
/// </summary>
public sealed class EnbW3dRenderMaterialPass(
    int Ordinal,
    IReadOnlyList<EnbW3dRenderVertexMaterialBinding> VertexMaterials,
    EnbW3dRenderBindingCardinality VertexMaterialCardinality,
    IReadOnlyList<EnbW3dRenderShaderBinding> Shaders,
    EnbW3dRenderBindingCardinality ShaderCardinality,
    IReadOnlyList<EnbRenderColor32> DiffuseColors,
    IReadOnlyList<EnbRenderColor32> DiffuseIllumination,
    IReadOnlyList<EnbRenderColor32> SpecularColors,
    IReadOnlyList<EnbW3dRenderTextureStage> TextureStages,
    IReadOnlyList<EnbW3dRenderTriangleBinding> TriangleBindings,
    EnbW3dCompiledMaterialSemantics? SingleBindingSemantics) : IEquatable<EnbW3dRenderMaterialPass>
{
    // Actual render state. Structural copies are explicit; owner updates mutate in place.
    public int Ordinal {get;set;}=Ordinal;
    public IReadOnlyList<EnbW3dRenderVertexMaterialBinding> VertexMaterials {get;set;}=VertexMaterials;
    public EnbW3dRenderBindingCardinality VertexMaterialCardinality {get;set;}=VertexMaterialCardinality;
    public IReadOnlyList<EnbW3dRenderShaderBinding> Shaders {get;set;}=Shaders;
    public EnbW3dRenderBindingCardinality ShaderCardinality {get;set;}=ShaderCardinality;
    public IReadOnlyList<EnbRenderColor32> DiffuseColors {get;set;}=DiffuseColors;
    public IReadOnlyList<EnbRenderColor32> DiffuseIllumination {get;set;}=DiffuseIllumination;
    public IReadOnlyList<EnbRenderColor32> SpecularColors {get;set;}=SpecularColors;
    public IReadOnlyList<EnbW3dRenderTextureStage> TextureStages {get;set;}=TextureStages;
    public IReadOnlyList<EnbW3dRenderTriangleBinding> TriangleBindings {get;set;}=TriangleBindings;
    public EnbW3dCompiledMaterialSemantics? SingleBindingSemantics {get;set;}=SingleBindingSemantics;
    // Swatch manager 0075BE00 copies uniform slots even when expanded bindings
    // override them for the source mesh's own draw.
    public EnbW3dRenderVertexMaterialBinding? CurrentRetailUniformMaterial { get; set; }
    public EnbW3dVertexLightingSemantics? CurrentRetailUniformLighting { get; set; }
    public uint CurrentRetailUniformShader { get; set; } = EnbW3dMaterialCompiler.RetailW3dShaderBaseline;

    public IReadOnlyList<EnbW3dMaterialPassColorChunkKind> ColorChunkOrder { get; set; }
        = Array.Empty<EnbW3dMaterialPassColorChunkKind>();

    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbW3dRenderMaterialPass { ");
        text.Append("Ordinal = ").Append((object?)Ordinal);
        text.Append(", VertexMaterials = ").Append((object?)VertexMaterials);
        text.Append(", VertexMaterialCardinality = ").Append((object?)VertexMaterialCardinality);
        text.Append(", Shaders = ").Append((object?)Shaders);
        text.Append(", ShaderCardinality = ").Append((object?)ShaderCardinality);
        text.Append(", DiffuseColors = ").Append((object?)DiffuseColors);
        text.Append(", DiffuseIllumination = ").Append((object?)DiffuseIllumination);
        text.Append(", SpecularColors = ").Append((object?)SpecularColors);
        text.Append(", TextureStages = ").Append((object?)TextureStages);
        text.Append(", TriangleBindings = ").Append((object?)TriangleBindings);
        text.Append(", SingleBindingSemantics = ").Append((object?)SingleBindingSemantics);
        text.Append(", CurrentRetailUniformMaterial = ").Append((object?)CurrentRetailUniformMaterial);
        text.Append(", CurrentRetailUniformLighting = ").Append((object?)CurrentRetailUniformLighting);
        text.Append(", CurrentRetailUniformShader = ").Append((object?)CurrentRetailUniformShader);
        text.Append(", ColorChunkOrder = ").Append((object?)ColorChunkOrder);
        return text.Append(" }").ToString();
    }
    // Derived data owned by this pass (formerly ConditionalWeakTables keyed by pass
    // identity). Copy() starts with none of it, as a copy was a new weak-table key.
    internal EnbW3dDrawMaterialBinding? DerivedDrawMaterial;
    internal EnbW3dDrawBlendClassBox? DerivedBlendClass;
    public EnbW3dRenderMaterialPass Copy(){var copy=(EnbW3dRenderMaterialPass)MemberwiseClone();copy.DerivedDrawMaterial=null;copy.DerivedBlendClass=null;return copy;}
    public EnbW3dRenderMaterialPass SetOrdinal(int value) {Ordinal=value;return this;}
    public EnbW3dRenderMaterialPass SetVertexMaterials(IReadOnlyList<EnbW3dRenderVertexMaterialBinding> value) {VertexMaterials=value;return this;}
    public EnbW3dRenderMaterialPass SetVertexMaterialCardinality(EnbW3dRenderBindingCardinality value) {VertexMaterialCardinality=value;return this;}
    public EnbW3dRenderMaterialPass SetShaders(IReadOnlyList<EnbW3dRenderShaderBinding> value) {Shaders=value;return this;}
    public EnbW3dRenderMaterialPass SetShaderCardinality(EnbW3dRenderBindingCardinality value) {ShaderCardinality=value;return this;}
    public EnbW3dRenderMaterialPass SetDiffuseColors(IReadOnlyList<EnbRenderColor32> value) {DiffuseColors=value;return this;}
    public EnbW3dRenderMaterialPass SetDiffuseIllumination(IReadOnlyList<EnbRenderColor32> value) {DiffuseIllumination=value;return this;}
    public EnbW3dRenderMaterialPass SetSpecularColors(IReadOnlyList<EnbRenderColor32> value) {SpecularColors=value;return this;}
    public EnbW3dRenderMaterialPass SetTextureStages(IReadOnlyList<EnbW3dRenderTextureStage> value) {TextureStages=value;return this;}
    public EnbW3dRenderMaterialPass SetTriangleBindings(IReadOnlyList<EnbW3dRenderTriangleBinding> value) {TriangleBindings=value;return this;}
    public EnbW3dRenderMaterialPass SetSingleBindingSemantics(EnbW3dCompiledMaterialSemantics? value) {SingleBindingSemantics=value;return this;}
    public EnbW3dRenderMaterialPass SetCurrentRetailUniformMaterial(EnbW3dRenderVertexMaterialBinding? value) {CurrentRetailUniformMaterial=value;return this;}
    public EnbW3dRenderMaterialPass SetCurrentRetailUniformLighting(EnbW3dVertexLightingSemantics? value) {CurrentRetailUniformLighting=value;return this;}
    public EnbW3dRenderMaterialPass SetCurrentRetailUniformShader(uint value) {CurrentRetailUniformShader=value;return this;}
    public EnbW3dRenderMaterialPass SetColorChunkOrder(IReadOnlyList<EnbW3dMaterialPassColorChunkKind> value) {ColorChunkOrder=value;return this;}
    public bool Equals(EnbW3dRenderMaterialPass? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<int>.Default.Equals(Ordinal,other.Ordinal) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderVertexMaterialBinding>>.Default.Equals(VertexMaterials,other.VertexMaterials) &&
        EqualityComparer<EnbW3dRenderBindingCardinality>.Default.Equals(VertexMaterialCardinality,other.VertexMaterialCardinality) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderShaderBinding>>.Default.Equals(Shaders,other.Shaders) &&
        EqualityComparer<EnbW3dRenderBindingCardinality>.Default.Equals(ShaderCardinality,other.ShaderCardinality) &&
        EqualityComparer<IReadOnlyList<EnbRenderColor32>>.Default.Equals(DiffuseColors,other.DiffuseColors) &&
        EqualityComparer<IReadOnlyList<EnbRenderColor32>>.Default.Equals(DiffuseIllumination,other.DiffuseIllumination) &&
        EqualityComparer<IReadOnlyList<EnbRenderColor32>>.Default.Equals(SpecularColors,other.SpecularColors) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderTextureStage>>.Default.Equals(TextureStages,other.TextureStages) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderTriangleBinding>>.Default.Equals(TriangleBindings,other.TriangleBindings) &&
        EqualityComparer<EnbW3dCompiledMaterialSemantics?>.Default.Equals(SingleBindingSemantics,other.SingleBindingSemantics) &&
        EqualityComparer<EnbW3dRenderVertexMaterialBinding?>.Default.Equals(CurrentRetailUniformMaterial,other.CurrentRetailUniformMaterial) &&
        EqualityComparer<EnbW3dVertexLightingSemantics?>.Default.Equals(CurrentRetailUniformLighting,other.CurrentRetailUniformLighting) &&
        EqualityComparer<uint>.Default.Equals(CurrentRetailUniformShader,other.CurrentRetailUniformShader) &&
        EqualityComparer<IReadOnlyList<EnbW3dMaterialPassColorChunkKind>>.Default.Equals(ColorChunkOrder,other.ColorChunkOrder);
    public override bool Equals(object? other)=>other is EnbW3dRenderMaterialPass owner&&Equals(owner);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbW3dRenderMaterialPass));
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(Ordinal!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderVertexMaterialBinding>>.Default.GetHashCode(VertexMaterials!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dRenderBindingCardinality>.Default.GetHashCode(VertexMaterialCardinality!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderShaderBinding>>.Default.GetHashCode(Shaders!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dRenderBindingCardinality>.Default.GetHashCode(ShaderCardinality!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbRenderColor32>>.Default.GetHashCode(DiffuseColors!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbRenderColor32>>.Default.GetHashCode(DiffuseIllumination!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbRenderColor32>>.Default.GetHashCode(SpecularColors!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderTextureStage>>.Default.GetHashCode(TextureStages!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderTriangleBinding>>.Default.GetHashCode(TriangleBindings!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dCompiledMaterialSemantics?>.Default.GetHashCode(SingleBindingSemantics!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dRenderVertexMaterialBinding?>.Default.GetHashCode(CurrentRetailUniformMaterial!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dVertexLightingSemantics?>.Default.GetHashCode(CurrentRetailUniformLighting!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(CurrentRetailUniformShader!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dMaterialPassColorChunkKind>>.Default.GetHashCode(ColorChunkOrder!);
            return hash;
        }
    }
    public static bool operator ==(EnbW3dRenderMaterialPass? left,EnbW3dRenderMaterialPass? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbW3dRenderMaterialPass? left,EnbW3dRenderMaterialPass? right)=>!(left==right);
    public void Deconstruct(out int Ordinal,out IReadOnlyList<EnbW3dRenderVertexMaterialBinding> VertexMaterials,out EnbW3dRenderBindingCardinality VertexMaterialCardinality,out IReadOnlyList<EnbW3dRenderShaderBinding> Shaders,out EnbW3dRenderBindingCardinality ShaderCardinality,out IReadOnlyList<EnbRenderColor32> DiffuseColors,out IReadOnlyList<EnbRenderColor32> DiffuseIllumination,out IReadOnlyList<EnbRenderColor32> SpecularColors,out IReadOnlyList<EnbW3dRenderTextureStage> TextureStages,out IReadOnlyList<EnbW3dRenderTriangleBinding> TriangleBindings,out EnbW3dCompiledMaterialSemantics? SingleBindingSemantics)
    {
        Ordinal=this.Ordinal;
        VertexMaterials=this.VertexMaterials;
        VertexMaterialCardinality=this.VertexMaterialCardinality;
        Shaders=this.Shaders;
        ShaderCardinality=this.ShaderCardinality;
        DiffuseColors=this.DiffuseColors;
        DiffuseIllumination=this.DiffuseIllumination;
        SpecularColors=this.SpecularColors;
        TextureStages=this.TextureStages;
        TriangleBindings=this.TriangleBindings;
        SingleBindingSemantics=this.SingleBindingSemantics;
    }
}



public sealed class EnbW3dRenderHierarchy(
    uint Version,
    string Name,
    Vector3 Center,
    bool HasSyntheticRetailRoot,
    IReadOnlyList<EnbW3dRenderPivot> Pivots,
    IReadOnlyList<EnbW3dRenderPivotFixup> PivotFixups) : IEquatable<EnbW3dRenderHierarchy>
{
    // Former record members, kept identical. Derived render data is owned
    // outside equality and is not carried by Copy().
    public uint Version { get; set; } = Version;
    public string Name { get; set; } = Name;
    public Vector3 Center { get; set; } = Center;
    public bool HasSyntheticRetailRoot { get; set; } = HasSyntheticRetailRoot;
    public IReadOnlyList<EnbW3dRenderPivot> Pivots { get; set; } = Pivots;
    public IReadOnlyList<EnbW3dRenderPivotFixup> PivotFixups { get; set; } = PivotFixups;
    internal EnbCurrentRetailShipLegoRuntime.AttachInverseOwner? DerivedAttachInverse;
    public EnbW3dRenderHierarchy Copy(){var copy=(EnbW3dRenderHierarchy)MemberwiseClone();copy.ResetDerivedData();return copy;}
    private void ResetDerivedData(){DerivedAttachInverse=null;}
    public EnbW3dRenderHierarchy SetVersion(uint value) {Version=value;return this;}
    public EnbW3dRenderHierarchy SetName(string value) {Name=value;return this;}
    public EnbW3dRenderHierarchy SetCenter(Vector3 value) {Center=value;return this;}
    public EnbW3dRenderHierarchy SetHasSyntheticRetailRoot(bool value) {HasSyntheticRetailRoot=value;return this;}
    public EnbW3dRenderHierarchy SetPivots(IReadOnlyList<EnbW3dRenderPivot> value) {Pivots=value;return this;}
    public EnbW3dRenderHierarchy SetPivotFixups(IReadOnlyList<EnbW3dRenderPivotFixup> value) {PivotFixups=value;return this;}
    public EnbW3dRenderHierarchy SetCurrentRetailRuntimeName(string? value) {CurrentRetailRuntimeName=value;return this;}
    public EnbW3dRenderHierarchy SetCurrentRetailAnimationTranslationScale(float value) {CurrentRetailAnimationTranslationScale=value;return this;}
    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbW3dRenderHierarchy { ");
        text.Append("Version = ").Append((object?)Version);
        text.Append(", Name = ").Append((object?)Name);
        text.Append(", Center = ").Append((object?)Center);
        text.Append(", HasSyntheticRetailRoot = ").Append((object?)HasSyntheticRetailRoot);
        text.Append(", Pivots = ").Append((object?)Pivots);
        text.Append(", PivotFixups = ").Append((object?)PivotFixups);
        text.Append(", CurrentRetailRuntimeName = ").Append((object?)CurrentRetailRuntimeName);
        text.Append(", CurrentRetailAnimationTranslationScale = ").Append((object?)CurrentRetailAnimationTranslationScale);
        return text.Append(" }").ToString();
    }
    public override bool Equals(object? obj)=>Equals(obj as EnbW3dRenderHierarchy);
    public bool Equals(EnbW3dRenderHierarchy? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<uint>.Default.Equals(Version,other.Version) &&
        EqualityComparer<string>.Default.Equals(Name,other.Name) &&
        EqualityComparer<Vector3>.Default.Equals(Center,other.Center) &&
        EqualityComparer<bool>.Default.Equals(HasSyntheticRetailRoot,other.HasSyntheticRetailRoot) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderPivot>>.Default.Equals(Pivots,other.Pivots) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderPivotFixup>>.Default.Equals(PivotFixups,other.PivotFixups) &&
        EqualityComparer<string?>.Default.Equals(CurrentRetailRuntimeName,other.CurrentRetailRuntimeName) &&
        EqualityComparer<float>.Default.Equals(CurrentRetailAnimationTranslationScale,other.CurrentRetailAnimationTranslationScale);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbW3dRenderHierarchy));
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(Version!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(Name!);
            hash=hash*-1521134295+EqualityComparer<Vector3>.Default.GetHashCode(Center!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(HasSyntheticRetailRoot!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderPivot>>.Default.GetHashCode(Pivots!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderPivotFixup>>.Default.GetHashCode(PivotFixups!);
            hash=hash*-1521134295+EqualityComparer<string?>.Default.GetHashCode(CurrentRetailRuntimeName!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(CurrentRetailAnimationTranslationScale!);
            return hash;
        }
    }
    public static bool operator ==(EnbW3dRenderHierarchy? left,EnbW3dRenderHierarchy? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbW3dRenderHierarchy? left,EnbW3dRenderHierarchy? right)=>!(left==right);
    //The draw binding name can identify a missing authored dependency while
    //00988AF0's owner-local default tree has an empty native Get_Name string.
    public string? CurrentRetailRuntimeName { get; set; }
    public string RuntimeName => CurrentRetailRuntimeName ?? Name;

    //0098A0A0 stores each scaled pivot separately from the cumulative +18
    //factor subsequently consumed by animated translations (00989755).
    public float CurrentRetailAnimationTranslationScale { get; set; } = 1;
    public EnbW3dRenderHierarchy ScaleStoredTranslations(float scale)
    {
        if(scale==1)return this;
        return this.Copy().SetPivots(Pivots.Select(pivot=>pivot with
            {LocalTransform=EnbRetailParticleHierarchyTransform.ScaleTranslation(pivot.LocalTransform,scale)}).ToArray()).SetCurrentRetailAnimationTranslationScale(CurrentRetailAnimationTranslationScale*scale);
    }
}

/// <summary>
/// Renderer-owned form of one authored W3D hierarchy pivot. Current retail
/// consumes Q[0..3] directly as quaternion (x,y,z,w) and builds the pivot-local
/// transform as rotation followed by translation. System.Numerics uses row
/// vectors, so the equivalent local matrix is Rotation * Translation.
/// </summary>
public sealed record EnbW3dRenderPivot(
    int Ordinal,
    string Name,
    uint ParentIndex,
    Vector3 Translation,
    Vector3 EulerAngles,
    Quaternion Rotation,
    Matrix4x4 LocalTransform)
{
    public bool IsRoot => ParentIndex == uint.MaxValue;
}

/// <summary>
/// Authored 4x3 pivot-fixup payload retained without assigning skin/bind
/// semantics. Rigid hierarchy evaluation does not consume this data.
/// </summary>
public readonly record struct EnbW3dRenderPivotFixup(
    float M00, float M01, float M02,
    float M10, float M11, float M12,
    float M20, float M21, float M22,
    float M30, float M31, float M32);

/// <summary>
/// Immutable renderer-side copy of one hierarchy animation. Current retail
/// applies the +1 legacy pivot shift only to classic 0x0200 animation channels;
/// compressed 0x0280 flavor-0 channels retain their authored pivot indices.
/// </summary>
public sealed class EnbW3dRenderHierarchyAnimation(
    int Ordinal,
    uint Version,
    string Name,
    string HierarchyName,
    uint NumFrames,
    uint FrameRate,
    bool HasLegacySyntheticRootChannelShift,
    IReadOnlyList<EnbW3dRenderAnimationChannel> Channels,
    IReadOnlyList<EnbW3dRenderAnimationBitChannel> BitChannels) : IEquatable<EnbW3dRenderHierarchyAnimation>
{
    // Former record members, kept identical. Derived render data is owned
    // outside equality and is not carried by Copy().
    public int Ordinal { get; set; } = Ordinal;
    public uint Version { get; set; } = Version;
    public string Name { get; set; } = Name;
    public string HierarchyName { get; set; } = HierarchyName;
    public uint NumFrames { get; set; } = NumFrames;
    public uint FrameRate { get; set; } = FrameRate;
    public bool HasLegacySyntheticRootChannelShift { get; set; } = HasLegacySyntheticRootChannelShift;
    public IReadOnlyList<EnbW3dRenderAnimationChannel> Channels { get; set; } = Channels;
    public IReadOnlyList<EnbW3dRenderAnimationBitChannel> BitChannels { get; set; } = BitChannels;
    internal EnbW3dAnimationSampler.ChannelIndex? DerivedChannelIndex;
    public EnbW3dRenderHierarchyAnimation Copy(){var copy=(EnbW3dRenderHierarchyAnimation)MemberwiseClone();copy.ResetDerivedData();return copy;}
    private void ResetDerivedData(){DerivedChannelIndex=null;}
    public EnbW3dRenderHierarchyAnimation SetOrdinal(int value) {Ordinal=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetVersion(uint value) {Version=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetName(string value) {Name=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetHierarchyName(string value) {HierarchyName=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetNumFrames(uint value) {NumFrames=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetFrameRate(uint value) {FrameRate=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetHasLegacySyntheticRootChannelShift(bool value) {HasLegacySyntheticRootChannelShift=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetChannels(IReadOnlyList<EnbW3dRenderAnimationChannel> value) {Channels=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetBitChannels(IReadOnlyList<EnbW3dRenderAnimationBitChannel> value) {BitChannels=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetTimeMappedDefinition(EnbW3dTimeMappedAnimation? value) {TimeMappedDefinition=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetTimeMappedPlayback(EnbW3dTimeMappedPlayback? value) {TimeMappedPlayback=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetFamily(EnbW3dHAnimFamily value) {Family=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetCompressionFlavor(ushort? value) {CompressionFlavor=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetCompressedChannels(IReadOnlyList<EnbW3dRenderCompressedAnimationChannel> value) {CompressedChannels=value;return this;}
    public EnbW3dRenderHierarchyAnimation SetCompressedBitChannels(IReadOnlyList<EnbW3dRenderCompressedAnimationBitChannel> value) {CompressedBitChannels=value;return this;}
    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbW3dRenderHierarchyAnimation { ");
        text.Append("Ordinal = ").Append((object?)Ordinal);
        text.Append(", Version = ").Append((object?)Version);
        text.Append(", Name = ").Append((object?)Name);
        text.Append(", HierarchyName = ").Append((object?)HierarchyName);
        text.Append(", NumFrames = ").Append((object?)NumFrames);
        text.Append(", FrameRate = ").Append((object?)FrameRate);
        text.Append(", HasLegacySyntheticRootChannelShift = ").Append((object?)HasLegacySyntheticRootChannelShift);
        text.Append(", Channels = ").Append((object?)Channels);
        text.Append(", BitChannels = ").Append((object?)BitChannels);
        text.Append(", TimeMappedDefinition = ").Append((object?)TimeMappedDefinition);
        text.Append(", TimeMappedPlayback = ").Append((object?)TimeMappedPlayback);
        text.Append(", Family = ").Append((object?)Family);
        text.Append(", CompressionFlavor = ").Append((object?)CompressionFlavor);
        text.Append(", CompressedChannels = ").Append((object?)CompressedChannels);
        text.Append(", CompressedBitChannels = ").Append((object?)CompressedBitChannels);
        return text.Append(" }").ToString();
    }
    public override bool Equals(object? obj)=>Equals(obj as EnbW3dRenderHierarchyAnimation);
    public bool Equals(EnbW3dRenderHierarchyAnimation? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<int>.Default.Equals(Ordinal,other.Ordinal) &&
        EqualityComparer<uint>.Default.Equals(Version,other.Version) &&
        EqualityComparer<string>.Default.Equals(Name,other.Name) &&
        EqualityComparer<string>.Default.Equals(HierarchyName,other.HierarchyName) &&
        EqualityComparer<uint>.Default.Equals(NumFrames,other.NumFrames) &&
        EqualityComparer<uint>.Default.Equals(FrameRate,other.FrameRate) &&
        EqualityComparer<bool>.Default.Equals(HasLegacySyntheticRootChannelShift,other.HasLegacySyntheticRootChannelShift) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderAnimationChannel>>.Default.Equals(Channels,other.Channels) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderAnimationBitChannel>>.Default.Equals(BitChannels,other.BitChannels) &&
        EqualityComparer<EnbW3dTimeMappedAnimation?>.Default.Equals(TimeMappedDefinition,other.TimeMappedDefinition) &&
        EqualityComparer<EnbW3dTimeMappedPlayback?>.Default.Equals(TimeMappedPlayback,other.TimeMappedPlayback) &&
        EqualityComparer<EnbW3dHAnimFamily>.Default.Equals(Family,other.Family) &&
        EqualityComparer<ushort?>.Default.Equals(CompressionFlavor,other.CompressionFlavor) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderCompressedAnimationChannel>>.Default.Equals(CompressedChannels,other.CompressedChannels) &&
        EqualityComparer<IReadOnlyList<EnbW3dRenderCompressedAnimationBitChannel>>.Default.Equals(CompressedBitChannels,other.CompressedBitChannels);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbW3dRenderHierarchyAnimation));
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(Ordinal!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(Version!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(Name!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(HierarchyName!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(NumFrames!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(FrameRate!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(HasLegacySyntheticRootChannelShift!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderAnimationChannel>>.Default.GetHashCode(Channels!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderAnimationBitChannel>>.Default.GetHashCode(BitChannels!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dTimeMappedAnimation?>.Default.GetHashCode(TimeMappedDefinition!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dTimeMappedPlayback?>.Default.GetHashCode(TimeMappedPlayback!);
            hash=hash*-1521134295+EqualityComparer<EnbW3dHAnimFamily>.Default.GetHashCode(Family!);
            hash=hash*-1521134295+EqualityComparer<ushort?>.Default.GetHashCode(CompressionFlavor!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderCompressedAnimationChannel>>.Default.GetHashCode(CompressedChannels!);
            hash=hash*-1521134295+EqualityComparer<IReadOnlyList<EnbW3dRenderCompressedAnimationBitChannel>>.Default.GetHashCode(CompressedBitChannels!);
            return hash;
        }
    }
    public static bool operator ==(EnbW3dRenderHierarchyAnimation? left,EnbW3dRenderHierarchyAnimation? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbW3dRenderHierarchyAnimation? left,EnbW3dRenderHierarchyAnimation? right)=>!(left==right);
    public EnbW3dTimeMappedAnimation? TimeMappedDefinition { get; set; }
    public EnbW3dTimeMappedPlayback? TimeMappedPlayback { get; set; }
    public float PlaybackFrameRate => TimeMappedDefinition?.FrameRate ?? FrameRate;
    public EnbW3dHAnimFamily Family { get; set; } = EnbW3dHAnimFamily.Classic0200;
    public ushort? CompressionFlavor { get; set; }
    public IReadOnlyList<EnbW3dRenderCompressedAnimationChannel> CompressedChannels { get; set; } =
        Array.Empty<EnbW3dRenderCompressedAnimationChannel>();
    public IReadOnlyList<EnbW3dRenderCompressedAnimationBitChannel> CompressedBitChannels { get; set; } =
        Array.Empty<EnbW3dRenderCompressedAnimationBitChannel>();
}

public sealed record EnbW3dRenderAnimationChannel(
    ushort FirstFrame,
    ushort LastFrame,
    ushort VectorLength,
    ushort Flags,
    EnbW3dAnimationChannelKind? KnownKind,
    ushort SourcePivotIndex,
    int PivotIndex,
    IReadOnlyList<float> Data)
{
    public int FrameCount => LastFrame - FirstFrame + 1;
}

public sealed record EnbW3dRenderAnimationBitChannel(
    ushort FirstFrame,
    ushort LastFrame,
    ushort Flags,
    EnbW3dAnimationBitChannelKind? KnownKind,
    ushort SourcePivotIndex,
    int PivotIndex,
    byte DefaultValue,
    IReadOnlyList<byte> PackedData)
{
    public int FrameCount => LastFrame - FirstFrame + 1;
}

public sealed record EnbW3dRenderCompressedAnimationChannel(
    ushort PivotIndex,
    byte VectorLength,
    byte Flags,
    EnbW3dAnimationChannelKind? KnownKind,
    IReadOnlyList<uint> TimeCodes,
    IReadOnlyList<float> Data)
{
    public int KeyCount => TimeCodes.Count;
}

public sealed record EnbW3dRenderCompressedAnimationBitChannel(
    ushort PivotIndex,
    byte Flags,
    EnbW3dAnimationBitChannelKind? KnownKind,
    byte Reserved,
    IReadOnlyList<uint> TimeCodes)
{
    public int KeyCount => TimeCodes.Count;
}

public enum EnbW3dRenderHierarchyNodeKind
{
    RenderObject,
    Collision,
    Skin,
    Shadow,
}

public sealed record EnbW3dRenderHierarchyModel(
    int Ordinal,
    uint Version,
    string Name,
    string HierarchyName,
    IReadOnlyList<EnbW3dRenderHierarchyModelNode> Nodes);

public readonly record struct EnbW3dRenderHierarchyModelNode(
    int Ordinal,
    EnbW3dRenderHierarchyNodeKind Kind,
    string RenderObjectName,
    ushort PivotIndex);

public sealed record EnbW3dRenderHlod(
    string Name,
    string HierarchyName,
    IReadOnlyList<EnbW3dRenderHlodLevel> Levels)
{
    public EnbW3dRenderHlodAggregate? Aggregate { get; init; }
    public EnbW3dRenderHlodProxyArray? ProxyArray { get; init; }
    /// <summary>0300 pivots have already passed 009B6000's legacy remap.</summary>
    public bool IsConvertedHierarchyModel { get; init; }
    public bool IsConvertedLegacyLod { get; init; }
    public bool UsesRetainedPivotIndices => IsConvertedHierarchyModel || IsConvertedLegacyLod;

    public bool HasAggregateExtension => Aggregate is not null;
    public bool HasProxyArrayExtension => ProxyArray is not null;
}

public sealed record EnbW3dRenderHlodAggregate(
    uint RawValue,
    IReadOnlyList<EnbW3dRenderHlodMember> Members);

public sealed record EnbW3dRenderHlodProxyArray(
    uint RawHeaderValue,
    IReadOnlyList<EnbW3dRenderHlodMember> Members);

public sealed record EnbW3dRenderHlodLevel(
    int Ordinal,
    float Threshold,
    uint RawThreshold,
    IReadOnlyList<EnbW3dRenderHlodMember> Members);

public readonly record struct EnbW3dRenderHlodMember(uint ReferenceIndex, string Name);

/// <summary>
/// Deterministic compiler from the asset-layer W3D representation into the
/// renderer's immutable, buffer-ready representation.
/// </summary>
public static class EnbW3dRenderResourceCompiler
{
    //00988AD0/00988AF0: a missing tree creates one owner-local identity
    //pivot. Its native name is empty; the binding alias is only a draw join.
    public static EnbW3dRenderHierarchy CreateDefaultHierarchy(string bindingName)
        => new(0, bindingName, Vector3.Zero, false,
            [new(0, "RootTransform", uint.MaxValue, Vector3.Zero, Vector3.Zero,
                Quaternion.Identity, Matrix4x4.Identity)], [])
            { CurrentRetailRuntimeName = string.Empty };
    private static readonly EnbW3dRenderVertexMaterial RetailDefaultVertexMaterial = new(
        -1,
        "<retail-default>",
        0u,
        new EnbRenderPackedRgb8(255, 255, 255, 0),
        new EnbRenderPackedRgb8(255, 255, 255, 0),
        new EnbRenderPackedRgb8(0, 0, 0, 0),
        new EnbRenderPackedRgb8(0, 0, 0, 0),
        0f,
        1f,
        0f,
        null,
        null);

    // 00930C10 submits the D3DMATERIAL8 at00BBAF00 (power1) with lighting off.
    private static readonly EnbW3dRenderVertexMaterial RetailNullVertexMaterial = RetailDefaultVertexMaterial with { Power = 1f };

    /// <summary>
    /// 0080EAF0 replaces descriptor pass count and uniform slots. Expanded
    /// target arrays and coordinate/color storage remain owned by the target.
    /// The source is an already loaded descriptor, including post-load shader state.
    /// </summary>
    public static bool TryReplaceUniformDescriptor(
        EnbW3dRenderMesh target,
        IReadOnlyList<EnbW3dRenderMaterialPass> descriptor,
        out IReadOnlyList<EnbW3dRenderMaterialPass> passes,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(descriptor);
        diagnostic = null;
        passes = Array.Empty<EnbW3dRenderMaterialPass>();
        var result = new List<EnbW3dRenderMaterialPass>(descriptor.Count);
        for (int ordinal = 0; ordinal < descriptor.Count; ordinal++)
        {
            var incoming = descriptor[ordinal];
            var previous = target.MaterialPasses.FirstOrDefault(pass => pass.Ordinal == ordinal);
            bool expandedMaterials = previous?.VertexMaterialCardinality == EnbW3dRenderBindingCardinality.PerVertex;
            bool expandedShaders = previous?.ShaderCardinality == EnbW3dRenderBindingCardinality.PerTriangle;
            var material = incoming.CurrentRetailUniformMaterial;
            var lighting = incoming.CurrentRetailUniformLighting ?? EnbW3dVertexLightingSemantics.RetailDefault;
            if (!EnbW3dMaterialCompiler.TryDecodePackedRetailState(incoming.CurrentRetailUniformShader,
                    out var fixedFunction, out diagnostic))
                return false;
            var shader = new EnbW3dRenderShaderBinding(incoming.CurrentRetailUniformShader, default, fixedFunction)
                { SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState };
            var stages = new List<EnbW3dRenderTextureStage>(2);
            for (int stageOrdinal = 0; stageOrdinal < 2; stageOrdinal++)
            {
                var oldStage = previous?.TextureStages.FirstOrDefault(stage => stage.Ordinal == stageOrdinal);
                var uniformTexture = incoming.TextureStages.FirstOrDefault(stage => stage.Ordinal == stageOrdinal)?.CurrentRetailUniformTexture;
                bool expandedTextures = oldStage?.BindingCardinality == EnbW3dRenderBindingCardinality.PerTriangle;
                int coordinate = stageOrdinal == 0
                    ? material?.CurrentRetailRuntimeCoordinates?.Stage0 ?? 0
                    : material?.CurrentRetailRuntimeCoordinates?.Stage1 ?? 1;
                if (expandedMaterials)
                    coordinate = oldStage?.EffectiveTextureCoordinateSetIndex ?? stageOrdinal;
                var coordinates = target.RetailTextureCoordinateSets.FirstOrDefault(set => set.Index == coordinate)?.Coordinates
                    ?? Array.Empty<Vector2>();
                stages.Add(new EnbW3dRenderTextureStage(stageOrdinal,
                    expandedTextures ? oldStage!.Textures : uniformTexture is null ? [] : [uniformTexture],
                    oldStage?.RuntimeTexCoords ?? Array.Empty<Vector2>(),
                    expandedTextures ? EnbW3dRenderBindingCardinality.PerTriangle :
                        uniformTexture is null ? EnbW3dRenderBindingCardinality.None : EnbW3dRenderBindingCardinality.Uniform)
                {
                    CurrentRetailUniformTexture = uniformTexture,
                    RetailDescriptorCoordinateSetIndex = oldStage?.RetailDescriptorCoordinateSetIndex ?? -1,
                    EffectiveTextureCoordinateSetIndex = coordinate,
                    EffectiveRuntimeTexCoords = coordinates,
                });
            }
            var triangles = new List<EnbW3dRenderTriangleBinding>(target.Indices.Count / 3);
            for (int triangle = 0; triangle < target.Indices.Count / 3; triangle++)
            {
                var oldBinding = previous?.TriangleBindings[triangle];
                var selectedMaterial = expandedMaterials ? oldBinding!.Value.VertexMaterial : material;
                var selectedShader = expandedShaders ? oldBinding!.Value.Shader : shader;
                var selectedLighting = expandedMaterials
                    ? oldBinding!.Value.Semantics?.VertexLighting ?? EnbW3dVertexLightingSemantics.RetailDefault : lighting;
                if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(
                        selectedShader?.FixedFunction.PackedRetailState ?? incoming.CurrentRetailUniformShader,
                        selectedMaterial?.Material ?? RetailNullVertexMaterial, 2,
                        selectedMaterial is null ? EnbW3dVertexLightingSemantics.RetailNullMaterial : selectedLighting,
                        out var semantics, out diagnostic))
                    return false;
                var textures = stages.Select(stage => stage.BindingCardinality switch
                {
                    EnbW3dRenderBindingCardinality.PerTriangle => stage.Textures[triangle].IsUnbound ? null : stage.Textures[triangle],
                    EnbW3dRenderBindingCardinality.Uniform => stage.CurrentRetailUniformTexture,
                    _ => null,
                }).ToArray();
                triangles.Add(new(triangle, checked((int)target.Indices[triangle * 3]),
                    selectedMaterial, selectedShader, textures, semantics)
                {
                    UsesImplicitVertexMaterial = selectedMaterial is null,
                    CurrentRetailNullMaterial = selectedMaterial is null,
                    UsesImplicitShader = false,
                });
            }
            EnbW3dCompiledMaterialSemantics? single = null;
            if (!expandedMaterials && !expandedShaders)
            {
                if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(incoming.CurrentRetailUniformShader,
                        material?.Material ?? RetailNullVertexMaterial, 2,
                        material is null ? EnbW3dVertexLightingSemantics.RetailNullMaterial : lighting, out var semantics, out diagnostic))
                    return false;
                single = semantics;
            }
            result.Add(new EnbW3dRenderMaterialPass(ordinal,
                expandedMaterials ? previous!.VertexMaterials : material is null ? [] : [material.Value],
                expandedMaterials ? EnbW3dRenderBindingCardinality.PerVertex :
                    material is null ? EnbW3dRenderBindingCardinality.None : EnbW3dRenderBindingCardinality.Uniform,
                expandedShaders ? previous!.Shaders : [shader],
                expandedShaders ? EnbW3dRenderBindingCardinality.PerTriangle : EnbW3dRenderBindingCardinality.Uniform,
                previous?.DiffuseColors ?? [], previous?.DiffuseIllumination ?? [], previous?.SpecularColors ?? [],
                stages.ToArray(), EnbW3dTriangleBindingPool.Freeze(triangles), single)
            {
                CurrentRetailUniformMaterial = material,
                CurrentRetailUniformLighting = incoming.CurrentRetailUniformLighting,
                CurrentRetailUniformShader = incoming.CurrentRetailUniformShader,
                ColorChunkOrder = previous?.ColorChunkOrder ?? [],
            });
        }
        passes = result.ToArray();
        return true;
    }
    /// <summary>
    /// Copies the active descriptor arrays and remaps their material/texture
    /// pointers through the model's loaded tables (009462D0/00951EC0/00952080).
    /// Hidden uniform pointers behind expanded arrays remain shared.
    /// </summary>
    public static bool TryCopyModelDescriptor(EnbW3dRenderMesh mesh,
        EnbCurrentRetailMeshModelOwner source, EnbCurrentRetailMeshModelOwner destination,
        out IReadOnlyList<EnbW3dRenderMaterialPass> passes, out string? diagnostic)
    {
        passes = [];
        diagnostic = null;
        EnbW3dRenderVertexMaterialBinding? Material(EnbW3dRenderVertexMaterialBinding? binding)
        {
            if (binding is not { IsUnbound: false } value) return null;
            var remapped = source.RemapMaterialTo(destination, value.Material);
            return remapped is EnbW3dRenderVertexMaterial material ? value with { Material = material } : null;
        }
        EnbW3dRenderTextureBinding? Texture(EnbW3dRenderTextureBinding? binding)
        {
            if (binding is null || binding.IsUnbound) return null;
            // Model copies retain texture objects; only foreign pointers disappear.
            object identity = (object?)binding.CurrentRetailTextureState ?? binding;
            return source.RemapTextureTo(destination, identity) is null ? null : binding;
        }
        var result = new List<EnbW3dRenderMaterialPass>(mesh.MaterialPasses.Count);
        foreach (var pass in mesh.MaterialPasses)
        {
            bool expanded = pass.VertexMaterialCardinality == EnbW3dRenderBindingCardinality.PerVertex;
            var uniform = expanded ? pass.CurrentRetailUniformMaterial : Material(pass.CurrentRetailUniformMaterial);
            var materials = expanded
                ? pass.VertexMaterials.Select(binding => Material(binding) ??
                    new EnbW3dRenderVertexMaterialBinding(uint.MaxValue, RetailNullVertexMaterial) { IsUnbound = true }).ToArray()
                : uniform is {} value ? new[] { value } : [];
            var stages = pass.TextureStages.Select(stage =>
            {
                bool expandedTextures = stage.BindingCardinality == EnbW3dRenderBindingCardinality.PerTriangle;
                var texture = expandedTextures ? stage.CurrentRetailUniformTexture : Texture(stage.CurrentRetailUniformTexture);
                return (stage).Copy().SetCurrentRetailUniformTexture(texture).SetTextures(expandedTextures ? stage.Textures.Select(binding => Texture(binding) ??
                        new EnbW3dRenderTextureBinding(uint.MaxValue, string.Empty, null)).ToArray()
                        : texture is null ? [] : [texture]).SetBindingCardinality(expandedTextures ? EnbW3dRenderBindingCardinality.PerTriangle :
                        texture is null ? EnbW3dRenderBindingCardinality.None : EnbW3dRenderBindingCardinality.Uniform);
            }).ToArray();
            var triangles = new List<EnbW3dRenderTriangleBinding>(pass.TriangleBindings.Count);
            foreach (var triangle in pass.TriangleBindings)
            {
                EnbW3dRenderVertexMaterialBinding? material = expanded ? materials[triangle.MaterialVertexIndex] : uniform;
                if (material is { IsUnbound: true }) material = null;
                var lighting = material is null ? EnbW3dVertexLightingSemantics.RetailNullMaterial :
                    triangle.Semantics!.Value.VertexLighting;
                if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(
                        triangle.Semantics!.Value.FixedFunction.PackedRetailState,
                        material?.Material ?? RetailNullVertexMaterial, stages.Length, lighting,
                        out var semantics, out diagnostic)) return false;
                triangles.Add(triangle with
                {
                    VertexMaterial = material,
                    UsesImplicitVertexMaterial = material is null,
                    CurrentRetailNullMaterial = material is null,
                    Semantics = semantics,
                    Textures = stages.Select(stage => stage.BindingCardinality == EnbW3dRenderBindingCardinality.PerTriangle
                        ? stage.Textures[triangle.TriangleOrdinal].IsUnbound ? null : stage.Textures[triangle.TriangleOrdinal]
                        : stage.CurrentRetailUniformTexture).ToArray(),
                });
            }
            EnbW3dCompiledMaterialSemantics? single = pass.SingleBindingSemantics;
            if (single is {} previous)
            {
                if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(previous.FixedFunction.PackedRetailState,
                        uniform?.Material ?? RetailNullVertexMaterial, stages.Length,
                        uniform is null ? EnbW3dVertexLightingSemantics.RetailNullMaterial : previous.VertexLighting,
                        out var semantics, out diagnostic)) return false;
                single = semantics;
            }
            result.Add((pass).Copy().SetVertexMaterials(materials).SetVertexMaterialCardinality(expanded ? EnbW3dRenderBindingCardinality.PerVertex :
                    uniform is null ? EnbW3dRenderBindingCardinality.None : EnbW3dRenderBindingCardinality.Uniform).SetCurrentRetailUniformMaterial(uniform).SetCurrentRetailUniformLighting(expanded ? pass.CurrentRetailUniformLighting :
                    uniform is null ? EnbW3dVertexLightingSemantics.RetailNullMaterial : pass.CurrentRetailUniformLighting).SetShaders(EnbW3dShaderBindingTable.Freeze(pass.Shaders)).SetTextureStages(stages).SetTriangleBindings(EnbW3dTriangleBindingPool.Freeze(triangles)).SetSingleBindingSemantics(single));
        }
        passes = result.ToArray();
        return true;
    }

    private sealed class RetailTextureCoordinatePool
    {
        private const int MaximumCoordinateSets = 8;
        private readonly Dictionary<(int PassOrdinal, int StageOrdinal), int> descriptorIndices = [];
        private readonly List<EnbW3dRenderTextureCoordinateSet> sets = [];

        public IReadOnlyList<EnbW3dRenderTextureCoordinateSet> Sets => sets;

        public static RetailTextureCoordinatePool Build(EnbW3dMesh mesh)
        {
            var pool = new RetailTextureCoordinatePool();

            // The current-retail descriptor result moves to the asset
            // boundary. Reconstructing this pool from only surviving modern
            // stage children loses root-0x0005 ordering and rebinding behavior.
            foreach (EnbW3dPrimaryTextureCoordinateSet sourceSet in mesh.CurrentRetailPrimaryTextureCoordinateSets)
            {
                if (pool.sets.Count >= MaximumCoordinateSets)
                    break;
                pool.sets.Add(new EnbW3dRenderTextureCoordinateSet(
                    sourceSet.Index,
                    sourceSet.RetailCrc32,
                    Freeze(sourceSet.Coordinates)));
            }

            foreach (EnbW3dPrimaryTextureCoordinateBinding binding in mesh.CurrentRetailPrimaryTextureCoordinateBindings)
            {
                if ((uint)binding.CoordinateSetIndex < (uint)pool.sets.Count)
                {
                    pool.descriptorIndices[(binding.MaterialPassOrdinal, binding.TextureStageOrdinal)] =
                        binding.CoordinateSetIndex;
                }
            }

            return pool;
        }

        public int GetDescriptorIndex(int passOrdinal, int stageOrdinal)
            => descriptorIndices.TryGetValue((passOrdinal, stageOrdinal), out int index) ? index : -1;

        public IReadOnlyList<Vector2> ResolveEffectiveCoordinates(int effectiveIndex)
            => effectiveIndex >= 0 && effectiveIndex < sets.Count
                ? sets[effectiveIndex].Coordinates
                : Array.Empty<Vector2>();

    }

    public static bool TryCompile(
        EnbResolvedW3dResource source,
        out EnbW3dRenderResource resource,
        out string? diagnostic)
        => TryCompile(source,out resource,out diagnostic,false,out _);

    internal static bool TryCompile(EnbResolvedW3dResource source,out EnbW3dRenderResource resource,
        out string? diagnostic,bool independentRoots,out EnbW3dModel admittedModel)
    {
        ArgumentNullException.ThrowIfNull(source);
        admittedModel=source.Model;
        resource = default!;
        diagnostic = null;

        var textureDependencies = new Dictionary<string, EnbResolvedTextureReference>(
            StringComparer.OrdinalIgnoreCase);
        foreach (EnbResolvedTextureReference texture in source.Textures)
        {
            if (!string.IsNullOrWhiteSpace(texture.LogicalName))
                textureDependencies.TryAdd(texture.LogicalName.Trim(), texture);
        }

        var meshes = new List<EnbW3dRenderMesh>(source.Model.Meshes.Count);
        var admittedMeshes=new HashSet<int>();var admittedHierarchies=new HashSet<int>();var admittedAnimations=new HashSet<int>();
        for (int meshOrdinal = 0; meshOrdinal < source.Model.Meshes.Count; meshOrdinal++)
        {
            EnbW3dMesh mesh = source.Model.Meshes[meshOrdinal];
            if (!TryCompileMesh(meshes.Count, mesh, textureDependencies, out EnbW3dRenderMesh compiled, out diagnostic))
            {if(independentRoots)continue;return false;}
            admittedMeshes.Add(meshOrdinal);
            meshes.Add(compiled);
        }

        var hierarchies = new List<EnbW3dRenderHierarchy>(source.Model.Hierarchies.Count);
        for(int hierarchyOrdinal=0;hierarchyOrdinal<source.Model.Hierarchies.Count;hierarchyOrdinal++)
        {
            var hierarchy=source.Model.Hierarchies[hierarchyOrdinal];
            if (!TryCompileHierarchy(hierarchy, out EnbW3dRenderHierarchy compiled, out diagnostic))
            {if(independentRoots)continue;return false;}
            admittedHierarchies.Add(hierarchyOrdinal);
            hierarchies.Add(compiled);
        }

        var animations = new List<EnbW3dRenderHierarchyAnimation>(source.Model.Animations.Count);
        for (int animationOrdinal = 0; animationOrdinal < source.Model.Animations.Count; animationOrdinal++)
        {
            if (!TryCompileAnimation(animations.Count, source.Model.Animations[animationOrdinal], out EnbW3dRenderHierarchyAnimation compiled, out diagnostic))
            {if(independentRoots)continue;return false;}
            admittedAnimations.Add(animationOrdinal);
            animations.Add(compiled);
        }
        if(independentRoots && (admittedMeshes.Count!=source.Model.Meshes.Count || admittedHierarchies.Count!=source.Model.Hierarchies.Count || admittedAnimations.Count!=source.Model.Animations.Count))
            admittedModel=source.Model.RetainCompiledRoots(admittedMeshes,admittedHierarchies,admittedAnimations);
        diagnostic=null;

        var hierarchyModels = new List<EnbW3dRenderHierarchyModel>(source.Model.HierarchyModels.Count);
        for (int modelOrdinal = 0; modelOrdinal < source.Model.HierarchyModels.Count; modelOrdinal++)
            hierarchyModels.Add(CompileHierarchyModel(modelOrdinal, source.Model.HierarchyModels[modelOrdinal]));

        var hlods = new List<EnbW3dRenderHlod>(source.Model.HierarchicalLods.Count);
        foreach (EnbW3dHierarchicalLod hlod in source.Model.HierarchicalLods)
        {
            var levels = new List<EnbW3dRenderHlodLevel>(hlod.Levels.Count);
            foreach (EnbW3dLodLevel level in hlod.Levels)
            {
                EnbW3dRenderHlodMember[] members = level.Members
                    .Select(static member => new EnbW3dRenderHlodMember(member.ReferenceIndex, member.Name))
                    .ToArray();
                levels.Add(new EnbW3dRenderHlodLevel(
                    level.Ordinal,
                    level.Threshold,
                    level.RawThreshold,
                    Freeze(members)));
            }

            EnbW3dRenderHlodAggregate? aggregate = hlod.Aggregate is null
                ? null
                : new EnbW3dRenderHlodAggregate(
                    hlod.Aggregate.RawValue,
                    Freeze(hlod.Aggregate.Members
                        .Select(static member => new EnbW3dRenderHlodMember(member.ReferenceIndex, member.Name))
                        .ToArray()));
            EnbW3dRenderHlodProxyArray? proxyArray = hlod.ProxyArray is null
                ? null
                : new EnbW3dRenderHlodProxyArray(
                    hlod.ProxyArray.RawHeaderValue,
                    Freeze(hlod.ProxyArray.Members
                        .Select(static member => new EnbW3dRenderHlodMember(member.ReferenceIndex, member.Name))
                        .ToArray()));

            hlods.Add(new EnbW3dRenderHlod(
                hlod.Name,
                hlod.HierarchyName,
                Freeze(levels))
            {
                Aggregate = aggregate,
                ProxyArray = proxyArray,
            });
        }

        resource = new EnbW3dRenderResource(
            EnbW3dRenderResourceKey.From(source.ResourceLocation),
            source.LogicalFileName,
            Freeze(meshes),
            Freeze(hierarchies),
            Freeze(animations),
            Freeze(hierarchyModels),
            Freeze(hlods))
        {
            Collections = source.Model.Collections.ToArray(),
            LegacyLods = source.Model.LegacyLods.ToArray(),
            ParticleEmitters=source.Model.ParticleEmitters.ToArray(),
            Spheres=source.Model.Spheres.ToArray(),Rings=source.Model.Rings.ToArray(),
            SpecialMaterials=source.Model.SpecialMaterials.ToArray(),
            // 009989D0 draws boxes only when the global display mask matches;
            // the normal client mask is zero. Keep their HLOD identity/bounds/cost.
            ExternalRenderObjects = source.Model.Boxes.Select(box =>
                new EnbW3dExternalRenderObject(box.Name, EnbW3dExternalRenderObjectKind.Box,
                    source.LogicalFileName, source.ResourceLocation.ArchiveName) { BoxDefinition = box }).Concat(source.Model.NullObjects.Select(value=>
                    new EnbW3dExternalRenderObject(value.Name,EnbW3dExternalRenderObjectKind.NullObject,
                        source.LogicalFileName,source.ResourceLocation.ArchiveName))).Concat(source.Model.Dazzles.Select(value =>
                    new EnbW3dExternalRenderObject(value.Name, EnbW3dExternalRenderObjectKind.Dazzle,
                        source.LogicalFileName, source.ResourceLocation.ArchiveName) { DazzleTypeName = value.Type })).Concat(source.Model.SpecialMaterials.Select(value=>
                    new EnbW3dExternalRenderObject(value.Name,EnbW3dExternalRenderObjectKind.SpecialMaterial,
                        source.LogicalFileName,source.ResourceLocation.ArchiveName){SpecialMaterialDefinition=value})).ToArray(),
        };
        return true;
    }

    public static bool TryCompileHierarchy(
        EnbW3dHierarchy hierarchy,
        out EnbW3dRenderHierarchy compiled,
        out string? diagnostic)
    {
        compiled = default!;
        diagnostic = null;

        if (!IsFinite(hierarchy.Center))
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' contains a non-finite center.";
            return false;
        }

        bool synthesizeRetailRoot = hierarchy.Version < 0x0003_0000u;
        var pivots = new List<EnbW3dRenderPivot>(hierarchy.Pivots.Count + (synthesizeRetailRoot ? 1 : 0));
        if (synthesizeRetailRoot)
        {
            pivots.Add(new EnbW3dRenderPivot(
                0,
                "RootTransform",
                uint.MaxValue,
                Vector3.Zero,
                Vector3.Zero,
                Quaternion.Identity,
                Matrix4x4.Identity));
        }

        foreach (EnbW3dPivot pivot in hierarchy.Pivots)
        {
            if (!pivot.Rotation.IsFinite || !IsFinite(pivot.Translation))
            {
                diagnostic = $"Hierarchy '{hierarchy.Name}' pivot {pivot.Ordinal} contains non-finite authored transform data.";
                return false;
            }

            var rotation = new Quaternion(
                pivot.Rotation.Q0,
                pivot.Rotation.Q1,
                pivot.Rotation.Q2,
                pivot.Rotation.Q3);
            Matrix4x4 local = EnbCurrentRetailHierarchyLoadStorage.AuthoredLocal(pivot.Translation,rotation);
            if (!IsFinite(local))
            {
                diagnostic = $"Hierarchy '{hierarchy.Name}' pivot {pivot.Ordinal} produced a non-finite local transform.";
                return false;
            }

            int compiledOrdinal = pivot.Ordinal + (synthesizeRetailRoot ? 1 : 0);
            uint compiledParent = synthesizeRetailRoot
                ? pivot.ParentIndex == uint.MaxValue ? 0u : checked(pivot.ParentIndex + 1u)
                : pivot.ParentIndex;

            pivots.Add(new EnbW3dRenderPivot(
                compiledOrdinal,
                pivot.Name,
                compiledParent,
                pivot.Translation,
                pivot.EulerAngles,
                rotation,
                local));
        }

        EnbW3dRenderPivotFixup[] fixups = hierarchy.PivotFixups
            .Select(static fixup => new EnbW3dRenderPivotFixup(
                fixup.M00, fixup.M01, fixup.M02,
                fixup.M10, fixup.M11, fixup.M12,
                fixup.M20, fixup.M21, fixup.M22,
                fixup.M30, fixup.M31, fixup.M32))
            .ToArray();

        compiled = new EnbW3dRenderHierarchy(
            hierarchy.Version,
            hierarchy.Name,
            hierarchy.Center,
            synthesizeRetailRoot,
            Freeze(pivots),
            Freeze(fixups));
        return true;
    }

    public static bool TryCompileAnimation(
        int ordinal,
        EnbW3dHierarchyAnimation animation,
        out EnbW3dRenderHierarchyAnimation compiled,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(animation);
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        compiled = default!;
        diagnostic = null;

        if (animation.NumFrames == 0 && animation.Family != EnbW3dHAnimFamily.Adaptive02C0)
        {
            diagnostic = $"Hierarchy animation '{animation.Name}' contains zero frames.";
            return false;
        }

        bool legacyShift = animation.Family == EnbW3dHAnimFamily.Classic0200 && animation.Version < 0x0003_0000u;
        var channels = new List<EnbW3dRenderAnimationChannel>(animation.Channels.Count);
        foreach (EnbW3dAnimationChannel channel in animation.Channels)
        {
            if (channel.Data.Any(static value => !float.IsFinite(value)))
            {
                diagnostic = $"Hierarchy animation '{animation.Name}' contains non-finite channel data.";
                return false;
            }

            channels.Add(new EnbW3dRenderAnimationChannel(
                channel.FirstFrame,
                channel.LastFrame,
                channel.VectorLength,
                channel.Flags,
                channel.KnownKind,
                channel.PivotIndex,
                checked(channel.PivotIndex + (legacyShift ? 1 : 0)),
                Freeze(channel.Data)));
        }

        var bitChannels = new List<EnbW3dRenderAnimationBitChannel>(animation.BitChannels.Count);
        foreach (EnbW3dAnimationBitChannel channel in animation.BitChannels)
        {
            bitChannels.Add(new EnbW3dRenderAnimationBitChannel(
                channel.FirstFrame,
                channel.LastFrame,
                channel.Flags,
                channel.KnownKind,
                channel.PivotIndex,
                checked(channel.PivotIndex + (legacyShift ? 1 : 0)),
                channel.DefaultValue,
                Freeze(channel.PackedData.ToArray())));
        }

        if (animation.Family == EnbW3dHAnimFamily.Compressed0280 && animation.CompressionFlavor != 0)
        {
            diagnostic = $"Hierarchy animation '{animation.Name}' uses compressed 0x0280 flavor {animation.CompressionFlavor}; R6Z21 executes only current-retail flavor 0.";
            return false;
        }

        var compressedChannels = new List<EnbW3dRenderCompressedAnimationChannel>(animation.CompressedChannels.Count);
        foreach (EnbW3dCompressedAnimationChannel channel in animation.CompressedChannels)
        {
            if (channel.TimeCodes.Count == 0 ||
                channel.Data.Count != checked(channel.TimeCodes.Count * channel.VectorLength) ||
                channel.Data.Any(static value => !float.IsFinite(value)))
            {
                diagnostic = $"Hierarchy animation '{animation.Name}' contains invalid compressed channel storage.";
                return false;
            }

            compressedChannels.Add(new EnbW3dRenderCompressedAnimationChannel(
                channel.PivotIndex,
                channel.VectorLength,
                channel.Flags,
                channel.KnownKind,
                Freeze(channel.TimeCodes),
                Freeze(channel.Data)));
        }

        var compressedBitChannels = new List<EnbW3dRenderCompressedAnimationBitChannel>(animation.CompressedBitChannels.Count);
        foreach (EnbW3dCompressedAnimationBitChannel channel in animation.CompressedBitChannels)
        {
            if (channel.TimeCodes.Count == 0)
            {
                diagnostic = $"Hierarchy animation '{animation.Name}' contains an empty compressed bit channel.";
                return false;
            }

            compressedBitChannels.Add(new EnbW3dRenderCompressedAnimationBitChannel(
                channel.PivotIndex,
                channel.Flags,
                channel.KnownKind,
                channel.Reserved,
                Freeze(channel.TimeCodes)));
        }

        compiled = new EnbW3dRenderHierarchyAnimation(
            ordinal,
            animation.Version,
            animation.Name,
            animation.HierarchyName,
            animation.NumFrames,
            animation.FrameRate,
            legacyShift,
            Freeze(channels),
            Freeze(bitChannels))
        {
            Family = animation.Family,
            TimeMappedDefinition = animation.TimeMapped,
            CompressionFlavor = animation.CompressionFlavor,
            CompressedChannels = Freeze(compressedChannels),
            CompressedBitChannels = Freeze(compressedBitChannels),
        };
        return true;
    }

    private static EnbW3dRenderHierarchyModel CompileHierarchyModel(
        int ordinal,
        EnbW3dHierarchyModel model)
    {
        bool usesLegacySyntheticRoot = model.Version < 0x0003_0000u;
        EnbW3dRenderHierarchyModelNode[] nodes = model.Nodes
            .Select(node => new EnbW3dRenderHierarchyModelNode(
                node.Ordinal,
                MapHierarchyNodeKind(node.Kind),
                node.RenderObjectName,
                usesLegacySyntheticRoot
                    ? node.PivotIndex == ushort.MaxValue ? (ushort)0 : checked((ushort)(node.PivotIndex + 1))
                    : node.PivotIndex))
            .ToArray();

        return new EnbW3dRenderHierarchyModel(
            ordinal,
            model.Version,
            model.Name,
            model.HierarchyName,
            Freeze(nodes));
    }

    private static EnbW3dRenderHierarchyNodeKind MapHierarchyNodeKind(EnbW3dHierarchyNodeKind kind)
        => kind switch
        {
            EnbW3dHierarchyNodeKind.RenderObject => EnbW3dRenderHierarchyNodeKind.RenderObject,
            EnbW3dHierarchyNodeKind.Collision => EnbW3dRenderHierarchyNodeKind.Collision,
            EnbW3dHierarchyNodeKind.Skin => EnbW3dRenderHierarchyNodeKind.Skin,
            EnbW3dHierarchyNodeKind.Shadow => EnbW3dRenderHierarchyNodeKind.Shadow,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown W3D hierarchy node kind."),
        };

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static bool TryCompileMesh(
        int meshOrdinal,
        EnbW3dMesh mesh,
        IReadOnlyDictionary<string, EnbResolvedTextureReference> textureDependencies,
        out EnbW3dRenderMesh compiled,
        out string? diagnostic)
    {
        compiled = default!;
        diagnostic = null;

        if (!IsFinite(mesh.BoundingBoxMin) ||
            !IsFinite(mesh.BoundingBoxMax) ||
            !IsFinite(mesh.BoundingSphereCenter) ||
            !float.IsFinite(mesh.BoundingRadius) ||
            mesh.BoundingRadius < 0)
        {
            diagnostic = $"Mesh '{mesh.Name}' has invalid authored bounding-volume data.";
            return false;
        }

        if (mesh.TextureInfos.Count != 0 && mesh.TextureInfos.Count != mesh.TextureNames.Count)
        {
            diagnostic =
                $"Mesh '{mesh.Name}' has {mesh.TextureInfos.Count} TextureInfo records for " +
                $"{mesh.TextureNames.Count} texture names; R4H requires either no sidecar list or one entry per texture.";
            return false;
        }

        uint[] indices = new uint[checked(mesh.Triangles.Count * 3)];
        var triangleMetadata = new EnbW3dRenderTriangleMetadata[mesh.Triangles.Count];
        for (int triangleIndex = 0; triangleIndex < mesh.Triangles.Count; triangleIndex++)
        {
            EnbW3dTriangle triangle = mesh.Triangles[triangleIndex];
            int indexOffset = triangleIndex * 3;
            indices[indexOffset] = triangle.Vertex0;
            indices[indexOffset + 1] = triangle.Vertex1;
            indices[indexOffset + 2] = triangle.Vertex2;
            triangleMetadata[triangleIndex] = new EnbW3dRenderTriangleMetadata(
                triangle.Attributes,
                triangle.Normal,
                triangle.Distance);
        }

        EnbW3dRenderVertexMaterial[] vertexMaterials = mesh.VertexMaterials
            .Select(static material => CompileVertexMaterial(material))
            .ToArray();

        EnbW3dRenderVertexMaterial[] legacyRootMaterials = mesh.LegacyRootMaterials
            .Select(static material => CompileLegacyRootMaterial(material))
            .ToArray();

        IReadOnlyList<EnbW3dMaterialPass> sourcePasses = mesh.MaterialPasses;
        if (mesh.CurrentRetailPrimaryPass0MaterialDescriptor is not null && sourcePasses.Count == 0)
        {
            sourcePasses =
            [
                new EnbW3dMaterialPass(
                    0,
                    Array.Empty<uint>(),
                    Array.Empty<uint>(),
                    Array.Empty<EnbColor32>(),
                    Array.Empty<EnbColor32>(),
                    Array.Empty<EnbColor32>(),
                    Array.Empty<EnbW3dTextureStage>())
                {
                    HasPrimaryColorSource = mesh.CurrentRetailPrimaryPass0HasColorSource,
                },
            ];
        }

        RetailTextureCoordinatePool textureCoordinatePool = RetailTextureCoordinatePool.Build(mesh);
        if (!TryPreprocessVertexLighting(
                mesh,
                vertexMaterials,
                legacyRootMaterials,
                sourcePasses,
                textureCoordinatePool,
                out EnbW3dMeshLightingPreprocess lightingPreprocess,
                out diagnostic))
        {
            diagnostic = $"Mesh '{mesh.Name}': {diagnostic}";
            return false;
        }

        var passes = new List<EnbW3dRenderMaterialPass>(sourcePasses.Count);
        foreach (EnbW3dMaterialPass pass in sourcePasses)
        {
            if (!TryCompilePass(
                    mesh,
                    pass,
                    vertexMaterials,
                    legacyRootMaterials,
                    lightingPreprocess,
                    textureDependencies,
                    textureCoordinatePool,
                    out EnbW3dRenderMaterialPass compiledPass,
                    out diagnostic))
            {
                return false;
            }
            passes.Add(compiledPass);
        }

        if (!TryApplyRetailPostLoadOptimization(
                passes,
                textureCoordinatePool,
                (mesh.Attributes & 0x2000u) != 0,
                out IReadOnlyList<EnbW3dRenderMaterialPass> optimizedPasses,
                out EnbW3dRetailShaderOptimizationKind optimizationKind,
                out diagnostic))
        {
            diagnostic = $"Mesh '{mesh.Name}': {diagnostic}";
            return false;
        }

        // Current retail's base mesh allocation always owns a vertexCount-sized
        // zeroed normal array. A missing authored normal child leaves those zeros
        // intact; it does not arm the lazy normal-regeneration dirty bit. Keep the
        // format layer's useful "missing" provenance while materializing the exact
        // runtime stream at the Rendering boundary.
        IReadOnlyList<Vector3> runtimeNormals = mesh.AuthoredNormalChunkType is null
            ? Enumerable.Repeat(Vector3.Zero, mesh.Vertices.Count).ToArray()
            : mesh.Normals;

        bool hasLegacySyntheticRootInfluenceShift =
            mesh.Version < 0x0003_0000u && mesh.VertexInfluences.Count != 0;
        ushort[] runtimeBoneIndices = mesh.VertexInfluences
            .Select(influence => hasLegacySyntheticRootInfluenceShift
                ? unchecked((ushort)(influence.BoneIndex + 1))
                : influence.BoneIndex)
            .ToArray();

        compiled = new EnbW3dRenderMesh(
            meshOrdinal,
            mesh.Name,
            mesh.ContainerName,
            mesh.BoundingRadius,
            Freeze(mesh.Vertices),
            Freeze(runtimeNormals),
            Freeze(runtimeBoneIndices),
            Freeze(indices),
            Freeze(triangleMetadata),
            Freeze(vertexMaterials),
            optimizedPasses)
        {
            CurrentRetailUsesMeshRenderer = true,
            CurrentRetailLoadedMaterials = Freeze(mesh.CurrentRetailLoadedMaterialReferences is {} loadedMaterials
                ? loadedMaterials.Select(reference => reference.SourceKind == EnbW3dPrimaryMaterialSourceKind.LegacyRootMaterial
                    ? legacyRootMaterials[(int)reference.SourceIndex] : vertexMaterials[(int)reference.SourceIndex])
                : vertexMaterials),
            CurrentRetailLoadedMaterialRuntime = Freeze(mesh.CurrentRetailLoadedMaterialReferences is {} loadedRuntime
                ? loadedRuntime.Select(reference=>lightingPreprocess.LoadedMaterials[reference.SourceKind==EnbW3dPrimaryMaterialSourceKind.LegacyRootMaterial
                    ?legacyRootMaterials[(int)reference.SourceIndex]:vertexMaterials[(int)reference.SourceIndex]])
                :vertexMaterials.Select(material=>lightingPreprocess.LoadedMaterials[material])),
            ModernTextureDefinitions = Freeze(mesh.TextureNames.Select((name, index) =>
                new EnbW3dRenderTextureDefinition((uint)index, name,
                    mesh.TextureInfos.Count == 0 ? null : mesh.TextureInfos[index]))),
            TextureAcquisitions = Freeze(mesh.TextureAcquisitions),
            CurrentRetailLoadedTextureNames = Freeze(mesh.CurrentRetailLoadedTextureNames),
            LegacyTextureNames = Freeze(mesh.LegacyRootTextures.Select(texture => texture.Name)),
            Attributes = mesh.Attributes,
            GeometryVersion = mesh.Version,
            AuthoredSortLevel = mesh.AuthoredSortLevel,
            BoundingBoxMin = mesh.BoundingBoxMin,
            BoundingBoxMax = mesh.BoundingBoxMax,
            BoundingSphereCenter = mesh.BoundingSphereCenter,
            NormalSource = mesh.AuthoredNormalChunkType is null
                ? EnbW3dRenderNormalSource.RetailZeroInitializedMissingChunk
                : EnbW3dRenderNormalSource.AuthoredNormalChunk,
            AuthoredNormalChunkType = mesh.AuthoredNormalChunkType,
            AuthoredVertexJoinMap = mesh.AuthoredVertexJoinMap is {} joinMap ? Freeze(joinMap) : null,
            AuthoredAabTreePayload = mesh.AuthoredAabTreePayload is {} treePayload ? Freeze(treePayload) : null,
            CurrentRetailRenderPassCount = checked((int)(mesh.MaterialInfo?.PassCount ?? 1u)),
            HasLegacySyntheticRootInfluenceShift = hasLegacySyntheticRootInfluenceShift,
            EffectiveColor1 = lightingPreprocess.EffectiveColor1,
            RetailTextureCoordinateSets = Freeze(textureCoordinatePool.Sets),
            RetailPostLoadShaderOptimization = optimizationKind,
            CurrentRetailGeometrySortRequestOverride = mesh.CurrentRetailPrimaryPass0MaterialDescriptor is null
                ? null
                : mesh.CurrentRetailGeometrySortRequested,
            SecondaryMaterialDescriptorChunks = Freeze(mesh.SecondaryMaterialDescriptorChunks.Select(static chunk =>
                new EnbW3dRenderSecondaryMaterialDescriptorChunk(
                    chunk.MaterialPassOrdinal,
                    chunk.TextureStageOrdinal,
                    chunk.ChunkType,
                    Freeze(chunk.Payload.ToArray())))),
        };
        return true;
    }

    private static bool TryCompilePass(
        EnbW3dMesh mesh,
        EnbW3dMaterialPass pass,
        IReadOnlyList<EnbW3dRenderVertexMaterial> vertexMaterials,
        IReadOnlyList<EnbW3dRenderVertexMaterial> legacyRootMaterials,
        EnbW3dMeshLightingPreprocess lightingPreprocess,
        IReadOnlyDictionary<string, EnbResolvedTextureReference> textureDependencies,
        RetailTextureCoordinatePool textureCoordinatePool,
        out EnbW3dRenderMaterialPass compiled,
        out string? diagnostic)
    {
        compiled = default!;
        diagnostic = null;

        if (!lightingPreprocess.PassMaterials.TryGetValue(pass.Ordinal, out EnbW3dPassMaterialRuntimeLayout? materialLayout))
        {
            diagnostic = $"Mesh '{mesh.Name}' pass {pass.Ordinal} has no current-retail material runtime layout.";
            return false;
        }

        var materialBindings = materialLayout.ToRenderBindings();
        EnbW3dRenderBindingCardinality materialCardinality = materialLayout.Cardinality;

        bool usePrimaryDescriptor =
            pass.Ordinal == 0 &&
            mesh.CurrentRetailPrimaryPass0MaterialDescriptor is not null;

        if (!TryCompileShaderBindings(
                mesh,
                pass,
                usePrimaryDescriptor ? mesh.CurrentRetailPrimaryPass0MaterialDescriptor : null,
                out IReadOnlyList<EnbW3dRenderShaderBinding> shaderBindings,
                out EnbW3dRenderBindingCardinality shaderCardinality,
                out diagnostic))
        {
            diagnostic = $"Mesh '{mesh.Name}' pass {pass.Ordinal}: {diagnostic}";
            return false;
        }

        if (!TryCompileTextureStages(
                mesh,
                pass,
                usePrimaryDescriptor ? mesh.CurrentRetailPrimaryPass0MaterialDescriptor : null,
                textureDependencies,
                textureCoordinatePool,
                out IReadOnlyList<EnbW3dRenderTextureStage> textureStages,
                out diagnostic))
        {
            diagnostic = $"Mesh '{mesh.Name}' pass {pass.Ordinal}: {diagnostic}";
            return false;
        }


        EnbW3dRenderShaderBinding? retainedShader = shaderCardinality == EnbW3dRenderBindingCardinality.Uniform
            ? shaderBindings[0] : null;
        if (usePrimaryDescriptor)
        {
            if (!TryCompileShaderReference(mesh, mesh.CurrentRetailPrimaryPass0MaterialDescriptor!.UniformShader,
                    out var uniformShader, out diagnostic))
                return false;
            retainedShader = uniformShader;
        }
        EnbW3dCompiledMaterialSemantics? singleBindingSemantics = null;
        if (materialCardinality != EnbW3dRenderBindingCardinality.PerVertex &&
            shaderCardinality != EnbW3dRenderBindingCardinality.PerTriangle)
        {
            EnbW3dRenderVertexMaterial singleMaterial = materialCardinality == EnbW3dRenderBindingCardinality.Uniform
                ? materialBindings[0].Material
                : RetailDefaultVertexMaterial;
            EnbW3dRenderShaderBinding? singleShader = shaderCardinality == EnbW3dRenderBindingCardinality.Uniform
                ? shaderBindings[0]
                : null;
            EnbW3dVertexLightingSemantics lighting = materialLayout.Resolve(0).State.ToSemantics();

            if (!TryCompileEffectiveSemantics(
                    singleShader,
                    singleMaterial,
                    textureStages.Count,
                    lighting,
                    out EnbW3dCompiledMaterialSemantics semantics,
                    out diagnostic))
            {
                diagnostic = $"Mesh '{mesh.Name}' pass {pass.Ordinal}: {diagnostic}";
                return false;
            }
            singleBindingSemantics = semantics;
        }

        var triangleBindings = new List<EnbW3dRenderTriangleBinding>(mesh.Triangles.Count);
        var triangleStates = new EnbW3dTriangleBindingPool();
        var textureLists = new EnbW3dTextureBindingListPool();
        var effectiveTextures = new EnbW3dRenderTextureBinding?[textureStages.Count];
        for (int triangleOrdinal = 0; triangleOrdinal < mesh.Triangles.Count; triangleOrdinal++)
        {
            EnbW3dTriangle triangle = mesh.Triangles[triangleOrdinal];
            int materialVertexIndex = checked((int)triangle.Vertex0);

            EnbW3dRenderVertexMaterialBinding? authoredMaterial = materialCardinality switch
            {
                EnbW3dRenderBindingCardinality.None => null,
                EnbW3dRenderBindingCardinality.Uniform => materialBindings[0],
                EnbW3dRenderBindingCardinality.PerVertex => materialBindings[materialVertexIndex],
                _ => throw new InvalidOperationException("Unexpected material binding cardinality."),
            };

            EnbW3dRenderShaderBinding? authoredShader = shaderCardinality switch
            {
                EnbW3dRenderBindingCardinality.None => null,
                EnbW3dRenderBindingCardinality.Uniform => shaderBindings[0],
                EnbW3dRenderBindingCardinality.PerTriangle => shaderBindings[triangleOrdinal],
                _ => throw new InvalidOperationException("Unexpected shader binding cardinality."),
            };

            EnbW3dRenderVertexMaterial effectiveMaterial = authoredMaterial?.Material ?? RetailDefaultVertexMaterial;
            EnbW3dVertexLightingSemantics lighting = materialLayout.Resolve(materialVertexIndex).State.ToSemantics();

            for (int stageIndex = 0; stageIndex < textureStages.Count; stageIndex++)
            {
                EnbW3dRenderTextureStage stage = textureStages[stageIndex];
                effectiveTextures[stageIndex] = stage.BindingCardinality switch
                {
                    EnbW3dRenderBindingCardinality.None => null,
                    EnbW3dRenderBindingCardinality.Uniform => stage.Textures[0],
                    EnbW3dRenderBindingCardinality.PerTriangle => stage.Textures[triangleOrdinal].IsUnbound
                        ? null
                        : stage.Textures[triangleOrdinal],
                    _ => throw new InvalidOperationException("Unexpected texture binding cardinality."),
                };
            }

            if (!TryCompileEffectiveSemantics(
                    authoredShader,
                    effectiveMaterial,
                    textureStages.Count,
                    lighting,
                    out EnbW3dCompiledMaterialSemantics semantics,
                    out diagnostic))
            {
                diagnostic = $"Mesh '{mesh.Name}' pass {pass.Ordinal} triangle {triangleOrdinal}: {diagnostic}";
                return false;
            }

            triangleBindings.Add(triangleStates.Create(
                triangleOrdinal,
                materialVertexIndex,
                authoredMaterial,
                authoredShader,
                textureLists.Freeze(effectiveTextures),
                semantics) with
            {
                UsesImplicitVertexMaterial = authoredMaterial is null,
                UsesImplicitShader = authoredShader is null,
            });
        }

        compiled = new EnbW3dRenderMaterialPass(
            pass.Ordinal,
            materialBindings,
            materialCardinality,
            shaderBindings,
            shaderCardinality,
            Freeze(pass.DiffuseColors.Select(static color => ToRenderColor(color))),
            Freeze(pass.DiffuseIllumination.Select(static color => ToRenderColor(color))),
            // Current retail's active pass loader skips chunk 0x003E. Retain the
            // decoded values for provenance, but do not fold them into effective
            // triangle state or material semantics.
            Freeze(pass.SpecularColors.Select(static color => ToRenderColor(color))),
            textureStages,
            Freeze(triangleBindings),
            singleBindingSemantics)
        {
            ColorChunkOrder = Freeze(pass.ColorChunkOrder),
            CurrentRetailUniformMaterial = materialLayout.RetainedUniform?.ToRenderBinding(),
            CurrentRetailUniformLighting = materialLayout.RetainedUniform?.State.ToSemantics(),
            CurrentRetailUniformShader = retainedShader?.FixedFunction.PackedRetailState
                ?? EnbW3dMaterialCompiler.RetailW3dShaderBaseline,
        };
        return true;
    }

    private static bool TryCompileShaderBindings(
        EnbW3dMesh mesh,
        EnbW3dMaterialPass pass,
        EnbW3dPrimaryPass0MaterialDescriptor? descriptor,
        out IReadOnlyList<EnbW3dRenderShaderBinding> bindings,
        out EnbW3dRenderBindingCardinality cardinality,
        out string? diagnostic)
    {
        diagnostic = null;
        var result = new List<EnbW3dRenderShaderBinding>();

        if (descriptor is not null)
        {
            if (descriptor.PerTriangleShaders.Count != 0)
            {
                if (descriptor.PerTriangleShaders.Count != mesh.Triangles.Count)
                {
                    bindings = Array.Empty<EnbW3dRenderShaderBinding>();
                    cardinality = default;
                    diagnostic = $"primary pass-0 descriptor has {descriptor.PerTriangleShaders.Count} expanded shaders for {mesh.Triangles.Count} triangles.";
                    return false;
                }

                cardinality = EnbW3dRenderBindingCardinality.PerTriangle;
                foreach (EnbW3dPrimaryShaderReference reference in descriptor.PerTriangleShaders)
                {
                    if (!TryCompileShaderReference(mesh, reference, out EnbW3dRenderShaderBinding binding, out diagnostic))
                    {
                        bindings = Array.Empty<EnbW3dRenderShaderBinding>();
                        return false;
                    }
                    result.Add(binding);
                }
            }
            else
            {
                cardinality = EnbW3dRenderBindingCardinality.Uniform;
                if (!TryCompileShaderReference(mesh, descriptor.UniformShader, out EnbW3dRenderShaderBinding binding, out diagnostic))
                {
                    bindings = Array.Empty<EnbW3dRenderShaderBinding>();
                    return false;
                }
                result.Add(binding);
            }

            bindings = Freeze(result);
            return true;
        }

        if (!TryClassifyBindingCardinality(
                pass.ShaderIds.Count,
                mesh.Triangles.Count,
                EnbW3dRenderBindingCardinality.PerTriangle,
                out cardinality))
        {
            bindings = Array.Empty<EnbW3dRenderShaderBinding>();
            diagnostic =
                $"pass {pass.Ordinal} has {pass.ShaderIds.Count} shader IDs; current retail accepts 0, 1, or the triangle count {mesh.Triangles.Count}.";
            return false;
        }

        foreach (uint sourceId in pass.ShaderIds)
        {
            var reference = EnbW3dPrimaryShaderReference.FromModernShader(sourceId);
            if (!TryCompileShaderReference(mesh, reference, out EnbW3dRenderShaderBinding binding, out diagnostic))
            {
                bindings = Array.Empty<EnbW3dRenderShaderBinding>();
                return false;
            }
            result.Add(binding);
        }

        bindings = Freeze(result);
        return true;
    }

    private static bool TryCompileShaderReference(
        EnbW3dMesh mesh,
        EnbW3dPrimaryShaderReference reference,
        out EnbW3dRenderShaderBinding binding,
        out string? diagnostic)
    {
        diagnostic = null;
        if (reference.SourceKind == EnbW3dPrimaryShaderSourceKind.PackedRetailState)
        {
            if (!EnbW3dMaterialCompiler.TryDecodePackedRetailState(
                    reference.Value,
                    out EnbW3dFixedFunctionState fixedFunction,
                    out diagnostic))
            {
                binding = default;
                return false;
            }

            binding = new EnbW3dRenderShaderBinding(reference.Value, default, fixedFunction)
            {
                SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState,
            };
            return true;
        }

        if (reference.Value >= (uint)mesh.Shaders.Count)
        {
            binding = default;
            diagnostic = $"references missing shader {reference.Value}.";
            return false;
        }

        EnbW3dRenderShaderRecord shader = EnbW3dRenderShaderRecord.From(mesh.Shaders[(int)reference.Value]);
        if (!EnbW3dMaterialCompiler.TryDecodeShader(shader, out EnbW3dFixedFunctionState decoded, out diagnostic))
        {
            binding = default;
            return false;
        }

        binding = new EnbW3dRenderShaderBinding(reference.Value, shader, decoded)
        {
            SourceKind = EnbW3dRenderBindingSourceKind.ModernAuthored,
        };
        return true;
    }

    private static bool TryCompileTextureStages(
        EnbW3dMesh mesh,
        EnbW3dMaterialPass pass,
        EnbW3dPrimaryPass0MaterialDescriptor? descriptor,
        IReadOnlyDictionary<string, EnbResolvedTextureReference> textureDependencies,
        RetailTextureCoordinatePool textureCoordinatePool,
        out IReadOnlyList<EnbW3dRenderTextureStage> stages,
        out string? diagnostic)
    {
        diagnostic = null;
        var sourceStages = pass.TextureStages.ToList();
        bool descriptorHasStage0TextureBinding = descriptor is not null &&
            (descriptor.UniformTextureStage0 is not null || descriptor.PerTriangleTexturesStage0.Count != 0);
        if (descriptorHasStage0TextureBinding && sourceStages.All(static stage => stage.Ordinal != 0))
        {
            sourceStages.Insert(0, new EnbW3dTextureStage(0, Array.Empty<uint>(), Array.Empty<Vector2>()));
        }

        var result = new List<EnbW3dRenderTextureStage>(sourceStages.Count);
        foreach (EnbW3dTextureStage stage in sourceStages)
        {
            IReadOnlyList<EnbW3dRenderTextureBinding> bindings;
            EnbW3dRenderBindingCardinality cardinality;

            if (descriptor is not null && stage.Ordinal == 0)
            {
                if (!TryCompilePrimaryStage0TextureBindings(
                        mesh,
                        descriptor,
                        textureDependencies,
                        out bindings,
                        out cardinality,
                        out diagnostic))
                {
                    stages = Array.Empty<EnbW3dRenderTextureStage>();
                    return false;
                }
            }
            else
            {
                if (!TryCompileModernTextureBindings(
                        mesh,
                        pass.Ordinal,
                        stage,
                        textureDependencies,
                        out bindings,
                        out cardinality,
                        out diagnostic))
                {
                    stages = Array.Empty<EnbW3dRenderTextureStage>();
                    return false;
                }
            }

            EnbW3dRenderTextureBinding? uniformTexture = cardinality == EnbW3dRenderBindingCardinality.Uniform
                ? bindings[0] : null;
            if (descriptor is not null && stage.Ordinal == 0)
            {
                uniformTexture = null;
                if (descriptor.UniformTextureStage0 is EnbW3dPrimaryTextureReference uniformReference)
                {
                    if (!TryCompileTextureReference(mesh, uniformReference, textureDependencies, out var retainedTexture, out diagnostic))
                    {
                        stages = Array.Empty<EnbW3dRenderTextureStage>();
                        return false;
                    }
                    uniformTexture = retainedTexture;
                }
            }
            int descriptorCoordinateSetIndex = textureCoordinatePool.GetDescriptorIndex(pass.Ordinal, stage.Ordinal);
            int effectiveCoordinateSetIndex = descriptorCoordinateSetIndex < 0 ? 0 : descriptorCoordinateSetIndex;
            result.Add(new EnbW3dRenderTextureStage(
                stage.Ordinal,
                bindings,
                Freeze(stage.RuntimeTexCoords),
                cardinality)
            {
                CurrentRetailUniformTexture = uniformTexture,
                RetailDescriptorCoordinateSetIndex = descriptorCoordinateSetIndex,
                EffectiveTextureCoordinateSetIndex = effectiveCoordinateSetIndex,
                EffectiveRuntimeTexCoords = textureCoordinatePool.ResolveEffectiveCoordinates(effectiveCoordinateSetIndex),
            });
        }

        stages = Freeze(result.OrderBy(static stage => stage.Ordinal));
        return true;
    }

    private static bool TryCompilePrimaryStage0TextureBindings(
        EnbW3dMesh mesh,
        EnbW3dPrimaryPass0MaterialDescriptor descriptor,
        IReadOnlyDictionary<string, EnbResolvedTextureReference> textureDependencies,
        out IReadOnlyList<EnbW3dRenderTextureBinding> bindings,
        out EnbW3dRenderBindingCardinality cardinality,
        out string? diagnostic)
    {
        diagnostic = null;
        var result = new List<EnbW3dRenderTextureBinding>();
        var definitions = new Dictionary<EnbW3dPrimaryTextureReference, EnbW3dRenderTextureBinding>();
        EnbW3dRenderTextureBinding? unbound = null;
        if (descriptor.PerTriangleTexturesStage0.Count != 0)
        {
            if (descriptor.PerTriangleTexturesStage0.Count != mesh.Triangles.Count)
            {
                bindings = Array.Empty<EnbW3dRenderTextureBinding>();
                cardinality = default;
                diagnostic = $"primary pass-0 descriptor has {descriptor.PerTriangleTexturesStage0.Count} expanded stage-0 textures for {mesh.Triangles.Count} triangles.";
                return false;
            }

            cardinality = EnbW3dRenderBindingCardinality.PerTriangle;
            foreach (EnbW3dPrimaryTextureReference? reference in descriptor.PerTriangleTexturesStage0)
            {
                if (reference is null)
                {
                    result.Add(unbound ??= new EnbW3dRenderTextureBinding(uint.MaxValue, string.Empty, null));
                    continue;
                }
                if (definitions.TryGetValue(reference.Value, out var existing)) { result.Add(existing); continue; }
                if (!TryCompileTextureReference(mesh, reference.Value, textureDependencies, out EnbW3dRenderTextureBinding binding, out diagnostic))
                {
                    bindings = Array.Empty<EnbW3dRenderTextureBinding>();
                    return false;
                }
                definitions.Add(reference.Value, binding);
                result.Add(binding);
            }
        }
        else if (descriptor.UniformTextureStage0 is EnbW3dPrimaryTextureReference uniform)
        {
            cardinality = EnbW3dRenderBindingCardinality.Uniform;
            if (!TryCompileTextureReference(mesh, uniform, textureDependencies, out EnbW3dRenderTextureBinding binding, out diagnostic))
            {
                bindings = Array.Empty<EnbW3dRenderTextureBinding>();
                return false;
            }
            result.Add(binding);
        }
        else
        {
            cardinality = EnbW3dRenderBindingCardinality.None;
        }

        bindings = Freeze(result);
        return true;
    }

    private static bool TryCompileModernTextureBindings(
        EnbW3dMesh mesh,
        int passOrdinal,
        EnbW3dTextureStage stage,
        IReadOnlyDictionary<string, EnbResolvedTextureReference> textureDependencies,
        out IReadOnlyList<EnbW3dRenderTextureBinding> bindings,
        out EnbW3dRenderBindingCardinality cardinality,
        out string? diagnostic)
    {
        diagnostic = null;
        if (!TryClassifyBindingCardinality(
                stage.TextureIds.Count,
                mesh.Triangles.Count,
                EnbW3dRenderBindingCardinality.PerTriangle,
                out cardinality))
        {
            bindings = Array.Empty<EnbW3dRenderTextureBinding>();
            diagnostic =
                $"stage {stage.Ordinal} has {stage.TextureIds.Count} texture IDs; current retail accepts 0, 1, or the triangle count {mesh.Triangles.Count}.";
            return false;
        }

        var result = new List<EnbW3dRenderTextureBinding>(stage.TextureIds.Count);
        var definitions = new Dictionary<uint, EnbW3dRenderTextureBinding>();
        foreach (uint sourceId in stage.TextureIds)
        {
            if (definitions.TryGetValue(sourceId, out var existing)) { result.Add(existing); continue; }
            if (sourceId == uint.MaxValue)
            {
                if (cardinality != EnbW3dRenderBindingCardinality.PerTriangle)
                {
                    bindings = Array.Empty<EnbW3dRenderTextureBinding>();
                    diagnostic =
                        $"pass {passOrdinal} stage {stage.Ordinal} uses 0xFFFFFFFF in the uniform texture-ID form; current retail only treats -1 as unbound in the expanded per-triangle form.";
                    return false;
                }
                var unbound = new EnbW3dRenderTextureBinding(uint.MaxValue, string.Empty, null);
                definitions.Add(sourceId, unbound);
                result.Add(unbound);
                continue;
            }

            var reference = new EnbW3dPrimaryTextureReference(EnbW3dPrimaryTextureSourceKind.ModernTexture, sourceId);
            if (!TryCompileTextureReference(mesh, reference, textureDependencies, out EnbW3dRenderTextureBinding binding, out diagnostic))
            {
                bindings = Array.Empty<EnbW3dRenderTextureBinding>();
                return false;
            }
            definitions.Add(sourceId, binding);
            result.Add(binding);
        }

        bindings = Freeze(result);
        return true;
    }

    private static bool TryCompileTextureReference(
        EnbW3dMesh mesh,
        EnbW3dPrimaryTextureReference reference,
        IReadOnlyDictionary<string, EnbResolvedTextureReference> textureDependencies,
        out EnbW3dRenderTextureBinding binding,
        out string? diagnostic)
    {
        diagnostic = null;
        string logicalName;
        EnbW3dTextureSamplingSemantics sampling;
        EnbW3dRenderBindingSourceKind sourceKind;

        //009BC810 resolves 0049 IDs against the shared temporary table+F8,
        // not against just the modern0030 entries. Legacy selectors already
        // retain their decoded source reference.
        if (reference.SourceKind == EnbW3dPrimaryTextureSourceKind.ModernTexture &&
            mesh.CurrentRetailLoadedTextureReferences is {} loaded)
        {
            if (reference.SourceIndex >= (uint)loaded.Count)
            {
                binding = default!;
                diagnostic = $"references missing loaded texture {reference.SourceIndex}.";
                return false;
            }
            reference = loaded[(int)reference.SourceIndex];
        }

        if (reference.SourceKind == EnbW3dPrimaryTextureSourceKind.LegacyRootTexture)
        {
            if (reference.SourceIndex >= (uint)mesh.LegacyRootTextures.Count)
            {
                binding = default!;
                diagnostic = $"references missing legacy root texture {reference.SourceIndex}.";
                return false;
            }
            logicalName = mesh.LegacyRootTextures[(int)reference.SourceIndex].Name.Trim();
            sampling = EnbW3dTextureSamplingSemantics.RetailDefault;
            sourceKind = EnbW3dRenderBindingSourceKind.LegacyRoot;
        }
        else
        {
            if (reference.SourceIndex >= (uint)mesh.TextureNames.Count)
            {
                binding = default!;
                diagnostic = $"references missing modern texture {reference.SourceIndex}.";
                return false;
            }
            logicalName = mesh.TextureNames[(int)reference.SourceIndex].Trim();
            EnbW3dTextureInfo? textureInfo = mesh.TextureInfos.Count == 0
                ? null
                : mesh.TextureInfos[(int)reference.SourceIndex];
            sampling = EnbW3dTextureSemanticCompiler.Compile(textureInfo);
            sourceKind = EnbW3dRenderBindingSourceKind.ModernAuthored;
        }

        textureDependencies.TryGetValue(logicalName, out EnbResolvedTextureReference? dependency);
        binding = new EnbW3dRenderTextureBinding(
            reference.SourceIndex,
            logicalName,
            dependency?.Location is EnbMixResourceLocation location
                ? EnbW3dRenderTextureLocation.From(location)
                : null)
        {
            Sampling = sampling,
            SourceKind = sourceKind,
        };
        return true;
    }

    private static bool TryApplyRetailPostLoadOptimization(
        IReadOnlyList<EnbW3dRenderMaterialPass> passes,
        RetailTextureCoordinatePool textureCoordinatePool,
        bool twoSided,
        out IReadOnlyList<EnbW3dRenderMaterialPass> optimizedPasses,
        out EnbW3dRetailShaderOptimizationKind optimizationKind,
        out string? diagnostic)
    {
        diagnostic = null;
        optimizationKind = EnbW3dRetailShaderOptimizationKind.GeneralPerShader;

        if (passes.Count == 2 &&
            passes[0].ShaderCardinality != EnbW3dRenderBindingCardinality.PerTriangle &&
            passes[1].ShaderCardinality != EnbW3dRenderBindingCardinality.PerTriangle)
        {
            uint pass0Uniform = GetUniformPackedState(passes[0]);
            uint pass1Uniform = GetUniformPackedState(passes[1]);
            EnbW3dRetailTwoPassShaderOptimization pair =
                EnbW3dRetailShaderOptimizer.OptimizeUniformTwoPass(pass0Uniform, pass1Uniform);

            if (pair.Kind != EnbW3dRetailShaderOptimizationKind.GeneralPerShader)
            {
                optimizationKind = pair.Kind;
                IReadOnlyList<EnbW3dRenderTextureStage> pass0Stages = pair.Kind == EnbW3dRetailShaderOptimizationKind.TwoPassTextureHandoff
                    ? ApplyPass1Stage0Handoff(passes[0], passes[1], textureCoordinatePool)
                    : passes[0].TextureStages;

                if (!TryRebuildPassWithPostLoadState(
                        passes[0],
                        pass0Stages,
                        pair.Pass0PackedState,
                        optimizeExpandedShaders: false,
                        twoSided,
                        out EnbW3dRenderMaterialPass optimized0,
                        out diagnostic) ||
                    !TryRebuildPassWithPostLoadState(
                        passes[1],
                        passes[1].TextureStages,
                        pair.Pass1PackedState,
                        optimizeExpandedShaders: false,
                        twoSided,
                        out EnbW3dRenderMaterialPass optimized1,
                        out diagnostic))
                {
                    optimizedPasses = Array.Empty<EnbW3dRenderMaterialPass>();
                    return false;
                }

                optimizedPasses = Freeze(new[] { optimized0, optimized1 });
                return true;
            }
        }

        var result = new List<EnbW3dRenderMaterialPass>(passes.Count);
        foreach (EnbW3dRenderMaterialPass pass in passes)
        {
            uint uniformPackedState = EnbW3dRetailShaderOptimizer.OptimizeGeneralFog(GetUniformPackedState(pass));
            if (!TryRebuildPassWithPostLoadState(
                    pass,
                    pass.TextureStages,
                    uniformPackedState,
                    optimizeExpandedShaders: true,
                    twoSided,
                    out EnbW3dRenderMaterialPass rebuilt,
                    out diagnostic))
            {
                optimizedPasses = Array.Empty<EnbW3dRenderMaterialPass>();
                return false;
            }
            result.Add(rebuilt);
        }

        optimizedPasses = Freeze(result);
        return true;
    }

    private static uint GetUniformPackedState(EnbW3dRenderMaterialPass pass)
        => pass.CurrentRetailUniformShader;

    private static IReadOnlyList<EnbW3dRenderTextureStage> ApplyPass1Stage0Handoff(
        EnbW3dRenderMaterialPass pass0,
        EnbW3dRenderMaterialPass pass1,
        RetailTextureCoordinatePool textureCoordinatePool)
    {
        EnbW3dRenderTextureStage? pass0Stage0 = pass0.TextureStages.FirstOrDefault(static stage => stage.Ordinal == 0);
        EnbW3dRenderTextureStage? pass1Stage0 = pass1.TextureStages.FirstOrDefault(static stage => stage.Ordinal == 0);

        IReadOnlyList<EnbW3dRenderTextureBinding> effectiveTextures;
        EnbW3dRenderBindingCardinality effectiveCardinality;

        // FUN_009BCE00 always replaces pass 0's uniform stage-0 texture pointer
        // with pass 1's. A pre-existing pass-0 expanded array remains authoritative;
        // otherwise a pass-1 expanded array is copied wholesale when present.
        if (pass0Stage0 is not null && pass0Stage0.BindingCardinality == EnbW3dRenderBindingCardinality.PerTriangle)
        {
            effectiveTextures = pass0Stage0.Textures;
            effectiveCardinality = EnbW3dRenderBindingCardinality.PerTriangle;
        }
        else if (pass1Stage0 is not null)
        {
            effectiveTextures = pass1Stage0.Textures;
            effectiveCardinality = pass1Stage0.BindingCardinality;
        }
        else
        {
            effectiveTextures = Array.Empty<EnbW3dRenderTextureBinding>();
            effectiveCardinality = EnbW3dRenderBindingCardinality.None;
        }

        // FUN_009BCE00 copies material slot 0 from pass 1 to every effective
        // pass-0 material. This is the stage-0 texture-coordinate
        // index: FUN_00930910 emits it as D3DTSS_TEXCOORDINDEX when no mapper
        // owns the stage, while mapper programs receive the same base index.
        // FUN_009475E0 normalizes descriptor -1 to index 0 before the copy, so
        // a missing source stage also yields 0.
        int effectiveCoordinateSetIndex = pass1Stage0?.EffectiveTextureCoordinateSetIndex ?? 0;
        var replacement = new EnbW3dRenderTextureStage(
            0,
            effectiveTextures,
            pass0Stage0?.RuntimeTexCoords ?? Array.Empty<Vector2>(),
            effectiveCardinality)
        {
            CurrentRetailUniformTexture = pass1Stage0?.CurrentRetailUniformTexture,
            RetailDescriptorCoordinateSetIndex = pass0Stage0?.RetailDescriptorCoordinateSetIndex ?? -1,
            EffectiveTextureCoordinateSetIndex = effectiveCoordinateSetIndex,
            EffectiveRuntimeTexCoords = textureCoordinatePool.ResolveEffectiveCoordinates(effectiveCoordinateSetIndex),
        };

        var stages = pass0.TextureStages.Where(static stage => stage.Ordinal != 0).ToList();
        stages.Add(replacement);
        stages.Sort(static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        return Freeze(stages);
    }

    private static bool TryRebuildPassWithPostLoadState(
        EnbW3dRenderMaterialPass pass,
        IReadOnlyList<EnbW3dRenderTextureStage> textureStages,
        uint uniformPackedState,
        bool optimizeExpandedShaders,
        bool twoSided,
        out EnbW3dRenderMaterialPass rebuilt,
        out string? diagnostic)
    {
        diagnostic = null;
        // 009BAA00 maps authored 0x2000 to MeshModel+18 bit 0x100.
        // 009BCC40 clears culling on every uniform and expanded shader.
        if (twoSided) uniformPackedState &= 0xfff7ffffu;
        var shaders = new List<EnbW3dRenderShaderBinding>(pass.Shaders.Count);
        foreach (EnbW3dRenderShaderBinding shader in pass.Shaders)
        {
            uint packed = optimizeExpandedShaders
                ? EnbW3dRetailShaderOptimizer.OptimizeGeneralFog(shader.FixedFunction.PackedRetailState)
                : uniformPackedState;
            if (twoSided) packed &= 0xfff7ffffu;
            if (!EnbW3dMaterialCompiler.TryDecodePackedRetailState(
                    packed,
                    out EnbW3dFixedFunctionState fixedFunction,
                    out diagnostic))
            {
                rebuilt = default!;
                return false;
            }
            shaders.Add(new EnbW3dRenderShaderBinding(shader.SourceId, shader.Shader, fixedFunction)
            {
                SourceKind = shader.SourceKind,
            });
        }

        EnbW3dCompiledMaterialSemantics? singleBindingSemantics = null;
        if (pass.SingleBindingSemantics is EnbW3dCompiledMaterialSemantics authoredSingle)
        {
            EnbW3dRenderVertexMaterial material = pass.VertexMaterialCardinality == EnbW3dRenderBindingCardinality.Uniform
                ? pass.VertexMaterials[0].Material
                : RetailDefaultVertexMaterial;
            uint packed = pass.ShaderCardinality == EnbW3dRenderBindingCardinality.Uniform
                ? shaders[0].FixedFunction.PackedRetailState
                : uniformPackedState;
            if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(
                    packed,
                    material,
                    textureStages.Count,
                    authoredSingle.VertexLighting,
                    out EnbW3dCompiledMaterialSemantics compiledSingle,
                    out diagnostic))
            {
                rebuilt = default!;
                return false;
            }
            singleBindingSemantics = compiledSingle;
        }

        var triangleBindings = new List<EnbW3dRenderTriangleBinding>(pass.TriangleBindings.Count);
        var textureLists = new EnbW3dTextureBindingListPool();
        var textures = new EnbW3dRenderTextureBinding?[textureStages.Count];
        foreach (EnbW3dRenderTriangleBinding triangle in pass.TriangleBindings)
        {
            EnbW3dRenderShaderBinding? shader = pass.ShaderCardinality switch
            {
                EnbW3dRenderBindingCardinality.None => null,
                EnbW3dRenderBindingCardinality.Uniform => shaders[0],
                EnbW3dRenderBindingCardinality.PerTriangle => shaders[triangle.TriangleOrdinal],
                _ => throw new InvalidOperationException("Unexpected post-load shader cardinality."),
            };
            uint packed = shader?.FixedFunction.PackedRetailState ?? uniformPackedState;
            EnbW3dRenderVertexMaterial material = triangle.VertexMaterial?.Material ?? RetailDefaultVertexMaterial;
            EnbW3dVertexLightingSemantics lighting = triangle.Semantics?.VertexLighting ?? EnbW3dVertexLightingSemantics.RetailDefault;

            for (int stageIndex = 0; stageIndex < textureStages.Count; stageIndex++)
            {
                EnbW3dRenderTextureStage stage = textureStages[stageIndex];
                textures[stageIndex] = stage.BindingCardinality switch
                {
                    EnbW3dRenderBindingCardinality.None => null,
                    EnbW3dRenderBindingCardinality.Uniform => stage.Textures[0],
                    EnbW3dRenderBindingCardinality.PerTriangle => stage.Textures[triangle.TriangleOrdinal],
                    _ => throw new InvalidOperationException("Unexpected post-load texture cardinality."),
                };
            }

            if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(
                    packed,
                    material,
                    textureStages.Count,
                    lighting,
                    out EnbW3dCompiledMaterialSemantics semantics,
                    out diagnostic))
            {
                rebuilt = default!;
                return false;
            }

            triangleBindings.Add(triangle with
            {
                Shader = shader,
                Textures = textureLists.Freeze(textures),
                Semantics = semantics,
            });
        }

        rebuilt = (pass).Copy().SetCurrentRetailUniformShader(uniformPackedState).SetShaders(Freeze(shaders)).SetTextureStages(textureStages).SetTriangleBindings(Freeze(triangleBindings)).SetSingleBindingSemantics(singleBindingSemantics);
        return true;
    }

    private static bool TryCompileEffectiveSemantics(
        EnbW3dRenderShaderBinding? authoredShader,
        EnbW3dRenderVertexMaterial material,
        int textureStageCount,
        EnbW3dVertexLightingSemantics vertexLighting,
        out EnbW3dCompiledMaterialSemantics semantics,
        out string? diagnostic)
    {

        if (authoredShader is not EnbW3dRenderShaderBinding shader)
        {
            return EnbW3dMaterialCompiler.TryCompilePackedRetailState(
                EnbW3dMaterialCompiler.RetailW3dShaderBaseline,
                material,
                textureStageCount,
                vertexLighting,
                out semantics,
                out diagnostic);
        }

        return shader.SourceKind == EnbW3dRenderBindingSourceKind.PackedRetailState
            ? EnbW3dMaterialCompiler.TryCompilePackedRetailState(
                shader.FixedFunction.PackedRetailState,
                material,
                textureStageCount,
                vertexLighting,
                out semantics,
                out diagnostic)
            : EnbW3dMaterialCompiler.TryCompileWithVertexLighting(
                shader.Shader,
                material,
                textureStageCount,
                vertexLighting,
                out semantics,
                out diagnostic);
    }

    private static bool TryPreprocessVertexLighting(
        EnbW3dMesh mesh,
        IReadOnlyList<EnbW3dRenderVertexMaterial> vertexMaterials,
        IReadOnlyList<EnbW3dRenderVertexMaterial> legacyRootMaterials,
        IReadOnlyList<EnbW3dMaterialPass> sourcePasses,
        RetailTextureCoordinatePool textureCoordinatePool,
        out EnbW3dMeshLightingPreprocess result,
        out string? diagnostic)
    {
        result = default!;
        diagnostic = null;

        EnbRenderColor32[]? color1 = mesh.LegacyRootColor1.Count == 0
            ? null
            : mesh.LegacyRootColor1.Select(static color => ToRenderColor(color)).ToArray();
        foreach (EnbW3dMaterialPass pass in sourcePasses)
        {
            foreach (EnbW3dMaterialPassColorChunkKind kind in pass.ColorChunkOrder)
            {
                switch (kind)
                {
                    case EnbW3dMaterialPassColorChunkKind.DiffuseColor:
                        if (color1 is null)
                            color1 = pass.DiffuseColors.Select(static color => ToRenderColor(color)).ToArray();
                        break;

                    case EnbW3dMaterialPassColorChunkKind.DiffuseIllumination:
                        if (color1 is null)
                        {
                            color1 = pass.DiffuseIllumination
                                .Select(static color => new EnbRenderColor32(color.R, color.G, color.B, 255))
                                .ToArray();
                        }
                        else
                        {
                            for (int vertexIndex = 0; vertexIndex < color1.Length; vertexIndex++)
                            {
                                EnbColor32 illumination = pass.DiffuseIllumination[vertexIndex];
                                EnbRenderColor32 current = color1[vertexIndex];
                                color1[vertexIndex] = new EnbRenderColor32(
                                    MultiplyByte(current.R, illumination.R),
                                    MultiplyByte(current.G, illumination.G),
                                    MultiplyByte(current.B, illumination.B),
                                    current.A);
                            }
                        }
                        break;

                    default:
                        diagnostic = $"Unknown W3D material-pass color chunk kind {kind}.";
                        return false;
                }
            }
        }

        bool initialLightingEnabled = IsCurrentRetailInitialLightingEnabled(mesh.Attributes);
        EnbW3dMaterialRuntimeState[] modernStates = vertexMaterials
            .Select(material => new EnbW3dMaterialRuntimeState(material, initialLightingEnabled))
            .ToArray();
        EnbW3dMaterialRuntimeState[] legacyStates = legacyRootMaterials
            .Select(material => new EnbW3dMaterialRuntimeState(material, initialLightingEnabled))
            .ToArray();

        var passLayouts = new Dictionary<int, EnbW3dPassMaterialRuntimeLayout>();
        foreach (EnbW3dMaterialPass pass in sourcePasses)
        {
            EnbW3dPrimaryPass0MaterialDescriptor? descriptor =
                pass.Ordinal == 0 ? mesh.CurrentRetailPrimaryPass0MaterialDescriptor : null;
            if (!TryBuildPassMaterialRuntimeLayout(
                    mesh,
                    pass,
                    descriptor,
                    modernStates,
                    legacyStates,
                    initialLightingEnabled,
                    out EnbW3dPassMaterialRuntimeLayout layout,
                    out diagnostic))
            {
                return false;
            }
            passLayouts.Add(pass.Ordinal, layout);
        }

        bool retainLightingOptimization = true;
        foreach (EnbW3dMaterialPass pass in sourcePasses)
        {
            EnbW3dPassMaterialRuntimeLayout layout = passLayouts[pass.Ordinal];
            bool passUsesColor1 = pass.HasPrimaryColorSource;
            foreach (EnbW3dMaterialRuntimeState state in layout.EnumerateDistinct(mesh.Vertices.Count))
            {
                // FUN_009475E0 resets diffuse and emissive source for every pass,
                // but intentionally does not reset ambient source on a shared
                // material object.
                state.DiffuseSource = passUsesColor1
                    ? EnbW3dMaterialColorSource.Color1
                    : EnbW3dMaterialColorSource.Material;
                state.EmissiveSource = EnbW3dMaterialColorSource.Material;
                state.LightingEnabled = initialLightingEnabled;
                // 009475E0 writes both coordinates into the retained material.
                // Shared materials expose the last pass's state to every pass.
                state.Coordinates = new(
                    Math.Max(0, textureCoordinatePool.GetDescriptorIndex(pass.Ordinal, 0)),
                    Math.Max(0, textureCoordinatePool.GetDescriptorIndex(pass.Ordinal, 1)));
            }

            if (!passUsesColor1 || color1 is null)
                continue;

            (bool anyAmbient, bool anyDiffuse, bool anyEmissive) = InspectPassMaterialChannels(
                layout,
                mesh.Vertices.Count);

            EnbMaterialFoldChannel? foldChannel = null;
            EnbW3dMaterialColorSource? ambientAfterFold = null;
            EnbW3dMaterialColorSource? diffuseAfterFold = null;
            EnbW3dMaterialColorSource? emissiveAfterFold = null;

            if (!anyAmbient && anyDiffuse && !anyEmissive)
            {
                foldChannel = EnbMaterialFoldChannel.Diffuse;
                ambientAfterFold = EnbW3dMaterialColorSource.Color1;
                diffuseAfterFold = EnbW3dMaterialColorSource.Material;
                emissiveAfterFold = EnbW3dMaterialColorSource.Material;
            }
            else if (!anyAmbient && !anyDiffuse && anyEmissive)
            {
                foldChannel = EnbMaterialFoldChannel.Emissive;
                ambientAfterFold = EnbW3dMaterialColorSource.Material;
                diffuseAfterFold = EnbW3dMaterialColorSource.Color1;
                emissiveAfterFold = EnbW3dMaterialColorSource.Material;
            }
            else if (anyAmbient && !anyDiffuse && !anyEmissive)
            {
                foldChannel = EnbMaterialFoldChannel.Ambient;
                ambientAfterFold = EnbW3dMaterialColorSource.Material;
                diffuseAfterFold = EnbW3dMaterialColorSource.Color1;
                emissiveAfterFold = EnbW3dMaterialColorSource.Material;
            }
            else if (anyAmbient && anyDiffuse && !anyEmissive)
            {
                foldChannel = EnbMaterialFoldChannel.Ambient;
                ambientAfterFold = EnbW3dMaterialColorSource.Color1;
                diffuseAfterFold = EnbW3dMaterialColorSource.Color1;
                emissiveAfterFold = EnbW3dMaterialColorSource.Material;
            }

            if (foldChannel is EnbMaterialFoldChannel channel)
            {
                for (int vertexIndex = 0; vertexIndex < color1.Length; vertexIndex++)
                {
                    EnbW3dMaterialRuntimeState state = layout.Resolve(vertexIndex).State;
                    if (!TryFoldMaterialIntoColor1(color1[vertexIndex], state.Material, channel, out color1[vertexIndex], out diagnostic))
                        return false;
                    state.AmbientSource = ambientAfterFold!.Value;
                    state.DiffuseSource = diffuseAfterFold!.Value;
                    state.EmissiveSource = emissiveAfterFold!.Value;
                }
            }

            if (sourcePasses.Count != 1 && anyAmbient)
                retainLightingOptimization = false;
        }

        if (retainLightingOptimization && color1 is not null)
        {
            foreach (EnbW3dMaterialPass pass in sourcePasses)
            {
                if (!pass.HasPrimaryColorSource)
                    continue;

                EnbW3dPassMaterialRuntimeLayout layout = passLayouts[pass.Ordinal];
                (bool anyAmbient, bool anyDiffuse, bool anyEmissive) = InspectPassMaterialChannels(
                    layout,
                    mesh.Vertices.Count);
                if (anyAmbient || anyDiffuse || !anyEmissive)
                    continue;

                foreach (EnbW3dMaterialRuntimeState state in layout.EnumerateDistinct(mesh.Vertices.Count))
                    state.LightingEnabled = false;
            }
        }

        var loadedRuntime=new Dictionary<EnbW3dRenderVertexMaterial,EnbW3dLoadedMaterialRuntime>(ReferenceEqualityComparer.Instance);
        foreach(var state in modernStates.Concat(legacyStates))
            loadedRuntime[state.Material]=new(state.Material,state.Coordinates,state.ToSemantics());
        result = new EnbW3dMeshLightingPreprocess(
            color1 is null ? Array.Empty<EnbRenderColor32>() : Freeze(color1),
            passLayouts,loadedRuntime);
        return true;
    }

    private static bool TryBuildPassMaterialRuntimeLayout(
        EnbW3dMesh mesh,
        EnbW3dMaterialPass pass,
        EnbW3dPrimaryPass0MaterialDescriptor? descriptor,
        IReadOnlyList<EnbW3dMaterialRuntimeState> modernStates,
        IReadOnlyList<EnbW3dMaterialRuntimeState> legacyStates,
        bool initialLightingEnabled,
        out EnbW3dPassMaterialRuntimeLayout layout,
        out string? diagnostic)
    {
        diagnostic = null;

        if (descriptor is not null)
        {
            if (descriptor.PerVertexMaterials.Count != 0)
            {
                if (descriptor.PerVertexMaterials.Count != mesh.Vertices.Count)
                {
                    layout = default!;
                    diagnostic = $"Pass {pass.Ordinal} primary descriptor has {descriptor.PerVertexMaterials.Count} expanded materials for {mesh.Vertices.Count} vertices.";
                    return false;
                }

                var expanded = new EnbW3dResolvedMaterialState[mesh.Vertices.Count];
                for (int vertexIndex = 0; vertexIndex < expanded.Length; vertexIndex++)
                {
                    EnbW3dPrimaryMaterialReference? reference = descriptor.PerVertexMaterials[vertexIndex];
                    if (reference is null)
                    {
                        // Current retail zero-initializes the expanded pointer
                        // array. A selector need not touch an orphan vertex. Model
                        // that unbound slot with the same implicit material used
                        // when no descriptor material is installed at all.
                        expanded[vertexIndex] = EnbW3dResolvedMaterialState.Implicit(
                            new EnbW3dMaterialRuntimeState(RetailDefaultVertexMaterial, initialLightingEnabled));
                        continue;
                    }

                    if (!TryResolveMaterialReference(
                            reference.Value,
                            modernStates,
                            legacyStates,
                            out expanded[vertexIndex],
                            out diagnostic))
                    {
                        layout = default!;
                        return false;
                    }
                }

                EnbW3dResolvedMaterialState? retained = null;
                if (descriptor.UniformMaterial is EnbW3dPrimaryMaterialReference hiddenUniform)
                {
                    if (!TryResolveMaterialReference(hiddenUniform, modernStates, legacyStates, out var hiddenState, out diagnostic))
                    {
                        layout = default!;
                        return false;
                    }
                    retained = hiddenState;
                }
                layout = EnbW3dPassMaterialRuntimeLayout.PerVertex(pass.Ordinal, expanded);
                layout.RetainedUniform = retained;
                return true;
            }

            if (descriptor.UniformMaterial is EnbW3dPrimaryMaterialReference uniform)
            {
                if (!TryResolveMaterialReference(uniform, modernStates, legacyStates, out EnbW3dResolvedMaterialState resolved, out diagnostic))
                {
                    layout = default!;
                    return false;
                }
                layout = EnbW3dPassMaterialRuntimeLayout.Uniform(pass.Ordinal, resolved);
                return true;
            }

            layout = EnbW3dPassMaterialRuntimeLayout.Implicit(
                pass.Ordinal,
                new EnbW3dMaterialRuntimeState(RetailDefaultVertexMaterial with { }, initialLightingEnabled));
            return true;
        }

        if (!TryClassifyBindingCardinality(
                pass.VertexMaterialIds.Count,
                mesh.Vertices.Count,
                EnbW3dRenderBindingCardinality.PerVertex,
                out EnbW3dRenderBindingCardinality cardinality))
        {
            layout = default!;
            diagnostic = $"Pass {pass.Ordinal} has invalid vertex-material binding cardinality while preprocessing current-retail COLOR1 state.";
            return false;
        }

        if (cardinality == EnbW3dRenderBindingCardinality.None)
        {
            layout = EnbW3dPassMaterialRuntimeLayout.Implicit(
                pass.Ordinal,
                new EnbW3dMaterialRuntimeState(RetailDefaultVertexMaterial with { }, initialLightingEnabled));
            return true;
        }

        var resolvedBindings = new EnbW3dResolvedMaterialState[pass.VertexMaterialIds.Count];
        for (int index = 0; index < pass.VertexMaterialIds.Count; index++)
        {
            uint sourceId = pass.VertexMaterialIds[index];
            if (mesh.CurrentRetailLoadedMaterialReferences is {} loadedMaterials)
            {
                if (sourceId >= (uint)loadedMaterials.Count ||
                    !TryResolveMaterialReference(loadedMaterials[(int)sourceId], modernStates, legacyStates,
                        out resolvedBindings[index], out diagnostic))
                {
                    layout = default!;
                    diagnostic ??= $"Pass {pass.Ordinal} references missing loaded material {sourceId}.";
                    return false;
                }
                continue;
            }
            if (sourceId >= (uint)modernStates.Count)
            {
                layout = default!;
                diagnostic = $"Pass {pass.Ordinal} references missing vertex material {sourceId} while preprocessing current-retail COLOR1 state.";
                return false;
            }
            resolvedBindings[index] = new EnbW3dResolvedMaterialState(
                sourceId,
                EnbW3dRenderBindingSourceKind.ModernAuthored,
                modernStates[(int)sourceId]);
        }

        layout = cardinality == EnbW3dRenderBindingCardinality.Uniform
            ? EnbW3dPassMaterialRuntimeLayout.Uniform(pass.Ordinal, resolvedBindings[0])
            : EnbW3dPassMaterialRuntimeLayout.PerVertex(pass.Ordinal, resolvedBindings);
        return true;
    }

    private static bool TryResolveMaterialReference(
        EnbW3dPrimaryMaterialReference reference,
        IReadOnlyList<EnbW3dMaterialRuntimeState> modernStates,
        IReadOnlyList<EnbW3dMaterialRuntimeState> legacyStates,
        out EnbW3dResolvedMaterialState resolved,
        out string? diagnostic)
    {
        diagnostic = null;
        if (reference.SourceKind == EnbW3dPrimaryMaterialSourceKind.LegacyRootMaterial)
        {
            if (reference.SourceIndex >= (uint)legacyStates.Count)
            {
                resolved = default!;
                diagnostic = $"Primary descriptor references missing legacy root material {reference.SourceIndex}.";
                return false;
            }
            resolved = new EnbW3dResolvedMaterialState(
                reference.SourceIndex,
                EnbW3dRenderBindingSourceKind.LegacyRoot,
                legacyStates[(int)reference.SourceIndex]);
            return true;
        }

        if (reference.SourceIndex >= (uint)modernStates.Count)
        {
            resolved = default!;
            diagnostic = $"Primary descriptor references missing modern vertex material {reference.SourceIndex}.";
            return false;
        }
        resolved = new EnbW3dResolvedMaterialState(
            reference.SourceIndex,
            EnbW3dRenderBindingSourceKind.ModernAuthored,
            modernStates[(int)reference.SourceIndex]);
        return true;
    }

    private static bool IsCurrentRetailInitialLightingEnabled(uint authoredAttributes)
    {
        // Current retail does not test the authored MeshHeader3.Attributes value
        // directly here. The contradiction pass proved that the mesh loader
        // first translates selected authored bits into MeshModel+0x18 runtime
        // flags. In the current image DAT_00BB9650 is the fixed value 1:
        //
        //   authored 0x04000000 -> internal 0x4000 (takes precedence)
        //   authored 0x02000000 -> internal 0x2000 (only when 0x04000000 absent)
        //
        // FUN_009BD480 then enables lighting exactly when internal 0x2000 is
        // clear. Authored 0x00002000 instead maps to internal 0x0100 and is not
        // the lighting-disable input.
        bool selectorOnePriority = (authoredAttributes & 0x0400_0000u) != 0;
        bool internalLightingDisable =
            !selectorOnePriority &&
            (authoredAttributes & 0x0200_0000u) != 0;
        return !internalLightingDisable;
    }

    private static (bool AnyAmbient, bool AnyDiffuse, bool AnyEmissive) InspectPassMaterialChannels(
        EnbW3dPassMaterialRuntimeLayout layout,
        int vertexCount)
    {
        bool anyAmbient = false;
        bool anyDiffuse = false;
        bool anyEmissive = false;
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            EnbW3dRenderVertexMaterial material = layout.Resolve(vertexIndex).State.Material;
            if (material.CurrentRetailUniformOverride is EnbW3dRenderMaterialUniformOverride exact)
            {
                anyAmbient |= HasNonZeroRgb(exact.Ambient);
                anyDiffuse |= HasNonZeroRgb(exact.Diffuse);
                anyEmissive |= HasNonZeroRgb(exact.Emissive);
            }
            else
            {
                anyAmbient |= HasNonZeroRgb(material.Ambient);
                anyDiffuse |= HasNonZeroRgb(material.Diffuse);
                anyEmissive |= HasNonZeroRgb(material.Emissive);
            }
        }
        return (anyAmbient, anyDiffuse, anyEmissive);
    }

    private static bool TryFoldMaterialIntoColor1(
        EnbRenderColor32 color,
        EnbW3dRenderVertexMaterial material,
        EnbMaterialFoldChannel channel,
        out EnbRenderColor32 folded,
        out string? diagnostic)
    {
        if (material.CurrentRetailUniformOverride is EnbW3dRenderMaterialUniformOverride exact)
        {
            Vector3 rgb = channel switch
            {
                EnbMaterialFoldChannel.Ambient => exact.Ambient,
                EnbMaterialFoldChannel.Diffuse => exact.Diffuse,
                EnbMaterialFoldChannel.Emissive => exact.Emissive,
                _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown material fold channel."),
            };
            folded = new EnbRenderColor32(
                MultiplyByteByFloat(color.R, rgb.X),
                MultiplyByteByFloat(color.G, rgb.Y),
                MultiplyByteByFloat(color.B, rgb.Z),
                unchecked((byte)(int)MathF.Truncate(color.A * exact.DiffuseAlpha)));
            diagnostic = null;
            return true;
        }

        EnbRenderPackedRgb8 packed = channel switch
        {
            EnbMaterialFoldChannel.Ambient => material.Ambient,
            EnbMaterialFoldChannel.Diffuse => material.Diffuse,
            EnbMaterialFoldChannel.Emissive => material.Emissive,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown material fold channel."),
        };

        folded = new EnbRenderColor32(
            MultiplyByte(color.R, packed.R),
            MultiplyByte(color.G, packed.G),
            MultiplyByte(color.B, packed.B),
            unchecked((byte)(int)MathF.Truncate(color.A * material.Opacity)));
        diagnostic = null;
        return true;
    }

    private static bool HasNonZeroRgb(EnbRenderPackedRgb8 color)
        => color.R != 0 || color.G != 0 || color.B != 0;

    private static bool HasNonZeroRgb(Vector3 color)
        => color.X != 0 || color.Y != 0 || color.Z != 0;

    private static byte MultiplyByte(byte left, byte right)
        => (byte)((left * right) / 255);

    private static byte MultiplyByteByFloat(byte left, float right)
    {
        int value = (int)MathF.Truncate(left * right);
        return unchecked((byte)Math.Clamp(value, byte.MinValue, byte.MaxValue));
    }

    private sealed record EnbW3dMeshLightingPreprocess(
        IReadOnlyList<EnbRenderColor32> EffectiveColor1,
        IReadOnlyDictionary<int, EnbW3dPassMaterialRuntimeLayout> PassMaterials,
        IReadOnlyDictionary<EnbW3dRenderVertexMaterial,EnbW3dLoadedMaterialRuntime> LoadedMaterials);

    private sealed class EnbW3dMaterialRuntimeState
    {
        public EnbW3dMaterialRuntimeState(EnbW3dRenderVertexMaterial material, bool lightingEnabled)
        {
            Material = material;
            LightingEnabled = lightingEnabled;
        }

        public EnbW3dRenderVertexMaterial Material { get; }
        public bool LightingEnabled { get; set; }
        public EnbW3dMaterialRuntimeCoordinates Coordinates { get; set; } = new(0, 1);
        public EnbW3dMaterialColorSource AmbientSource { get; set; } = EnbW3dMaterialColorSource.Material;
        public EnbW3dMaterialColorSource DiffuseSource { get; set; } = EnbW3dMaterialColorSource.Material;
        public EnbW3dMaterialColorSource EmissiveSource { get; set; } = EnbW3dMaterialColorSource.Material;

        public EnbW3dVertexLightingSemantics ToSemantics()
            => new(
                LightingEnabled,
                AmbientSource,
                DiffuseSource,
                EnbW3dMaterialColorSource.Material,
                EmissiveSource);
    }

    private sealed record EnbW3dResolvedMaterialState(
        uint SourceId,
        EnbW3dRenderBindingSourceKind SourceKind,
        EnbW3dMaterialRuntimeState State)
    {
        public static EnbW3dResolvedMaterialState Implicit(EnbW3dMaterialRuntimeState state)
            => new(uint.MaxValue, EnbW3dRenderBindingSourceKind.ModernAuthored, state);

        public EnbW3dRenderVertexMaterialBinding ToRenderBinding()
            => new(SourceId, State.Material)
            {
                SourceKind = SourceKind,
                CurrentRetailRuntimeCoordinates = State.Coordinates,
            };
    }

    private sealed class EnbW3dPassMaterialRuntimeLayout
    {
        private EnbW3dPassMaterialRuntimeLayout(
            int passOrdinal,
            EnbW3dRenderBindingCardinality cardinality,
            EnbW3dResolvedMaterialState implicitOrUniform,
            IReadOnlyList<EnbW3dResolvedMaterialState> expanded)
        {
            PassOrdinal = passOrdinal;
            Cardinality = cardinality;
            ImplicitOrUniform = implicitOrUniform;
            // 0094761F..00947638 allocates a pass-local uniform material when both slots are absent.
            RetainedUniform = cardinality != EnbW3dRenderBindingCardinality.PerVertex ? implicitOrUniform : null;
            Expanded = expanded;
        }

        public EnbW3dResolvedMaterialState? RetainedUniform { get; set; }
        public int PassOrdinal { get; }
        public EnbW3dRenderBindingCardinality Cardinality { get; }
        public EnbW3dResolvedMaterialState ImplicitOrUniform { get; }
        public IReadOnlyList<EnbW3dResolvedMaterialState> Expanded { get; }

        public static EnbW3dPassMaterialRuntimeLayout Implicit(int passOrdinal, EnbW3dMaterialRuntimeState state)
            => new(passOrdinal, EnbW3dRenderBindingCardinality.None, EnbW3dResolvedMaterialState.Implicit(state), Array.Empty<EnbW3dResolvedMaterialState>());

        public static EnbW3dPassMaterialRuntimeLayout Uniform(int passOrdinal, EnbW3dResolvedMaterialState state)
            => new(passOrdinal, EnbW3dRenderBindingCardinality.Uniform, state, Array.Empty<EnbW3dResolvedMaterialState>());

        public static EnbW3dPassMaterialRuntimeLayout PerVertex(int passOrdinal, IReadOnlyList<EnbW3dResolvedMaterialState> states)
            => new(passOrdinal, EnbW3dRenderBindingCardinality.PerVertex, EnbW3dResolvedMaterialState.Implicit(new EnbW3dMaterialRuntimeState(RetailDefaultVertexMaterial, true)), states);

        public EnbW3dResolvedMaterialState Resolve(int vertexIndex)
            => Cardinality == EnbW3dRenderBindingCardinality.PerVertex
                ? Expanded[vertexIndex]
                : ImplicitOrUniform;

        public IEnumerable<EnbW3dMaterialRuntimeState> EnumerateDistinct(int vertexCount)
        {
            var seen = new HashSet<EnbW3dMaterialRuntimeState>();
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                EnbW3dMaterialRuntimeState state = Resolve(vertexIndex).State;
                if (seen.Add(state))
                    yield return state;
            }
        }

        public IReadOnlyList<EnbW3dRenderVertexMaterialBinding> ToRenderBindings()
        {
            if (Cardinality == EnbW3dRenderBindingCardinality.None)
                return Array.Empty<EnbW3dRenderVertexMaterialBinding>();
            if (Cardinality == EnbW3dRenderBindingCardinality.Uniform)
                return Freeze(new[] { ImplicitOrUniform.ToRenderBinding() });
            return Freeze(Expanded.Select(static value => value.ToRenderBinding()));
        }
    }

    private enum EnbMaterialFoldChannel
    {
        Ambient,
        Diffuse,
        Emissive,
    }

    private static bool TryClassifyBindingCardinality(
        int count,
        int expandedCount,
        EnbW3dRenderBindingCardinality expandedKind,
        out EnbW3dRenderBindingCardinality cardinality)
    {
        if (count == 0)
        {
            cardinality = EnbW3dRenderBindingCardinality.None;
            return true;
        }

        // Retail branches on a four-byte payload first, so count==1 is the
        // uniform form even when the mesh itself has one vertex/triangle.
        if (count == 1)
        {
            cardinality = EnbW3dRenderBindingCardinality.Uniform;
            return true;
        }

        if (count == expandedCount)
        {
            cardinality = expandedKind;
            return true;
        }

        cardinality = default;
        return false;
    }

    private static EnbW3dRenderVertexMaterial CompileVertexMaterial(EnbW3dVertexMaterial material)
        => new(
            material.Ordinal,
            material.Name,
            material.Info.Attributes,
            ToRenderRgb(material.Info.Ambient),
            ToRenderRgb(material.Info.Diffuse),
            ToRenderRgb(material.Info.Specular),
            ToRenderRgb(material.Info.Emissive),
            material.Info.Power,
            material.Info.Opacity,
            material.Info.UnknownFloat,
            CompileMapper(material.Info.Mapper0TypeSelector, material.Mapper0),
            CompileMapper(material.Info.Mapper1TypeSelector, material.Mapper1));

    private static EnbW3dRenderVertexMaterial CompileLegacyRootMaterial(EnbW3dLegacyRootMaterial material)
        => new(
            -1 - material.Ordinal,
            material.Name,
            material.Attributes,
            default,
            default,
            default,
            default,
            material.Power,
            material.DiffuseAlpha,
            0.0f,
            null,
            null)
        {
            IsLegacyRootMaterial = true,
            CurrentRetailUniformOverride = new EnbW3dRenderMaterialUniformOverride(
                material.Ambient,
                material.Diffuse,
                material.Specular,
                material.Emissive,
                material.Power,
                material.DiffuseAlpha),
        };

    private static EnbW3dRenderMapper? CompileMapper(byte rawSelector, EnbW3dMapperArguments? mapper)
    {
        EnbW3dMapperTypeSelector selector = (EnbW3dMapperTypeSelector)rawSelector;
        if (!selector.IsInstantiatedByCurrentRetail())
            return null;

        return new EnbW3dRenderMapper(
            selector,
            mapper?.Raw ?? string.Empty,
            mapper?.UScale ?? 1.0f,
            mapper?.VScale ?? 1.0f,
            mapper?.UPerSecond ?? 0.0f,
            mapper?.VPerSecond ?? 0.0f,
            mapper?.UOffset ?? 0.0f,
            mapper?.VOffset ?? 0.0f,
            mapper?.ClampFix ?? false);
    }

    private static EnbRenderPackedRgb8 ToRenderRgb(EnbW3dPackedRgb color)
        => new(color.R, color.G, color.B, color.Reserved);

    private static EnbRenderColor32 ToRenderColor(EnbColor32 color)
        => new(color.R, color.G, color.B, color.A);

    private static IReadOnlyList<EnbW3dRenderShaderBinding> Freeze(IEnumerable<EnbW3dRenderShaderBinding> values)
        => EnbW3dShaderBindingTable.Freeze(values);

    private static EnbW3dRenderTriangleBinding[] Freeze(IEnumerable<EnbW3dRenderTriangleBinding> values)
        => EnbW3dTriangleBindingPool.Freeze(values);

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
        // Only the decoder's immutable streams can cross this boundary without a copy.
        // Caller-owned arrays and read-only wrappers may still have mutable backing storage.
        => values is System.Collections.Immutable.ImmutableArray<T> && values is IReadOnlyList<T> immutable
            ? immutable : Array.AsReadOnly(values.ToArray());
}

/// <summary>
/// Thread-safe CPU resource cache. This caches immutable compiled W3D resources;
/// later slices may layer backend upload caches on the same stable key.
/// </summary>
public sealed class EnbW3dRenderResourceCache : IDisposable
{
    public static EnbW3dRenderResourceCache ForAssetNamespace(EnbRetailAssetBundle assets)
        =>EnbRenderSession.Of(assets).ResourceCache;
    internal static EnbW3dRenderResourceCache CreateForAssetNamespace(EnbRetailAssetBundle a)
        =>new(definition=>
            (definition.LogicalName.ToLowerInvariant(),new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)
                {CurrentRetailThumbnail=a.ResolveTextureThumbnail(definition.LogicalName)}),
            EnbW3dRetailMapperRandomSource.Process,Path.GetDirectoryName(a.Paths.ClientExecutablePath));

    // Secondary scenes already compile their ordinary meshes at FILE load.
    // Only files containing this family enter its constructor/publication path.
    public static EnbW3dRenderResource BindSpecialMaterialFile(EnbRetailAssetBundle assets,
        EnbResolvedW3dResource source,EnbW3dRenderResource compiled)
    {
        if(compiled.SpecialMaterials.Count==0)return compiled;
        var cache=ForAssetNamespace(assets);
        if(!cache.TryPublishFile(source,out _,out var bound,out var error))throw new InvalidDataException(error);
        return bound.Copy().SetExternalRenderObjects(bound.ExternalRenderObjects.Select(external=>
        {
            if(external.SpecialMaterialDefinition is not null && cache.TryFindPrototypeSource(external.Name,out var registered) &&
                registered.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.SpecialMaterial} entry)
                return external with {SpecialMaterialDefinition=registered.Model.SpecialMaterials[entry.Ordinal]};
            return external;
        }).ToArray());
    }
    private readonly object _gate = new();
    private readonly Func<EnbW3dTextureAcquisition, (string Key, EnbW3dTextureCreationRequest Creation)>? _resolveTexture;
    private readonly EnbCurrentRetailTextureRegistry _textures = new();
    private readonly EnbW3dRetailMapperRandomSource? _mapperRandomSource;
    private readonly List<EnbCurrentRetailCompiledTextureOwner> _textureOwners = [];
    private readonly Dictionary<EnbCurrentRetailSpecialMaterialResource,EnbRetailSpecialMaterialTextureOwner> _specialTextureOwners = new(ReferenceEqualityComparer.Instance);
    private readonly string _specialWorkingDirectory = Environment.CurrentDirectory;
    public EnbRetailSpecialMaterialTextureOwner? SpecialMaterialTextures(EnbCurrentRetailSpecialMaterialResource resource)
    {lock(_gate)return _specialTextureOwners.GetValueOrDefault(resource);}
    public EnbRetailSpecialMaterialInstance CreateSpecialMaterialInstance(EnbCurrentRetailSpecialMaterialDefinition definition)
    {
        lock(_gate){ObjectDisposedException.ThrowIf(_disposed,this);var instance=new EnbRetailSpecialMaterialInstance(definition);
            instance.RetainTextures(SpecialMaterialTextures);return instance;}
    }
    private readonly Dictionary<EnbW3dRenderMesh, MeshPrototypeModel> _meshPrototypeModels = new(ReferenceEqualityComparer.Instance);
    private sealed class MeshPrototypeModel
    {
        public required EnbCurrentRetailMeshModelOwner Owner { get; init; }
        public HashSet<Action<object>> RetireCallbacks { get; } = [];
        public void Retire(object identity)
        {
            foreach (var callback in RetireCallbacks) callback(identity);
        }
    }
    private bool _disposed;
    public EnbW3dRenderResourceCache() { }
    public EnbW3dRenderResourceCache(Func<EnbW3dTextureAcquisition,
        (string Key, EnbW3dTextureCreationRequest Creation)> resolveTexture,
        EnbW3dRetailMapperRandomSource? mapperRandomSource=null,string? specialWorkingDirectory=null)
    {
        _specialWorkingDirectory=specialWorkingDirectory??Environment.CurrentDirectory;
        _resolveTexture = resolveTexture ?? throw new ArgumentNullException(nameof(resolveTexture));
        _mapperRandomSource=mapperRandomSource;
    }

    /// <summary>00458CD0..58D0A uses0091DE00's same name-only registry,
    /// default creation selector/format and explicit clamp writes.</summary>
    public EnbCurrentRetailTextureRegistry.Lease AcquireRslEffectPassTexture(string logicalName)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_resolveTexture is null) throw new InvalidOperationException("Texture acquisition resolver is not configured.");
            var request = _resolveTexture(new(logicalName, null));
            var lease = _textures.Acquire(request.Key, request.Creation, null);
            lease.State.ApplyRslEffectPassAddressing();
            return lease;
        }
    }

    /// <summary>0048B7B0 and0091DE00 use the same manager+407C name registry.
    /// Named graphics factories request selector0/format0; a prior acquisition
    /// retains its creation and mutable sampler state.</summary>
    public EnbCurrentRetailTextureRegistry.Lease AcquireNamedTexture(string logicalName)
        => AcquireW3dTexture(new(logicalName,null));

    //0094CCC0 supplies authored creation flags and updates sampler state on registry hits.
    public EnbCurrentRetailTextureRegistry.Lease AcquireW3dTexture(EnbW3dTextureAcquisition acquisition)
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(_resolveTexture is null)throw new InvalidOperationException("Texture acquisition resolver is not configured.");
            var request=_resolveTexture(acquisition);
            return _textures.Acquire(request.Key,request.Creation,acquisition.Info);
        }
    }

    /// <summary>0092E800 retains the selected TextureClass pointer, independently
    /// of its prototype and of any later acquisition under the same name.</summary>
    public EnbCurrentRetailTextureRegistry.Lease? RetainTextureObject(EnbCurrentRetailTextureObjectState texture)
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            //Private recolored TextureClass objects have no registry entry;
            //the descriptor's strong state reference already owns those.
            return _textures.TryRetain(texture,out var lease)?lease:null;
        }
    }

    /// <summary>Retain the published mesh's loaded texture table for a model.
    /// The caller supplies its proven loaded material/mapper table separately.
    /// Models can outlive prototype-cache teardown (0091D150/0091E000).</summary>
    public EnbCurrentRetailMeshModelOwner CreateModelOwner(EnbW3dRenderMesh mesh,
        IEnumerable<(object Material, object? Mapper0, object? Mapper1)> materials,
        Action<object>? retire = null,EnbCurrentRetailMeshGeometryState? geometry=null)
    {
        mesh=EnbCurrentRetailMeshTexturePreparation.ModelSource(mesh);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var owner = _textureOwners.FirstOrDefault(owner => ReferenceEquals(owner.Mesh, mesh))
                ?? throw new ArgumentException("Mesh is not owned by this configured prototype cache.", nameof(mesh));
            return new(materials, retire, owner.LoadedTextures,geometry??mesh.CurrentRetailLoadedGeometry);
        }
    }

    /// <summary>Use the same prototype material mappers retained by descriptor
    /// users. The caller supplies the actual loaded material table; this does
    /// not infer legacy membership from the compiled modern table.</summary>
    public bool TryCreateModelOwner(EnbW3dRenderMesh mesh,
        IEnumerable<EnbW3dRenderVertexMaterial> loadedMaterials, uint constructionClock,
        out EnbCurrentRetailMeshModelOwner owner, out string? diagnostic,
        Action<object>? retire = null,EnbCurrentRetailMeshGeometryState? geometry=null,
        IReadOnlyList<EnbCurrentRetailTextureObjectState>? loadedTextures=null)
    {
        mesh=EnbCurrentRetailMeshTexturePreparation.ModelSource(mesh);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner = default!;
            diagnostic = null;
            var textures = _textureOwners.FirstOrDefault(value => ReferenceEquals(value.Mesh, mesh));
            if (textures is null)
            {
                diagnostic = "Mesh is not owned by this configured prototype cache.";
                return false;
            }
            var materials = new List<(object Material, object? Mapper0, object? Mapper1)>();
            foreach (var material in loadedMaterials)
            {
                //0080EAF0 appends a slot even for a null swatch material.
                if (material is null) { materials.Add((null!, null, null)); continue; }
                if (!_materialMappers.TryGetValue(material, out var pair))
                {
                    if (!EnbW3dSharedMaterialMappers.TryCreate(material, constructionClock, out pair, out diagnostic,_mapperRandomSource))
                        return false;
                    _materialMappers.Add(material, pair);
                }
                materials.Add((material, material.Mapper0 is null ? null : pair.Stage0,
                    material.Mapper1 is null ? null : pair.Stage1));
            }
            //0093E680 -> MeshClass clone0093ED00/0093EA50 shares model+8C.
            //The named prototype retains its template until0091EDE0 removes it.
            //Only an unchanged loaded table belongs to that template; explicit
            //geometry and post-mutation tables retain their separate owners.
            bool unchanged = geometry is null && materials.Count == mesh.CurrentRetailLoadedMaterials.Count &&
                materials.Where((value,index) => !ReferenceEquals(value.Material,mesh.CurrentRetailLoadedMaterials[index])).Any() == false &&
                (loadedTextures is null || loadedTextures.Count == mesh.CurrentRetailLoadedTextureStates.Count &&
                    loadedTextures.Where((value,index) => !ReferenceEquals(value,mesh.CurrentRetailLoadedTextureStates[index])).Any() == false);
            if (unchanged && _registeredPrototypes.Values.Any(prototype =>
                prototype.Source.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Mesh} entry &&
                ReferenceEquals(prototype.Resource.Meshes[entry.Ordinal],mesh)))
            {
                if (!_meshPrototypeModels.TryGetValue(mesh,out var prototype))
                {
                    MeshPrototypeModel? created = null;
                    var template = new EnbCurrentRetailMeshModelOwner(materials,
                        identity => created!.Retire(identity), textures.LoadedTextures,mesh.CurrentRetailLoadedGeometry);
                    created = new() { Owner=template };
                    _meshPrototypeModels.Add(mesh,prototype=created);
                }
                if (retire is not null) prototype.RetireCallbacks.Add(retire);
                owner=prototype.Owner.CloneRenderObject();
                return true;
            }
            var temporary=new List<EnbCurrentRetailTextureRegistry.Lease>();
            try
            {
                IEnumerable<object> textureOwners=loadedTextures is null ? textures.LoadedTextures : loadedTextures.Select(state =>
                {
                    if(!_textures.TryRetain(state,out var lease))return (object)state;
                    temporary.Add(lease);return lease;
                });
                owner = new(materials, retire, textureOwners,geometry??mesh.CurrentRetailLoadedGeometry?.CopyModel());
            }
            finally { foreach(var lease in temporary)lease.Dispose(); }
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var prototype in _meshPrototypeModels.Values) prototype.Owner.Dispose();
            _meshPrototypeModels.Clear();
            _resources.Clear();
            _registeredPrototypes.Clear();
            _configuredPrototypeNames.Clear();
            _prototypeSourcesSnapshot=null;
            _registeredHierarchies.Clear();
            _materialMappers.Clear();
            foreach (var owner in _textureOwners) owner.Dispose();
            _textureOwners.Clear();
            foreach(var owner in _specialTextureOwners.Values)owner.Dispose();
            _specialTextureOwners.Clear();
            _textures.Clear();
        }
    }
    private readonly Dictionary<EnbW3dRenderResourceKey, EnbW3dRenderResource> _resources = [];
    private readonly Dictionary<string, (EnbW3dRenderResource Resource, EnbResolvedW3dResource Source)> _registeredPrototypes =
        new(StringComparer.OrdinalIgnoreCase);
    private long _prototypeRegistration;
    private readonly HashSet<string> _configuredPrototypeNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>48B220/91ECA0 publishes a configured RenderObj prototype in
    /// the same name table used by91D370's FILE duplicate check. Its concrete
    /// render owner is retained by the caller, rather than a decoded FILE.</summary>
    public void RegisterConfiguredPrototypeName(string name)
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(!_configuredPrototypeNames.Add(name))return;
            _prototypeNamespaceVersion++;
            _prototypeSourcesSnapshot=null;
        }
    }
    private long _prototypeNamespaceVersion;
    private IReadOnlyDictionary<string,EnbResolvedW3dResource>? _prototypeSourcesSnapshot;
    private readonly Dictionary<string, EnbW3dRenderHierarchy> _registeredHierarchies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>00964130 rejects duplicate names after loading;00964380
    ///returns the retained first hierarchy independently of prototype lookup.</summary>
    public bool TryFindHierarchy(string name, out EnbW3dRenderHierarchy hierarchy)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _registeredHierarchies.TryGetValue(name, out hierarchy!);
        }
    }

    /// <summary>0091DC30 registry and literal file lookup, followed by
    ///009C5210's owner-local default tree. A successful primary file load
    ///never retries the secondary path merely because its tree is absent.</summary>
    public bool TryResolveHierarchy(EnbRetailW3dFileResolver files, string name,
        bool lazyLoadEnabled, out EnbW3dRenderHierarchy hierarchy, out string? diagnostic,
        uint constructionClock = 0)
    {
        diagnostic = null;
        //009C529D tests the first byte: an empty supplied name constructs
        //the default directly, without querying the manager for ".w3d".
        if (name.Length == 0)
        {
            hierarchy = EnbW3dRenderResourceCompiler.CreateDefaultHierarchy(name);
            return true;
        }
        if (TryFindHierarchy(name, out hierarchy)) return true;
        if (lazyLoadEnabled)
        {
            string fileName = name + ".w3d";
            bool decoded = files.TryResolve(fileName, name, out var source, out _, out bool loaded);
            if (!loaded)
                decoded = files.TryResolve("..\\" + fileName, name, out source, out _, out loaded);
            if (decoded && !TryLoadFile(source, out _, out diagnostic, constructionClock)) return false;
            if (TryFindHierarchy(name, out hierarchy)) return true;
        }
        hierarchy = EnbW3dRenderResourceCompiler.CreateDefaultHierarchy(name);
        return true;
    }
    // The lookup is not a native owner. Prototypes/models/descriptors retain
    // materials; retired replacements must not live until the asset session ends.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> _materialMappers = new();

    // Descriptor consumers also encounter loader defaults and replacement
    // materials outside the loaded table. This retains those exact references;
    // it does not add them to, or reconstruct, a model's loaded material table.
    public bool TryRetainMaterialMappers(IEnumerable<EnbW3dRenderVertexMaterial> materials,
        uint constructionClock,
        out IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> retained,
        out string? diagnostic)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>(ReferenceEqualityComparer.Instance);
            retained = result;
            diagnostic = null;
            foreach (var material in materials)
            {
                if (!_materialMappers.TryGetValue(material, out var pair))
                {
                    if (!EnbW3dSharedMaterialMappers.TryCreate(material, constructionClock, out pair, out diagnostic,_mapperRandomSource)) return false;
                    _materialMappers.Add(material, pair);
                }
                result[material] = pair;
            }
            return true;
        }
    }

    public bool TryRetainDescriptor(IReadOnlyList<EnbW3dRenderMaterialPass> passes,
        uint constructionClock, out EnbCurrentRetailSwatchDescriptor descriptor, out string? diagnostic)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            descriptor = default!;
            var materials = passes.Select(pass => pass.CurrentRetailUniformMaterial?.Material)
                .OfType<EnbW3dRenderVertexMaterial>();
            if (!TryRetainMaterialMappers(materials, constructionClock, out var retained, out diagnostic)) return false;
            var table = new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>(ReferenceEqualityComparer.Instance);
            foreach (var entry in retained) table.Add(entry.Key, entry.Value);
            return EnbCurrentRetailSwatchDescriptor.TryCreate(passes, constructionClock, table,
                out descriptor, out diagnostic);
        }
    }


    public int Count
    {
        get
        {
            lock (_gate)
                return _resources.Count;
        }
    }

    /// <summary>0091D4B0 looks up an already loaded prototype before its lazy
    /// filename path. Registration order is retained;0091D370 rejects duplicates.</summary>
    public bool TryFindPrototype(string name, out EnbW3dRenderResource resource)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_configuredPrototypeNames.Contains(name) && _registeredPrototypes.TryGetValue(name, out var registered))
            {
                resource = registered.Resource;
                return true;
            }
        }
        resource = default!;
        return false;
    }

    /// <summary>Retain the actual loaded source for0091D4B0's registry hit.
    /// A caller must not replace that hit with a guessed name-derived file.</summary>
    public bool TryFindPrototypeSource(string name, out EnbResolvedW3dResource source)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_configuredPrototypeNames.Contains(name) && _registeredPrototypes.TryGetValue(name, out var prototype))
            {
                source = prototype.Source with { RequestedObjectName = name };
                return true;
            }
        }
        source = default!;
        return false;
    }

    /// <summary>Capture existing publications before asynchronous object
    /// preparation. Later removal/republication cannot change this lookup's
    /// selected sources; a subsequent acquisition gets a new version.</summary>
    public IReadOnlyDictionary<string,EnbResolvedW3dResource> CapturePrototypeSources(out long version)
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            version=_prototypeNamespaceVersion;
            return _prototypeSourcesSnapshot??=new System.Collections.ObjectModel.ReadOnlyDictionary<string,EnbResolvedW3dResource>(
                _registeredPrototypes.Where(pair=>!_configuredPrototypeNames.Contains(pair.Key)).ToDictionary(pair=>pair.Key,
                    pair=>pair.Value.Source with {RequestedObjectName=pair.Key},StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>0091EDE0 removes only the named prototype. Compiled model
    ///storage remains available to existing children; an explicit later FILE
    ///load constructs and registers a new prototype.</summary>
    public bool RemovePrototype(string name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if(_configuredPrototypeNames.Remove(name))
            {
                _prototypeNamespaceVersion++;
                _prototypeSourcesSnapshot=null;
                return true;
            }
            //The native NULL prototype is built in and outside this registry.
            if (string.Equals(name, "NULL", StringComparison.OrdinalIgnoreCase) ||
                !_registeredPrototypes.Remove(name,out var removed)) return false;
            _prototypeNamespaceVersion++;
            _prototypeSourcesSnapshot=null;
            if (removed.Source.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Mesh} entry &&
                _meshPrototypeModels.Remove(removed.Resource.Meshes[entry.Ordinal],out var prototype))
                prototype.Owner.Dispose();
            return true;
        }
    }

    public bool TryGetRegisteredPrototype(EnbResolvedW3dResource source, out EnbW3dRenderResource resource)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registeredPrototypes.TryGetValue(source.RequestedObjectName, out var registered) &&
                registered.Source.PrototypeRegistration == source.PrototypeRegistration)
            {
                resource = registered.Resource;
                return true;
            }
            resource = default!;
            return false;
        }
    }

    private void RegisterPrototypes(EnbResolvedW3dResource source, EnbW3dRenderResource resource)
    {
        if(source.CurrentRetailAggregate is {} aggregate)
        {
            //009A4540 creates its base through the ordinary manager. The
            //decoded table here belongs to that base file, not the wrapper;
            //later named Mesh/HMODEL lookups must not inherit attachments.
            source=source with {CurrentRetailAggregate=null,LogicalFileName=aggregate.BaseLogicalFileName,
                RequestedObjectName=aggregate.BaseRequestedObjectName,ResourceLocation=aggregate.BaseResourceLocation,
                LooseFilePath=aggregate.BaseLooseFilePath};
            resource=resource.Copy().SetKey(EnbW3dRenderResourceKey.From(source.ResourceLocation)).SetLogicalFileName(source.LogicalFileName);
        }
        foreach (int ordinal in source.Model.TopLevelHierarchyOrdinals)
        {
            var hierarchy = resource.Hierarchies[ordinal];
            _registeredHierarchies.TryAdd(hierarchy.Name, hierarchy);
        }
        foreach (var entry in PrototypeFileEntries(source))
        {
            string name = entry.Aggregate?.Name??PrototypeName(resource, entry.Ordinary!.Value);
            if (string.Equals(name, "NULL", StringComparison.OrdinalIgnoreCase)) continue;
            if (_registeredPrototypes.ContainsKey(name) || _configuredPrototypeNames.Contains(name)) continue;
            _registeredPrototypes.Add(name, (resource, source with
                { RegisteredPrototype = entry.Ordinary, AggregatePrototype=entry.Aggregate is null?null:entry,
                    PrototypeRegistration = ++_prototypeRegistration }));
            _prototypeNamespaceVersion++;
            _prototypeSourcesSnapshot=null;
        }
    }

    private static IEnumerable<EnbCurrentRetailW3dPrototypeFileEntry> PrototypeFileEntries(EnbResolvedW3dResource source)
        => source.PrototypeFile?.Entries??source.Model.PrototypeEntries.Select(entry=>new EnbCurrentRetailW3dPrototypeFileEntry(entry.ChunkOffset,entry,null));

    public static string PrototypeName(EnbW3dRenderResource resource, EnbW3dPrototypeEntry entry)
        => entry.Kind switch
        {
            EnbW3dPrototypeKind.Mesh => EnbW3dRenderObjectNameSemantics.RegisteredMeshName(resource.Meshes[entry.Ordinal]),
            EnbW3dPrototypeKind.HierarchyModel => resource.HierarchyModels[entry.Ordinal].Name,
            EnbW3dPrototypeKind.HierarchicalLod => resource.HierarchicalLods[entry.Ordinal].Name,
            EnbW3dPrototypeKind.Box => resource.ExternalRenderObjects.Where(value=>value.Kind==EnbW3dExternalRenderObjectKind.Box).ElementAt(entry.Ordinal).Name,
            EnbW3dPrototypeKind.Collection => resource.Collections[entry.Ordinal].Name,
            EnbW3dPrototypeKind.LegacyLod => resource.LegacyLods[entry.Ordinal].Name,
            EnbW3dPrototypeKind.ParticleEmitter => resource.ParticleEmitters[entry.Ordinal].Name,
            EnbW3dPrototypeKind.Sphere => resource.Spheres[entry.Ordinal].Header.Name,
            EnbW3dPrototypeKind.Ring => resource.Rings[entry.Ordinal].Header.Name,
            EnbW3dPrototypeKind.SpecialMaterial => resource.SpecialMaterials[entry.Ordinal].Name,
            EnbW3dPrototypeKind.Dazzle => resource.ExternalRenderObjects.Where(value=>value.Kind==EnbW3dExternalRenderObjectKind.Dazzle).ElementAt(entry.Ordinal).Name,
            EnbW3dPrototypeKind.NullObject => resource.ExternalRenderObjects.Where(value=>value.Kind==EnbW3dExternalRenderObjectKind.NullObject).ElementAt(entry.Ordinal).Name,
            _ => throw new InvalidDataException("Unknown decoded prototype entry."),
        };

    /// <summary>0091D220/0091D270 explicitly loads FILE even when its prototypes
    /// already exist. Mesh loading precedes0091D370's duplicate-name rejection;
    /// repeat loads therefore replay texture and mapper construction while retaining
    /// the original registered prototype and its material/mapper identities.</summary>
    public bool TryLoadFile(EnbResolvedW3dResource source,
        out EnbW3dRenderResource resource,out string? diagnostic,uint? mapperCreationMilliseconds=null,
        EnbRetailCrtRandomSource? geometryRandom=null)
        => TryPublishFile(source,out _,out resource,out diagnostic,mapperCreationMilliseconds,geometryRandom:geometryRandom);

    /// <summary>The caller explicitly supplies any synchronous constructor
    /// initialization. Live-world deferred loading supplies none; worker tasks
    /// and prewarm never imply native resource-thread eligibility.</summary>
    public EnbCurrentRetailW3dFilePublicationContext CreatePublicationContext(uint? constructionClock=null,
        Action<string,EnbCurrentRetailTextureObjectState>? initializeColdTexture=null)
        => new(source=>TryPublishFile(source,out var bound,out _,out var diagnostic,constructionClock,initializeColdTexture)
            ?bound:throw new InvalidDataException(diagnostic),name=>TryFindPrototypeSource(name,out var source)?source:null);

    public bool TryGetOrCompile(EnbResolvedW3dResource source,out EnbW3dRenderResource resource,
        out string? diagnostic,uint? mapperCreationMilliseconds=null)
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(_resources.TryGetValue(EnbW3dRenderResourceKey.From(source.ResourceLocation),out resource!))
            {diagnostic=null;return true;}
            return TryPublishFile(source,out _,out resource,out diagnostic,mapperCreationMilliseconds);
        }
    }

    public bool TryPublishFile(EnbResolvedW3dResource source,out EnbResolvedW3dResource boundSource,
        out EnbW3dRenderResource resource,out string? diagnostic,uint? mapperCreationMilliseconds=null,
        Action<string,EnbCurrentRetailTextureObjectState>? initializeColdTexture=null,
        EnbRetailCrtRandomSource? geometryRandom=null)
    {
        ArgumentNullException.ThrowIfNull(source);
        boundSource=source;resource=default!;
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(!EnbW3dRenderResourceCompiler.TryCompile(source,out var compiled,out diagnostic,true,out var admittedModel))return false;
            var publication=source.PrototypeFile??new EnbCurrentRetailW3dPrototypeFile(source.Model,PrototypeFileEntries(source).ToArray());
            source=source with {Model=admittedModel,PrototypeFile=publication.WithCompiledModel(admittedModel)};
            boundSource=source;
            var owners=new List<EnbCurrentRetailCompiledTextureOwner>();
            var failedMeshes=new HashSet<int>();
            var constructedNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"NULL"};
            var boundParticles=new List<(int ChunkOffset,EnbCurrentRetailParticleEmitterDefinition Definition)>();
            var meshes=compiled.Meshes.ToArray();
            var roots=PrototypeFileEntries(source).Select(entry=>(entry.ChunkOffset,Entry:(EnbCurrentRetailW3dPrototypeFileEntry?)entry,Particle:(EnbCurrentRetailParticleEmitterDefinition?)null,TextureName:(string?)null))
                .Concat((source.PrototypeFile?.LegacyTextureConstructions??[]).Select(p=>(p.ChunkOffset,Entry:(EnbCurrentRetailW3dPrototypeFileEntry?)null,Particle:(EnbCurrentRetailParticleEmitterDefinition?)source.PrototypeFile!.LegacyParticleDefinitions.FirstOrDefault(value=>value.ChunkOffset==p.ChunkOffset).Definition,TextureName:(string?)p.TextureName)))
                .OrderBy(root=>root.ChunkOffset);
            try
            {
                foreach(var root in roots)
                {
                    EnbCurrentRetailCompiledTextureOwner? rootOwner=null;
                    var specialOwners=new List<(EnbCurrentRetailSpecialMaterialResource Resource,EnbRetailSpecialMaterialTextureOwner Owner)>();
                    try
                    {
                    if(root.Entry?.Ordinary is {Kind:EnbW3dPrototypeKind.Mesh} meshEntry)
                    {
                        var mesh=meshes[meshEntry.Ordinal];
                        if(_resolveTexture is not null)
                        {
                            rootOwner=new EnbCurrentRetailCompiledTextureOwner(mesh,_textures,_resolveTexture,initializeColdTexture);
                            meshes[meshEntry.Ordinal]=rootOwner.Mesh;
                        }
                        if(mapperCreationMilliseconds is {} clock)
                            foreach(var material in mesh.CurrentRetailLoadedMaterials)
                                if(!_materialMappers.TryGetValue(material,out _) &&
                                    EnbW3dSharedMaterialMappers.TryCreate(material,clock,out var pair,out _,_mapperRandomSource))
                                    _materialMappers.Add(material,pair);
                        //009BAA00 completes geometry/material loading before
                        //009BAD42 builds a missing collision tree. This runs
                        //even when the later registry rejects a duplicate name.
                        var authored=source.Model.Meshes[meshEntry.Ordinal];
                        if(authored.Vertices.Count!=0 && authored.Triangles.Count!=0)
                        {
                            var random=geometryRandom??EnbRetailCrtRandomSource.Current;
                            uint seed=random.State;
                            EnbCurrentRetailMeshGeometryState geometry;
                            try {geometry=EnbCurrentRetailMeshGeometryLoader.Load(authored,1,ref seed,
                                payload=>EnbCurrentRetailMeshAabTree.LoadOwnedAuthored(payload.ToArray()));}
                            catch(ArgumentException ex){throw new InvalidDataException("Invalid loaded mesh geometry: "+mesh.Name,ex);}
                            finally {random.State=seed;}
                            if(rootOwner is not null)
                            {rootOwner.SetLoadedGeometry(geometry);meshes[meshEntry.Ordinal]=rootOwner.Mesh;}
                            else meshes[meshEntry.Ordinal]=(mesh).Copy().SetCurrentRetailLoadedGeometry(geometry);
                        }
                    }
                    else if(root.Entry?.Ordinary is {Kind:EnbW3dPrototypeKind.SpecialMaterial} special && _resolveTexture is not null)
                    {
                        foreach(var member in compiled.SpecialMaterials[special.Ordinal].Members)
                            if(member.Resource is {} material)
                            {
                                try
                                {
                                    specialOwners.Add((material,new(material,name=>
                                    {
                                        var request=_resolveTexture(new(name,null));
                                        var lease=_textures.Acquire(request.Key,request.Creation,null,out bool created);
                                        try {if(created)initializeColdTexture?.Invoke(name,lease.State);return lease;}
                                        catch {lease.Dispose();throw;}
                                    },_specialWorkingDirectory)));
                                }
                                catch(Exception ex) when(ex is InvalidDataException or IOException or UnauthorizedAccessException)
                                {
                                    // Keep sibling members. The unavailable member is
                                    // diagnosed locally when its delegate is submitted.
                                }
                            }
                    }
                    else if(root.TextureName is {} textureName && _resolveTexture is not null)
                    {
                        uint format=0;
                        if(textureName.Length!=0)
                        {
                            var request=_resolveTexture(new(textureName,null));
                            using var lease=_textures.Acquire(request.Key,request.Creation,null,out bool created);
                            if(created)initializeColdTexture?.Invoke(textureName,lease.State);
                            format=lease.State.CurrentRetailFormat;
                        }
                        if(root.Particle is {} particle)boundParticles.Add((root.ChunkOffset,particle.ResolveLegacyTextureFormat(format)));
                    }
                    //The constructor runs even for duplicate names. Only the
                    //first prototype keeps its constructed texture ownership.
                    string? name=root.Entry is {} entry?entry.Aggregate?.Name??PrototypeName(compiled,entry.Ordinary!.Value):root.Particle?.Name;
                    bool published=name is not null && !_registeredPrototypes.ContainsKey(name) &&
                        !_configuredPrototypeNames.Contains(name) && constructedNames.Add(name);
                    foreach(var item in specialOwners)
                        if(published)_specialTextureOwners.Add(item.Resource,item.Owner);else item.Owner.Dispose();
                    specialOwners.Clear();
                    if(rootOwner is not null)
                    {
                        if(published)owners.Add(rootOwner);else rootOwner.Dispose();
                        rootOwner=null;
                    }
                    }
                    catch(Exception ex) when(ex is InvalidDataException or IOException)
                    {
                        rootOwner?.Dispose();
                        foreach(var item in specialOwners)item.Owner.Dispose();
                        if(root.Entry?.Ordinary is {Kind:EnbW3dPrototypeKind.Mesh} failed)failedMeshes.Add(failed.Ordinal);
                        //A local acquisition failure cannot erase siblings.
                        //Unrepresented state prevents this from proving NAME absence.
                        if(source.PrototypeFile is {} partial)
                            source=source with {PrototypeFile=partial with {HasUnrepresentedRenderPrototypes=true,HasOtherUnrepresentedRenderPrototypes=true}};
                    }
                }
                if(failedMeshes.Count!=0)
                {
                    var retained=Enumerable.Range(0,meshes.Length).Where(i=>!failedMeshes.Contains(i)).ToHashSet();
                    var model=source.Model.RetainCompiledRoots(retained,Enumerable.Range(0,source.Model.Hierarchies.Count).ToHashSet(),
                        Enumerable.Range(0,source.Model.Animations.Count).ToHashSet());
                    source=source with {Model=model,PrototypeFile=source.PrototypeFile?.WithCompiledModel(model)};
                    meshes=meshes.Where((_,i)=>retained.Contains(i)).Select((mesh,i)=>
                    {
                        var owner=owners.FirstOrDefault(value=>ReferenceEquals(value.Mesh,mesh));
                        if(owner is null)return (mesh).Copy().SetOrdinal(i);
                        owner.SetPublicationOrdinal(i);return owner.Mesh;
                    }).ToArray();
                }
                boundSource=source;
                if(boundParticles.Count!=0)
                {
                    var boundPublication=source.PrototypeFile!.BindLegacyParticles(boundParticles);
                    boundSource=source with {Model=boundPublication.Model,PrototypeFile=boundPublication};
                    compiled=compiled.Copy().SetParticleEmitters(boundPublication.Model.ParticleEmitters);
                }
                compiled=compiled.Copy().SetMeshes(Array.AsReadOnly(meshes));
                var key=EnbW3dRenderResourceKey.From(source.ResourceLocation);
                bool firstFile=!_resources.TryGetValue(key,out var previous);
                _textureOwners.AddRange(owners);
                RegisterPrototypes(boundSource,compiled);
                if(firstFile)_resources.Add(key,compiled);
                resource=previous??compiled;diagnostic=null;return true;
            }
            catch
            {
                foreach(var owner in owners)owner.Dispose();
                throw;
            }
        }
    }
}
