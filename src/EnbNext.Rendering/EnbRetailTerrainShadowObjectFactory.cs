using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using EnbNext.Assets;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>A SHADOW render object (retail mesh, NULL or HLOD RenderObj). Transform rows are
/// retail's 3x4 row-major matrix; flags are the RenderObj word at +18 (0400 visible,
/// 2000 bounds current, 20000 child poses stale).</summary>
public abstract class EnbRetailShadowObject
{
    internal const uint VisibleBit = 0x400, BoundsCurrent = 0x2000, PosesStale = 0x20000;
    internal int References = 1;
    internal EnbRetailShadowObject? Parent;
    internal uint Flags = 0x601;
    internal readonly float[] Rows = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
    internal bool IdentityTransform;
    internal Vector4 WorldSphere = new(0, 0, 0, 1);
    internal Vector3 WorldBoxCenter, WorldBoxExtent = Vector3.One;
    internal string Name = "";

    /// <summary>RenderObj class id (virtual 0C): mesh 0, NULL 16, HLOD 19.</summary>
    internal abstract int ClassId { get; }
    internal abstract Vector4 LocalSphere();
    internal abstract (Vector3 Center, Vector3 Extent) LocalBox();
    /// <summary>Virtual F4: the cached world box, recalculated when stale.</summary>
    internal abstract (Vector3 Center, Vector3 Extent) WorldBox();
    /// <summary>Virtual D0: ray test; true when a nearer hit was recorded.</summary>
    internal abstract bool Cast(EnbRetailShadowRay ray);
    internal abstract void Scale(float scale);
    /// <summary>Virtual 50.</summary>
    internal virtual void AssignTransform(ReadOnlySpan<float> rows) => AssignRows(rows);
    /// <summary>Virtual 90: child pose refresh (only HLODs have children).</summary>
    internal virtual void RefreshChildren() { }
    internal virtual void Destroy() { }

    // 941C20: copy, test the raw identity words, invalidate cached bounds.
    protected void AssignRows(ReadOnlySpan<float> rows)
    {
        rows[..12].CopyTo(Rows);
        IdentityTransform = IsIdentity();
        Flags &= ~BoundsCurrent;
    }
    private bool IsIdentity()
    {
        uint different = 0;
        for (int i = 0; i < 12; i++) different |= BitConverter.SingleToUInt32Bits(Rows[i]) ^ (i is 0 or 5 or 10 ? 0x3f800000u : 0);
        return different == 0;
    }
    // 941D90: a stale ancestor makes the root refresh its child poses first.
    internal void RefreshTransform()
    {
        var parent = Parent;
        if (parent is null) return;
        bool stale = (parent.Flags & PosesStale) != 0;
        while (parent.Parent is not null)
        {
            stale |= (parent.Flags & PosesStale) != 0;
            parent = parent.Parent;
        }
        if (!stale) return;
        parent.RefreshChildren();
        IdentityTransform = IsIdentity();
    }
    internal Matrix4x4 Matrix => new(Rows[0], Rows[4], Rows[8], 0, Rows[1], Rows[5], Rows[9], 0, Rows[2], Rows[6], Rows[10], 0, Rows[3], Rows[7], Rows[11], 1);
    // 9421B0 inherited world bounds: local box and sphere through the refreshed transform.
    protected void RecalculateWorldBounds()
    {
        var box = LocalBox();
        var sphere = LocalSphere();
        RefreshTransform();
        RefreshTransform();
        var bounds = EnbCurrentRetailRenderBoundsStorage.Calculate(Matrix, sphere, box.Center, box.Extent, EnbRetailLinePrecision.Single24, true);
        WorldSphere = bounds.Sphere; WorldBoxCenter = bounds.BoxCenter; WorldBoxExtent = bounds.BoxExtent;
        Flags |= BoundsCurrent;
    }
}

/// <summary>A downward SHADOW sample query (46E120) and its nearest-hit result.</summary>
public sealed class EnbRetailShadowRay
{
    public uint CollisionType = 1;
    public Vector3 Start, End, Delta, Direction;
    public float Length;
    public bool StartBad, ComputeContact = true;
    public float Fraction = 1;
    public Vector3 Normal, Contact;
    public uint Surface;
    public EnbRetailShadowObject? Hit;
}

/// <summary>Shared mesh model (retail MeshModel): geometry, compiled descriptor and material owner.</summary>
internal sealed class EnbRetailShadowModel(EnbW3dMesh source, EnbW3dRenderMesh mesh, EnbCurrentRetailMeshModelOwner owner)
{
    public EnbW3dMesh Source { get; } = source;
    public EnbW3dRenderMesh Mesh { get; } = mesh;
    public EnbCurrentRetailMeshModelOwner Owner { get; } = owner;
    public string? NameOverride;
    public int References = 1;
    private EnbRetailTerrainShadowMeshCast? cast;
    public EnbCurrentRetailMeshGeometryState Geometry => Owner.Geometry!;
    // 93ED30 -> 92A700: the model's full registered name.
    public string Name => NameOverride ?? EnbW3dRenderObjectNameSemantics.RegisteredMeshName(Mesh);
    public uint FacingFlags => Geometry.Flags & 0xa00;
    public EnbRetailTerrainShadowMeshCast Cast => cast ??= new(Geometry, Source.Triangles.Select(t => unchecked((byte)t.Attributes)).ToArray());
    public void GeometryChanged() => cast = null;
    public void Release() { if (--References == 0) Owner.Dispose(); }
}

