using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace EnbNext.Assets;

// client.exe A08B70 -> A0A380. The root owns shared members; cloning a
// RenderObj does not reparse these streams or copy its cached draw delegates.
public sealed record EnbCurrentRetailSpecialMaterialDefinition(string Name,
    ImmutableArray<byte> Header, uint DeclaredMemberCount,
    ImmutableArray<EnbCurrentRetailSpecialMaterialMember> Members)
{
    public Vector3 Minimum => Vector(Header.AsSpan(), 0x28);
    public Vector3 Maximum => Vector(Header.AsSpan(), 0x34);
    public Vector3 SphereCenter => Vector(Header.AsSpan(), 0x40);
    public float SphereRadius => Float(Header.AsSpan(), 0x4c);
    public uint Flags => UInt(Header.AsSpan(), 0);
    public uint CollisionMask => ((UInt(Header.AsSpan(), 4) >> 4) & 255) << 1;

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out EnbCurrentRetailSpecialMaterialDefinition definition)
    {
        definition = null!;
        var reader = new Reader(bytes);
        if (!reader.Open() || reader.Id != 0xb00 || reader.End != bytes.Length ||
            !reader.Open() || reader.Id != 0xb01) return false;
        string name = Text(reader.Rest());
        reader.Close();
        // A0A380 checks the first child's ID, but only the second child's size.
        if (!reader.Open() || !reader.Read(0x50, out var header)) return false;
        var retainedHeader = header.ToArray().ToImmutableArray();
        uint count = UInt(header, 0x10);
        reader.Close();
        var members = ImmutableArray.CreateBuilder<EnbCurrentRetailSpecialMaterialMember>();
        while (members.Count < unchecked((int)count) && reader.Open())
        {
            if (reader.Id != 0xb03) members.Add(ReadMember(ref reader));
            reader.Close();
        }
        definition = new(name, retainedHeader, count, members.ToImmutable());
        return true;
    }

    private static EnbCurrentRetailSpecialMaterialMember ReadMember(ref Reader reader)
    {
        // A0E040 returns 2 without closing a short header. Its caller ignores
        // that status, then closes the still-open header. Keep that cursor edge.
        if (!reader.Open() || !reader.Read(0x38, out var header)) return new([], 0, 0, ImmutableDictionary<uint, ImmutableArray<byte>>.Empty, null, false);
        var retainedHeader = header.ToArray().ToImmutableArray();
        uint triangles = UInt(header, 0), vertices = UInt(header, 4);
        reader.Close();
        var streams = new Dictionary<uint, ImmutableArray<byte>>();
        EnbCurrentRetailSpecialMaterialResource? resource = null;
        bool complete = true;
        while (reader.Open())
        {
            uint id = reader.Id;
            if (id == 0xb40) resource = ReadResource(ref reader);
            else if (id is >= 0xb43 and <= 0xb4b)
            {
                int width = id switch { 0xb45 => 6, 0xb46 => 4, 0xb47 or 0xb48 => 8, _ => 12 };
                uint rows = id == 0xb45 ? triangles : vertices;
                var data = reader.Rest();
                int copied = (int)Math.Min((ulong)(data.Length / width), rows) * width;
                streams[id] = data[..copied].ToArray().ToImmutableArray();
                complete = (ulong)copied == (ulong)rows * (uint)width;
            }
            reader.Close();
            if (!complete) break; // A0E310 stops after the first short stream.
        }
        return new(retainedHeader, triangles, vertices, streams.ToImmutableDictionary(), resource, complete);
    }

    private static EnbCurrentRetailSpecialMaterialResource? ReadResource(ref Reader reader)
    {
        // A08E20: selector child, then resource child. Neither ID is checked.
        if (!reader.Open() || !reader.Read(4, out var selectorBytes)) return null;
        uint selector = UInt(selectorBytes, 0);
        reader.Close();
        if (!reader.Open()) return null;
        if (selector is < 1 or > 5) { reader.Close(); return null; }
        string name = "UnNamed", texture = "", secondTexture = "";
        uint flags = 0;
        Vector3 ambient = selector == 5 ? Vector3.One : Vector3.Zero;
        Vector3 diffuse = selector == 5 ? Vector3.Zero : Vector3.One;
        Vector3 specular = selector is 2 or 3 ? Vector3.One : Vector3.Zero;
        Vector2 parameter = new(1, 0), secondParameter = new(1, 0);
        // A0EF80 invokes A0F030 even if the common child has another ID.
        if (reader.Open())
        {
            var common = reader.Rest();
            for (int at = 0; Micro(common, ref at, out byte id, out var value);)
            {
                if (id == 0) name = Text(value);
                else if (id == 1 && value.Length >= 4) flags = UInt(value, 0);
            }
            reader.Close();
        }
        uint derivedId = selector switch { 5 => 0x16490470, 2 => 0x16490460, _ => 0x16490450 };
        while (reader.Open())
        {
            if (reader.Id == derivedId)
            {
                var derived = reader.Rest();
                for (int at = 0; Micro(derived, ref at, out byte id, out var value);)
                {
                    if (id == 0) texture = Text(value);
                    else if (selector is 3 or 4 && id == 1) secondTexture = Text(value);
                    else
                    {
                        int vectorId = id - (selector is 3 or 4 ? 2 : 1);
                        if (vectorId == 0 && value.Length >= 12) ambient = Vector(value, 0);
                        else if (vectorId == 1 && value.Length >= 12) diffuse = Vector(value, 0);
                        else if (vectorId == 2 && selector is 2 or 3 or 5 && value.Length >= 12) specular = Vector(value, 0);
                        else if ((selector == 4 && id == 4 || selector == 3 && id == 5) && value.Length >= 8)
                            parameter = new(Float(value, 0), Float(value, 4));
                        else if (selector == 3 && id == 6 && value.Length >= 8)
                            secondParameter = new(Float(value, 0), Float(value, 4));
                    }
                }
            }
            reader.Close();
        }
        reader.Close();
        return new(selector, name, flags, texture, secondTexture, ambient, diffuse, specular, parameter, secondParameter);
    }

    private static bool Micro(ReadOnlySpan<byte> bytes, ref int at, out byte id, out ReadOnlySpan<byte> value)
    {
        id = 0; value = default;
        if (bytes.Length - at < 2) return false;
        id = bytes[at++]; int length = bytes[at++];
        if (length > bytes.Length - at) return false;
        value = bytes.Slice(at, length); at += length; return true;
    }
    internal static uint UInt(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    internal static float Float(ReadOnlySpan<byte> bytes, int at) => BitConverter.UInt32BitsToSingle(UInt(bytes, at));
    internal static Vector3 Vector(ReadOnlySpan<byte> bytes, int at) => new(Float(bytes, at), Float(bytes, at + 4), Float(bytes, at + 8));
    private static string Text(ReadOnlySpan<byte> bytes)
    { int end = bytes.IndexOf((byte)0); return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]); }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private readonly List<(uint Id, int End)> _stack = [(0, bytes.Length)];
        private int _at;
        public uint Id => _stack[^1].Id;
        public int End => _stack[^1].End;
        public bool Open()
        {
            if (End - _at < 8) return false;
            uint id = UInt(_bytes, _at), size = UInt(_bytes, _at + 4) & 0x7fffffff;
            long end = _at + 8L + size;
            if (end > End) return false;
            _at += 8; _stack.Add((id, (int)end)); return true;
        }
        public void Close()
        { if (_stack.Count > 1) { _at = End; _stack.RemoveAt(_stack.Count - 1); } }
        public bool Read(int size, out ReadOnlySpan<byte> value)
        {
            value = default;
            if (size > End - _at) return false;
            value = _bytes.Slice(_at, size); _at += size; return true;
        }
        public ReadOnlySpan<byte> Rest() { var value = _bytes.Slice(_at, End - _at); _at = End; return value; }
    }
}

