using System.Numerics;
using EnbNext.Rendering;
using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbSoftwareSpatialSoundTests
{
    [InstalledRetailTheory]
    [InlineData("1077_Gate_War_Hulk",5000f,1000f)]
    [InlineData("1115_Aganju",950000f,100000f)]
    public void AuthoredReversedRadiiAreSubmittedWithoutInventedOrderingRejection(string name,float full,float drop)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var definition=assets.Sounds!.GetDefinitionOrFile(name);
        Assert.Equal(full,definition.EffectiveMaxVolumeRadius);
        Assert.Equal(drop,definition.EffectiveDropOffRadius);
        //0043E62E/+78 and0043E640/+198 submit both authored radii unchanged.
        Assert.Null(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(definition));
    }
    [Fact]
    public void StereoBalancePreservesChannelsAndUsesNativeVolumeSteps()
    {
        var center=EnbCurrentRetailSoftwareSpatialSound.Gains(new(100,0,0),new(0,0,-100),1,1500,2500);
        Assert.Equal((2047/2048f,2047/2048f),center);
        var right=EnbCurrentRetailSoftwareSpatialSound.Gains(new(0,-100,0),new(100,0,0),1,1500,2500);
        var left=EnbCurrentRetailSoftwareSpatialSound.Gains(new(0,100,0),new(-100,0,0),1,1500,2500);
        // Native float phase and interpolated table truncate the rightward
        // pan to 126, retaining Miles coefficient 31 on the opposite channel.
        Assert.Equal(31/2048f,right.Left);Assert.Equal(center.Right,right.Right);
        Assert.Equal(center.Left,left.Left);Assert.Equal(0,left.Right);
        var half=EnbCurrentRetailSoftwareSpatialSound.Gains(new(2000,0,0),new(0,0,-2000),1,1500,2500);
        Assert.Equal((1008/2048f,1008/2048f),half);
        Assert.Equal((0f,0f),EnbCurrentRetailSoftwareSpatialSound.Gains(new(2500,0,0),new(0,0,-2500),1,1500,2500));
        Assert.Equal((0f,0f),EnbCurrentRetailSoftwareSpatialSound.Gains(Vector3.Zero,Vector3.Zero,0,1500,2500));
    }
}
