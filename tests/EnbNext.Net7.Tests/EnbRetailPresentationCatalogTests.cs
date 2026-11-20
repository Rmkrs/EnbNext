using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailPresentationCatalogTests
{
    [Fact]
    public void RetailIniReaderIsCaseInsensitiveLastValueWinsAndPreservesBareKeys()
    {
        EnbRetailIniDocument document = EnbRetailIniDocument.Parse("""
            global-value=ignored

            [BASE-10]
            FILE=first.w3d
            file=second.w3d
            NULLOBJECT

            [base-10]
            Name=HULL_A
            """);

        Assert.Single(document.SectionNames);
        Assert.True(document.TryGetSection("BASE-10", out IReadOnlyDictionary<string, string> values));
        Assert.Equal("second.w3d", values["FILE"]);
        Assert.Equal("HULL_A", values["name"]);
        Assert.Equal(string.Empty, values["nullobject"]);
    }

    [Fact]
    public void BaseAssetCatalogUsesExactBaseHyphenGrammarAndAllClosedResolverBranches()
    {
        var catalog = new EnbRetailPresentationCatalog();
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

        Assert.Equal(EnbBaseAssetDefinitionKind.NullObject, catalog.ResolveBaseAsset(-1).Kind);
        Assert.False(catalog.ResolveBaseAsset(-1).IsCatalogEntry);

        EnbBaseAssetDefinition nullObject = catalog.ResolveBaseAsset(10);
        Assert.Equal(EnbBaseAssetDefinitionKind.NullObject, nullObject.Kind);
        Assert.True(nullObject.IsCatalogEntry);

        EnbBaseAssetDefinition rsl = catalog.ResolveBaseAsset(11);
        Assert.Equal(EnbBaseAssetDefinitionKind.Rsl, rsl.Kind);
        Assert.Equal(101, rsl.RslId);
        Assert.Equal(102, rsl.LinkId);
        Assert.Equal(103, rsl.RsId);
        Assert.True(rsl.OverbrightOff);

        EnbBaseAssetDefinition file = catalog.ResolveBaseAsset(12);
        Assert.Equal(EnbBaseAssetDefinitionKind.FileName, file.Kind);
        Assert.Equal("ships.rsl", file.File);
        Assert.Equal("HULL_A", file.Name);

        EnbBaseAssetDefinition width = catalog.ResolveBaseAsset(13);
        Assert.Equal(EnbBaseAssetDefinitionKind.ProceduralWidth, width.Kind);
        Assert.Equal(2.5f, width.Width);

        Assert.Equal(EnbBaseAssetDefinitionKind.NoRenderObject, catalog.ResolveBaseAsset(14).Kind);
        Assert.True(catalog.ResolveBaseAsset(15).IsMissing);
        Assert.True(catalog.ResolveBaseAsset(9999).IsMissing);
    }

    [Fact]
    public void RslPropertyPresenceCommitsToRslBranchEvenWhenSelectorIsInvalid()
    {
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadBaseAssetsText("""
            [BASE-1]
            RSLID=-1
            FILE=fallback.w3d
            NAME=FALLBACK
            Width=12
            """);

        EnbBaseAssetDefinition definition = catalog.ResolveBaseAsset(1);
        Assert.Equal(EnbBaseAssetDefinitionKind.Missing, definition.Kind);
        Assert.True(definition.IsCatalogEntry);
        Assert.Null(definition.File);
        Assert.Null(definition.Width);
    }

    [Theory]
    [InlineData("$FFFFFFFF",-1)] [InlineData("-1tail",-1)] [InlineData("FFFFFFFFh",-1)]
    [InlineData("4294967295",-1)] [InlineData("$xyz",-1)]
    [InlineData("0junk",0)] [InlineData("nonsense",0)] [InlineData("$10",16)]
    [InlineData("20h",32)] [InlineData("4294967297",1)]
    public void BaseRslSelectorsUseCurrentRetailIntegerConversion(string raw,int expected)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadBaseAssetsText($"[BASE-1]\nRSLID={raw}\nLINKID={raw}\nRSID={raw}\nFILE=ignored.w3d\nNAME=IGNORED\n");
        var definition=catalog.ResolveBaseAsset(1);
        if(expected==-1)Assert.Equal(EnbBaseAssetDefinitionKind.Missing,definition.Kind);
        else
        {
            Assert.Equal(EnbBaseAssetDefinitionKind.Rsl,definition.Kind);
            Assert.Equal(expected,definition.RslId);Assert.Equal(expected,definition.LinkId);Assert.Equal(expected,definition.RsId);
        }
        Assert.Null(definition.File);Assert.Null(definition.Name);
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

        var catalog = new EnbRetailPresentationCatalog();
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
            EnbEffectDefinition definition = Assert.IsType<EnbEffectDefinition>(catalog.FindEffect(100 + i));
            Assert.NotEqual(EnbEffectClassKind.Unknown, definition.EffectClassKind);
            Assert.Equal(classes[i], definition.EffectClass);
        }

        EnbEffectPresentationResolution tractor = catalog.ResolveEffect(2);
        Assert.False(tractor.MissingDefinition);
        Assert.Equal(2, tractor.DefinitionId);
        Assert.Equal(EnbEffectClassKind.LineEffect, tractor.Definition!.EffectClassKind);
        Assert.True(tractor.Definition.InfiniteDuration);
        Assert.Equal(68, tractor.Definition.BaseAssetId);
        Assert.Equal(68, tractor.BaseAsset!.Selector);
        Assert.Equal(390, tractor.Definition.StartLinkId);
        Assert.True(tractor.Definition.UnaryStartLinkToTarget);

        EnbEffectPresentationResolution prospect = catalog.ResolveEffect(191);
        Assert.Equal(1911, prospect.BaseAsset!.Selector);

        Assert.Null(catalog.ResolveEffect(108).BaseAsset); // SoundEffect
        Assert.Null(catalog.ResolveEffect(109).BaseAsset); // PassEffect
        Assert.Null(catalog.ResolveEffect(111).BaseAsset); // RenderStateEffect

        EnbEffectPresentationResolution missing = catalog.ResolveEffect(65535);
        Assert.True(missing.MissingDefinition);
        Assert.Null(missing.Definition);
        Assert.Null(missing.BaseAsset);
    }

    [Fact]
    public void EffectDurationInfiniteTokenRemainsCaseSensitive()
    {
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("""
            [EFFECT-1]
            EffectClass=PassEffect
            Duration=INFINITE

            [EFFECT-2]
            EffectClass=PassEffect
            Duration=infinite
            """);

        Assert.True(catalog.FindEffect(1)!.InfiniteDuration);
        Assert.False(catalog.FindEffect(2)!.InfiniteDuration);
        Assert.Null(catalog.FindEffect(2)!.DurationValue);
    }

    [Fact]
    public void EffectLinkTraversalIsDeterministicAndFailsSafeForCyclesMissingLinksAndLimits()
    {
        var catalog = new EnbRetailPresentationCatalog();
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

        EnbEffectGraphResolution graph = catalog.ResolveEffectGraph(1);
        Assert.Equal(new[] { 1, 2, 3 }, graph.Definitions.Select(d => d.DefinitionId).ToArray());
        Assert.Equal(new[] { 99 }, graph.MissingDefinitionIds);
        Assert.True(graph.CycleDetected);
        Assert.False(graph.TraversalLimitReached);

        EnbEffectGraphResolution limited = catalog.ResolveEffectGraph(1, maxDefinitions: 2);
        Assert.Equal(2, limited.Definitions.Count);
        Assert.True(limited.TraversalLimitReached);
    }
}
