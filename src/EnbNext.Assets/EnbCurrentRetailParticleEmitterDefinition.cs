using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace EnbNext.Assets;

public readonly record struct EnbCurrentRetailParticleColorKey(float TimeSeconds, byte R, byte G, byte B, byte Reserved)
{
    // 0048D850 adjusts the decoded floating-point property, before animation.
    // Keep the authored bytes as provenance without quantizing the shifted key.
    public Vector3? RuntimeRgb { get; init; }
    public Vector3 NormalizedRgb => RuntimeRgb ?? new Vector3(R / 255f, G / 255f, B / 255f);
}

public readonly record struct EnbCurrentRetailParticleScalarKey(float TimeSeconds, float Value);

public sealed record EnbCurrentRetailParticleColorProperty(
    Vector3 Random,
    IReadOnlyList<EnbCurrentRetailParticleColorKey> Keys)
{
    public EnbCurrentRetailParticleColorProperty? BeforeRuntimeSetter {get;init;}
}

public sealed record EnbCurrentRetailParticleScalarProperty(
    float Random,
    IReadOnlyList<EnbCurrentRetailParticleScalarKey> Keys);

public readonly record struct EnbCurrentRetailParticleRandomizer(
    uint ClassId,
    Vector3 Values);

public readonly record struct EnbCurrentRetailParticleShader(
    byte DepthCompare,
    byte DepthMask,
    byte ColorMask,
    byte DestinationBlend,
    byte FogFunction,
    byte PrimaryGradient,
    byte SecondaryGradient,
    byte SourceBlend,
    byte Texturing,
    byte DetailColorFunction,
    byte DetailAlphaFunction,
    byte ShaderPreset,
    byte AlphaTest,
    byte PostDetailColorFunction,
    byte PostDetailAlphaFunction,
    byte Padding)
{
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 16)
            throw new ArgumentException("A particle shader destination must provide at least 16 bytes.", nameof(destination));

        destination[0] = DepthCompare;
        destination[1] = DepthMask;
        destination[2] = ColorMask;
        destination[3] = DestinationBlend;
        destination[4] = FogFunction;
        destination[5] = PrimaryGradient;
        destination[6] = SecondaryGradient;
        destination[7] = SourceBlend;
        destination[8] = Texturing;
        destination[9] = DetailColorFunction;
        destination[10] = DetailAlphaFunction;
        destination[11] = ShaderPreset;
        destination[12] = AlphaTest;
        destination[13] = PostDetailColorFunction;
        destination[14] = PostDetailAlphaFunction;
        destination[15] = Padding;
    }
}

