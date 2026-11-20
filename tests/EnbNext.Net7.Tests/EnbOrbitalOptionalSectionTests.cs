using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbOrbitalOptionalSectionTests
{
    [Fact]
    public void AbsentSlotsDoNotDiscardLaterOrbitalsOrCompactTheirIndexes()
    {
        var scene = EnbRetailOrbitalSceneDefinition.Parse(EnbRetailIniDocument.Parse("""
            [ORBITALS]
            NUM_ORBITALS=5
            [ORBITAL001]
            OBJECT=69
            SCALE=2
            [ORBITAL003]
            OBJECT=182
            DISTANCE=900
            [ORBITAL005]
            OBJECT=999
            """));
        Assert.Equal(new[] {1,3},scene.Orbitals.Select(o=>o.Index));
        Assert.Equal(new[] {69,182},scene.Orbitals.Select(o=>o.BaseAsset));
        Assert.Equal(2,scene.Orbitals[0].Scale);
        Assert.Equal(900,scene.Orbitals[1].Distance);
    }

    [Fact]
    public void MissingGlobalsUseNativeEmptySceneDefaults()
    {
        var scene = EnbRetailOrbitalSceneDefinition.Parse(EnbRetailIniDocument.Parse("[ORBITAL000]\nOBJECT=69"));
        Assert.Empty(scene.Orbitals);
        Assert.Equal("LightColor.Earth",scene.LightColorSpline);
        Assert.Equal("SkyColor.Earth",scene.SkyColorSpline);
        Assert.Equal("SkyEmissive.Default",scene.SkyEmissiveSpline);
    }
}
