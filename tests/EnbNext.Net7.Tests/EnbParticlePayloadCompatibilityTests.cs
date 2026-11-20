using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticlePayloadCompatibilityTests
{

    [InstalledRetailTheory]
    [InlineData("FLARE.W3D")] [InlineData("astro00.w3d")] [InlineData("ms02a-pat.w3d")]
    public void NativeChunkHintsTailsAndUnknownOptionalsPreserveDefinition(string name)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.RetailMixResources!.TryReadResource(name,out var bytes,out _));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var original,out var error,assets.Textures),error);
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(ParticleChunkFixture.Extend(bytes),out var changed,out error,assets.Textures),error);
        var options=new System.Text.Json.JsonSerializerOptions{IncludeFields=true};
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original,options),System.Text.Json.JsonSerializer.Serialize(changed,options));
    }

    [Fact]
    public void MandatoryParticlePrefixRequiresOrderAndCompleteHeaderAndUserData()
    {
        var bytes=Legacy();
        var swapped=Chunk(0x500,[..bytes.AsSpan(36,20),..bytes.AsSpan(8,28),..bytes.AsSpan(56)]);
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(swapped,out _,out _));
        var omitted=Chunk(0x500,[..bytes.AsSpan(8,28),..bytes.AsSpan(56)]);
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(omitted,out _,out _));
        var shortHeader=Chunk(0x500,[..Chunk(0x501,bytes.AsSpan(16,19).ToArray()),..bytes.AsSpan(36)]);
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(shortHeader,out _,out _));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48),1);
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out _,out _));
    }

    [Theory]
    [InlineData(0x50Au,16)] [InlineData(0x50Bu,16)] [InlineData(0x50Cu,12)]
    public void RepeatedShortOptionalKeysRetainLastCompleteInitialValue(uint type,int headerLength)
    {
        byte[] first=new byte[headerLength+8],last=new byte[headerLength+3];
        BinaryPrimitives.WriteSingleLittleEndian(first.AsSpan(headerLength+4),.75f);
        BinaryPrimitives.WriteSingleLittleEndian(last.AsSpan(4),.25f);
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(Legacy(Chunk(type,first),Chunk(type,last)),out var d,out var error),error);
        var property=type==0x50A?d.Rotation:type==0x50B?d.Frame:d.BlurTime!;
        Assert.Equal(.25f,property.Random);
        Assert.Equal(new EnbCurrentRetailParticleScalarKey(0,.75f),Assert.Single(property.Keys));
    }

    [InstalledRetailTheory]
    [InlineData("FLARE.W3D")]
    [InlineData("P_PURP.W3D")]
    [InlineData("SGBLAST.W3D")]
    [InlineData("SGSPARKS.W3D")]
    [InlineData("SPARK01.W3D")]
    [InlineData("astro00.w3d")]
    [InlineData("astro01.w3d")]
    [InlineData("astro02.w3d")]
    [InlineData("mwrkblood.w3d")]
    public void InstalledNativeAcceptedPayloadsDecodeWithResolvedShader(string name)
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.RetailMixResources!.TryReadResource(name, out var bytes, out _));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out var d, out var error, assets.Textures), error);
        Assert.False(d.RequiresLegacyTextureShaderResolution);
        Assert.Equal(new EnbCurrentRetailParticleScalarKey(0, 0), Assert.Single(d.Rotation.Keys));
        Assert.Equal(new EnbCurrentRetailParticleScalarKey(0, 0), Assert.Single(d.Frame.Keys));
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes.AsSpan(0, bytes.Length - 1), out _, out _));
    }

    [Fact]
    public void LegacyConversionRetainsNativeCurvesBoxRandomizersAndIgnoresEndSize()
    {
        var bytes = Legacy();
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out var raw, out var error), error);
        Assert.True(raw.RequiresLegacyTextureShaderResolution);
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out var d, out error, new(null)), error);
        Assert.False(d.RequiresLegacyTextureShaderResolution);
        Assert.Equal(1u, d.BurstSize);
        Assert.Equal(0u, d.RenderMode);
        Assert.Equal(0u, d.FrameMode);
        Assert.Equal(new EnbCurrentRetailParticleRandomizer(0, new(2f)), d.CreationRandomizer);
        Assert.Equal(new EnbCurrentRetailParticleRandomizer(0, new(7f)), d.VelocityRandomizer);
        Assert.Equal(new EnbCurrentRetailParticleScalarKey(0, 4), Assert.Single(d.Size.Keys));
        Assert.Equal(new EnbCurrentRetailParticleColorKey[] { new(0, 10, 20, 30, 255), new(.5f, 40, 50, 60, 0) }, d.Color.Keys);
        Assert.Equal(new EnbCurrentRetailParticleScalarKey[] { new(0, 1), new(.5f, 0) }, d.Opacity.Keys);
        Assert.Equal(Vector3.Zero, d.Color.Random);
        Assert.Equal((byte)1, d.Shader.SourceBlend);
        Assert.Equal((byte)1, d.Shader.DestinationBlend);
        // Switching the same mandatory prefix to V2 requires its V2 data.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x20000);
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out _, out _));
    }

    [Theory]
    [InlineData(0x50Au)]
    [InlineData(0x50Bu)]
    public void ShortInitialRecordRetainsZeroButAdditionalRecordRemainsMandatory(uint type)
    {
        foreach (int length in Enumerable.Range(16, 8))
        {
            byte[] scalar = new byte[length];
            BinaryPrimitives.WriteSingleLittleEndian(scalar.AsSpan(4), .25f);
            BinaryPrimitives.WriteSingleLittleEndian(scalar.AsSpan(8), .75f);
            Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(Legacy(Chunk(type, scalar)), out var d, out var error), error);
            var property = type == 0x50A ? d.Rotation : d.Frame;
            Assert.Equal(.25f, property.Random);
            Assert.Equal(new EnbCurrentRetailParticleScalarKey(0, 0), Assert.Single(property.Keys));
            if (type == 0x50A) Assert.Equal(.75f, d.InitialOrientationRandom);
            BinaryPrimitives.WriteUInt32LittleEndian(scalar, 1);
            Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(Legacy(Chunk(type, scalar)), out _, out _));
        }
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(Legacy(Chunk(type, new byte[15])), out _, out _));
        var keys = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(keys, 1);
        BinaryPrimitives.WriteSingleLittleEndian(keys.AsSpan(20), 2);
        BinaryPrimitives.WriteSingleLittleEndian(keys.AsSpan(24), .5f);
        BinaryPrimitives.WriteSingleLittleEndian(keys.AsSpan(28), 3);
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(Legacy(Chunk(type, keys)), out var full, out _));
        Assert.Equal(new EnbCurrentRetailParticleScalarKey[] { new(0, 2), new(.5f, 3) }, (type == 0x50A ? full.Rotation : full.Frame).Keys);
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(Legacy(Chunk(type, keys[..24])), out _, out _));
    }

    [InstalledRetailFact]
    public void NativeRejectedInstalledAdditionalRotationKeyStillFails()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.RetailMixResources!.TryReadResource("nebspew_p01.w3d", out var bytes, out _));
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out _, out var error, assets.Textures));
        Assert.Contains("0x050A", error);
    }

    [Theory]
    [InlineData(16, 2, 5)]
    [InlineData(24, 1, 1)]
    [InlineData(32, 2, 5)]
    public void LegacyShaderUsesAuthoredTextureFormatEvenWhenEveryPixelIsOpaque(int bits, byte source, byte destination)
    {
        string directory = Path.Combine(Path.GetTempPath(), "EnbParticleFormat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] tga = new byte[18 + bits / 8];
            tga[2] = 2; tga[12] = 1; tga[14] = 1; tga[16] = (byte)bits; tga[17] = 0x20;
            tga.AsSpan(18).Fill(255);
            using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, "mixfile_art_01.mix"))))
            {
                uint table = (uint)(16 + tga.Length);
                writer.Write(0x3158494Du); writer.Write(table); writer.Write(table + 16); writer.Write(0u);
                writer.Write(tga); writer.Write(1u); writer.Write(1u); writer.Write(16u); writer.Write((uint)tga.Length);
                writer.Write(1u); byte[] name = Encoding.ASCII.GetBytes("legacy.tga"); writer.Write((byte)name.Length); writer.Write(name);
            }
            var textures = new EnbRetailTextureResolver(EnbMixResourceCatalog.Load(directory)!);
            Assert.Equal(bits, textures.Resolve("legacy.tga").Image!.TgaPixelDepth);
            Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(Legacy(), out var d, out var error, textures), error);
            Assert.Equal(source, d.Shader.SourceBlend); Assert.Equal(destination, d.Shader.DestinationBlend);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static byte[] Legacy(params byte[][] extra)
    {
        byte[] header = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(header, 0x10000);
        Encoding.ASCII.GetBytes("legacy\0").CopyTo(header, 4);
        byte[] info = new byte[332]; Encoding.ASCII.GetBytes("legacy.tga\0").CopyTo(info, 0);
        foreach (var (offset, value) in new[] { (260, 4f), (264, 99f), (268, 1f), (272, 10f), (280, 7f), (284, 2000f), (288, .5f) })
            BinaryPrimitives.WriteSingleLittleEndian(info.AsSpan(offset), value);
        new byte[] { 10, 20, 30, 255, 40, 50, 60, 0 }.CopyTo(info, 324);
        return Chunk(0x500, new[] { Chunk(0x501, header), Chunk(0x502, new byte[12]), Chunk(0x503, info) }.Concat(extra).SelectMany(x => x).ToArray());
    }

    private static byte[] Chunk(uint type, byte[] payload)
    {
        byte[] result = new byte[payload.Length + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(result, type);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(result, 8); return result;
    }
}