/// <summary>
/// Exact decoded state of one current-retail W3D 0x0500 ParticleEmitter
/// definition. The representation retains numeric class/render/frame selectors;
/// the runtime consumes a bounded selector-0/triangle-or-quad/1x1 slice.
/// </summary>
public sealed class EnbCurrentRetailParticleEmitterDefinition(
    uint Version,
    string Name,
    string TextureFileName,
    float LifetimeSeconds,
    float EmissionRatePerSecond,
    int MaximumEmissions,
    Vector3 VelocityPerSecond,
    Vector3 AccelerationPerSecondSquared,
    float FadeTimeSeconds,
    float Gravity,
    float Elasticity,
    uint BurstSize,
    EnbCurrentRetailParticleRandomizer CreationRandomizer,
    EnbCurrentRetailParticleRandomizer VelocityRandomizer,
    float OutwardVelocityPerSecond,
    float VelocityInheritanceFactor,
    EnbCurrentRetailParticleShader Shader,
    uint RenderMode,
    uint FrameMode,
    EnbCurrentRetailParticleColorProperty Color,
    EnbCurrentRetailParticleScalarProperty Opacity,
    EnbCurrentRetailParticleScalarProperty Size,
    EnbCurrentRetailParticleScalarProperty Rotation,
    float InitialOrientationRandom,
    EnbCurrentRetailParticleScalarProperty Frame) : IEquatable<EnbCurrentRetailParticleEmitterDefinition>
{
    // Former record members, kept identical. Derived render data is owned
    // outside equality and is not carried by Copy().
    public uint Version { get; set; } = Version;
    public string Name { get; set; } = Name;
    public string TextureFileName { get; set; } = TextureFileName;
    public float LifetimeSeconds { get; set; } = LifetimeSeconds;
    public float EmissionRatePerSecond { get; set; } = EmissionRatePerSecond;
    public int MaximumEmissions { get; set; } = MaximumEmissions;
    public Vector3 VelocityPerSecond { get; set; } = VelocityPerSecond;
    public Vector3 AccelerationPerSecondSquared { get; set; } = AccelerationPerSecondSquared;
    public float FadeTimeSeconds { get; set; } = FadeTimeSeconds;
    public float Gravity { get; set; } = Gravity;
    public float Elasticity { get; set; } = Elasticity;
    public uint BurstSize { get; set; } = BurstSize;
    public EnbCurrentRetailParticleRandomizer CreationRandomizer { get; set; } = CreationRandomizer;
    public EnbCurrentRetailParticleRandomizer VelocityRandomizer { get; set; } = VelocityRandomizer;
    public float OutwardVelocityPerSecond { get; set; } = OutwardVelocityPerSecond;
    public float VelocityInheritanceFactor { get; set; } = VelocityInheritanceFactor;
    public EnbCurrentRetailParticleShader Shader { get; set; } = Shader;
    public uint RenderMode { get; set; } = RenderMode;
    public uint FrameMode { get; set; } = FrameMode;
    public EnbCurrentRetailParticleColorProperty Color { get; set; } = Color;
    public EnbCurrentRetailParticleScalarProperty Opacity { get; set; } = Opacity;
    public EnbCurrentRetailParticleScalarProperty Size { get; set; } = Size;
    public EnbCurrentRetailParticleScalarProperty Rotation { get; set; } = Rotation;
    public float InitialOrientationRandom { get; set; } = InitialOrientationRandom;
    public EnbCurrentRetailParticleScalarProperty Frame { get; set; } = Frame;
    public EnbRenderPrepared? RenderPrepared;
    public EnbCurrentRetailParticleEmitterDefinition Copy(){var copy=(EnbCurrentRetailParticleEmitterDefinition)MemberwiseClone();copy.ResetDerivedData();return copy;}
    private void ResetDerivedData(){RenderPrepared=null;}
    public EnbCurrentRetailParticleEmitterDefinition SetVersion(uint value) {Version=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetName(string value) {Name=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetTextureFileName(string value) {TextureFileName=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetLifetimeSeconds(float value) {LifetimeSeconds=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetEmissionRatePerSecond(float value) {EmissionRatePerSecond=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetMaximumEmissions(int value) {MaximumEmissions=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetVelocityPerSecond(Vector3 value) {VelocityPerSecond=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetAccelerationPerSecondSquared(Vector3 value) {AccelerationPerSecondSquared=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetFadeTimeSeconds(float value) {FadeTimeSeconds=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetGravity(float value) {Gravity=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetElasticity(float value) {Elasticity=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetBurstSize(uint value) {BurstSize=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetCreationRandomizer(EnbCurrentRetailParticleRandomizer value) {CreationRandomizer=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetVelocityRandomizer(EnbCurrentRetailParticleRandomizer value) {VelocityRandomizer=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetOutwardVelocityPerSecond(float value) {OutwardVelocityPerSecond=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetVelocityInheritanceFactor(float value) {VelocityInheritanceFactor=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetShader(EnbCurrentRetailParticleShader value) {Shader=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetRenderMode(uint value) {RenderMode=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetFrameMode(uint value) {FrameMode=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetColor(EnbCurrentRetailParticleColorProperty value) {Color=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetOpacity(EnbCurrentRetailParticleScalarProperty value) {Opacity=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetSize(EnbCurrentRetailParticleScalarProperty value) {Size=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetRotation(EnbCurrentRetailParticleScalarProperty value) {Rotation=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetInitialOrientationRandom(float value) {InitialOrientationRandom=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetFrame(EnbCurrentRetailParticleScalarProperty value) {Frame=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetBlurTime(EnbCurrentRetailParticleScalarProperty? value) {BlurTime=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetLinePropertyWords(ImmutableArray<uint> value) {LinePropertyWords=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetInitialEmissionTimeSeconds(float value) {InitialEmissionTimeSeconds=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetLocalSpace(bool value) {LocalSpace=value;return this;}
    public EnbCurrentRetailParticleEmitterDefinition SetRequiresLegacyTextureShaderResolution(bool value) {RequiresLegacyTextureShaderResolution=value;return this;}
    public override string ToString()
    {
        var text=new System.Text.StringBuilder("EnbCurrentRetailParticleEmitterDefinition { ");
        text.Append("Version = ").Append((object?)Version);
        text.Append(", Name = ").Append((object?)Name);
        text.Append(", TextureFileName = ").Append((object?)TextureFileName);
        text.Append(", LifetimeSeconds = ").Append((object?)LifetimeSeconds);
        text.Append(", EmissionRatePerSecond = ").Append((object?)EmissionRatePerSecond);
        text.Append(", MaximumEmissions = ").Append((object?)MaximumEmissions);
        text.Append(", VelocityPerSecond = ").Append((object?)VelocityPerSecond);
        text.Append(", AccelerationPerSecondSquared = ").Append((object?)AccelerationPerSecondSquared);
        text.Append(", FadeTimeSeconds = ").Append((object?)FadeTimeSeconds);
        text.Append(", Gravity = ").Append((object?)Gravity);
        text.Append(", Elasticity = ").Append((object?)Elasticity);
        text.Append(", BurstSize = ").Append((object?)BurstSize);
        text.Append(", CreationRandomizer = ").Append((object?)CreationRandomizer);
        text.Append(", VelocityRandomizer = ").Append((object?)VelocityRandomizer);
        text.Append(", OutwardVelocityPerSecond = ").Append((object?)OutwardVelocityPerSecond);
        text.Append(", VelocityInheritanceFactor = ").Append((object?)VelocityInheritanceFactor);
        text.Append(", Shader = ").Append((object?)Shader);
        text.Append(", RenderMode = ").Append((object?)RenderMode);
        text.Append(", FrameMode = ").Append((object?)FrameMode);
        text.Append(", Color = ").Append((object?)Color);
        text.Append(", Opacity = ").Append((object?)Opacity);
        text.Append(", Size = ").Append((object?)Size);
        text.Append(", Rotation = ").Append((object?)Rotation);
        text.Append(", InitialOrientationRandom = ").Append((object?)InitialOrientationRandom);
        text.Append(", Frame = ").Append((object?)Frame);
        text.Append(", BlurTime = ").Append((object?)BlurTime);
        text.Append(", LinePropertyWords = ").Append((object?)LinePropertyWords);
        text.Append(", InitialEmissionTimeSeconds = ").Append((object?)InitialEmissionTimeSeconds);
        text.Append(", LocalSpace = ").Append((object?)LocalSpace);
        text.Append(", RequiresLegacyTextureShaderResolution = ").Append((object?)RequiresLegacyTextureShaderResolution);
        return text.Append(" }").ToString();
    }
    public override bool Equals(object? obj)=>Equals(obj as EnbCurrentRetailParticleEmitterDefinition);
    public bool Equals(EnbCurrentRetailParticleEmitterDefinition? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<uint>.Default.Equals(Version,other.Version) &&
        EqualityComparer<string>.Default.Equals(Name,other.Name) &&
        EqualityComparer<string>.Default.Equals(TextureFileName,other.TextureFileName) &&
        EqualityComparer<float>.Default.Equals(LifetimeSeconds,other.LifetimeSeconds) &&
        EqualityComparer<float>.Default.Equals(EmissionRatePerSecond,other.EmissionRatePerSecond) &&
        EqualityComparer<int>.Default.Equals(MaximumEmissions,other.MaximumEmissions) &&
        EqualityComparer<Vector3>.Default.Equals(VelocityPerSecond,other.VelocityPerSecond) &&
        EqualityComparer<Vector3>.Default.Equals(AccelerationPerSecondSquared,other.AccelerationPerSecondSquared) &&
        EqualityComparer<float>.Default.Equals(FadeTimeSeconds,other.FadeTimeSeconds) &&
        EqualityComparer<float>.Default.Equals(Gravity,other.Gravity) &&
        EqualityComparer<float>.Default.Equals(Elasticity,other.Elasticity) &&
        EqualityComparer<uint>.Default.Equals(BurstSize,other.BurstSize) &&
        EqualityComparer<EnbCurrentRetailParticleRandomizer>.Default.Equals(CreationRandomizer,other.CreationRandomizer) &&
        EqualityComparer<EnbCurrentRetailParticleRandomizer>.Default.Equals(VelocityRandomizer,other.VelocityRandomizer) &&
        EqualityComparer<float>.Default.Equals(OutwardVelocityPerSecond,other.OutwardVelocityPerSecond) &&
        EqualityComparer<float>.Default.Equals(VelocityInheritanceFactor,other.VelocityInheritanceFactor) &&
        EqualityComparer<EnbCurrentRetailParticleShader>.Default.Equals(Shader,other.Shader) &&
        EqualityComparer<uint>.Default.Equals(RenderMode,other.RenderMode) &&
        EqualityComparer<uint>.Default.Equals(FrameMode,other.FrameMode) &&
        EqualityComparer<EnbCurrentRetailParticleColorProperty>.Default.Equals(Color,other.Color) &&
        EqualityComparer<EnbCurrentRetailParticleScalarProperty>.Default.Equals(Opacity,other.Opacity) &&
        EqualityComparer<EnbCurrentRetailParticleScalarProperty>.Default.Equals(Size,other.Size) &&
        EqualityComparer<EnbCurrentRetailParticleScalarProperty>.Default.Equals(Rotation,other.Rotation) &&
        EqualityComparer<float>.Default.Equals(InitialOrientationRandom,other.InitialOrientationRandom) &&
        EqualityComparer<EnbCurrentRetailParticleScalarProperty>.Default.Equals(Frame,other.Frame) &&
        EqualityComparer<EnbCurrentRetailParticleScalarProperty?>.Default.Equals(BlurTime,other.BlurTime) &&
        EqualityComparer<ImmutableArray<uint>>.Default.Equals(LinePropertyWords,other.LinePropertyWords) &&
        EqualityComparer<float>.Default.Equals(InitialEmissionTimeSeconds,other.InitialEmissionTimeSeconds) &&
        EqualityComparer<bool>.Default.Equals(LocalSpace,other.LocalSpace) &&
        EqualityComparer<bool>.Default.Equals(RequiresLegacyTextureShaderResolution,other.RequiresLegacyTextureShaderResolution);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash=EqualityComparer<Type>.Default.GetHashCode(typeof(EnbCurrentRetailParticleEmitterDefinition));
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(Version!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(Name!);
            hash=hash*-1521134295+EqualityComparer<string>.Default.GetHashCode(TextureFileName!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(LifetimeSeconds!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(EmissionRatePerSecond!);
            hash=hash*-1521134295+EqualityComparer<int>.Default.GetHashCode(MaximumEmissions!);
            hash=hash*-1521134295+EqualityComparer<Vector3>.Default.GetHashCode(VelocityPerSecond!);
            hash=hash*-1521134295+EqualityComparer<Vector3>.Default.GetHashCode(AccelerationPerSecondSquared!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(FadeTimeSeconds!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(Gravity!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(Elasticity!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(BurstSize!);
            hash=hash*-1521134295+EqualityComparer<EnbCurrentRetailParticleRandomizer>.Default.GetHashCode(CreationRandomizer!);
            hash=hash*-1521134295+EqualityComparer<EnbCurrentRetailParticleRandomizer>.Default.GetHashCode(VelocityRandomizer!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(OutwardVelocityPerSecond!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(VelocityInheritanceFactor!);
            hash=hash*-1521134295+EqualityComparer<EnbCurrentRetailParticleShader>.Default.GetHashCode(Shader!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(RenderMode!);
            hash=hash*-1521134295+EqualityComparer<uint>.Default.GetHashCode(FrameMode!);
            hash=hash*-1521134295+EqualityComparer<EnbCurrentRetailParticleColorProperty>.Default.GetHashCode(Color!);
            hash=hash*-1521134295+EqualityComparer<EnbCurrentRetailParticleScalarProperty>.Default.GetHashCode(Opacity!);
            hash=hash*-1521134295+EqualityComparer<EnbCurrentRetailParticleScalarProperty>.Default.GetHashCode(Size!);
            hash=hash*-1521134295+EqualityComparer<EnbCurrentRetailParticleScalarProperty>.Default.GetHashCode(Rotation!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(InitialOrientationRandom!);
            hash=hash*-1521134295+EqualityComparer<EnbCurrentRetailParticleScalarProperty>.Default.GetHashCode(Frame!);
            hash=hash*-1521134295+EqualityComparer<EnbCurrentRetailParticleScalarProperty?>.Default.GetHashCode(BlurTime!);
            hash=hash*-1521134295+EqualityComparer<ImmutableArray<uint>>.Default.GetHashCode(LinePropertyWords!);
            hash=hash*-1521134295+EqualityComparer<float>.Default.GetHashCode(InitialEmissionTimeSeconds!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(LocalSpace!);
            hash=hash*-1521134295+EqualityComparer<bool>.Default.GetHashCode(RequiresLegacyTextureShaderResolution!);
            return hash;
        }
    }
    public static bool operator ==(EnbCurrentRetailParticleEmitterDefinition? left,EnbCurrentRetailParticleEmitterDefinition? right)=>ReferenceEquals(left,right)||left is not null&&left.Equals(right);
    public static bool operator !=(EnbCurrentRetailParticleEmitterDefinition? left,EnbCurrentRetailParticleEmitterDefinition? right)=>!(left==right);
    // 0097D1B8..D1D3 replaces an authored zero with one at emitter construction.
    // Keep BurstSize unchanged as provenance of the decoded file.
    public uint RuntimeBurstSize => Math.Max(1u,BurstSize);

    // 00915F70: 12-byte header, initial key, then the authored additional keys.
    // Absence is retained separately from an explicitly authored zero curve.
    public EnbCurrentRetailParticleScalarProperty? BlurTime { get; set; }

    // 00915D30 copies the full 64-byte 0509 payload. Keep its selectors and
    // reserved words intact until the corresponding line renderer consumes them.
    public ImmutableArray<uint> LinePropertyWords { get; set; } = [];

    // 050D +0 becomes the initial interval remainder (seconds -> ms);
    // +4 selects local-space emission and forwards the emitter transform to
    // the independent buffer. Remaining extension words are reserved here.
    public float InitialEmissionTimeSeconds { get; set; }
    public bool LocalSpace { get; set; }

    // V1 has no authored shader: 00915160 selects it from the loaded texture.
    // A byte-only inspection can retain the definition, but cannot submit it
    // until the asset-loading caller supplies its texture resolver.
    public bool RequiresLegacyTextureShaderResolution { get; set; }

    /// <summary>00915160 captures TextureClass+54 during FILE loading, then
    /// releases the texture. Later surface completion cannot change this shader.</summary>
    public EnbCurrentRetailParticleEmitterDefinition ResolveLegacyTextureFormat(uint format)
    {
        if(!RequiresLegacyTextureShaderResolution)return this;
        bool alpha=format is 2 or 6 or 7 or 9 or 10 or 12 or 15 or 16 or 21 or 22 or 23 or 24;
        return this.Copy().SetShader(new(3,0,0,(byte)(alpha?5:1),0,0,0,(byte)(alpha?2:1),1,0,0,0,0,0,0,0)).SetRequiresLegacyTextureShaderResolution(false);
    }
}

/// <summary>
/// Strict current-retail decoder for the W3D 0x0500 definition consumed by
/// ParticleEmitterDefClass. Child sizes and key counts are validated before a
/// runtime definition is admitted; unknown or malformed emitted state never
/// reaches rendering.
/// </summary>
public static class EnbCurrentRetailParticleEmitterDecoder
{
    /// <summary>00914FB0's legacy conversion boundary precedes optional chunks.
    /// These are ordered reads of501/502/503, not successful full decoding.</summary>
    public static bool TryReadLegacyTextureConstructionName(ReadOnlySpan<byte> bytes,out string name)
    {
        name=string.Empty;
        if(bytes.Length<8 || BinaryPrimitives.ReadUInt32LittleEndian(bytes)!=0x500)return false;
        uint rootSize=BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..])&0x7fffffffu;
        if(rootSize>(uint)(bytes.Length-8))return false;
        int offset=8,end=8+(int)rootSize;
        ReadOnlySpan<byte> info=default;
        for(uint expected=0x501;expected<=0x503;expected++)
        {
            if(end-offset<8 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..])!=expected)return false;
            uint size=BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset+4)..])&0x7fffffffu;
            if(size>(uint)(end-offset-8))return false;
            var payload=bytes.Slice(offset+8,(int)size);
            if(expected==0x501 && (payload.Length<20 || BinaryPrimitives.ReadUInt32LittleEndian(payload)>0x10000))return false;
            if(expected==0x502 && (payload.Length<12 || BinaryPrimitives.ReadUInt32LittleEndian(payload[4..])>(uint)(payload.Length-12)))return false;
            if(expected==0x503){if(payload.Length<332)return false;info=payload;}
            offset+=8+(int)size;
        }
        var text=info[..260];int zero=text.IndexOf((byte)0);if(zero>=0)text=text[..zero];
        name=System.Text.Encoding.ASCII.GetString(text);return true;
    }
    private const uint ParticleEmitterChunk = 0x0500;
    private const uint HeaderChunk = 0x0501;
    private const uint InfoChunk = 0x0503;
    private const uint InfoV2Chunk = 0x0504;
    private const uint PropertiesChunk = 0x0505;
    private const uint LinePropertiesChunk = 0x0509;
    private const uint RotationKeyframesChunk = 0x050A;
    private const uint FrameKeyframesChunk = 0x050B;
    private const uint BlurTimeKeyframesChunk = 0x050C;
    private const int ChunkHeaderLength = 8;
    private const int InfoLength = 332;
    private const int InfoV2Length = 124;
    private const int PropertiesHeaderLength = 40;
    private const int RotationHeaderLength = 16;
    private const int FrameHeaderLength = 16;

    public static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out EnbCurrentRetailParticleEmitterDefinition definition,
        out string? diagnostic,
        EnbRetailTextureResolver? textures = null)
    {
        definition = default!;
        diagnostic = null;

        if (!TryReadChunk(bytes, 0, bytes.Length, out ChunkView root) ||
            root.Type != ParticleEmitterChunk ||
            root.End != bytes.Length)
        {
            diagnostic = "ParticleEmitter resource is not one exact top-level 0x0500 container.";
            return false;
        }

        // 00914FB0 -> 00915460 opens the 0501 header through 009D4310.
        // OpenChunk masks the parent's size with 7FFFFFFF; an unset root
        // container hint does not prevent schema-directed child reads.

        HeaderState? header = null;
        InfoState? info = null;
        InfoV2State? infoV2 = null;
        PropertiesState? properties = null;
        RotationState? rotation = null;
        ScalarState? frame = null;
        ScalarState? blur = null;
        // 00914377/009143EB..F0 zero the embedded 16-word block. 0097D9F8
        // always forwards its address, including when optional 0509 is absent.
        ImmutableArray<uint> lineProperties = ImmutableArray.Create(new uint[16]);
        float initialEmissionTime = 0;
        bool localSpace = false;
        bool legacyUserInfoValid = false;
        int childOrdinal = 0;
        int offset = root.PayloadStart;
        while (offset < root.PayloadEnd)
        {
            if (!TryReadChunk(bytes, offset, root.PayloadEnd, out ChunkView child))
            {
                diagnostic = $"ParticleEmitter child stream is malformed at 0x{offset:X}.";
                return false;
            }

            ReadOnlySpan<byte> payload = bytes.Slice(child.PayloadStart, child.PayloadLength);
            if (childOrdinal == 1 && child.Type == 0x0502)
                legacyUserInfoValid = payload.Length >= 12 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]) <= payload.Length - 12;
            //00914FB0 consumes501/502/503 in order, then504/505 for V2.
            //Only its optional loop skips unknown chunks, including repeated
            //mandatory types. OpenChunk ignores the container hint.
            int mandatoryCount = header is {} h && h.Version <= 0x10000 ? 3 : 5;
            bool accepted = childOrdinal < mandatoryCount
                ? child.Type == HeaderChunk + (uint)childOrdinal && childOrdinal switch
                {
                    0 => TryReadHeader(payload,out header),
                    1 => legacyUserInfoValid,
                    2 => TryReadInfo(payload,out info),
                    3 => TryReadInfoV2(payload,out infoV2),
                    4 => TryReadProperties(payload,out properties),
                    _ => false,
                }
                : child.Type switch
            {
                RotationKeyframesChunk => TryReadRotation(payload, out rotation, rotation?.Property.Keys[0].Value ?? 0),
                FrameKeyframesChunk => TryReadScalarKeys(payload, FrameHeaderLength, out frame, initialValue: frame?.Property.Keys[0].Value ?? 0),
                // 00915F70 preserves W_Ptorp03b's coincident blur keys; the
                // buffer selects the last key at that time (00961BBC..BCC).
                BlurTimeKeyframesChunk => TryReadScalarKeys(payload, 12, out blur, allowCoincidentTimes: true, initialValue: blur?.Property.Keys[0].Value ?? 0),
                LinePropertiesChunk => TryReadLineProperties(payload, out lineProperties),
                0x050D => TryReadExtension(payload, out initialEmissionTime, out localSpace),
                _ => true,
            };
            if (!accepted)
            {
                diagnostic = $"ParticleEmitter child 0x{child.Type:X4} is out of order or has an invalid payload ({payload.Length} bytes).";
                return false;
            }

            offset = child.End;
            childOrdinal++;
        }

        if (header is null || info is null)
        {
            diagnostic = "ParticleEmitter definition is missing 0x0501 or 0x0503.";
            return false;
        }

        bool legacy = header.Value.Version <= 0x10000;
        if (legacy)
        {
            if (!legacyUserInfoValid)
            {
                diagnostic = "Legacy ParticleEmitter definition has missing or truncated 0x0502 user data.";
                return false;
            }
            // 00915160: preserve the legacy size, start/end colors and fade
            // time. EndSize is not consumed by this conversion. Randomizer
            // class 0 is a box; position randomness is multiplied by .001.
            InfoState old = info.Value;
            if (!float.IsFinite(old.LegacySize) || !float.IsFinite(old.PositionRandom) || !float.IsFinite(old.VelocityRandom))
            {
                diagnostic = "Legacy ParticleEmitter size/randomizer data is non-finite.";
                return false;
            }
            var image = textures is null || string.IsNullOrWhiteSpace(old.TextureFileName)
                ? null : textures.Resolve(old.TextureFileName).Image;
            bool alpha = image?.Format is EnbTextureFormat.DdsDxt2 or EnbTextureFormat.DdsDxt3 or EnbTextureFormat.DdsDxt4 or EnbTextureFormat.DdsDxt5 or
                EnbTextureFormat.CurrentRetailRuntimeArgb4444 or EnbTextureFormat.CurrentRetailMissingArgb8 or
                EnbTextureFormat.CurrentRetailRuntimeArgb8 ||
                image?.TgaPixelDepth is 16 or 32 || image?.CurrentRetailSurfaceFormat is 6 or 7 or 9;
            // 0093B2D0/0093B2E0 initialize 94033/980B3; 009958B0 expands
            // them to the W3D shader bytes. DXT1 deliberately selects additive.
            var shader = new EnbCurrentRetailParticleShader(3, 0, 0,
                (byte)(alpha ? 5 : 1), 0, 0, 0, (byte)(alpha ? 2 : 1), 1, 0, 0, 0, 0, 0, 0, 0);
            infoV2 = new(1, new(0, new(old.PositionRandom * .001f)),
                new(0, new(old.VelocityRandom)), 0, 0, shader, 0, 0);
            properties = new(new(Vector3.Zero, [old.StartColor, old.EndColor with { TimeSeconds = old.FadeTime }]),
                new(0, [new(0, old.StartColor.Reserved / 255f), new(old.FadeTime, old.EndColor.Reserved / 255f)]),
                new(0, [new(0, old.LegacySize)]));
        }
        else if (infoV2 is null || properties is null)
        {
            diagnostic = "ParticleEmitter V2 definition is missing 0x0504 or 0x0505.";
            return false;
        }

        // Retail constructor 00914310 zeroes rotation/frame properties; the
        // child reader 00914FB0 leaves those defaults when 0x050A/0x050B are absent.
        // Older pwarp01/02 definitions rely on this. Present malformed chunks still fail.
        definition = new EnbCurrentRetailParticleEmitterDefinition(
            header.Value.Version,
            header.Value.Name,
            info.Value.TextureFileName,
            info.Value.Lifetime,
            info.Value.EmissionRate,
            info.Value.MaximumEmissions,
            info.Value.Velocity,
            info.Value.Acceleration,
            info.Value.FadeTime,
            info.Value.Gravity,
            info.Value.Elasticity,
            infoV2.Value.BurstSize,
            infoV2.Value.CreationRandomizer,
            infoV2.Value.VelocityRandomizer,
            infoV2.Value.OutwardVelocity,
            infoV2.Value.VelocityInheritance,
            infoV2.Value.Shader,
            infoV2.Value.RenderMode,
            infoV2.Value.FrameMode,
            properties.Value.Color,
            properties.Value.Opacity,
            properties.Value.Size,
            rotation?.Property ?? new(0,[new(0,0)]),
            rotation?.InitialOrientationRandom ?? 0,
            frame?.Property ?? new(0,[new(0,0)]))
        {
            BlurTime = blur?.Property,
            LinePropertyWords = lineProperties,
            InitialEmissionTimeSeconds = initialEmissionTime,
            LocalSpace = localSpace,
            RequiresLegacyTextureShaderResolution = legacy && textures is null,
        };
        return true;
    }

    private static bool TryReadExtension(ReadOnlySpan<byte> payload, out float initialTime, out bool localSpace)
    {
        initialTime = 0; localSpace = false;
        if (payload.Length < 40 || payload[8..40].IndexOfAnyExcept((byte)0) >= 0) return false;
        initialTime = BinaryPrimitives.ReadSingleLittleEndian(payload);
        uint flag = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (!float.IsFinite(initialTime) || initialTime < 0 || flag > 1) return false;
        localSpace = flag != 0;
        return true;
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> payload, out HeaderState? state)
    {
        state = null;
        if (payload.Length < 20)
            return false;

        payload = payload[..20];

        int terminator = payload.Slice(4).IndexOf((byte)0);
        if (terminator <= 0)
            return false;
        string name = Encoding.ASCII.GetString(payload.Slice(4, terminator));
        if (string.IsNullOrWhiteSpace(name))
            return false;

        state = new HeaderState(BinaryPrimitives.ReadUInt32LittleEndian(payload), name);
        return true;
    }

    private static bool TryReadInfo(ReadOnlySpan<byte> payload, out InfoState? state)
    {
        state = null;
        if (payload.Length < InfoLength)
            return false;

        int terminator = payload.Slice(0, 260).IndexOf((byte)0);
        if (terminator < 0)
            return false;

        // Current 0097D9EE loads definition +12C as FLOAT, then __ftol at
        // 0097DA05 supplies the constructor's integer emission limit. The
        // corresponding 0503 field is +114, not an integer bit pattern.
        float maximumEmissions = ReadSingle(payload, 276);
        if (!float.IsFinite(maximumEmissions) || maximumEmissions < int.MinValue || maximumEmissions >= 2147483648d)
            return false;

        state = new InfoState(
            Encoding.ASCII.GetString(payload.Slice(0, terminator)),
            ReadSingle(payload, 268),
            ReadSingle(payload, 272),
            (int)maximumEmissions,
            ReadVector3(payload, 300),
            ReadVector3(payload, 312),
            ReadSingle(payload, 288),
            ReadSingle(payload, 292),
            ReadSingle(payload, 296));
        state = state.Value with
        {
            LegacySize = ReadSingle(payload, 260),
            VelocityRandom = ReadSingle(payload, 280),
            PositionRandom = ReadSingle(payload, 284),
            StartColor = new(0, payload[324], payload[325], payload[326], payload[327]),
            EndColor = new(0, payload[328], payload[329], payload[330], payload[331]),
        };
        return state.Value.IsFinite;
    }

    private static bool TryReadInfoV2(ReadOnlySpan<byte> payload, out InfoV2State? state)
    {
        state = null;
        if (payload.Length < InfoV2Length)
            return false;

        EnbCurrentRetailParticleRandomizer creation = ReadRandomizer(payload, 4);
        EnbCurrentRetailParticleRandomizer velocity = ReadRandomizer(payload, 36);
        var shader = new EnbCurrentRetailParticleShader(
            payload[76], payload[77], payload[78], payload[79],
            payload[80], payload[81], payload[82], payload[83],
            payload[84], payload[85], payload[86], payload[87],
            payload[88], payload[89], payload[90], payload[91]);
        state = new InfoV2State(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            creation,
            velocity,
            ReadSingle(payload, 68),
            ReadSingle(payload, 72),
            shader,
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(92, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(96, 4)));
        return state.Value.IsFinite;
    }

    private static bool TryReadProperties(ReadOnlySpan<byte> payload, out PropertiesState? state)
    {
        state = null;
        if (payload.Length < PropertiesHeaderLength)
            return false;

        uint colorCount = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint opacityCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        uint sizeCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        ulong totalCount = (ulong)colorCount + opacityCount + sizeCount;
        if (colorCount == 0 || opacityCount == 0 || sizeCount == 0 ||
            totalCount > (ulong)((payload.Length - PropertiesHeaderLength) / 8))
        {
            return false;
        }

        Vector3 colorRandom = new(payload[12] / 255f, payload[13] / 255f, payload[14] / 255f);
        float opacityRandom = ReadSingle(payload, 16);
        float sizeRandom = ReadSingle(payload, 20);
        int offset = PropertiesHeaderLength;
        var colors = new EnbCurrentRetailParticleColorKey[(int)colorCount];
        for (int i = 0; i < colors.Length; i++, offset += 8)
        {
            colors[i] = new EnbCurrentRetailParticleColorKey(
                ReadSingle(payload, offset),
                payload[offset + 4], payload[offset + 5], payload[offset + 6], payload[offset + 7]);
        }

        var opacity = new EnbCurrentRetailParticleScalarKey[(int)opacityCount];
        for (int i = 0; i < opacity.Length; i++, offset += 8)
            opacity[i] = new EnbCurrentRetailParticleScalarKey(ReadSingle(payload, offset), ReadSingle(payload, offset + 4));

        var size = new EnbCurrentRetailParticleScalarKey[(int)sizeCount];
        for (int i = 0; i < size.Length; i++, offset += 8)
            size[i] = new EnbCurrentRetailParticleScalarKey(ReadSingle(payload, offset), ReadSingle(payload, offset + 4));

        if (!ValidateColorKeys(colors) || !ValidateScalarKeys(opacity) || !ValidateScalarKeys(size) ||
            !IsFinite(colorRandom) || !float.IsFinite(opacityRandom) || !float.IsFinite(sizeRandom))
        {
            return false;
        }

        state = new PropertiesState(
            new EnbCurrentRetailParticleColorProperty(colorRandom, colors),
            new EnbCurrentRetailParticleScalarProperty(opacityRandom, opacity),
            new EnbCurrentRetailParticleScalarProperty(sizeRandom, size));
        return true;
    }

    private static bool TryReadRotation(ReadOnlySpan<byte> payload, out RotationState? state, float initialValue = 0)
    {
        state = null;
        if (!TryReadScalarKeys(payload, RotationHeaderLength, out ScalarState? scalar, initialValue: initialValue) ||
            scalar is not ScalarState parsed)
            return false;

        float initialOrientationRandom = ReadSingle(payload, 8);
        if (!float.IsFinite(initialOrientationRandom))
            return false;
        state = new RotationState(parsed.Property, initialOrientationRandom);
        return true;
    }

    private static bool TryReadScalarKeys(ReadOnlySpan<byte> payload, int headerLength, out ScalarState? state,
        bool allowCoincidentTimes = false, float initialValue = 0)
    {
        state = null;
        if (payload.Length < headerLength)
            return false;

        uint additionalKeyCount = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        // 00915D80/00915E90/00915F70 only replace the retained initial value after
        // a complete optional 8-byte read. No additional keys means a short
        // optional record is accepted, including after a prior complete chunk.
        if (additionalKeyCount == 0 && payload.Length < headerLength + 8)
        {
            float shortRandom = ReadSingle(payload, 4);
            if (!float.IsFinite(shortRandom)) return false;
            state = new(new(shortRandom, [new(0, initialValue)]));
            return true;
        }
        ulong declaredKeyCount = (ulong)additionalKeyCount + 1;
        if (declaredKeyCount > (ulong)((payload.Length - headerLength) / 8))
            return false;
        int keyCount = (int)declaredKeyCount;

        float random = ReadSingle(payload, 4);
        var keys = new EnbCurrentRetailParticleScalarKey[keyCount];
        int offset = headerLength;
        for (int i = 0; i < keys.Length; i++, offset += 8)
            keys[i] = new EnbCurrentRetailParticleScalarKey(ReadSingle(payload, offset), ReadSingle(payload, offset + 4));
        if (!float.IsFinite(random) || !ValidateScalarKeys(keys, allowCoincidentTimes))
            return false;

        state = new ScalarState(new EnbCurrentRetailParticleScalarProperty(random, keys));
        return true;
    }

    private static bool ValidateColorKeys(IReadOnlyList<EnbCurrentRetailParticleColorKey> keys)
    {
        float previous = -1f;
        foreach (EnbCurrentRetailParticleColorKey key in keys)
        {
            // Installed GE_EN_buff01p2 has two distinct keys at t=0. The native
            // 00915840 reader copies them in authored order; don't merge them or
            // invent interpolation semantics at the asset decoding boundary.
            if (!float.IsFinite(key.TimeSeconds) || key.TimeSeconds < 0f || key.TimeSeconds < previous)
                return false;
            previous = key.TimeSeconds;
        }
        return keys.Count != 0 && keys[0].TimeSeconds == 0f;
    }

    private static bool TryReadLineProperties(ReadOnlySpan<byte> payload, out ImmutableArray<uint> words)
    {
        words = [];
        if (payload.Length < 64) return false;
        var builder = ImmutableArray.CreateBuilder<uint>(16);
        for (int offset = 0; offset < 64; offset += 4)
            builder.Add(BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4)));
        words = builder.MoveToImmutable();
        return true;
    }

    private static bool ValidateScalarKeys(IReadOnlyList<EnbCurrentRetailParticleScalarKey> keys,
        bool allowCoincidentTimes = false)
    {
        float previous = -1f;
        foreach (EnbCurrentRetailParticleScalarKey key in keys)
        {
            if (!float.IsFinite(key.TimeSeconds) || !float.IsFinite(key.Value) ||
                key.TimeSeconds < 0f || key.TimeSeconds < previous ||
                (!allowCoincidentTimes && key.TimeSeconds == previous))
            {
                return false;
            }
            previous = key.TimeSeconds;
        }
        return keys.Count != 0 && keys[0].TimeSeconds == 0f;
    }

    private static EnbCurrentRetailParticleRandomizer ReadRandomizer(ReadOnlySpan<byte> payload, int offset)
        => new(
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4)),
            ReadVector3(payload, offset + 4));

    private static Vector3 ReadVector3(ReadOnlySpan<byte> payload, int offset)
        => new(ReadSingle(payload, offset), ReadSingle(payload, offset + 4), ReadSingle(payload, offset + 8));

    private static float ReadSingle(ReadOnlySpan<byte> payload, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4)));

    private static bool TryReadChunk(ReadOnlySpan<byte> bytes, int offset, int end, out ChunkView chunk)
    {
        chunk = default;
        if (offset < 0 || end < offset || end > bytes.Length || end - offset < ChunkHeaderLength)
            return false;

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
        bool isContainer = (rawLength & 0x8000_0000u) != 0;
        uint payloadLength = rawLength & 0x7FFF_FFFFu;
        if (payloadLength > int.MaxValue)
            return false;

        int payloadStart = offset + ChunkHeaderLength;
        int payloadEnd;
        try
        {
            payloadEnd = checked(payloadStart + (int)payloadLength);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (payloadEnd > end)
            return false;

        chunk = new ChunkView(type, isContainer, payloadStart, (int)payloadLength, payloadEnd);
        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct ChunkView(uint Type, bool IsContainer, int PayloadStart, int PayloadLength, int End)
    {
        public int PayloadEnd => End;
    }

    private readonly record struct HeaderState(uint Version, string Name);

    private readonly record struct InfoState(
        string TextureFileName,
        float Lifetime,
        float EmissionRate,
        int MaximumEmissions,
        Vector3 Velocity,
        Vector3 Acceleration,
        float FadeTime,
        float Gravity,
        float Elasticity)
    {
        public float LegacySize { get; init; }
        public float VelocityRandom { get; init; }
        public float PositionRandom { get; init; }
        public EnbCurrentRetailParticleColorKey StartColor { get; init; }
        public EnbCurrentRetailParticleColorKey EndColor { get; init; }
        public bool IsFinite =>
            float.IsFinite(Lifetime) && float.IsFinite(EmissionRate) &&
            IsFiniteVector(Velocity) && IsFiniteVector(Acceleration) &&
            float.IsFinite(FadeTime) && float.IsFinite(Gravity) && float.IsFinite(Elasticity);

        private static bool IsFiniteVector(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private readonly record struct InfoV2State(
        uint BurstSize,
        EnbCurrentRetailParticleRandomizer CreationRandomizer,
        EnbCurrentRetailParticleRandomizer VelocityRandomizer,
        float OutwardVelocity,
        float VelocityInheritance,
        EnbCurrentRetailParticleShader Shader,
        uint RenderMode,
        uint FrameMode)
    {
        public bool IsFinite =>
            float.IsFinite(OutwardVelocity) && float.IsFinite(VelocityInheritance) &&
            IsFiniteVector(CreationRandomizer.Values) && IsFiniteVector(VelocityRandomizer.Values);

        private static bool IsFiniteVector(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private readonly record struct PropertiesState(
        EnbCurrentRetailParticleColorProperty Color,
        EnbCurrentRetailParticleScalarProperty Opacity,
        EnbCurrentRetailParticleScalarProperty Size);

    private readonly record struct RotationState(
        EnbCurrentRetailParticleScalarProperty Property,
        float InitialOrientationRandom);

    private readonly record struct ScalarState(EnbCurrentRetailParticleScalarProperty Property);
}