/// <summary>Mesh RenderObj (vtable B111A4).</summary>
internal sealed class EnbRetailShadowMesh : EnbRetailShadowObject
{
    private readonly EnbRetailTerrainShadowObjectFactory factory;
    internal EnbRetailShadowModel Model;
    internal EnbRetailShadowMesh(EnbRetailTerrainShadowObjectFactory factory, EnbRetailShadowModel model) { this.factory = factory; Model = model; }
    // 93EA50 copy: the prototype's transform-base words and its shared model.
    internal EnbRetailShadowMesh(EnbRetailShadowMesh prototype) : this(prototype.factory, prototype.Model)
    {
        Flags = prototype.Flags; prototype.Rows.CopyTo(Rows); IdentityTransform = prototype.IdentityTransform;
        WorldSphere = prototype.WorldSphere; WorldBoxCenter = prototype.WorldBoxCenter; WorldBoxExtent = prototype.WorldBoxExtent;
        Model.References++;
    }
    internal override int ClassId => 0;
    // 940CE0/940D10: the model's local sphere and box.
    internal override Vector4 LocalSphere() { var g = Model.Geometry; return new(g.Center, g.Radius); }
    internal override (Vector3 Center, Vector3 Extent) LocalBox()
    {
        Vector3 minimum = Model.Geometry.Minimum, maximum = Model.Geometry.Maximum;
        return (new((minimum.X + maximum.X) * .5f, (maximum.Y + minimum.Y) * .5f, (maximum.Z + minimum.Z) * .5f),
            new((maximum.X - minimum.X) * .5f, (maximum.Y - minimum.Y) * .5f, (maximum.Z - minimum.Z) * .5f));
    }
    // 463370 -> 940EB0: world sphere and box from the model bounds; facing models take a cube
    // around the world sphere.
    internal override (Vector3 Center, Vector3 Extent) WorldBox()
    {
        if ((Flags & BoundsCurrent) != 0) return (WorldBoxCenter, WorldBoxExtent);
        var local = LocalSphere();
        RefreshTransform();
        var m = Rows; var c = new Vector3(local.X, local.Y, local.Z);
        WorldSphere = new(((m[2] * c.Z + m[1] * c.Y) + m[0] * c.X) + m[3],
            ((m[4] * c.X + m[6] * c.Z) + m[5] * c.Y) + m[7],
            ((m[8] * c.X + m[10] * c.Z) + m[9] * c.Y) + m[11], local.W);
        if (Model.FacingFlags != 0)
        {
            WorldBoxCenter = new(WorldSphere.X, WorldSphere.Y, WorldSphere.Z);
            WorldBoxExtent = new(WorldSphere.W);
        }
        else
        {
            var (center, extent) = LocalBox();
            RefreshTransform();
            (WorldBoxCenter, WorldBoxExtent) = EnbRetailTerrainShadowObjectFactory.TransformBox(Rows, center, extent);
        }
        Flags |= BoundsCurrent;
        return (WorldBoxCenter, WorldBoxExtent);
    }
    // 940530: class/visibility gates, then the model cast with the contact back in world space.
    internal override bool Cast(EnbRetailShadowRay ray)
    {
        if ((ray.CollisionType & Flags & 0xff) == 0 || (Flags & VisibleBit) == 0 || ray.StartBad) return false;
        RefreshTransform();
        float fraction = ray.Fraction;
        if (!Model.Cast.Cast(Rows, ray.Start, ray.End, ray.Direction, ref fraction, out var normal, out uint surface, out var contact))
            return false;
        ray.Fraction = fraction; ray.Normal = normal; ray.Surface = surface; ray.Hit = this;
        if (ray.ComputeContact) ray.Contact = contact;
        return true;
    }
    // 93EDD0: a shared model is copied before its vectors are separated and scaled.
    internal override void Scale(float scale)
    {
        if (float.IsNaN(scale) || scale == 1f) return;
        if (Model.References != 1)
        {
            var copy = factory.CopyModel(Model);
            Model.Release(); Model = copy;
        }
        Model.Geometry.SeparateVectorStorage();
        factory.ScaleGeometry(Model, scale);
        Flags &= ~BoundsCurrent;
        (Parent as EnbRetailShadowHlod)?.RefreshLocalBounds();
    }
    internal override void Destroy() => Model.Release();
}

