using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLineEffectTilingTests
{
    private static EnbRetailLineEffectTiling Create(float minimum,float tile=8,int subdivisions=6)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=LineEffect\nTilingMinLength={minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return new(catalog.FindEffect(1)!,tile,subdivisions);
    }
    [Fact]
    public void ZeroLengthWithOverflowedReciprocalKeepsNativeZeroTileAndSubdivisionLimit()
    {
        var state=Create(float.Epsilon);state.Update(Vector3.Zero,Vector3.Zero);
        Assert.Equal(0,state.Tile);Assert.Equal(0,state.Subdivisions);
    }
    [Theory]
    [InlineData(0,0,0)] [InlineData(12.5,1,4)]
    [InlineData(25,2,5)] [InlineData(50,4,6)]
    [InlineData(75,6,6)] [InlineData(100,8,6)] [InlineData(150,8,6)]
    public void ShortLinesAdjustTileAndSubdivisionLimit(float length,float tile,int subdivisions)
    {
        var state=Create(100);state.Update(Vector3.Zero,new(length,0,0));
        Assert.Equal(tile,state.Tile);Assert.Equal(subdivisions,state.Subdivisions);
    }
    [Fact]
    public void EachInstanceRetainsItsOwnLastShortLineValuesAboveTheThreshold()
    {
        var state=Create(100);var other=Create(100);
        state.Update(Vector3.Zero,new(25,0,0));state.Update(Vector3.Zero,new(150,0,0));
        Assert.Equal(2,state.Tile);Assert.Equal(5,state.Subdivisions);
        Assert.Equal(8,other.Tile);Assert.Equal(6,other.Subdivisions);
        state.Update(Vector3.Zero,new(75,0,0));Assert.Equal(6,state.Tile);Assert.Equal(6,state.Subdivisions);
    }
    [Theory]
    [InlineData(0)] [InlineData(-10)]
    public void NonpositiveMinimumLeavesAuthoredValues(float minimum)
    {
        var state=Create(minimum);state.Update(Vector3.Zero,new(1,0,0));
        Assert.Equal(8,state.Tile);Assert.Equal(6,state.Subdivisions);
    }
}
