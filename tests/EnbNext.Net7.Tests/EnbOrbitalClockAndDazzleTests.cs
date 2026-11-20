using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;
public sealed class EnbOrbitalClockAndDazzleTests
{
    [Fact]
    public void DirectionalDefinitionRetainsAuthoredVectorWithoutNormalization()
    {
        const string text="[D]\nHistoryWeight=0\nUseCameraTranslation=0\nDazzleDirectionArea=.25\nDazzleDirection=2,3,4\nLensflareName=L\n[L]\nFlareCount=0";
        var d=EnbDazzleDefinition.Load(EnbRetailIniDocument.Parse(text),"D");
        Assert.Equal(.25f,d.DirectionArea);
        Assert.Equal(new System.Numerics.Vector3(2,3,4),d.Direction);
        Assert.Equal(System.Numerics.Vector3.Zero,EnbDazzleDefinition.Load(
            EnbRetailIniDocument.Parse(text.Replace("DazzleDirection=2,3,4\n","")),"D").Direction);
    }
    [Theory]
    [InlineData(0, 1)]
    [InlineData(70000000, 1)]
    [InlineData(75000000, .5f)]
    [InlineData(80000000, 0)]
    [InlineData(90000000, 0)]
    public void TranslatedSunFadesAtAuthoredDistanceBoundaries(float distance, float expected)
    {
        var text = "[SUN]\nDazzleDirectionArea=0\nHistoryWeight=0\nUseCameraTranslation=1\nHaloIntensity=0\nHaloScaleX=0\nHaloScaleY=0\nFadeoutStart=7e7\nFadeoutEnd=8e7\nLensflareName=LENS\n[LENS]\nFlareCount=0";
        var sun = EnbDazzleDefinition.Load(EnbRetailIniDocument.Parse(text), "SUN");
        Assert.Equal(expected, sun.DistanceIntensityFactor(distance * distance), 5);
        Assert.Equal(1, (sun with {UseCameraTranslation=false}).DistanceIntensityFactor(distance * distance));
        var zeroAreaHalo = EnbDazzleDefinition.Load(
            EnbRetailIniDocument.Parse(text.Replace("HaloIntensity=0", "HaloIntensity=1")), "SUN");
        Assert.Equal(expected, zeroAreaHalo.DistanceIntensityFactor(distance * distance), 5);
        var visibleHalo = EnbDazzleDefinition.Load(
            EnbRetailIniDocument.Parse(text.Replace("HaloIntensity=0", "HaloIntensity=1")
                .Replace("HaloScaleX=0", "HaloScaleX=1").Replace("HaloScaleY=0", "HaloScaleY=1")), "SUN");
        Assert.Equal(expected, visibleHalo.DistanceIntensityFactor(distance * distance), 5);
    }

    [Fact]
    public void DifferentSessionClocksResolveTheSameServerOrbitalTime()
    {
        EnbSceneSnapshot Snapshot(uint local,uint server)
        {
            var world=new Net7WorldState();
            byte[] packet=new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(packet,local);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4),server);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8),server);
            world.Time.ApplySetClientTime(packet,local);
            return new EnbPresentationSnapshotBuilder().Build(world,null,local);
        }
        var first=Snapshot(40000,1200000000);
        var relog=Snapshot(2000,1200000000);
        Assert.Equal(1200000100,first.GetOrbitalTimeMilliseconds(40100));
        Assert.Equal(first.GetOrbitalTimeMilliseconds(40100),relog.GetOrbitalTimeMilliseconds(2100));
        Assert.Equal(40000u,first.ClientTime);
        Assert.Equal(int.MaxValue,Snapshot(0,3300000000).GetOrbitalTimeMilliseconds(0));
        Assert.Equal(int.MinValue, (first with {ServerTimeCorrectionMilliseconds=-3000000000}).GetOrbitalTimeMilliseconds(100));
    }
    [Fact]
    public void DazzleReadsAuthoredAtlasCoordinatesAndColor()
    {
        var ini=EnbRetailIniDocument.Parse("""
            [STAR]
            DazzleDirectionArea=0
            HistoryWeight=0
            UseCameraTranslation=0
            DazzleColor=.93,.95,.75
            LensflareName=LENS
            [LENS]
            TextureName=authored.tga
            FlareCount=1
            FlareLocation1=-1.8
            FlareSize1=.3
            FlareUV1=.496,0,1,1
            """);
        var d=EnbDazzleDefinition.Load(ini,"STAR");
        Assert.Equal(.93f,d.Color.X);
        Assert.Equal("authored.tga",d.FlareTexture);
        var flare=Assert.Single(d.Flares);
        Assert.Equal(-1.8f,flare.Location);
        Assert.Equal(.496f,flare.Uv.X);
        Assert.Equal(1,flare.Uv.W);
    }
}