/// <summary>NULL RenderObj (vtable B11388): fixed 0.1 local bounds, never hit.</summary>
internal sealed class EnbRetailShadowNull : EnbRetailShadowObject
{
    internal EnbRetailShadowNull(string name) { Name = name; }
    internal override int ClassId => 0x16;
    internal override Vector4 LocalSphere() => new(0, 0, 0, 0.1f);
    internal override (Vector3 Center, Vector3 Extent) LocalBox() => (Vector3.Zero, new(0.1f));
    internal override (Vector3 Center, Vector3 Extent) WorldBox()
    {
        if ((Flags & BoundsCurrent) == 0) RecalculateWorldBounds();
        return (WorldBoxCenter, WorldBoxExtent);
    }
    internal override bool Cast(EnbRetailShadowRay ray) => false; // 40B924
    internal override void Scale(float scale) { } // 40654B -> 462FB0
}

/// <summary>A pivot hierarchy copied from a registered W3D hierarchy (9C5210, 00988C60) or the
/// default identity root (00988AF0): per pivot the authored local rows, the evaluated pose rows
/// and visibility. Retail keeps a parent word holding the parent index plus one.</summary>
internal sealed class EnbRetailShadowHierarchy
{
    private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
    private readonly byte[] names;   // retail 16-byte name fields
    private readonly uint[] indexWords, parentWords;
    private readonly float[] locals, poses;
    private readonly bool[] visible;
    public int Count { get; }

    private EnbRetailShadowHierarchy(int count)
    {
        Count = count;
        int records = Math.Max(1, count);
        names = new byte[records * 16]; indexWords = new uint[records]; parentWords = new uint[records];
        locals = new float[records * 12]; poses = new float[records * 12]; visible = new bool[records];
        for (int i = 0; i < records; i++)
        {
            Identity.CopyTo(locals, i * 12); Identity.CopyTo(poses, i * 12); visible[i] = true;
        }
    }

    public static EnbRetailShadowHierarchy From(EnbW3dRenderHierarchy hierarchy)
    {
        var result = new EnbRetailShadowHierarchy(hierarchy.Pivots.Count);
        for (int index = 0; index < hierarchy.Pivots.Count; index++)
        {
            var pivot = hierarchy.Pivots[index];
            byte[] name = Encoding.ASCII.GetBytes(pivot.Name);
            name.AsSpan(0, Math.Min(name.Length, 16)).CopyTo(result.names.AsSpan(index * 16));
            result.indexWords[index] = (uint)index;
            result.parentWords[index] = pivot.IsRoot ? 0 : (uint)pivot.ParentIndex + 1;
            EnbRetailAffineRows.Rows(pivot.LocalTransform, result.locals.AsSpan(index * 12, 12));
        }
        return result;
    }
    public static EnbRetailShadowHierarchy Default()
    {
        var result = new EnbRetailShadowHierarchy(1);
        "RootTransform"u8.CopyTo(result.names);
        return result;
    }
    // The name as retail's C-string compare reads it: a full 16-byte name runs on into the pivot
    // index word and then the parent word.
    public string RecordName(int index)
    {
        Span<byte> record = stackalloc byte[24];
        names.AsSpan(index * 16, 16).CopyTo(record);
        BinaryPrimitives.WriteUInt32LittleEndian(record[16..], indexWords[index]);
        BinaryPrimitives.WriteUInt32LittleEndian(record[20..], parentWords[index]);
        int length = record.IndexOf((byte)0);
        return Encoding.ASCII.GetString(record[..(length < 0 ? 24 : length)]);
    }
    // Members may name pivots of an unregistered skeleton while the default root stands in;
    // retail then reads past its records. Such pivots stay at identity and visible.
    private bool Contains(int pivot) => (uint)pivot < (uint)Math.Max(1, Count);
    public ReadOnlySpan<float> Pose(int pivot) => Contains(pivot) ? poses.AsSpan(pivot * 12, 12) : Identity;
    public bool Visible(int pivot) => !Contains(pivot) || visible[pivot];
    public float[] PoseRows(int pivot) => Pose(pivot).ToArray();
    // 009895B0: the root takes the supplied rows; every later pivot is its parent's pose times
    // its local rows.
    public void Evaluate(ReadOnlySpan<float> root)
    {
        root[..12].CopyTo(poses); visible[0] = true;
        for (int index = 1; index < Count; index++)
        {
            int parent = checked((int)(parentWords[index] - 1));
            EnbRetailAffineRows.Multiply(poses.AsSpan(parent * 12, 12), locals.AsSpan(index * 12, 12), poses.AsSpan(index * 12, 12));
            visible[index] = true;
        }
    }
    // 0098A0A0: the translation of each local transform.
    public void Scale(float scale)
    {
        if (float.IsNaN(scale) || scale == 1f) return;
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        for (int index = 0; index < Count; index++)
            foreach (int word in new[] { 3, 7, 11 }) locals[index * 12 + word] = (float)((R)locals[index * 12 + word] * scale);
    }
}

