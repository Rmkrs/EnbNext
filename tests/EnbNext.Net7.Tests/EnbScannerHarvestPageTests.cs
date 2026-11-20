using System.Reflection;
using EnbNext.Engine;
using EnbNext.Presentation;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbScannerHarvestPageTests
{
    [Theory]
    [InlineData(0x26, 1, true)]
    [InlineData(0x26, -1, true)]
    [InlineData(0x26, 0, false)]
    [InlineData(0x25, 5, false)]
    public void HarvestContentsPageFollowsTypeAndNonZeroProspectLevel(int type, int level, bool available)
    {
        // 005BF790: the contents page exists only for object type '&' (006F5E20) and a
        // Prospect skill level that is non-zero (+0x120 compared with 0, not > 0).
        var ui = new EnbScannerUi((_, _) => Task.FromResult(true), (_, _, _) => Task.FromResult(true));
        var scene = new EnbSceneFrame(); scene.Hud.ProspectSkillLevel = level;
        typeof(EnbScannerUi).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ui, scene);
        typeof(EnbScannerUi).GetField("_target", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ui, new EnbRenderObjectState { ObjectType = (byte)type });
        ui.ToggleHarvestContents();
        Assert.Equal(available, !ui.ShowTargetModel);
    }

    [Fact]
    public void LeftClickUsesTheSameScannerZoneAsRightClickIncludingTheLowerRight()
    {
        // 005BC5E0 command 1 serves both buttons; bit 0x10 only selects clear-target.
        var ui = new EnbScannerUi((_, _) => Task.FromResult(true), (_, _, _) => Task.FromResult(true));
        var scene = new EnbSceneFrame(); scene.Hud.ProspectSkillLevel = 1;
        typeof(EnbScannerUi).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ui, scene);
        typeof(EnbScannerUi).GetField("_target", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ui, new EnbRenderObjectState { ObjectType = 0x26 });
        Assert.True(ui.PressPanel(new(80, 115), 1));
        Assert.False(ui.ShowTargetModel);
    }
}
