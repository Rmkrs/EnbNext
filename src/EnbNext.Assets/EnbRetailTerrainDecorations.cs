using System.Numerics;
using System.Text;

namespace EnbNext.Assets;

public sealed record EnbRetailTerrainDecoration(int Prototype, Quaternion Rotation, Vector2 Position, float Scale)
{
    // Procedural479D00 emits a matrix directly; do not round-trip it through a quaternion.
    public Matrix4x4? GeneratedTransform {get;init;}
}
public sealed record EnbRetailTerrainDecorations(string[] Prototypes, EnbRetailTerrainDecoration[] Placements)
{
    public bool UsesUndecoratedFallback { get; init; }

    public static EnbRetailTerrainDecorations Load(uint sector, Func<string, byte[]> readResource,
        Func<EnbRetailTerrainDecorations>? generate=null)
    {
        byte[]? cache = null;
        try { cache = readResource($"land{sector:D4}.dcb"); }
        catch (FileNotFoundException) { }
        // 005137F0 selects 00479D00 if either decoded collection is empty.
        // 004795D0 retains completed records on a short read. The caller regenerates
        // only when one of the resulting collections is empty.
        var decoded = cache is null ? null : Decode(cache);
        if (decoded is { Prototypes.Length: > 0, Placements.Length: > 0 }) return decoded;
        if(generate is not null)return generate();
        var ini = EnbRetailIniDocument.Parse(Encoding.ASCII.GetString(readResource("decoration.ini")));
        int count = 0;
        if (ini.TryGetSection(sector.ToString(System.Globalization.CultureInfo.InvariantCulture), out var section) &&
            section.TryGetValue("NUM_DECOS", out var raw))
            count = int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        // 00479D76 reads NUM_DECOS with default 0; 00479D83 skips generation for <= 0.
        if (count > 0)
            throw new NotSupportedException($"Terrain {sector} requires procedural decoration generation (NUM_DECOS={count}).");
        return new([], []) { UsesUndecoratedFallback = true };
    }

    // 004795D0: flags, quaternion, X/Y, scale. Z comes from the terrain sampler.
    public static EnbRetailTerrainDecorations Decode(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var names = new List<string>();
        var placements = new List<EnbRetailTerrainDecoration>();
        EnbRetailTerrainDecorations Result() => new(names.ToArray(), placements.ToArray());
        bool Available(int length) => reader.BaseStream.Length-reader.BaseStream.Position >= length;
        if (!Available(8) || reader.ReadUInt32() != 0xDEC0DEC0) return Result();
        uint count = reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            if (!Available(32)) return Result();
            byte[] name = reader.ReadBytes(32);
            names.Add(Encoding.Latin1.GetString(name).Split('\0')[0]);
        }
        if (!Available(4)) return Result();
        uint total = reader.ReadUInt32();
        for (uint i = 0; i < total; i++)
        {
            if (!Available(32)) return Result();
            int prototype = (int)(reader.ReadUInt32() & 255);
            // 00479847..54 maps indices greater than count to zero. Also contain
            // the proven ==count off-by-one before 47A2F0 indexes the vector.
            if (prototype >= names.Count) prototype = 0;
            var rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            float scale = reader.ReadSingle();
            placements.Add(new(prototype, rotation, position, scale));
        }
        return Result();
    }
}