/// <summary>HLOD RenderObj (vtable B1327C): retained LOD levels and aggregate members on hierarchy pivots.</summary>
internal sealed class EnbRetailShadowHlod : EnbRetailShadowObject, IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>
{
    internal readonly record struct Member(EnbRetailShadowObject Child, int Pivot);
    internal readonly EnbRetailShadowHierarchy Hierarchy;
    internal readonly List<Member>[] Levels;
    internal readonly List<Member> Aggregates = [];
    private int boundingBoxIndex = -1;
    private Vector4 localSphere;
    private Vector3 localBoxCenter, localBoxExtent;
    internal byte HierarchyUpdated;

    internal EnbRetailShadowHlod(string name, EnbRetailShadowHierarchy hierarchy, int levels)
    {
        Name = name; Hierarchy = hierarchy;
        Levels = new List<Member>[levels];
        for (int i = 0; i < levels; i++) Levels[i] = [];
    }
    internal override int ClassId => 0x19;
    internal IEnumerable<Member> AllMembers => Levels.SelectMany(level => level).Concat(Aggregates);
    internal int MemberCount => Levels.Sum(level => level.Count) + Aggregates.Count;
    internal EnbRetailShadowObject MemberAt(int index)
    {
        foreach (var level in Levels) { if (index < level.Count) return level[index].Child; index -= level.Count; }
        return Aggregates[index].Child;
    }
    // A malformed source without levels has no last level to query.
    private List<Member> LastLevel => Levels.Length == 0 ? [] : Levels[^1];

    // 98CF90 without a special box: the stored local box. 98D0B0: a sphere around it.
    internal override (Vector3 Center, Vector3 Extent) LocalBox() => (localBoxCenter, localBoxExtent);
    internal override Vector4 LocalSphere()
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var (center, extent) = LocalBox();
        R squared = (R)extent.Y * extent.Y;
        squared += (R)extent.Z * extent.Z;
        squared += (R)extent.X * extent.X;
        float spilled = (float)squared;
        uint x = EnbRetailX87Load.LoadedFloatWord(BitConverter.SingleToUInt32Bits(center.X));
        return new(BitConverter.UInt32BitsToSingle(x), center.Y, center.Z, (float)R.Sqrt(spilled));
    }
    // 98D1D0 without a special box: the inherited cached world box.
    internal override (Vector3 Center, Vector3 Extent) WorldBox()
    {
        if ((Flags & BoundsCurrent) == 0) RecalculateWorldBounds();
        return (WorldBoxCenter, WorldBoxExtent);
    }
    // 98E680: the last retained level, then the aggregate members, in stored order.
    internal override bool Cast(EnbRetailShadowRay ray)
    {
        if ((Flags & PosesStale) != 0) RefreshChildren();
        bool hit = false;
        foreach (var member in LastLevel) hit |= member.Child.Cast(ray);
        foreach (var member in Aggregates) hit |= member.Child.Cast(ray);
        return hit;
    }
    // 98DEE0
    internal override void AssignTransform(ReadOnlySpan<float> rows)
    {
        AssignRows(rows);
        HierarchyUpdated = 0;
        Flags |= PosesStale;
    }
    // 98F090: pose the hierarchy at the world transform, then place, show or hide and refresh
    // each member in level then aggregate order.
    internal override void RefreshChildren()
    {
        Hierarchy.Evaluate(Rows);
        HierarchyUpdated = 1;
        foreach (var member in AllMembers.ToArray())
        {
            member.Child.AssignTransform(Hierarchy.Pose(member.Pivot));
            SetHidden(member.Child, !Hierarchy.Visible(member.Pivot));
            member.Child.RefreshChildren();
        }
        Flags &= ~PosesStale;
    }
    internal static void SetHidden(EnbRetailShadowObject child, bool hidden)
        => child.Flags = hidden ? child.Flags & ~VisibleBit : child.Flags | VisibleBit;
    // 98EF60
    internal override void Scale(float scale)
    {
        if (float.IsNaN(scale) || scale == 1f) return;
        foreach (var member in AllMembers.ToArray()) member.Child.Scale(scale);
        Hierarchy.Scale(scale);
        HierarchyUpdated = 0;
        (Parent as EnbRetailShadowHlod)?.RefreshLocalBounds();
    }
    // 98F1D0: local bounds over every member at its local pivot pose.
    internal void RefreshLocalBounds()
    {
        var runtime = new EnbCurrentRetailHlodBoundsRuntime { BoundingBoxIndex = boundingBoxIndex };
        runtime.Refresh(this);
        boundingBoxIndex = runtime.BoundingBoxIndex;
        var bounds = runtime.Bounds;
        localSphere = bounds.Sphere; localBoxCenter = bounds.BoxCenter; localBoxExtent = bounds.BoxExtent;
    }
    int IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.LastLevelMemberCount => LastLevel.Count;
    EnbRetailShadowObject IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.LastLevelMember(int index) => LastLevel[index].Child;
    int IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.ClassId(EnbRetailShadowObject child) => child.ClassId;
    string IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.Name(EnbRetailShadowObject child) => child.Name;
    int IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.ChildCount => MemberCount;
    void IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.EvaluateDefaultHierarchy()
        => Hierarchy.Evaluate([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0]);
    EnbRetailShadowObject IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.RetainedChild(int index) => MemberAt(index);
    // 98E3F0: the first matching member supplies its pivot.
    int IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.ChildPivot(EnbRetailShadowObject child)
    {
        foreach (var member in AllMembers) if (ReferenceEquals(member.Child, child)) return member.Pivot;
        return 0;
    }
    ReadOnlyMemory<float> IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.PivotMatrix(int pivot) => Hierarchy.PoseRows(pivot);
    Vector4 IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.LocalSphere(EnbRetailShadowObject child) => child.LocalSphere();
    (Vector3 Center, Vector3 Extent) IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.LocalBox(EnbRetailShadowObject child) => child.LocalBox();
    void IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.Release(EnbRetailShadowObject child) { }
    uint IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.Flags { get => Flags; set => Flags = value; }
    byte IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.HierarchyUpdated { get => HierarchyUpdated; set => HierarchyUpdated = value; }
    void IEnbCurrentRetailHlodBoundsProvider<EnbRetailShadowObject>.NotifyParentBounds() => (Parent as EnbRetailShadowHlod)?.RefreshLocalBounds();
    internal override void Destroy()
    {
        foreach (var member in AllMembers.ToArray()) { member.Child.Parent = null; EnbRetailTerrainShadowObjectFactory.Release(member.Child); }
        foreach (var level in Levels) level.Clear();
        Aggregates.Clear();
    }
}

