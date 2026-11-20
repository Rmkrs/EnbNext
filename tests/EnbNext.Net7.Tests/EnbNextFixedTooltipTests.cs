using System.Buffers.Binary;
using System.Collections.Immutable;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbNextFixedTooltipTests
{
    private static readonly EnbUiSize Screen = new(1280, 720);

    [Fact]
    public void AFixedTooltipKeepsToTheAreasCornerNearestTheScreenEdgesAndGrowsInwards()
    {
        var tooltip = new EnbUiRect(500, 300, 240, 90);
        // Bottom-right area: the tooltip's bottom-right corner is the area's.
        var placed = EnbTooltipPlacement.Fixed(tooltip, new(1060, 440, 200, 60), Screen);
        Assert.Equal(new EnbUiRect(1020, 410, 240, 90), placed);
        // Top-left area: its top-left corner.
        placed = EnbTooltipPlacement.Fixed(tooltip, new(20, 30, 200, 60), Screen);
        Assert.Equal(new EnbUiRect(20, 30, 240, 90), placed);
    }

    [Fact]
    public void AFixedTooltipNeverLeavesTheScreen()
    {
        var placed = EnbTooltipPlacement.Fixed(new(0, 0, 400, 300), new(1200, 650, 200, 60), Screen);
        Assert.True(placed.Right <= Screen.Width && placed.Bottom <= Screen.Height && placed.X >= 0 && placed.Y >= 0);
    }

    [Fact]
    public void TheSectorExtentIsReadFromServerParametersAfterTheZBand()
    {
        var raw = new byte[70];
        float[] values = [-500, 500, -2000, -3000, 4000, 5000];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4), values[i]);
        var environment = new EnbSceneEnvironmentSnapshot(0, false, 0, [.. raw], null, 0, 0, false, false, 0, 0, null, 0);
        Assert.Equal((new System.Numerics.Vector2(-2000, -3000), new System.Numerics.Vector2(4000, 5000)), environment.SectorExtent);
        Assert.Null((environment with { RawServerParameters = ImmutableArray<byte>.Empty }).SectorExtent);
    }
}
