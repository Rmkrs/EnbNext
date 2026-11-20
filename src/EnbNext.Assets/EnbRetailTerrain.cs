using System.Numerics;

namespace EnbNext.Assets;

/// <summary>Installed landNNNN.ini and its P2 height field. Retail 004634F0/004637C0.</summary>
public sealed class EnbRetailTerrain(
    uint Sector,
    int Width,
    ushort[] Heights,
    float HorizontalScale,
    float VerticalScale,
    string ColorMap,
    string DetailMap,
    Vector2 DetailScale,
    bool FlipColorMap,
    int PatchGridWidth,
    bool HasSea,
    float SeaLevel,
    string SeaSwatch) : IEquatable<EnbRetailTerrain>
{
    // Former record members, kept identical. Derived render data is owned
    // outside equality and is not carried by Copy().
    public uint Sector { get; set; } = Sector;
    public int Width { get; set; } = Width;
    public ushort[] Heights { get; set; } = Heights;
    public float HorizontalScale { get; set; } = HorizontalScale;
    public float VerticalScale { get; set; } = VerticalScale;
    public string ColorMap { get; set; } = ColorMap;
    public string DetailMap { get; set; } = DetailMap;
    public Vector2 DetailScale { get; set; } = DetailScale;
    public bool FlipColorMap { get; set; } = FlipColorMap;
    public int PatchGridWidth { get; set; } = PatchGridWidth;
    public bool HasSea { get; set; } = HasSea;
    public float SeaLevel { get; set; } = SeaLevel;
    public string SeaSwatch { get; set; } = SeaSwatch;
    public EnbRenderPrepared? RenderPrepared;
    public EnbRetailTerrain Copy(){var copy=(EnbRetailTerrain)MemberwiseClone();copy.ResetDerivedData();return copy;}
    private void ResetDerivedData(){RenderPrepared=null;}
    public EnbRetailTerrain SetSector(uint value) {Sector=value;return this;}
    public EnbRetailTerrain SetWidth(int value) {Width=value;return this;}
    public EnbRetailTerrain SetHeights(ushort[] value) {Heights=value;return this;}
    public EnbRetailTerrain SetHorizontalScale(float value) {HorizontalScale=value;return this;}
    public EnbRetailTerrain SetVerticalScale(float value) {VerticalScale=value;return this;}
    public EnbRetailTerrain SetColorMap(string value) {ColorMap=value;return this;}
    public EnbRetailTerrain SetDetailMap(string value) {DetailMap=value;return this;}
    public EnbRetailTerrain SetDetailScale(Vector2 value) {DetailScale=value;return this;}
    public EnbRetailTerrain SetFlipColorMap(bool value) {FlipColorMap=value;return this;}
    public EnbRetailTerrain SetPatchGridWidth(int value) {PatchGridWidth=value;return this;}
    public EnbRetailTerrain SetHasSea(bool value) {HasSea=value;return this;}
    public EnbRetailTerrain SetSeaLevel(float value) {SeaLevel=value;return this;}
    public EnbRetailTerrain SetSeaSwatch(string value) {SeaSwatch=value;return this;}
    public EnbRetailTerrain SetHeightMap(string value) {HeightMap=value;return this;}
    public EnbRetailTerrain SetOriginalMaximum(ushort value) {OriginalMaximum=value;return this;}
    public EnbRetailTerrain SetWrapping(bool value) {Wrapping=value;return this;}
    public EnbRetailTerrain SetLightmapChunk(int value) {LightmapChunk=value;return this;}
    public EnbRetailTerrain SetSeaDetailScales(Vector4 value) {SeaDetailScales=value;return this;}
    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbRetailTerrain { ");
        text.Append("Sector = ").Append((object?)Sector);
        text.Append(", Width = ").Append((object?)Width);
        text.Append(", Heights = ").Append((object?)Heights);
        text.Append(", HorizontalScale = ").Append((object?)HorizontalScale);
        text.Append(", VerticalScale = ").Append((object?)VerticalScale);
        text.Append(", ColorMap = ").Append((object?)ColorMap);
        text.Append(", DetailMap = ").Append((object?)DetailMap);
        text.Append(", DetailScale = ").Append((object?)DetailScale);
        text.Append(", FlipColorMap = ").Append((object?)FlipColorMap);
        text.Append(", PatchGridWidth = ").Append((object?)PatchGridWidth);
        text.Append(", HasSea = ").Append((object?)HasSea);
        text.Append(", SeaLevel = ").Append((object?)SeaLevel);
        text.Append(", SeaSwatch = ").Append((object?)SeaSwatch);
        text.Append(", HeightMap = ").Append((object?)HeightMap);
        text.Append(", OriginalMaximum = ").Append((object?)OriginalMaximum);
        text.Append(", Wrapping = ").Append((object?)Wrapping);
        text.Append(", LightmapChunk = ").Append((object?)LightmapChunk);
        text.Append(", SeaDetailScales = ").Append((object?)SeaDetailScales);
        return text.Append(" }").ToString();
    }
    public override bool Equals(object? obj)=>Equals(obj as EnbRetailTerrain);
    public bool Equals(EnbRetailTerrain? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<uint>.Default.Equals(Sector,other.Sector) &&
        EqualityComparer<int>.Default.Equals(Width,other.Width) &&
        EqualityComparer<ushort[]>.Default.Equals(Heights,other.Heights) &&
        EqualityComparer<float>.Default.Equals(HorizontalScale,other.HorizontalScale) &&
        EqualityComparer<float>.Default.Equals(VerticalScale,other.VerticalScale) &&
        EqualityComparer<string>.Default.Equals(ColorMap,other.ColorMap) &&
        EqualityComparer<string>.Default.Equals(DetailMap,other.DetailMap) &&
        EqualityComparer<Vector2>.Default.Equals(DetailScale,other.DetailScale) &&
        EqualityComparer<bool>.Default.Equals(FlipColorMap,other.FlipColorMap) &&
        EqualityComparer<int>.Default.Equals(PatchGridWidth,other.PatchGridWidth) &&
        EqualityComparer<bool>.Default.Equals(HasSea,other.HasSea) &&
        EqualityComparer<float>.Default.Equals(SeaLevel,other.SeaLevel) &&
        EqualityComparer<string>.Default.Equals(SeaSwatch,other.SeaSwatch) &&
        EqualityComparer<string>.Default.Equals(HeightMap,other.HeightMap) &&
        EqualityComparer<ushort>.Default.Equals(OriginalMaximum,other.OriginalMaximum) &&
        EqualityComparer<bool>.Default.Equals(Wrapping,other.Wrapping) &&
        EqualityComparer<int>.Default.Equals(LightmapChunk,other.LightmapChunk) &&
        EqualityComparer<Vector4>.Default.Equals(SeaDetailScales,other.SeaDetailScales);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbRetailTerrain));
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(Sector!);
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(Width!);
            hash=hash*-1521134295+EqualityComparer<ushort[]>.Default.GetHashCode(Heights!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(HorizontalScale!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(VerticalScale!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(ColorMap!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(DetailMap!);
            hash=hash*-1521134295+EqualityComparer<Vector2>.Default.GetHashCode(DetailScale!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(FlipColorMap!);
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(PatchGridWidth!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(HasSea!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(SeaLevel!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(SeaSwatch!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(HeightMap!);
            hash=hash*-1521134295+EqualityComparer<ushort>.Default.GetHashCode(OriginalMaximum!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(Wrapping!);
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(LightmapChunk!);
            hash=hash*-1521134295+EqualityComparer<Vector4>.Default.GetHashCode(SeaDetailScales!);
            return hash;
        }
    }
    public static bool operator ==(EnbRetailTerrain? left,EnbRetailTerrain? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbRetailTerrain? left,EnbRetailTerrain? right)=>!(left==right);
    public string HeightMap { get; set; } = "";
    public ushort OriginalMaximum { get; set; }
    public bool Wrapping { get; set; }
    public int LightmapChunk { get; set; } = 8;
    public Vector4 SeaDetailScales { get; set; } = Vector4.One;
    public Vector3 Position(int x, int y)
        => new((x - Width / 2) * HorizontalScale / Width,
            (y - Width / 2) * HorizontalScale / Width,
            Heights[(y & (Width - 1)) * Width + (x & (Width - 1))] * VerticalScale);

    public static EnbRetailTerrain Load(uint sector, string iniText, Func<string, byte[]> readResource)
    {
        var ini = EnbRetailIniDocument.Parse(iniText);
        IReadOnlyDictionary<string, string> Section(string name)
            => ini.TryGetSection(name, out var section) ? section : new Dictionary<string, string>();
        var height = Section("HEIGHTMAP");
        var land = Section("LAND");
        var sea = Section("SEA");
        static string Text(IReadOnlyDictionary<string, string> s, string k, string fallback = "")
            => s.TryGetValue(k, out var value) ? value : fallback;
        static float Number(IReadOnlyDictionary<string, string> s, string k, float fallback)
            => EnbRetailIniDocument.TryReadFloat(s, k, out var value) ? value : fallback;
        bool wrapping=EnbRetailIniDocument.ReadBool(height,"WRAPPING");
        bool flip = !height.ContainsKey("FLIP_PGM") || EnbRetailIniDocument.ReadBool(height, "FLIP_PGM");
        var field = EnbRetailTerrainHeightField.Decode(readResource(Text(height, "HEIGHT_MAP")), flip, int.MaxValue);
        int width = field.Width;
        // Existing renderer admission remains separate from the native file decoder.
        if (width < 2 || width > 4096 || !BitOperations.IsPow2(width))
            throw new InvalidDataException("Terrain height map dimensions are not supported by the renderer.");
        ushort[] samples = field.Samples;
        ushort maximum = field.OriginalMaximum;
        float horizontal = Number(height, "HSCALE", 1);
        float vertical = Number(height, "VSCALE", 1);
        int patches = EnbRetailIniDocument.ReadCurrentRetailInteger(land, "PATCH_GRID_WIDTH", 16);
        if (maximum == 0 || !float.IsFinite(horizontal) || horizontal <= 0 ||
            !float.IsFinite(vertical) || patches < 1 || patches > width || !BitOperations.IsPow2(patches))
            throw new InvalidDataException("Terrain scale or patch grid is invalid.");
        // 004634F0 uses the largest sample, not the PGM header's advertised maximum.
        return new(sector, width, samples, horizontal, vertical / maximum,
            Text(land, "COLOR_MAP"), Text(land, "DETAIL_MAP"),
            new(Number(land, "XDETAIL_SCALE", 1), Number(land, "YDETAIL_SCALE", 1)),
            EnbRetailIniDocument.ReadBool(land, "FLIP_TGA"), patches,
            EnbRetailIniDocument.ReadBool(Section("GENERAL"), "HASSEA"),
            Number(sea, "SEA_LEVEL", 0), Text(sea, "SWATCH", "water_swatch"))
            { HeightMap = Text(height, "HEIGHT_MAP"), OriginalMaximum=field.OriginalMaximum, Wrapping=wrapping,
                LightmapChunk = EnbRetailIniDocument.ReadCurrentRetailInteger(land, "LIGHTMAP_CHUNK", 8),
                SeaDetailScales = new(
                Number(sea, "DETAIL_SCALE", 1), Number(sea, "DETAIL_SCALE2", 1),
                Number(sea, "DETAIL_SCALE3", 1), Number(sea, "DETAIL_SCALE4", 1)) };
    }
}