/// <summary>Retained private mesh/NULL/HLOD factory for terrain prelight (retail 46E6AB onwards).
/// Uses the caller's first-registration cache and mapper/texture context; hierarchy lookup never
/// performs an independent file load.</summary>
public sealed class EnbRetailTerrainShadowObjectFactory : IDisposable
{
    private readonly EnbMixResourceCatalog catalog;
    private readonly EnbRetailFileResolver? files;
    private readonly EnbRetailW3dFileResolver? prototypeFiles;
    private readonly EnbW3dRenderResourceCache cache;
    private readonly uint clock;
    private readonly EnbRetailCrtRandomSource geometryRandom;
    private readonly Dictionary<long, EnbRetailShadowMesh> prototypes = [];
    private readonly List<EnbRetailShadowModel> models = [];
    private readonly HashSet<string> creating = new(StringComparer.OrdinalIgnoreCase);
    private int recursiveFailures;
    private bool disposed;

    public EnbRetailTerrainShadowObjectFactory(EnbMixResourceCatalog catalog, EnbW3dRenderResourceCache cache,
        uint constructionClock, uint crtThreadSeed, EnbRetailFileResolver? files = null)
    {
        this.catalog = catalog; this.cache = cache; clock = constructionClock; this.files = files;
        prototypeFiles = files is null ? null : new(files, catalog);
        geometryRandom = new() { State = crtThreadSeed };
    }
    public uint CrtThreadSeed => geometryRandom.State;

    public EnbRetailShadowObject? Create(string name, out string? diagnostic)
    {
        // Contain malformed recursive graphs locally instead of overflowing the client stack.
        if (!creating.Add(name)) { recursiveFailures = unchecked(recursiveFailures + 1); diagnostic = "Recursive SHADOW prototype: " + name; return null; }
        try
        {
            var owner = CreateCore(name, out diagnostic);
            if (owner is not null)
            {
                try { Postprocess(owner); }
                catch { Release(owner); throw; }
            }
            return owner;
        }
        finally { creating.Remove(name); }
    }
    // 46E713 -> 48B0F0, then 48E9D0. Every retained HLOD level and aggregate is visited.
    private void Postprocess(EnbRetailShadowObject owner)
    {
        switch (owner)
        {
            case EnbRetailShadowMesh mesh:
                var geometry = mesh.Model.Geometry; var previousTree = geometry.Tree;
                var state = new EnbCurrentRetailRenderObjectState(mesh.Flags);
                uint seed = geometryRandom.State;
                try { geometry.PostprocessConfiguredMesh(mesh.Model.Mesh.CurrentRetailRenderPassCount, ref state, ref seed); }
                finally { geometryRandom.State = seed; }
                mesh.Flags = state.Flags;
                if (!ReferenceEquals(previousTree, geometry.Tree)) mesh.Model.GeometryChanged();
                break;
            case EnbRetailShadowHlod hlod:
                for (int index = 0; index < hlod.MemberCount; index++) Postprocess(hlod.MemberAt(index));
                break;
        }
    }
    private EnbRetailShadowObject? CreateCore(string name, out string? diagnostic)
    {
        ObjectDisposedException.ThrowIf(disposed, this); diagnostic = null;
        if (string.Equals(name, "NULL", StringComparison.OrdinalIgnoreCase)) return new EnbRetailShadowNull("NULL");
        if (!cache.TryFindPrototypeSource(name, out var source))
        {
            bool resolved = files is null
                ? EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(catalog, name, out var loaded, out diagnostic)
                : prototypeFiles!.TryResolveLazyFile(name, out loaded, out diagnostic);
            if (!resolved) return null;
            foreach (var publication in loaded.AcquisitionPublications.Count == 0 ? [loaded] : loaded.AcquisitionPublications)
                if (!cache.TryLoadFile(publication, out _, out diagnostic, clock, geometryRandom)) return null;
            if (!cache.TryFindPrototypeSource(name, out source)) return null;
        }
        if (source.AggregatePrototype is { Aggregate: { } aggregate }) return CreateAggregate(aggregate, out diagnostic);
        if (!cache.TryGetRegisteredPrototype(source, out var compiled)) return null;
        var entry = source.RegisteredPrototype!.Value;
        return entry.Kind switch
        {
            EnbW3dPrototypeKind.Mesh => CreateMesh(source, compiled.Meshes[entry.Ordinal]),
            EnbW3dPrototypeKind.NullObject => new EnbRetailShadowNull(source.Model.NullObjects[entry.Ordinal].Name),
            EnbW3dPrototypeKind.HierarchicalLod => CreateHlod(source.Model.HierarchicalLods[entry.Ordinal]),
            _ => throw new NotSupportedException($"SHADOW factory has no proven binding for {entry.Kind}."),
        };
    }

