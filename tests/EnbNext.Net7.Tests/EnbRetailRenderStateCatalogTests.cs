using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailRenderStateCatalogTests
{
    [Theory]
    [InlineData("RST_SIMPLE_SHADOW=T", true, true, false, true, false)]
    [InlineData("RST_SHADOW_SOURCE=T", true, true, false, false, false)]
    [InlineData("RST_PROJECTION_TARGET=T", true, false, true, false, false)]
    [InlineData("RST_HINT_STATIONARY=T", false, false, false, false, true)]
    [InlineData("RST_USE_CULLING=T", true, false, false, false, false)]
    [InlineData("RST_SIMPLE_SHADOW=F", false, false, false, false, false)]
    [InlineData("RST_SIMPLE_SHADOW=T\nRST_SHADOW_SOURCE=F\nRST_USE_CULLING=F", true, true, false, true, false)]
    public void ProjectorFlagsApplyRetailImplications(string authored, bool culling,
        bool source, bool target, bool simple, bool stationary)
    {
        var catalog = EnbRetailRenderStateCatalog.ParseText($"[0]\n{authored}\n[0\\STATE0]\nTYPE=object\nOBJECT=SHIP\n");
        Assert.True(catalog.TryGetLibrary(0, out var library));
        Assert.Equal(new EnbRslLibraryFlags(culling, stationary, source, target, simple), library.Flags);
        foreach (string line in authored.Split('\n'))
        {
            string[] pair = line.Split('=');
            Assert.Equal(pair[1], library.Properties[pair[0]]);
        }
    }

    [Fact]
    public void RetailIniReaderAcceptsSectionHeadersWithTrailingRetailComments()
    {
        EnbRetailIniDocument document = EnbRetailIniDocument.Parse(
            "[33\\STATE0] ; KenRentryentryentry\nID=0\nTYPE=sound\nSOUND=null.wav\n");

        Assert.True(document.TryGetSection("33\\STATE0", out IReadOnlyDictionary<string, string> values));
        Assert.Equal("sound", values["TYPE"]);
        Assert.False(document.TryGetSection("33\\STATE0] ; KenRentryentryentry", out _));
    }

    [Fact]
    public void CatalogUsesRetailSequentialLibraryAndStateStopRules()
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(
            """
            [0\STATE0]
            ID=0
            TYPE=object
            OBJECT=A

            [0\STATE1]
            ID=1
            TYPE=anim
            SUFFIX=-1

            [0\STATE3]
            ID=3
            TYPE=sound
            SOUND=SHOULD_NOT_BE_REACHED

            [1\STATE0]
            ID=0
            TYPE=object
            OBJECT=B

            [3\STATE0]
            ID=0
            TYPE=object
            OBJECT=LIBRARY_AFTER_GAP
            """);

        Assert.Equal(2, catalog.LibraryCount);
        Assert.Equal(3, catalog.StateCount);
        Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition first));
        Assert.Equal(2, first.States.Count);
        Assert.True(catalog.TryGetLibrary(1, out _));
        Assert.False(catalog.TryGetLibrary(3, out _));
    }

    [Fact]
    public void StateIdsRemainSeparateFromSectionOrdinalsAndMissingIdFallsBackToOrdinal()
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(
            """
            [0\STATE0]
            TYPE=object
            OBJECT=BASE

            [0\STATE1]
            ID=222
            TYPE=sound
            SOUND=Rock_Asteroid_Mining
            """);

        Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));
        EnbRslStateDefinition fallback = library.States[0];
        Assert.Equal(0, fallback.SectionOrdinal);
        Assert.Equal(0, fallback.StateId);
        Assert.False(fallback.StateIdWasExplicit);

        EnbRslStateDefinition authored = library.States[1];
        Assert.Equal(1, authored.SectionOrdinal);
        Assert.Equal(222, authored.StateId);
        Assert.True(authored.StateIdWasExplicit);
        Assert.True(library.TryFindState(222, out EnbRslStateDefinition byId));
        Assert.Same(authored, byId);
    }

    [Fact]
    public void CatalogPreservesRootFlagsStateKindsLinksFlagsAndPivotMaps()
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(
            """
            [0]
            RST_SHADOW_SOURCE=T
            RST_SIMPLE_SHADOW=TRUE

            [0\STATE0]
            ID=0
            TYPE=object
            FILE=ship.w3d
            OBJECT=SHIP
            LINK=1
            RS_STARTS_ENABLED=yes

            [0\STATE0\PIVOTMAP0]
            BONE=~WEAP_01
            WEIGHT=0.75

            [0\STATE1]
            ID=1
            TYPE=anim
            SUFFIX=-2
            NEXT=2
            RS_ACTIVATE_NEXT=T

            [0\STATE2]
            ID=2
            TYPE=sound
            SOUND=ButtonClick01.wav
            RS_PRECACHED=true
            RS_DONT_LOOP_SOUND=TRUE
            """);

        Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));
        Assert.True(library.Flags.ShadowSource);
        Assert.True(library.Flags.SimpleShadow);
        Assert.False(library.Flags.ProjectionTarget);

        EnbRslStateDefinition obj = library.States[0];
        Assert.Equal(EnbRslStateType.Object, obj.Type);
        Assert.Equal("ship.w3d", obj.File);
        Assert.Equal("SHIP", obj.Object);
        Assert.Equal(1, obj.LinkStateId);
        Assert.True(obj.Flags.StartsEnabled);
        EnbRslPivotMap pivot = Assert.Single(obj.PivotMaps);
        Assert.Equal("~WEAP_01", pivot.Bone);
        Assert.Equal(0.75f, pivot.Weight!.Value, 4);

        EnbRslStateDefinition anim = library.States[1];
        Assert.Equal(EnbRslStateType.Animation, anim.Type);
        Assert.Equal("-2", anim.Suffix);
        Assert.Equal(2, anim.NextStateId);
        Assert.True(anim.Flags.ActivateNext);

        EnbRslStateDefinition sound = library.States[2];
        Assert.Equal(EnbRslStateType.Sound, sound.Type);
        Assert.Equal("ButtonClick01.wav", sound.Sound);
        Assert.True(sound.Flags.Precached);
        Assert.True(sound.Flags.DontLoopSound);
    }

    [Fact]
    public void InitialBaseAssetStateCommandsMatchRetailLinkAndRsidOrder()
    {
        IReadOnlyList<EnbRslRenderStateCommand> direct =
            EnbRslRenderStateSemantics.BuildInitialCommands(linkId: null, rsId: 3);
        EnbRslRenderStateCommand directCommand = Assert.Single(direct);
        Assert.Equal(EnbRslRenderStateCommandKind.ActivateState, directCommand.Kind);
        Assert.Equal(3, directCommand.StateId);
        Assert.Null(directCommand.ArgumentStateId);
        Assert.Equal(directCommand,Assert.Single(EnbRslRenderStateSemantics.BuildInitialCommands(linkId:-1,rsId:3)));

        IReadOnlyList<EnbRslRenderStateCommand> linked =
            EnbRslRenderStateSemantics.BuildInitialCommands(linkId: 7, rsId: 3);
        Assert.Collection(
            linked,
            command =>
            {
                Assert.Equal(EnbRslRenderStateCommandKind.InitializeState, command.Kind);
                Assert.Equal(7, command.StateId);
                Assert.Equal(3, command.ArgumentStateId);
            },
            command =>
            {
                Assert.Equal(EnbRslRenderStateCommandKind.ActivateState, command.Kind);
                Assert.Equal(7, command.StateId);
            });

        EnbRslRenderStateCommand missingRsid = Assert.Single(
            EnbRslRenderStateSemantics.BuildInitialCommands(linkId: null, rsId: null));
        Assert.Equal(-1, missingRsid.StateId);
    }

    [Fact]
    public void BaseAssetResolverBindsRslReferenceToCatalogAndInitialCommands()
    {
        var presentation = new EnbRetailPresentationCatalog();
        presentation.LoadBaseAssetsText(
            """
            [BASE-48]
            RSLID=2
            LINKID=5
            RSID=0

            [BASE-49]
            RSLID=9
            RSID=0
            """);

        EnbRetailRenderStateCatalog renderStates = EnbRetailRenderStateCatalog.ParseText(
            """
            [0\STATE0]
            ID=0
            TYPE=object
            OBJECT=A
            [1\STATE0]
            ID=0
            TYPE=object
            OBJECT=B
            [2\STATE0]
            ID=0
            TYPE=object
            OBJECT=C
            """);

        var resolver = new EnbRetailBaseAssetResolver(presentation, resources: null, renderStates);
        EnbBaseAssetResolution resolution = resolver.Resolve(48);

        Assert.True(resolution.IsResolved);
        EnbResolvedRslReference rsl = Assert.IsType<EnbResolvedRslReference>(resolution.Rsl);
        Assert.NotNull(rsl.Library);
        Assert.Equal(2, rsl.Library!.RslId);
        Assert.Equal(2, rsl.InitialStateCommands.Count);

        EnbBaseAssetResolution missing = resolver.Resolve(49);
        Assert.False(missing.IsResolved);
        Assert.Equal(EnbBaseAssetResolutionStatus.MissingRslLibrary, missing.Status);
    }
}
