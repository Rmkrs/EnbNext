using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10BuffTests
{
    [Fact]
    public void DefinitionsUseAuthoredNamesAssetsAndEscapedDescriptions()
    {
        var catalog = EnbRetailBuffCatalog.Parse(EnbRetailIniDocument.Parse("""
            [BUFF-0]
            BuffType=Cloak
            BuffAsset=BUF_CLOAKG
            BuffToolTip=Cloak
            BuffAltToolTip=You are invisible.\n
            IsGoodBuff=TRUE
            [BUFF-1]
            BuffType=Debuff
            BuffAsset=BUF_DEBUFF
            BuffToolTip=Authored name
            IsGoodBuff=FALSE
            """));
        Assert.Equal(new("Cloak","BUF_CLOAKG","Cloak","You are invisible.\n",true),catalog.Definitions["Cloak"]);
        Assert.False(catalog.Definitions["Debuff"].IsGood);
        Assert.False(catalog.Definitions.ContainsKey("unknown"));
    }

    [Fact]
    public void TooltipPreservesSourceOrderAndActiveMarkersWithoutInventingNumbers()
    {
        var elements = new List<Net7BuffElementPresentation>
        { new("Lorendavin","Cloak",9,true,1000),new("","Device",999,false,2000) };
        var buff = EnbSceneBuffSnapshot.From(new(14,"Cloak"," Stealth ",false,1000,true,elements));
        elements.Clear(); // No mutable source collection crosses the presentation boundary.
        Assert.Equal(2,buff.Elements.Length);
        Assert.Equal(14,buff.Slot);
        Assert.Equal("Cloak\n( Stealth )\n +Lorendavin: Cloak\n  (unknown): Device",
            buff.Tooltip(new("Cloak","BUF_CLOAKG","Cloak","You are invisible.",true)));
    }

    [Fact]
    public void VisibilityUsesAdvancingClockAndPermanentFlagNotStaleSnapshotFlag()
    {
        var buff = EnbSceneBuffSnapshot.From(new(0,"Cloak","",false,1000,false,[]));
        Assert.True(buff.IsVisible(999)); Assert.True(buff.IsVisible(1000)); Assert.False(buff.IsVisible(1001));
        Assert.False((buff with { RemovalTime=0 }).IsVisible(0));
        Assert.False((buff with { RemovalTime=-1 }).IsVisible(0));
        Assert.True((buff with { IsPermanent=true,RemovalTime=-1 }).IsVisible(long.MaxValue));
        Assert.Equal(1000,buff.RemovalTime); // Hiding never clears server state.
    }

    [Fact]
    public void DuplicateDefinitionsRetainFirstEntryLikeCurrentRetail()
        => Assert.Equal("A", EnbRetailBuffCatalog.Parse(EnbRetailIniDocument.Parse(
            "[BUFF-0]\nBuffType=X\nBuffAsset=A\n[BUFF-1]\nBuffType=X\nBuffAsset=B")).Definitions["X"].Asset);
}
