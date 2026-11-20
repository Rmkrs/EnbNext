using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbTargetAssessmentTests
{
    [Theory]
    [InlineData(null, 2, -1, "Level 25")]
    [InlineData(null, 0, null, "Level 25")]
    [InlineData(-1, 1, -1, "Level 25")]
    [InlineData(0, 1, 0, "Combat Level 0")]
    [InlineData(20, 1, 20, "Combat Level 20")]
    [InlineData(25, 2, 25, "Combat Level 25")]
    [InlineData(25, 0, null, "Level 25")]
    public void HudPreservesAssessmentUnlessActiveLevelOverridesIt(int? value, int state, int? expectedLevel, string expectedText)
    {
        var world = EnbR11FlightTests.FlightWorld();
        var aux = world.Objects.FindFirst(7)!.AuxData!;
        aux.Resolve(35).Value = new Net7AuxDataString(System.Text.Encoding.Latin1.GetBytes("Level 25"));
        aux.Resolve(35).State = 1;
        aux.Resolve(37).Value = value;
        aux.Resolve(37).State = (byte)state;
        var hud = new EnbPresentationSnapshotBuilder().Build(world, null, 1000).Hud;
        Assert.Equal(expectedLevel, hud.TargetThreatLevel);
        Assert.Equal(expectedText, EnbRetailTargetAssessment.Resolve(hud.TargetThreat, hud.TargetThreatLevel, hud.TargetThreatSound).Text);
    }
}