    // 93E920 prototype per registration, then a 93EA50 copy per request.
    private EnbRetailShadowMesh CreateMesh(EnbResolvedW3dResource source, EnbW3dRenderMesh mesh)
    {
        if (source.RegisteredPrototype is not { Kind: EnbW3dPrototypeKind.Mesh } registered || source.PrototypeRegistration == 0)
            throw new ArgumentException("A registered mesh source is required.", nameof(source));
        if (registered.Ordinal != mesh.Ordinal) throw new ArgumentException("Registered mesh ordinal differs from compiled mesh.", nameof(mesh));
        if (!prototypes.TryGetValue(source.PrototypeRegistration, out var prototype))
        {
            var authored = source.Model.Meshes[registered.Ordinal];
            uint seed = geometryRandom.State;
            EnbCurrentRetailMeshGeometryState geometry;
            // 48E9D0 mutates the registered model's tree. Keep that geometry shared here;
            // 93EDD0 separates it before a scaled instance writes.
            try
            {
                geometry = mesh.CurrentRetailLoadedGeometry is { } loaded ? loaded :
                    EnbCurrentRetailMeshGeometryLoader.Load(authored, 1, ref seed, payload => EnbCurrentRetailMeshAabTree.LoadOwnedAuthored(payload.ToArray()));
            }
            finally { geometryRandom.State = seed; }
            if (!cache.TryCreateModelOwner(mesh, mesh.CurrentRetailLoadedMaterials, clock, out var owner, out var reason, geometry: geometry))
                throw new InvalidOperationException(reason);
            var model = new EnbRetailShadowModel(authored, mesh, owner);
            models.Add(model);
            prototype = new EnbRetailShadowMesh(this, model);
            ApplyLoadedFlags(prototype);
            prototypes.Add(source.PrototypeRegistration, prototype);
        }
        return new EnbRetailShadowMesh(prototype);
    }
    // 00940444..00940527 successful-load tail: collision class mask, 0200 and the sort bit 4000.
    private static void ApplyLoadedFlags(EnbRetailShadowMesh mesh)
    {
        var model = mesh.Model; uint attributes = model.Source.Attributes;
        uint flags = new EnbCurrentRetailRenderObjectState(mesh.Flags).WithLowByteClassMask(((attributes >> 4) & 255) << 1).Flags;
        flags = (attributes & 0x1000) == 0 ? flags | 0x200 : flags & ~0x200u;
        var pass = model.Mesh.MaterialPasses.FirstOrDefault(value => value.Ordinal == 0);
        bool sorted = model.Source.CurrentRetailGeometrySortRequested || ((model.Geometry.Flags & 0x10) != 0);
        if (pass?.ShaderCardinality == EnbW3dRenderBindingCardinality.PerTriangle)
        {
            int triangles = model.Geometry.TriangleIndices.Length / 3;
            for (int triangle = 0; triangle < triangles; triangle++)
                sorted |= (pass.Shaders[triangle].FixedFunction.PackedRetailState & 0x400e0) != 0;
        }
        else sorted |= ((pass?.CurrentRetailUniformShader ?? 0) & 0x400e0) != 0;
        mesh.Flags = sorted ? flags | 0x4000 : flags & ~0x4000u;
    }
    // 93EDD0 model copy (copy constructor with its own material owner and descriptor).
    internal EnbRetailShadowModel CopyModel(EnbRetailShadowModel old)
    {
        var owner = old.Owner.CloneRenderObject();
        try
        {
            owner.EnsureUniqueModel(clock, force: true);
            if (!EnbW3dRenderResourceCompiler.TryCopyModelDescriptor(old.Mesh, old.Owner, owner, out var passes, out var reason))
                throw new InvalidOperationException(reason);
            var model = new EnbRetailShadowModel(old.Source, old.Mesh.Copy().SetMaterialPasses(passes), owner) { NameOverride = old.NameOverride };
            models.Add(model);
            return model;
        }
        catch { owner.Dispose(); throw; }
    }
    internal void ScaleGeometry(EnbRetailShadowModel model, float scale)
    {
        uint seed = geometryRandom.State;
        try { model.Geometry.ScaleSeparatedVectors(new Vector3(scale), ref seed); }
        finally { geometryRandom.State = seed; }
        model.GeometryChanged();
    }

