using System.Numerics;
using System.Reflection;
using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // resolves the installed radar font for the gutter labels
public sealed class EnbInstalledGutterLabelTests
{
    [Fact]
    public void NameGuildPairFollowsSelectionOrOnScreenPlayerWhateverItsText()
    {
        // 007B63C0 shows the name/guild pair for the selected object or an on-screen player
        // (object +0x94 == 1) and writes the 006FF7A0 text verbatim, so an empty name keeps
        // the guild line; 7B50D0 has no hexadecimal identity fallback.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var gutter = new EnbGutterUi((_, _) => Task.FromResult(true), () => null);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(EnbGutterUi).GetMethod("ResetAssets", instance)!.Invoke(gutter, [assets]);
        var marker = (EnbGutterUi.Marker)typeof(EnbGutterUi).GetMethod("CreateMarker", instance)!.Invoke(gutter, [1UL])!;
        var player = new EnbRadarContact(1, 42, 1, "", Vector3.Zero, 1, "", default, 0, 0, 0, 0,
            false, false, false, false, "[Guild]", null, null);
        marker.Contact = player; marker.DisplayName = ""; marker.X = 400; marker.Y = 300;
        MethodInfo persistent = typeof(EnbGutterUi).GetMethod("UpdatePersistentPlayerLabel", instance)!;
        persistent.Invoke(gutter, [marker, 1600.0, 1200.0]);
        Assert.True(marker.PersistentLabel.IsVisible);
        marker.Contact = player with { ObjectType = 0 };
        persistent.Invoke(gutter, [marker, 1600.0, 1200.0]);
        Assert.False(marker.PersistentLabel.IsVisible);
        MethodInfo hover = typeof(EnbGutterUi).GetMethod("UpdateHoverLabel", instance)!;
        hover.Invoke(gutter, [marker, 1600.0, 1200.0]);
        Assert.True(string.IsNullOrEmpty(gutter.HoverLabel.First.Text));
    }
}