public sealed record EnbCurrentRetailSpecialMaterialMember(ImmutableArray<byte> Header,
    uint TriangleCount, uint VertexCount, ImmutableDictionary<uint, ImmutableArray<byte>> Streams,
    EnbCurrentRetailSpecialMaterialResource? Resource, bool Complete);

public sealed class EnbCurrentRetailSpecialMaterialResource(
    uint Selector,
    string Name,
    uint Flags,
    string Texture,
    string SecondTexture,
    Vector3 Ambient,
    Vector3 Diffuse,
    Vector3 Specular,
    Vector2 Parameter,
    Vector2 SecondParameter) : IEquatable<EnbCurrentRetailSpecialMaterialResource>
{
    // Former record members, kept identical. Derived render data is owned
    // outside equality and is not carried by Copy().
    public uint Selector { get; set; } = Selector;
    public string Name { get; set; } = Name;
    public uint Flags { get; set; } = Flags;
    public string Texture { get; set; } = Texture;
    public string SecondTexture { get; set; } = SecondTexture;
    public Vector3 Ambient { get; set; } = Ambient;
    public Vector3 Diffuse { get; set; } = Diffuse;
    public Vector3 Specular { get; set; } = Specular;
    public Vector2 Parameter { get; set; } = Parameter;
    public Vector2 SecondParameter { get; set; } = SecondParameter;
    public EnbRenderPrepared? RenderPrepared;
    public EnbCurrentRetailSpecialMaterialResource Copy(){var copy=(EnbCurrentRetailSpecialMaterialResource)MemberwiseClone();copy.ResetDerivedData();return copy;}
    private void ResetDerivedData(){RenderPrepared=null;}
    public EnbCurrentRetailSpecialMaterialResource SetSelector(uint value) {Selector=value;return this;}
    public EnbCurrentRetailSpecialMaterialResource SetName(string value) {Name=value;return this;}
    public EnbCurrentRetailSpecialMaterialResource SetFlags(uint value) {Flags=value;return this;}
    public EnbCurrentRetailSpecialMaterialResource SetTexture(string value) {Texture=value;return this;}
    public EnbCurrentRetailSpecialMaterialResource SetSecondTexture(string value) {SecondTexture=value;return this;}
    public EnbCurrentRetailSpecialMaterialResource SetAmbient(Vector3 value) {Ambient=value;return this;}
    public EnbCurrentRetailSpecialMaterialResource SetDiffuse(Vector3 value) {Diffuse=value;return this;}
    public EnbCurrentRetailSpecialMaterialResource SetSpecular(Vector3 value) {Specular=value;return this;}
    public EnbCurrentRetailSpecialMaterialResource SetParameter(Vector2 value) {Parameter=value;return this;}
    public EnbCurrentRetailSpecialMaterialResource SetSecondParameter(Vector2 value) {SecondParameter=value;return this;}
    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbCurrentRetailSpecialMaterialResource { ");
        text.Append("Selector = ").Append((object?)Selector);
        text.Append(", Name = ").Append((object?)Name);
        text.Append(", Flags = ").Append((object?)Flags);
        text.Append(", Texture = ").Append((object?)Texture);
        text.Append(", SecondTexture = ").Append((object?)SecondTexture);
        text.Append(", Ambient = ").Append((object?)Ambient);
        text.Append(", Diffuse = ").Append((object?)Diffuse);
        text.Append(", Specular = ").Append((object?)Specular);
        text.Append(", Parameter = ").Append((object?)Parameter);
        text.Append(", SecondParameter = ").Append((object?)SecondParameter);
        return text.Append(" }").ToString();
    }
    public override bool Equals(object? obj)=>Equals(obj as EnbCurrentRetailSpecialMaterialResource);
    public bool Equals(EnbCurrentRetailSpecialMaterialResource? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<uint>.Default.Equals(Selector,other.Selector) &&
        EqualityComparer<string>.Default.Equals(Name,other.Name) &&
        EqualityComparer<uint>.Default.Equals(Flags,other.Flags) &&
        EqualityComparer<string>.Default.Equals(Texture,other.Texture) &&
        EqualityComparer<string>.Default.Equals(SecondTexture,other.SecondTexture) &&
        EqualityComparer<Vector3>.Default.Equals(Ambient,other.Ambient) &&
        EqualityComparer<Vector3>.Default.Equals(Diffuse,other.Diffuse) &&
        EqualityComparer<Vector3>.Default.Equals(Specular,other.Specular) &&
        EqualityComparer<Vector2>.Default.Equals(Parameter,other.Parameter) &&
        EqualityComparer<Vector2>.Default.Equals(SecondParameter,other.SecondParameter);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbCurrentRetailSpecialMaterialResource));
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(Selector!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(Name!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(Flags!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(Texture!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(SecondTexture!);
            hash=hash*-1521134295+EqualityComparer<Vector3>.Default.GetHashCode(Ambient!);
            hash=hash*-1521134295+EqualityComparer<Vector3>.Default.GetHashCode(Diffuse!);
            hash=hash*-1521134295+EqualityComparer<Vector3>.Default.GetHashCode(Specular!);
            hash=hash*-1521134295+EqualityComparer<Vector2>.Default.GetHashCode(Parameter!);
            hash=hash*-1521134295+EqualityComparer<Vector2>.Default.GetHashCode(SecondParameter!);
            return hash;
        }
    }
    public static bool operator ==(EnbCurrentRetailSpecialMaterialResource? left,EnbCurrentRetailSpecialMaterialResource? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbCurrentRetailSpecialMaterialResource? left,EnbCurrentRetailSpecialMaterialResource? right)=>!(left==right);

}