    // 98C420 construction over the decoded 0700 source.
    private EnbRetailShadowHlod CreateHlod(EnbW3dHierarchicalLod source)
    {
        // 9C5210: an empty hierarchy name, or one not registered, builds the default root.
        var hierarchy = source.HierarchyName.Length != 0 && cache.TryFindHierarchy(source.HierarchyName, out var registered)
            ? EnbRetailShadowHierarchy.From(registered) : EnbRetailShadowHierarchy.Default();
        var owner = new EnbRetailShadowHlod(source.Name, hierarchy, source.Levels.Count);
        try
        {
            for (int level = 0; level < source.Levels.Count; level++)
                foreach (var member in source.Levels[level].Members)
                {
                    var child = Create(member.Name, out _);
                    if (child is null) continue;
                    int pivot = unchecked((int)member.ReferenceIndex);
                    child.Parent = owner;
                    child.AssignTransform(hierarchy.Pose(pivot));
                    owner.Levels[level].Add(new(child, pivot));
                }
            if (source.Aggregate is { } aggregate)
                foreach (var member in aggregate.Members)
                {
                    var child = Create(member.Name, out _);
                    if (child is null) continue;
                    AddAggregateChild(owner, child, member.ReferenceIndex);
                    Release(child);
                }
            owner.Flags |= EnbRetailShadowObject.PosesStale;
            owner.RefreshLocalBounds();
            return owner;
        }
        catch { Release(owner); throw; }
    }
    // 98E4B0: a pivot outside the hierarchy adds nothing.
    private static bool AddAggregateChild(EnbRetailShadowHlod owner, EnbRetailShadowObject child, uint pivot)
    {
        if (unchecked((int)pivot) < 0 || unchecked((int)pivot) >= owner.Hierarchy.Count) return false;
        child.References++; child.Parent = owner;
        EnbRetailShadowHlod.SetHidden(child, !owner.Hierarchy.Visible((int)pivot));
        owner.Aggregates.Add(new(child, (int)pivot));
        owner.RefreshLocalBounds();
        owner.Flags |= EnbRetailShadowObject.PosesStale; owner.HierarchyUpdated = 0;
        return true;
    }
    private EnbRetailShadowObject? CreateAggregate(EnbCurrentRetailW3dAggregateDefinition aggregate, out string? diagnostic)
    {
        var owner = CreateAggregateObject(aggregate.BaseModelName, out diagnostic); if (owner is null) return null;
        try
        {
            foreach (var attachment in aggregate.Attachments)
            {
                var child = CreateAggregateObject(attachment.SubobjectName, out _); if (child is null) continue;
                try
                {
                    if (owner is EnbRetailShadowHlod hlod)
                    {
                        int pivot = 0;
                        for (int index = 0; index < hlod.Hierarchy.Count; index++)
                            if (string.Equals(hlod.Hierarchy.RecordName(index), attachment.BoneName, StringComparison.OrdinalIgnoreCase)) { pivot = index; break; }
                        AddAggregateChild(hlod, child, (uint)pivot);
                    }
                    // Mesh/NULL +80 resolves pivot zero then +84 returns zero without retaining.
                }
                finally { Release(child); }
            }
            if (owner is EnbRetailShadowMesh mesh) mesh.Model.NameOverride = aggregate.Name;
            else if (owner is EnbRetailShadowHlod) owner.Name = aggregate.Name;
            owner.Flags = aggregate.ForceSubObjectLod ? owner.Flags | 0x10000 : owner.Flags & 0xfffeffff;
            return owner;
        }
        catch { Release(owner); throw; }
    }
    private EnbRetailShadowObject? CreateAggregateObject(string name, out string? diagnostic)
    {
        int previousCycles = recursiveFailures;
        var owner = Create(name, out diagnostic); if (owner is not null || prototypeFiles is null || recursiveFailures != previousCycles) return owner;
        if (!prototypeFiles.TryResolveAggregateFile(name, out var source, out var loadDiagnostic)) { diagnostic ??= loadDiagnostic; return null; }
        if (!cache.TryLoadFile(source, out _, out diagnostic, clock, geometryRandom)) return null;
        return Create(name, out diagnostic);
    }
    public void Scale(EnbRetailShadowObject owner, float scale) => owner.Scale(scale);
    /// <summary>46E6AB..46E8C2: enumerate consecutive SHADOW%d sections, create each present
    /// Asset, then apply Scale and the column-filled M.</summary>
    public EnbRetailShadowObject[] CreateFromIni(EnbRetailTerrainOwnedIni ini, CancellationToken cancellation = default)
    {
        var retained = new List<EnbRetailShadowObject>();
        try
        {
            for (int index = 0; ; index = unchecked(index + 1))
            {
                cancellation.ThrowIfCancellationRequested();
                string section = "SHADOW" + index.ToString(CultureInfo.InvariantCulture);
                if (!ini.HasSection(section)) break;
                var owner = Create(Encoding.Latin1.GetString(ini.ReadText(section, "Asset", 128)), out _);
                if (owner is null) continue;
                try
                {
                    Scale(owner, ini.ReadFloat(section, "Scale", 1));
                    AssignTransform(owner, EnbRetailTerrainShadowMatrixParser.Parse(ini.ReadText(section, "M", 256)));
                    retained.Add(owner);
                }
                catch { Release(owner); throw; }
            }
            return retained.ToArray();
        }
        catch { foreach (var owner in retained) Release(owner); throw; }
    }
    public void AssignTransform(EnbRetailShadowObject owner, Matrix4x4 matrix)
        => owner.AssignTransform([matrix.M11, matrix.M21, matrix.M31, matrix.M41, matrix.M12, matrix.M22, matrix.M32, matrix.M42, matrix.M13, matrix.M23, matrix.M33, matrix.M43]);
    public static void Release(EnbRetailShadowObject? owner)
    {
        if (owner is null || --owner.References != 0) return;
        owner.Destroy();
    }
    void IDisposable.Dispose() => Dispose();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var model in models) if (model.References > 0) { model.References = 0; model.Owner.Dispose(); }
        models.Clear(); prototypes.Clear();
    }

    /// <summary>The mesh a decoration or its shadow draws (47C580): the first mesh, depth first,
    /// whose name holds ".d"/".D" (decoration) or ".s"/".S" (shadow).</summary>
    public static EnbRetailShadowObject? SelectMesh(EnbRetailShadowObject? owner, bool shadow)
    {
        switch (owner)
        {
            case null: return null;
            case EnbRetailShadowMesh mesh:
                string name = mesh.Model.Name; char lower = shadow ? 's' : 'd', upper = char.ToUpperInvariant(lower);
                for (int at = name.IndexOf('.'); at >= 0 && at + 1 < name.Length; at = name.IndexOf('.', at + 1))
                    if (name[at + 1] == lower || name[at + 1] == upper) { mesh.References++; return mesh; }
                return null;
            case EnbRetailShadowHlod hlod:
                for (int index = 0; index < hlod.MemberCount; index++)
                    if (SelectMesh(hlod.MemberAt(index), shadow) is { } found) return found;
                return null;
            default: return null;
        }
    }
    internal static EnbW3dRenderMesh Descriptor(EnbRetailShadowObject mesh) => ((EnbRetailShadowMesh)mesh).Model.Mesh;
    internal static EnbCurrentRetailMeshModelOwner ModelOwner(EnbRetailShadowObject mesh) => ((EnbRetailShadowMesh)mesh).Model.Owner;

    // 8C2F30: center rows accumulate from the translation, extents from |e*m|.
    internal static (Vector3 Center, Vector3 Extent) TransformBox(ReadOnlySpan<float> m, Vector3 center, Vector3 extent)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Vector3 worldCenter = default, worldExtent = default;
        for (int row = 0; row < 3; row++)
        {
            float wc = m[row * 4 + 3], we = 0;
            for (int column = 0; column < 3; column++)
            {
                float factor = m[row * 4 + column];
                wc = (float)((R)center[column] * factor + wc);
                we = (float)((R)MathF.Abs((float)((R)extent[column] * factor)) + we);
            }
            worldCenter[row] = wc; worldExtent[row] = we;
        }
        return (worldCenter, worldExtent);
    }

    /// <summary>The 46E120 sample query: collision type 1 from (x, y, top) down to the sample,
    /// nearest hit with the contact point. Returns the contact height when the fraction is below 1.</summary>
    public static float? CastDown(EnbRetailShadowObject owner, Vector3 position, float top)
    {
        float drop = position.Z - top;
        double squareRegister = (double)drop * drop;
        float square = (float)squareRegister;
        Vector3 direction = new(0, 0, drop);
        if (squareRegister != 0)
        {
            float inverse = EnbRetailFloat.ReciprocalSquareRoot(square);
            direction = new(0 * inverse, 0 * inverse, drop * inverse);
        }
        var ray = new EnbRetailShadowRay
        {
            Start = new(position.X, position.Y, top), End = position, Delta = new(0, 0, drop),
            Direction = direction, Length = (float)Math.Sqrt(square),
        };
        owner.Cast(ray);
        return ray.Fraction >= 1f ? null : ray.Contact.Z;
    }
}
