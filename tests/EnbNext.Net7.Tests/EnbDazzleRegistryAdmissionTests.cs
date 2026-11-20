using EnbNext.Assets;
using EnbNext.Rendering;
using System.Numerics;

namespace EnbNext.Net7.Tests;

public sealed class EnbDazzleRegistryAdmissionTests
{
    [Fact]
    public void FlareDefaultsAndAuthoredColorReachPackedVertexColor()
    {
        var ini=EnbRetailIniDocument.Parse(Ini.Replace("FlareSize1=.2","FlareColor1=.25,.5,2"));
        var flare=Assert.Single(EnbDazzleDefinition.Resolve(ini,"SECOND").Flares);
        Assert.Equal(1,flare.Size);
        Assert.Equal(new Vector3(.25f,.5f,2),flare.Color);
        Assert.Equal(new Vector3(31/255f,63/255f,1),EnbRetailDazzleFlareColor.Evaluate(flare.Color,.5f));
        Assert.Equal(Vector3.One,Assert.Single(EnbDazzleDefinition.Resolve(EnbRetailIniDocument.Parse(Ini),"SECOND").Flares).Color);
        Assert.Equal(Vector3.Zero,EnbRetailDazzleFlareColor.Evaluate(flare.Color,0));
    }

    private const string Ini="""
        [Dazzles_List]
        first=BASE
        99=SECOND
        [Lensflares_List]
        authored=LENS
        [BASE]
        HistoryWeight=.975
        HaloIntensity=1
        [SECOND]
        LensflareName=LENS
        [UNREGISTERED]
        LensflareName=LENS
        [LENS]
        TextureName=flare.tga
        FlareCount=1
        FlareSize1=.2
        """;

    [Theory]
    [InlineData("BASE","BASE",0)]
    [InlineData("SECOND","SECOND",1)]
    [InlineData("second","BASE",0)]
    [InlineData("UNREGISTERED","BASE",0)]
    [InlineData("missing","BASE",0)]
    public void RegistryUsesAuthoredOrderExactNamesAndIndexZeroFallback(string requested,string expected,int flares)
    {
        var actual=EnbDazzleDefinition.Resolve(EnbRetailIniDocument.Parse(Ini),requested);
        Assert.Equal(expected,actual.Type);Assert.Equal(flares,actual.Flares.Count);
        if(expected=="BASE"){Assert.Equal(.975f,actual.HistoryWeight);Assert.Equal("",actual.FlareTexture);}
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("lens")]
    public void UnmatchedLensNameDisablesOnlyGhosts(string lens)
    {
        var actual=EnbDazzleDefinition.Resolve(EnbRetailIniDocument.Parse(Ini.Replace("LensflareName=LENS","LensflareName="+lens)),"SECOND");
        Assert.Equal("SECOND",actual.Type);Assert.Empty(actual.Flares);Assert.Equal("",actual.FlareTexture);
        Assert.Equal(.95f,actual.HaloIntensity);
    }
}
