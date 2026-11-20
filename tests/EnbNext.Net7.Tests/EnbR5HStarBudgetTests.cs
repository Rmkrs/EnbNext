using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR5HStarBudgetTests
{
    [Fact]
    public void ExactStarSetFamilyWinsAndStopsAtFirstMissingOrdinal()
    {
        EnbRetailStarCatalog catalog = EnbRetailStarCatalog.ParseText("""
            [STAR0042\0]
            NumStars=100
            [STAR0042\1]
            NumStars=200
            [STAR0042\3]
            NumStars=999
            [STAR0000\0]
            NumStars=300
            [Default\0]
            NumStars=400
            """);

        EnbRetailStarLayerSelection selection = catalog.ResolveLayers(42);

        Assert.Equal(EnbRetailStarLayerFamily.ExactStarSet, selection.Family);
        Assert.Equal(42, selection.SelectedStarSetId);
        Assert.Equal([100, 200], selection.Layers.Select(layer => layer.NumStars));
    }

    [Fact]
    public void MissingExactFamilyFallsBackToSignedHundredBucket()
    {
        EnbRetailStarCatalog catalog = EnbRetailStarCatalog.ParseText("""
            [STAR1200\0]
            NumStars=321
            [STAR-0100\0]
            NumStars=654
            [Default\0]
            NumStars=999
            """);

        EnbRetailStarLayerSelection positive = catalog.ResolveLayers(1234);
        EnbRetailStarLayerSelection negative = catalog.ResolveLayers(-199);

        Assert.Equal(EnbRetailStarLayerFamily.HundredBucket, positive.Family);
        Assert.Equal(1200, positive.SelectedStarSetId);
        Assert.Equal(321, Assert.Single(positive.Layers).NumStars);

        // Signed C integer division truncates toward zero: -199 -> -100.
        Assert.Equal(EnbRetailStarLayerFamily.HundredBucket, negative.Family);
        Assert.Equal(-100, negative.SelectedStarSetId);
        Assert.Equal("STAR-0100\\0", Assert.Single(negative.Layers).SectionName);
        Assert.Equal(654, negative.Layers[0].NumStars);
    }

    [Fact]
    public void MissingStarFamiliesFallBackToDefaultInOrdinalOrder()
    {
        EnbRetailStarCatalog catalog = EnbRetailStarCatalog.ParseText("""
            [Default\0]
            NumStars=100
            [Default\1]
            NumStars=200
            """);

        EnbRetailStarLayerSelection selection = catalog.ResolveLayers(9876);

        Assert.Equal(EnbRetailStarLayerFamily.Default, selection.Family);
        Assert.Null(selection.SelectedStarSetId);
        Assert.Equal(["Default\\0", "Default\\1"], selection.Layers.Select(layer => layer.SectionName));
    }

    [Fact]
    public void StarLayerParserPreservesProvenRetailFieldsAndDefaults()
    {
        EnbRetailStarCatalog catalog = EnbRetailStarCatalog.ParseText("""
            [Default\0]
            Texture=starfield.tga
            TextureLog2Width=6
            Quads=yes
            NumStars=777
            QMCOffset=17
            MinSize=0.25
            MaxSize=2.5
            PickRandomly=no
            Hemisphere=yes

            [Default\1]
            """);

        EnbRetailStarLayerSelection selection = catalog.ResolveLayers(0);
        EnbRetailStarLayerDefinition authored = selection.Layers[0];
        EnbRetailStarLayerDefinition defaults = selection.Layers[1];

        Assert.Equal("starfield.tga", authored.Texture);
        Assert.Equal(6, authored.TextureLog2Width);
        Assert.True(authored.Quads);
        Assert.Equal(777, authored.NumStars);
        Assert.Equal(17, authored.QmcOffset);
        Assert.Equal(0.25f, authored.MinSize);
        Assert.Equal(2.5f, authored.MaxSize);
        Assert.False(authored.PickRandomly);
        Assert.True(authored.Hemisphere);

        //481EE0/9CB020 defaults to BDA908's empty string;91DE37/4B returns
        //no texture for null or empty. The catalog represents absence as null.
        Assert.Null(defaults.Texture);
        Assert.Equal(0, defaults.TextureLog2Width);
        Assert.False(defaults.Quads);
        Assert.Equal(1000, defaults.NumStars);
        Assert.Equal(0f, defaults.MinSize);
        Assert.Equal(1f, defaults.MaxSize);
        Assert.Equal(0, defaults.QmcOffset);
        Assert.True(defaults.PickRandomly);
        Assert.False(defaults.Hemisphere);
    }

    [Fact]
    public void FinalVisitedLayerIsClippedToRemainingStarBudget()
    {
        IReadOnlyList<EnbRetailStarLayerDefinition> layers =
        [
            Layer(300),
            Layer(300),
            Layer(100),
        ];

        EnbStarBudgetSelection selection = EnbStarBudgetSemantics.Allocate(layers, starBudget: 500);

        Assert.Equal(2, selection.VisitedLayerCount);
        Assert.Equal([300, 200], selection.LayerAllocations.Select(a => a.VisibleStarCount));
        Assert.Equal([500, 200], selection.LayerAllocations.Select(a => a.RemainingBudgetBeforeLayer));
        Assert.False(selection.LayerAllocations[0].IsPartial);
        Assert.True(selection.LayerAllocations[1].IsPartial);
        Assert.Equal(500, selection.VisibleRenderCost);
        Assert.Equal(500, selection.VisibleStarCount);
        Assert.Equal(700, selection.TotalConfiguredCost);
        Assert.Equal(500f, selection.ReportedCurrentCost);
    }

    [Fact]
    public void QuadLayerUsesTwoBudgetUnitsPerVisibleStarAndCanLeaveOneUnused()
    {
        IReadOnlyList<EnbRetailStarLayerDefinition> layers = [Layer(250, quads: true)];

        EnbStarBudgetSelection selection = EnbStarBudgetSemantics.Allocate(layers, starBudget: 499);
        EnbStarLayerBudgetAllocation allocation = Assert.Single(selection.LayerAllocations);

        Assert.Equal(500, layers[0].RetailRenderCost);
        Assert.Equal(249, allocation.VisibleStarCount);
        Assert.Equal(498, allocation.VisibleRenderCost);
        Assert.Equal(498, selection.VisibleRenderCost);
        Assert.Equal(499f, selection.ReportedCurrentCost);
        Assert.True(allocation.IsPartial);
    }

    [Fact]
    public void FirstLayerCanBePartiallyVisibleWhenItsAuthoredCostExceedsBudget()
    {
        IReadOnlyList<EnbRetailStarLayerDefinition> layers =
        [
            Layer(1000),
            Layer(1),
        ];

        EnbStarBudgetSelection selection = EnbStarBudgetSemantics.Allocate(layers, starBudget: 500);
        EnbStarLayerBudgetAllocation allocation = Assert.Single(selection.LayerAllocations);

        Assert.Equal(500, allocation.VisibleStarCount);
        Assert.Equal(500, allocation.VisibleRenderCost);
        Assert.True(allocation.IsPartial);
        Assert.Equal(500, selection.VisibleRenderCost);
        Assert.Equal(1001, selection.TotalConfiguredCost);
        Assert.Equal(500f, selection.ReportedCurrentCost);
    }

    [Fact]
    public void ManagerSubtractsCompleteAuthoredCostBeforeVisitingNextLayer()
    {
        IReadOnlyList<EnbRetailStarLayerDefinition> layers =
        [
            Layer(100, quads: true), // authored cost 200
            Layer(400),
            Layer(1),
        ];

        EnbStarBudgetSelection selection = EnbStarBudgetSemantics.Allocate(layers, starBudget: 501);

        Assert.Equal(2, selection.VisitedLayerCount);
        Assert.Equal(501, selection.LayerAllocations[0].RemainingBudgetBeforeLayer);
        Assert.Equal(301, selection.LayerAllocations[1].RemainingBudgetBeforeLayer);
        Assert.Equal(100, selection.LayerAllocations[0].VisibleStarCount);
        Assert.Equal(301, selection.LayerAllocations[1].VisibleStarCount);
        Assert.Equal(501, selection.VisibleRenderCost);
        Assert.Equal(601, selection.TotalConfiguredCost);
        Assert.Equal(501f, selection.ReportedCurrentCost);
    }

    [Fact]
    public void ZeroBudgetStillVisitsFirstLayerButRendersNoStars()
    {
        EnbStarBudgetSelection selection = EnbStarBudgetSemantics.Allocate([Layer(10), Layer(20)], 0);
        EnbStarLayerBudgetAllocation allocation = Assert.Single(selection.LayerAllocations);

        Assert.Equal(0, allocation.RemainingBudgetBeforeLayer);
        Assert.Equal(0, allocation.VisibleStarCount);
        Assert.Equal(0, selection.VisibleRenderCost);
        Assert.Equal(0f, selection.ReportedCurrentCost);
    }

    [Theory]
    [InlineData(-1, false, 1, 10)]
    [InlineData(-1, true, 1, 10)]
    [InlineData(int.MinValue, false, 2, 30)]
    [InlineData(int.MinValue, true, 2, 30)]
    public void MovieBudgetPreservesUnsignedLayerCountAndSignedManagerContinuation(int budget, bool quads, int visited, int stars)
    {
        //804B64 ->4826E0 stores the script int unchanged;481C22/28 treats it
        //as uint,482DB7/BB subtracts/wraps and tests signed,482E20 reports uint min.
        var selected=EnbStarBudgetSemantics.Allocate([Layer(10,quads),Layer(20,quads)],budget);
        Assert.Equal(visited,selected.VisitedLayerCount);
        Assert.Equal(stars,selected.VisibleStarCount);
        Assert.Equal(30*(quads?2:1),selected.ReportedCurrentCost);
        Assert.Equal(budget,selected.LayerAllocations[0].RemainingBudgetBeforeLayer);
        if(visited==2)Assert.Equal(unchecked(budget-10*(quads?2:1)),selected.LayerAllocations[1].RemainingBudgetBeforeLayer);
    }


    [Fact]
    public void InstalledAssetBundleLoadsStarsIniFromSelectedRetailClientIniDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"enbnext-r5h-{Guid.NewGuid():N}");
        try
        {
            string release = Path.Combine(root, "release");
            string clientIni = Path.Combine(root, "Data", "client", "ini");
            Directory.CreateDirectory(release);
            Directory.CreateDirectory(clientIni);
            Directory.CreateDirectory(Path.Combine(root, "Data", "client", "mixfiles"));
            File.WriteAllBytes(Path.Combine(release, "client.exe"), [0x4D, 0x5A]);
            File.WriteAllText(Path.Combine(clientIni, "basset.ini"), "[BASE-1]\nNULLOBJECT\n");
            File.WriteAllText(Path.Combine(clientIni, "stars.ini"), "[Default\\0]\nNumStars=123\n");

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail root was not resolved.");
            EnbRetailAssetBundle bundle = EnbRetailAssetBundle.Load(paths);

            Assert.Equal(Path.Combine(clientIni, "stars.ini"), paths.StarsIniPath);
            Assert.NotNull(bundle.Stars);
            Assert.Equal(123, Assert.Single(bundle.Stars!.ResolveLayers(42).Layers).NumStars);
            Assert.True(bundle.TryGetSource(EnbRetailAssetSourceKind.StarsIni, out EnbRetailAssetSource source));
            Assert.True(source.Exists);
            Assert.Equal(paths.StarsIniPath, source.Path);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static EnbRetailStarLayerDefinition Layer(int numStars, bool quads = false)
        => new(
            Ordinal: 0,
            SectionName: "synthetic",
            Texture: null,
            TextureLog2Width: 0,
            Quads: quads,
            NumStars: numStars,
            MinSize: 0f,
            MaxSize: 1f,
            QmcOffset: 0,
            PickRandomly: true,
            Hemisphere: false);
}
