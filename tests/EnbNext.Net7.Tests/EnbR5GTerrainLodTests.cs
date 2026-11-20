using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR5GTerrainLodTests
{
    [Fact]
    public void LandLodUsesExactCurrentRetailBaseDistanceTable()
    {
        int[] expected = [8, 16, 24, 32, 48, 64, 96, 128];

        for (int landLod = 0; landLod < expected.Length; landLod++)
            Assert.Equal(expected[landLod], EnbTerrainLodSemantics.GetBaseDistanceThreshold(landLod));
    }

    [Fact]
    public void PatchSelectionUsesStrictThresholdsAndCapsAtLevelFour()
    {
        Assert.Equal(0, EnbTerrainLodSemantics.SelectPatchLevel(7f, landLod: 0));
        Assert.Equal(1, EnbTerrainLodSemantics.SelectPatchLevel(8f, landLod: 0));
        Assert.Equal(1, EnbTerrainLodSemantics.SelectPatchLevel(15f, landLod: 0));
        Assert.Equal(2, EnbTerrainLodSemantics.SelectPatchLevel(16f, landLod: 0));
        Assert.Equal(3, EnbTerrainLodSemantics.SelectPatchLevel(32f, landLod: 0));
        Assert.Equal(4, EnbTerrainLodSemantics.SelectPatchLevel(64f, landLod: 0));
        Assert.Equal(4, EnbTerrainLodSemantics.SelectPatchLevel(10_000f, landLod: 0));
    }

    [Fact]
    public void DistanceIsTruncatedBeforeTerrainThresholdComparison()
    {
        Assert.Equal(0, EnbTerrainLodSemantics.SelectPatchLevel(7.9999f, landLod: 0));
        Assert.Equal(1, EnbTerrainLodSemantics.SelectPatchLevel(8.9999f, landLod: 0));
        Assert.Equal(1, EnbTerrainLodSemantics.SelectPatchLevel(15.9999f, landLod: 0));
        Assert.Equal(2, EnbTerrainLodSemantics.SelectPatchLevel(16.0000f, landLod: 0));
    }

    [Fact]
    public void LandLodTwoUsesTwentyFourThenDoublesFourTimes()
    {
        Assert.Equal(0, EnbTerrainLodSemantics.SelectPatchLevel(23f, landLod: 2));
        Assert.Equal(1, EnbTerrainLodSemantics.SelectPatchLevel(24f, landLod: 2));
        Assert.Equal(2, EnbTerrainLodSemantics.SelectPatchLevel(48f, landLod: 2));
        Assert.Equal(3, EnbTerrainLodSemantics.SelectPatchLevel(96f, landLod: 2));
        Assert.Equal(4, EnbTerrainLodSemantics.SelectPatchLevel(192f, landLod: 2));
    }

    [Fact]
    public void HigherLandLodKeepsTheSamePatchAtFinerDetail()
    {
        const float distance = 70f;

        Assert.Equal(4, EnbTerrainLodSemantics.SelectPatchLevel(distance, landLod: 0));
        Assert.Equal(0, EnbTerrainLodSemantics.SelectPatchLevel(distance, landLod: 7));
    }

    [Fact]
    public void NeighborMaskMarksOnlyEdgesWhoseNeighborIsFiner()
    {
        EnbTerrainLodNeighborMask mask = EnbTerrainLodSemantics.BuildNeighborMask(
            currentLevel: 3,
            negativeU: 2,
            positiveU: 3,
            negativeV: 4,
            positiveV: 0);

        Assert.Equal(
            EnbTerrainLodNeighborMask.NegativeU | EnbTerrainLodNeighborMask.PositiveV,
            mask);
    }


    [Fact]
    public void TerrainLodBoundaryRejectsImpossibleDistanceInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnbTerrainLodSemantics.SelectPatchLevel(-1f, landLod: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnbTerrainLodSemantics.SelectPatchLevel(float.NaN, landLod: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnbTerrainLodSemantics.SelectPatchLevel(float.PositiveInfinity, landLod: 0));
    }
}
