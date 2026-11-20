using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;

namespace EnbNext.Net7.Tests;

public sealed class Net7AssetDirectAuditTestsAsset03
{
    [Fact]
    public void Asset03BaseAssetResolverPreservesCurrentBranchOwnership()
    {
        var catalog = new Net7PresentationCatalogs();
        catalog.LoadBaseAssetsText("""
            [BASE-10]
            RSLID=-1
            FILE=should-not-fall-through.rsl
            NAME=SHOULD_NOT_WIN

            [BASE-11]
            RSLID=not-an-integer
            Width=99

            [BASE-12]
            RSLID=101
            LINKID=-1
            RSID=-1
            """);

        Net7BaseAssetDefinition sentinelRsl = catalog.ResolveBaseAsset(10);
        Assert.Equal(Net7BaseAssetDefinitionKind.Missing, sentinelRsl.Kind);
        Assert.True(sentinelRsl.IsCatalogEntry);

        Net7BaseAssetDefinition malformedRsl = catalog.ResolveBaseAsset(11);
        Assert.Equal(Net7BaseAssetDefinitionKind.Missing, malformedRsl.Kind);

        Net7BaseAssetDefinition rsl = catalog.ResolveBaseAsset(12);
        Assert.Equal(Net7BaseAssetDefinitionKind.Rsl, rsl.Kind);
        Assert.Equal(101, rsl.RslId);
        Assert.Null(rsl.LinkId);
        Assert.Null(rsl.RsId);
    }

    [Fact]
    public void Asset03CreateType08BypassesOrdinaryBaseAssetCatalogButRetainsSelector()
    {
        var catalog = new Net7PresentationCatalogs();
        catalog.LoadBaseAssetsText("""
            [BASE-77]
            RSLID=707
            """);
        var world = new Net7WorldState(presentation: catalog);

        Net7WorldObject special = world.MaterializeCreate(new Net7CreateUpdate(
            GameId: 0x100,
            Scale: 1f,
            BaseAsset: 77,
            ObjectType: 0x08,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0));
        Assert.Equal(77, special.BaseAsset);
        Assert.Null(special.BaseAssetPresentation);

        Net7WorldObject ordinary = world.MaterializeCreate(new Net7CreateUpdate(
            GameId: 0x101,
            Scale: 1f,
            BaseAsset: 77,
            ObjectType: 0x04,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0));
        Assert.Equal(77, ordinary.BaseAssetPresentation!.Selector);
    }

    [Fact]
    public void Asset03EffectResolverRejectsInvalidClassUsesExactInfiniteTokenAndMinusOneLinkSentinel()
    {
        var catalog = new Net7PresentationCatalogs();
        catalog.LoadEffectsText("""
            [EFFECT-0]
            EffectClass=PassEffect

            [EFFECT-1]
            EffectClass=PassEffect
            StartLinkID=0
            NextLinkID=-1
            Duration=INFINITE

            [EFFECT-2]
            EffectClass=PassEffect
            Duration=infinite

            [EFFECT-3]
            BaseAssetID=12

            [EFFECT-4]
            EffectClass=DefinitelyNotARetailEffectClass
            """);

        Net7EffectDefinition linked = Assert.IsType<Net7EffectDefinition>(catalog.FindEffect(1));
        Assert.Equal(0, linked.StartLinkId);
        Assert.Null(linked.NextLinkId);
        Assert.True(linked.InfiniteDuration);

        Net7EffectDefinition lowercaseInfinite = Assert.IsType<Net7EffectDefinition>(catalog.FindEffect(2));
        Assert.False(lowercaseInfinite.InfiniteDuration);
        Assert.Null(lowercaseInfinite.DurationValue);

        Net7EffectPresentationResolution missingClass = catalog.ResolveEffect(3);
        Assert.False(missingClass.MissingDefinition);
        Assert.True(missingClass.InvalidDefinition);
        Assert.NotNull(missingClass.Definition);
        Assert.Null(missingClass.BaseAsset);

        Net7EffectPresentationResolution unknownClass = catalog.ResolveEffect(4);
        Assert.False(unknownClass.MissingDefinition);
        Assert.True(unknownClass.InvalidDefinition);

        Net7EffectGraphResolution graph = catalog.ResolveEffectGraph(1);
        Assert.Equal(new[] { 1, 0 }, graph.Definitions.Select(definition => definition.DefinitionId).ToArray());
        Assert.Empty(graph.MissingDefinitionIds);
        Assert.False(graph.CycleDetected);
    }
}
