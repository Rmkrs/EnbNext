using EnbNext.Net7.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class Net7PresentationCatalogTests
{
    [Fact]
    public void BaseAssetCatalogUsesExactBaseHyphenGrammarAndAllClosedResolverBranches()
    {
        var catalog = new Net7PresentationCatalogs();
        catalog.LoadBaseAssetsText("""
            [BASE-10]
            NULLOBJECT=TRUE

            [BASE-11]
            RSLID=101
            LINKID=102
            RSID=103
            OverbrightOff=TRUE

            [BASE-12]
            FILE=ships.rsl
            NAME=HULL_A

            [BASE-13]
            Width=2.5

            [BASE-14]
            SomethingFuture=1

            [BASE_15]
            RSLID=999
            """);

        Assert.Equal(Net7BaseAssetDefinitionKind.NullObject, catalog.ResolveBaseAsset(-1).Kind);
        Assert.False(catalog.ResolveBaseAsset(-1).IsCatalogEntry);

        Net7BaseAssetDefinition nullObject = catalog.ResolveBaseAsset(10);
        Assert.Equal(Net7BaseAssetDefinitionKind.NullObject, nullObject.Kind);
        Assert.True(nullObject.IsCatalogEntry);

        Net7BaseAssetDefinition rsl = catalog.ResolveBaseAsset(11);
        Assert.Equal(Net7BaseAssetDefinitionKind.Rsl, rsl.Kind);
        Assert.Equal(101, rsl.RslId);
        Assert.Equal(102, rsl.LinkId);
        Assert.Equal(103, rsl.RsId);
        Assert.True(rsl.OverbrightOff);

        Net7BaseAssetDefinition file = catalog.ResolveBaseAsset(12);
        Assert.Equal(Net7BaseAssetDefinitionKind.FileName, file.Kind);
        Assert.Equal("ships.rsl", file.File);
        Assert.Equal("HULL_A", file.Name);

        Net7BaseAssetDefinition width = catalog.ResolveBaseAsset(13);
        Assert.Equal(Net7BaseAssetDefinitionKind.ProceduralWidth, width.Kind);
        Assert.Equal(2.5f, width.Width);

        Assert.Equal(Net7BaseAssetDefinitionKind.Unsupported, catalog.ResolveBaseAsset(14).Kind);
        Assert.True(catalog.ResolveBaseAsset(15).IsMissing); // underscore is not the current grammar
        Assert.True(catalog.ResolveBaseAsset(9999).IsMissing);
    }

    [Fact]
    public void EffectCatalogRecognizesExactCurrentClassUniverseAndPreservesSelectorNamespaces()
    {
        string[] classes =
        [
            "RObjLookAtEffect", "RObjEffect", "WarpEffect", "LineEffect",
            "StraightSeekEffect", "SpiralSeekEffect", "SplineRandomSeekEffect",
            "LinearRandomSeekEffect", "SoundEffect", "PassEffect", "AttachEffect",
            "RenderStateEffect", "ParticleJetEffect",
        ];

        var effectText = new System.Text.StringBuilder();
        for (int i = 0; i < classes.Length; i++)
        {
            effectText.AppendLine($"[EFFECT-{100 + i}]");
            effectText.AppendLine($"EffectClass={classes[i]}");
            effectText.AppendLine("BaseAssetID=68");
            effectText.AppendLine();
        }
        effectText.AppendLine("[EFFECT-2]");
        effectText.AppendLine("EffectClass=LineEffect");
        effectText.AppendLine("BaseAssetID=68");
        effectText.AppendLine("Duration=INFINITE");
        effectText.AppendLine("StartLinkID=390");
        effectText.AppendLine("UnaryStartLinkToTarget=TRUE");
        effectText.AppendLine("SFX=tractor.wav");
        effectText.AppendLine();
        effectText.AppendLine("[EFFECT-191]");
        effectText.AppendLine("EffectClass=LineEffect");
        effectText.AppendLine("BaseAssetID=1911");
        effectText.AppendLine("Duration=INFINITE");
        effectText.AppendLine("StartLinkID=794");
        effectText.AppendLine("UnaryStartLinkToTarget=TRUE");
        effectText.AppendLine();
        effectText.AppendLine("[EFFECT-390]");
        effectText.AppendLine("EffectClass=RObjEffect");
        effectText.AppendLine("BaseAssetID=1589");
        effectText.AppendLine("Duration=INFINITE");
        effectText.AppendLine();
        effectText.AppendLine("[EFFECT-794]");
        effectText.AppendLine("EffectClass=PassEffect");

        var catalog = new Net7PresentationCatalogs();
        catalog.LoadBaseAssetsText("""
            [BASE-68]
            RSLID=68
            [BASE-1911]
            RSLID=1911
            [BASE-1589]
            RSLID=1589
            """);
        catalog.LoadEffectsText(effectText.ToString());

        for (int i = 0; i < classes.Length; i++)
        {
            Net7EffectDefinition definition = Assert.IsType<Net7EffectDefinition>(catalog.FindEffect(100 + i));
            Assert.NotEqual(Net7EffectClassKind.Unknown, definition.EffectClassKind);
            Assert.Equal(classes[i], definition.EffectClass);
        }

        Net7EffectPresentationResolution tractor = catalog.ResolveEffect(2);
        Assert.False(tractor.MissingDefinition);
        Assert.Equal(2, tractor.DefinitionId);
        Assert.Equal(Net7EffectClassKind.LineEffect, tractor.Definition!.EffectClassKind);
        Assert.True(tractor.Definition.InfiniteDuration);
        Assert.Equal(68, tractor.Definition.BaseAssetId);
        Assert.Equal(68, tractor.BaseAsset!.Selector);
        Assert.Equal(390, tractor.Definition.StartLinkId);
        Assert.True(tractor.Definition.UnaryStartLinkToTarget);

        Net7EffectPresentationResolution prospect = catalog.ResolveEffect(191);
        Assert.Equal(1911, prospect.BaseAsset!.Selector);

        // These three current classes do not directly consume BaseAssetID even
        // if local data happens to contain such a property.
        Assert.Null(catalog.ResolveEffect(108).BaseAsset); // SoundEffect
        Assert.Null(catalog.ResolveEffect(109).BaseAsset); // PassEffect
        Assert.Null(catalog.ResolveEffect(111).BaseAsset); // RenderStateEffect

        Net7EffectPresentationResolution missing = catalog.ResolveEffect(65535);
        Assert.True(missing.MissingDefinition);
        Assert.Null(missing.Definition);
        Assert.Null(missing.BaseAsset);
    }

    [Fact]
    public void EffectLinkTraversalIsDeterministicAndFailsSafeForCyclesMissingLinksAndLimits()
    {
        var catalog = new Net7PresentationCatalogs();
        catalog.LoadEffectsText("""
            [EFFECT-1]
            EffectClass=PassEffect
            StartLinkID=2
            NextLinkID=99

            [EFFECT-2]
            EffectClass=PassEffect
            NextLinkID=3

            [EFFECT-3]
            EffectClass=PassEffect
            NextLinkID=2
            """);

        Net7EffectGraphResolution graph = catalog.ResolveEffectGraph(1);
        Assert.Equal(new[] { 1, 2, 3 }, graph.Definitions.Select(d => d.DefinitionId).ToArray());
        Assert.Equal(new[] { 99 }, graph.MissingDefinitionIds);
        Assert.True(graph.CycleDetected);
        Assert.False(graph.TraversalLimitReached);

        Net7EffectGraphResolution limited = catalog.ResolveEffectGraph(1, maxDefinitions: 2);
        Assert.Equal(2, limited.Definitions.Count);
        Assert.True(limited.TraversalLimitReached);
    }
}
