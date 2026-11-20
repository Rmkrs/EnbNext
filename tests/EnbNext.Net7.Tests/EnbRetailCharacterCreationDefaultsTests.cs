using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailCharacterCreationDefaultsTests
{
    [Fact]
    public void AvatarUsesInstalledDefinitionAndRetailBaseDefaults()
    {
        var ini = EnbRetailIniDocument.Parse("""
            [female_progen_warrior]
            Personality=6
            BodyType=2
            HeadTextureNum=3
            HairColor=.1,.2,.3
            BodyWeight0=.75
            BodyWeight1=.25
            ShirtPrimaryMetal=-1
            """);
        var avatar = EnbRetailCharacterCreationDefaults.Avatar(ini, 2, 0, 1);
        Assert.Equal((uint)2, avatar.Race);
        Assert.Equal((uint)1, avatar.Gender);
        Assert.Equal((byte)6, avatar.Personality);
        Assert.Equal(avatar.HairColor, avatar.BeardColor);
        Assert.NotSame(avatar.HairColor, avatar.BeardColor);
        Assert.Equal(new float[] { 0, 0, 1 }, avatar.TattooOffset);
        Assert.Equal(new uint[] { 0x3f400000, 0x3e800000, 0, 0, 0 }, avatar.BodyWeight);
        Assert.Empty(avatar.Skeleton);
        Assert.Equal(0xf1, Net7AvatarCreateCodec.EncodeAvatarData(avatar).Length);
    }

    [Theory]
    [InlineData(0u, new uint[] { 0x3f000000, 0x3e99999a, 0x3e800000, 0x3e800000, 0x3e800000 })]
    [InlineData(1u, new uint[] { 0x3f000000, 0x3e800000, 0x3e4ccccd, 0x3e19999a, 0x3dcccccd })]
    public void ClassPreviewAppliesRetailHeadWeightsWithoutChangingDefaults(uint gender, uint[] expected)
    {
        var ini = EnbRetailIniDocument.Parse($"""
            [{(gender == 0 ? "male" : "female")}_terran_warrior]
            HeadWeight0=.1
            HeadWeight1=.3
            HeadWeight2=.2
            HeadWeight3=.15
            HeadWeight4=.1
            """);
        var ordinary = EnbRetailCharacterCreationDefaults.Avatar(ini, 0, 0, gender);
        var preview = EnbRetailCharacterCreationDefaults.ClassPreviewAvatar(ini, 0, 0, gender);
        Assert.Equal(expected, preview.HeadWeight);
        Assert.Equal(0x3dcccccdu, ordinary.HeadWeight[0]);
    }

    [Theory]
    [InlineData(0u, 0u, 1u, 0u, 0x3d6147aeu, 0u, uint.MaxValue)]
    [InlineData(1u, 2u, 2u, 0u, 0u, uint.MaxValue, 0u)]
    [InlineData(2u, 1u, 0u, 0x3ef0a3d7u, 0u, uint.MaxValue, 0u)]
    public void ShipMatchesRetailStaticInitializer(uint race, uint profession, uint style,
        uint primaryRed, uint secondaryRed, uint primaryMetal, uint secondaryMetal)
    {
        var bytes = EnbRetailCharacterCreationDefaults.Ship(race, profession).ToArray();
        uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        Assert.Equal(194, bytes.Length);
        Assert.Equal(race, Word(0)); Assert.Equal(profession, Word(4));
        Assert.Equal(style, Word(8)); Assert.Equal(style, Word(12));
        Assert.Equal(0x07ffffffu, Word(16));
        Assert.All(bytes[20..46], b => Assert.Equal((byte)0, b));
        Assert.Equal(0x3f800000u, Word(46));
        for (int part = 0; part < 4; part++)
        {
            Assert.Equal(primaryRed, Word(58 + part * 34));
            Assert.Equal(secondaryRed, Word(75 + part * 34));
            Assert.Equal(primaryMetal, Word(58 + part * 34 + 13));
            Assert.Equal(secondaryMetal, Word(75 + part * 34 + 13));
            Assert.Equal(bytes[58..92], bytes[(58 + part * 34)..(92 + part * 34)]);
        }
    }

    [Theory]
    [InlineData(0u, 0, 0x3f000000u, 0x3f000000u)]
    [InlineData(0u, 1, 0x3ee147aeu, 0x3f0dd2f2u)]
    [InlineData(1u, 0, 0x3e1db22du, 0x3e77ced9u)]
    [InlineData(1u, 1, 0x3f000000u, 0x3f000000u)]
    [InlineData(2u, 0, 0x3f13b646u, 0x3f06a7f0u)]
    [InlineData(2u, 1, 0x3f000000u, 0x3f000000u)]
    public void ShipPaintPaletteUvMatchesRetailInitializer(uint race, int channel, uint u, uint v)
    {
        var point=EnbRetailCharacterCreationDefaults.ShipPaintPaletteUv(race,channel);
        Assert.Equal(u,BitConverter.SingleToUInt32Bits(point.U));
        Assert.Equal(v,BitConverter.SingleToUInt32Bits(point.V));
    }
}
