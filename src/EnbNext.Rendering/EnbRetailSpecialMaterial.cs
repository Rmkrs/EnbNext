using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public readonly record struct EnbRetailSpecialMaterialCapabilities(uint PixelShaderVersion,
    bool DotProduct3, bool ModulateAlphaAddColor)
{
    // A0B9B0 checks the major byte for equality, not >= 1. A0C3B0 always
    // admits selector 3 mode 1, independently of these device capabilities.
    public int Mode(uint selector) => selector switch
    {
        4 => (PixelShaderVersion & 0xff00) == 0x100 && (PixelShaderVersion & 255) >= 1 ? 3 : DotProduct3 ? 2 : 1,
        3 => 1,
        1 or 2 or 5 => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(selector))
    };
}

public sealed record EnbRetailSpecialMaterialGeometry(ImmutableArray<Vector3> Positions,
    ImmutableArray<Vector3> Normals, ImmutableArray<Vector2> Uv,
    ImmutableArray<Vector3> Auxiliary0, ImmutableArray<Vector3> Auxiliary1,
    ImmutableArray<Vector3> Auxiliary2, ImmutableArray<ushort> Indices)
{
    public static bool TryCreate(EnbCurrentRetailSpecialMaterialMember member,
        out EnbRetailSpecialMaterialGeometry geometry, out string? diagnostic)
    {
        geometry = null!; diagnostic = null;
        // Native 94F330/9512C0 allocate with truncated 16-bit counts, then fill
        // with full counts. Contain that overrun locally, never reject the FILE.
        if (member.VertexCount > ushort.MaxValue || member.TriangleCount * 3UL > ushort.MaxValue)
        { diagnostic = "Special-material member exceeds the native buffer count representation."; return false; }
        if (member.Header.IsEmpty || member.Resource is null)
        { diagnostic = "Special-material member has no initialized header or resource."; return false; }
        int n = (int)member.VertexCount, count = (int)member.TriangleCount * 3;
        var positions = Vectors(0xb43); var normals = Vectors(0xb44);
        var indices = new ushort[count];
        if (member.Streams.TryGetValue(0xb45, out var indexBytes))
            for (int i = 0; i < indexBytes.Length / 2; i++) indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.AsSpan()[(i * 2)..]);
        if (indices.Any(index => index >= n))
        { diagnostic = "Special-material member indexes outside its retained vertex buffer."; return false; }
        var uv = new Vector2[n];
        if (member.Streams.TryGetValue(0xb47, out var uvBytes))
            for (int i = 0; i < uvBytes.Length / 8; i++) uv[i] = new(F(uvBytes, i * 8), F(uvBytes, i * 8 + 4));
        var a = Vectors(0xb49); var b = Vectors(0xb4a); var c = Vectors(0xb4b);
        if (positions.Concat(normals).Concat(a).Concat(b).Concat(c).Any(v => !Finite(v)) ||
            uv.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y)))
        { diagnostic = "Special-material member contains nonfinite vertex inputs."; return false; }
        geometry = new(positions, normals, uv.ToImmutableArray(), a, b, c, indices.ToImmutableArray());
        return true;

        ImmutableArray<Vector3> Vectors(uint id)
        {
            var values = new Vector3[n];
            if (member.Streams.TryGetValue(id, out var bytes))
                for (int i = 0; i < bytes.Length / 12; i++) values[i] = new(F(bytes, i * 12), F(bytes, i * 12 + 4), F(bytes, i * 12 + 8));
            return values.ToImmutableArray();
        }
    }

    // A082D0 passes member+94 as COLOR, not B46's member+3C. The FILE
    // constructor leaves +94 null, so all admitted vertex fills write white.
    public ImmutableArray<byte> Fill(uint selector, int mode)
    {
        if (!(selector is 1 or 2 or 5 && mode == 0 || selector == 3 && mode == 1 || selector == 4 && mode is >= 1 and <= 3))
            throw new ArgumentOutOfRangeException(nameof(mode), "Unadmitted special-material program.");
        int stride = selector == 5 ? 0x1c : selector == 4 && mode > 1 ? 0x48 : 0x24;
        byte[] bytes = new byte[checked(Positions.Length * stride)];
        for (int i = 0; i < Positions.Length; i++)
        {
            int at = i * stride;
            V(at, Positions[i]); V(at + 12, Normals[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 24), uint.MaxValue);
            if (stride > 0x1c) { W(at + 28, Uv[i].X); W(at + 32, Uv[i].Y); }
            if (stride == 0x48) { V(at + 36, Auxiliary0[i]); V(at + 48, Auxiliary1[i]); V(at + 60, Auxiliary2[i]); }
        }
        return bytes.ToImmutableArray();
        void W(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
        void V(int at, Vector3 value) { W(at, value.X); W(at + 4, value.Y); W(at + 8, value.Z); }
    }
    private static float F(ImmutableArray<byte> bytes, int at) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan()[at..]);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

