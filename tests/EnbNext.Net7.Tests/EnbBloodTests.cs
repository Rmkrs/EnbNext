using EnbNext.Assets;
using EnbNext.Rendering;
using System.Numerics;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbBloodTests
{
    [Fact]
    public void InstalledOrganicBloodLoadsAllDamageTiers()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not { } paths) return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var blood=assets.Blood!.Resolve(393,true);
        foreach(var name in blood.Levels)
        {
            var asset=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,name,blood.Hsv);
            Assert.NotNull(asset.Mixed);
            Assert.NotEmpty(asset.Mixed.Sprites);
            Assert.NotEmpty(asset.Mixed.Streaks);
        }
    }
    [Theory]
    [InlineData(1f,0)]
    [InlineData(.99f,1)]
    [InlineData(.76f,1)]
    [InlineData(.75f,2)]
    [InlineData(.51f,2)]
    [InlineData(.5f,3)]
    [InlineData(0f,3)]
    public void NativeDamageThresholds(float remaining,int level)
        => Assert.Equal(level,EnbBloodCatalog.DamageLevel(remaining));

    [Fact]
    public void MissingHealthDoesNotInventDamage()
    {
        Assert.Equal(0,EnbBloodCatalog.DamageLevel(null));
        Assert.Equal(0,EnbBloodCatalog.DamageLevel(float.NaN));
    }

    [Fact]
    public void AuthoredOverrideAndOrganicDefault()
    {
        var catalog=new EnbBloodCatalog(EnbRetailIniDocument.Parse("[42]\nBloodType=2\n[Blood0]\nLevel0=metal\n[Blood1]\nLevel0=red\n[Blood2]\nLevel0=green\nScale=2\nHue=0.25"));
        Assert.Equal("red",catalog.Resolve(1,true).Levels[0]);
        Assert.Equal("metal",catalog.Resolve(1,false).Levels[0]);
        var selected=catalog.Resolve(42,true);
        Assert.Equal("green",selected.Levels[0]);
        Assert.Equal(2,selected.Scale);
        Assert.Equal(.25f,selected.Hsv.X);
    }

    [Fact]
    public void RepeatedBoneSelectionReplacesEarlierTier()
    {
        Assert.Equal(new[]{(0,0),(0,0),(0,1),(0,1),(0,2),(0,2)},
            EnbBloodCatalog.SelectAttachments(0x187B3,1,3));
        var selected=EnbBloodCatalog.SelectBones(0x187B3,1,3);
        Assert.Single(selected);
        Assert.Equal(2,selected[0]);
        Assert.Equal(EnbBloodCatalog.SelectBones(0x187B3,20,3),EnbBloodCatalog.SelectBones(0x187B3,20,3));
    }
}