// A09090 shares member ownership and clears every cached delegate. Managed
// immutable definitions take the refcount role; per-instance delegates remain
// independent and are released with the instance, not the FILE registration.
public sealed class EnbRetailSpecialMaterialInstance(EnbCurrentRetailSpecialMaterialDefinition definition) : IDisposable
{
    public EnbCurrentRetailSpecialMaterialDefinition Definition { get; } = definition;
    private readonly Dictionary<int, EnbRetailSpecialMaterialGeometry> _delegates = [];
    public int CachedDelegateCount => _delegates.Count;
    public bool ProjectionPhase { get; private set; }
    public object? RetainedProjection { get; private set; }
    private bool _sawProjectionContext;
    private readonly Dictionary<EnbCurrentRetailSpecialMaterialResource,EnbRetailSpecialMaterialTextureOwner.Reference> _textures=new(ReferenceEqualityComparer.Instance);
    public EnbRetailSpecialMaterialTextureOwner? Textures(EnbCurrentRetailSpecialMaterialResource resource)=>_textures.GetValueOrDefault(resource)?.Owner;
    public void RetainTextures(Func<EnbCurrentRetailSpecialMaterialResource,EnbRetailSpecialMaterialTextureOwner?> resolve)
    {
        foreach(var member in Definition.Members)
            if(member.Resource is {} resource && !_textures.ContainsKey(resource) && resolve(resource) is {} owner)
                _textures.Add(resource,owner.Retain());
    }
    public EnbRetailSpecialMaterialInstance Clone()
    {
        var clone=new EnbRetailSpecialMaterialInstance(Definition);
        foreach(var pair in _textures)clone._textures.Add(pair.Key,pair.Value.Retain());
        return clone;
    }
    public void Dispose(){foreach(var reference in _textures.Values)reference.Dispose();_textures.Clear();_delegates.Clear();}
    public bool TryGetGeometry(int ordinal, out EnbRetailSpecialMaterialGeometry geometry, out string? diagnostic)
    {
        diagnostic = null;
        if (_delegates.TryGetValue(ordinal, out geometry!)) return true;
        if (!EnbRetailSpecialMaterialGeometry.TryCreate(Definition.Members[ordinal], out geometry, out diagnostic)) return false;
        _delegates.Add(ordinal, geometry); return true;
    }
    // A09430: bit8 contexts save the projector and set pending bit80000.
    // The following ordinary context consumes that bit exactly once.
    public void ObserveContext(uint flags, object? projection)
    {
        if ((flags & 8) != 0)
        { _sawProjectionContext = true; RetainedProjection = projection; ProjectionPhase = false; }
        else
        { ProjectionPhase = _sawProjectionContext; _sawProjectionContext = false; }
    }
    public bool AdmitsPass(uint selector, int mode, int pass)
        => selector == 4 && mode == 3 ? pass == (ProjectionPhase ? 1 : 0) :
            pass >= 0 && pass < (selector == 4 && mode == 2 ? 2 : 1);
}
