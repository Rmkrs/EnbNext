using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed partial class Net7ManufacturingDomainTests
{
    private const uint LabId = 0x11223344;

    [Fact]
    public void CapturedTerminalResetPreservesTheServerFilterDespiteUnusedZeroTail()
    {
        var (world,domain)=CreateLab();
        Apply(world,("ManufacturingLab.TechLevelFilterBitfield",I32(511)));
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        Assert.Equal(511u,domain.LocalTechLevelFilterBitfield);
        // Sep 10 normal-session reset, envelope identity removed. Retail accepts
        // the unused two-zero tail; rejecting it left all checkboxes stale.
        byte[] reset=Convert.FromHexString("01020F010101000000121200FFFFFFFFF2031200FFFFFFFF1200FFFFFFFF1200FFFFFFFF1200FFFFFFFF1200FFFFFFFF1200FFFFFFFF121200FFFFFFFF00000000000000");
        new Net7AuxDataCodec().Apply(world.Objects.FindFirst(LabId)!.AuxData!,reset);
        domain.ReconcileAuthoritativeState();
        Assert.Equal(0u,domain.LocalTechLevelFilterBitfield);
        Assert.True(domain.ShouldSendFilter(true,511));
        Assert.False(domain.ShouldSendFilter(false,511));
    }

    [Fact]
    public void TerminalComponentIdentityDoesNotRequireACargoStackAndClearsByIdentity()
    {
        var (world,domain)=CreateLab();
        Apply(world,("ManufacturingLab.ManufactureComponents.0.ItemTemplateID",I32(3003)),
            ("ManufacturingLab.ManufactureComponents.0.StackCount",I32(0)),
            ("ManufacturingLab.ManufactureTarget.0.ItemTemplateID",I32(500)),
            ("ManufacturingLab.ManufactureTarget.0.StackCount",I32(0)),
            ("ManufacturingLab.KnownFormula.0.ItemName",Lp("Unavailable recipe")),
            ("ManufacturingLab.KnownFormula.0.ItemID",I32(-1)));
        var snapshot=EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain,null);
        Assert.Equal(2,snapshot.Items.Length);
        var component=Assert.Single(snapshot.Items.Where(i=>i.Inventory==13));
        Assert.Equal(500,Assert.Single(snapshot.Items.Where(i=>i.Inventory==12)).TemplateId);
        Assert.Equal(13,component.Inventory);
        Assert.Equal(3003,component.TemplateId);
        Assert.Equal(0,component.Count);
        Assert.Equal("Unavailable recipe",Assert.Single(snapshot.Lab!.KnownFormula).ItemName);
        Assert.False(domain.CanSelectManufactureItem(uint.MaxValue));
        Apply(world,("ManufacturingLab.ManufactureComponents.0.ItemTemplateID",I32(-1)));
        Assert.Equal(12,Assert.Single(EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain,null).Items).Inventory);
        Apply(world,("ManufacturingLab.ManufactureTarget.0.ItemTemplateID",I32(-1)));
        Assert.Empty(EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain,null).Items);
    }

    [Fact]
    public void SparseFormulaReceiptsPreserveEmptySlotBoundaryAndLaterMembership()
    {
        var (world,domain)=CreateLab();
        Apply(world,("ManufacturingLab.KnownFormula.0.ItemName",Lp("First")),
            ("ManufacturingLab.KnownFormula.0.ItemID",I32(100)),
            ("ManufacturingLab.KnownFormula.2.ItemName",Lp("Later")),
            ("ManufacturingLab.KnownFormula.2.ItemID",I32(102)),
            ("ManufacturingLab.PreviousAttempts.2.ItemName",Lp("Previous")));
        var lab=domain.CurrentLab!;
        Assert.Equal(3,lab.KnownFormula.Count);
        Assert.Equal(new Net7ManufacturingFormula("",-1,0),lab.KnownFormula[1]);
        Assert.Single(lab.KnownFormula.TakeWhile(f=>f.ItemName.Length!=0));
        Assert.True(lab.IsMapped(102));
        Assert.Empty(lab.PreviousAttempts.TakeWhile(f=>f.ItemName.Length!=0));
        Apply(world,("ManufacturingLab.KnownFormula.1.ItemName",Lp("Middle")));
        Assert.Equal(new[]{"First","Middle","Later"},domain.CurrentLab!.KnownFormula.Select(f=>f.ItemName));
    }

    [Fact]
    public void InventoryRefreshRevisionsTrackEqualReceiptsAndKeepCollectionsIndependent()
    {
        var (world,domain)=CreateLab();
        Apply(world,("ManufacturingLab.ManufactureComponents.0.StackCount",I32(0)));
        var first=domain.CurrentLab!;
        Assert.True(first.ComponentInventoryRevision>0);
        Apply(world,("ManufacturingLab.NegotiatedCost",U64(100)));
        Assert.Equal(first.ComponentInventoryRevision,domain.CurrentLab!.ComponentInventoryRevision);
        Apply(world,("ManufacturingLab.ManufactureComponents.0.StackCount",I32(0)));
        Assert.True(domain.CurrentLab!.ComponentInventoryRevision>first.ComponentInventoryRevision);
        Assert.Equal(first.TargetInventoryRevision,domain.CurrentLab!.TargetInventoryRevision);
        var componentRevision=domain.CurrentLab!.ComponentInventoryRevision;
        Apply(world,("ManufacturingLab.ManufactureTarget.0.StackCount",I32(0)));
        Assert.True(domain.CurrentLab!.TargetInventoryRevision>first.TargetInventoryRevision);
        Assert.Equal(componentRevision,domain.CurrentLab!.ComponentInventoryRevision);
        Assert.Equal(world.Objects.FindFirst(LabId)!.CreationOrdinal,domain.CurrentLab!.CreationOrdinal);
    }

    [Fact]
    public void FormulaRefreshRevisionTracksEqualValueReceiptsIndependentlyOfOtherLabUpdates()
    {
        var (world,domain)=CreateLab();
        Apply(world,("ManufacturingLab.KnownFormula.0.ItemName",Lp("Widget")));
        var first=domain.CurrentLab!;
        Assert.Equal(2,first.KnownFormulaState);
        Assert.True(first.KnownFormulaRevision>0);
        Apply(world,("ManufacturingLab.NegotiatedCost",U64(100)));
        Assert.Equal(first.KnownFormulaRevision,domain.CurrentLab!.KnownFormulaRevision);
        Apply(world,("ManufacturingLab.KnownFormula.0.ItemName",Lp("Widget")));
        Assert.True(domain.CurrentLab!.KnownFormulaRevision>first.KnownFormulaRevision);
        Assert.Equal(first.PreviousAttemptsRevision,domain.CurrentLab!.PreviousAttemptsRevision);
        Apply(world,("ManufacturingLab.PreviousAttempts.0.ItemName",Lp("Old")));
        Assert.True(domain.CurrentLab!.PreviousAttemptsRevision>first.PreviousAttemptsRevision);
    }

    [Fact]
    public void Type39ProjectionKeepsMappedPreviousAttemptsComponentsAndServerAuthoredScalarsDistinct()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world,
            ("ManufacturingLab.KnownFormula.0.ItemName", Lp("Widget")),
            ("ManufacturingLab.KnownFormula.0.ItemID", I32(1001)),
            ("ManufacturingLab.KnownFormula.0.TechLevel", I32(7)),
            ("ManufacturingLab.PreviousAttempts.0.ItemName", Lp("Old")),
            ("ManufacturingLab.PreviousAttempts.0.ItemID", I32(2002)),
            ("ManufacturingLab.PreviousAttempts.0.TechLevel", I32(4)),
            ("ManufacturingLab.ManufactureComponents.0.ItemTemplateID", I32(3003)),
            ("ManufacturingLab.ManufactureComponents.0.StackCount", I32(5)),
            ("ManufacturingLab.ManufactureComponents.0.Price", U64(123456789)),
            ("ManufacturingLab.ManufactureComponents.0.Quality", F32(0.875f)),
            ("ManufacturingLab.ManufactureComponents.0.BuilderName", Lp("Builder")),
            ("ManufacturingLab.NegotiatedCost", U64(777)),
            ("ManufacturingLab.BaseCost", U64(888)),
            ("ManufacturingLab.SuccessProbability", F32(0.75f)),
            ("ManufacturingLab.CriticalSuccessProbability", F32(0.125f)),
            ("ManufacturingLab.ExpectedQuality", F32(0.95f)),
            ("ManufacturingLab.MinimumQuality", F32(-1f)),
            ("ManufacturingLab.MaximumQuality", F32(1f)),
            ("ManufacturingLab.CurrentItemCategory", I32(42)),
            ("ManufacturingLab.AdditionalIterations", I32(9)),
            ("ManufacturingLab.Validity", I32(0x0F)),
            ("ManufacturingLab.Difficulty", I32(4)),
            ("ManufacturingLab.TechLevelFilterBitfield", I32(0x105)));

        Net7ManufacturingLabProjection lab = Assert.IsType<Net7ManufacturingLabProjection>(domain.CurrentLab);
        Assert.True(lab.IsMapped(1001));
        Assert.False(lab.IsMapped(2002));
        Assert.False(lab.IsMapped(-1));
        Assert.Equal(new Net7ManufacturingFormula("Widget", 1001, 7), Assert.Single(lab.KnownFormula));
        Assert.Equal(new Net7ManufacturingFormula("Old", 2002, 4), Assert.Single(lab.PreviousAttempts));
        Assert.Equal(6, lab.ManufactureComponents.Count);
        Assert.Equal(-1, lab.ManufactureTargets[0].ItemTemplateId);
        Assert.Equal(ulong.MaxValue, lab.ManufactureTargets[0].Price);
        Assert.Equal(-1f, lab.ManufactureTargets[0].Structure);
        Assert.Equal(-1f, lab.ManufactureTargets[0].Quality);
        Assert.Null(lab.ManufactureTargets[0].QualityPercent);
        Assert.Equal(3003, lab.ManufactureComponents[0].ItemTemplateId);
        Assert.Equal(5, lab.ManufactureComponents[0].StackCount);
        Assert.Equal(123456789UL, lab.ManufactureComponents[0].Price);
        Assert.Equal(0.875f, lab.ManufactureComponents[0].Quality);
        Assert.Equal(87.5f, lab.ManufactureComponents[0].QualityPercent!.Value);
        Assert.Equal("Builder", lab.ManufactureComponents[0].BuilderName);
        Assert.Equal(777UL, lab.NegotiatedCost);
        Assert.Equal(888UL, lab.BaseCost);
        Assert.Equal(75f, lab.SuccessPercent);
        Assert.Equal(12.5f, lab.CriticalSuccessPercent);
        Assert.Equal(95f, lab.ExpectedQualityPercent!.Value);
        Assert.Null(lab.QualityRangePercent);
        Assert.Null(lab.MinimumQualityPercent);
        Assert.Equal(-1f, lab.MinimumQuality);
        Assert.Null(lab.MaximumQualityPercent);
        Assert.Equal(42, lab.CurrentItemCategory);
        Assert.Equal(9, lab.AdditionalIterations);
        Assert.Equal(Net7ManufacturingValidity.AttemptNearSuccess, lab.Validity);
        Assert.True(lab.IsAttemptResult);
        Assert.Equal(Net7ManufacturingDifficulty.Hard, lab.Difficulty);
        Assert.Equal(0x105, lab.TechLevelFilterBitfield);
    }

    [Fact]
    public void CategoryBrowserIsLocalUntilVisibleAuthoritativeLeafIsSelected()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        Apply(world,
            ("ManufacturingLab.PrimaryCategories.0.Name", Lp("Primary")),
            ("ManufacturingLab.PrimaryCategories.0.Categories.1.Name", Lp("Category")),
            ("ManufacturingLab.PrimaryCategories.0.Categories.1.Subcategories.2.Name", Lp("Leaf")),
            ("ManufacturingLab.PrimaryCategories.0.Categories.1.Subcategories.2.ID", I32(0x1234)),
            ("ManufacturingLab.PrimaryCategories.0.Categories.1.Subcategories.2.Visible", U8(1)));

        Assert.False(domain.SelectCategory(1)); // category selection is not a depth-0 action
        Assert.True(domain.SelectPrimaryCategory(0));
        Assert.Equal(1, domain.BrowserDepth);
        Assert.False(domain.SelectPrimaryCategory(0)); // primary selection is not a depth-1 action
        Assert.True(domain.SelectCategory(1));
        Assert.Equal(2, domain.BrowserDepth);
        Assert.True(domain.TrySelectSubcategory(2, out uint leafId));
        Assert.Equal(3, domain.BrowserDepth);
        Assert.Equal(0x1234u, leafId);
        Assert.True(domain.IsAuthoritativeLeafId(0x1234));
        Assert.False(domain.IsAuthoritativeLeafId(2));

        // Display text is not packet provenance. Changing it leaves the leaf ID.
        Apply(world, ("ManufacturingLab.PrimaryCategories.0.Categories.1.Subcategories.2.Name", Lp("Renamed")));
        Assert.True(domain.IsAuthoritativeLeafId(0x1234));
    }

    [Fact]
    public void UniqueLeafAutoDrillOnlyResolvesWhenExactlyOneVisibleValidLeafExists()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        Apply(world,
            ("ManufacturingLab.PrimaryCategories.1.Categories.4.Subcategories.3.ID", I32(77)),
            ("ManufacturingLab.PrimaryCategories.1.Categories.4.Subcategories.3.Visible", U8(1)));
        Assert.True(domain.TryResolveUniqueVisibleLeaf(out uint id));
        Assert.Equal(77u, id);
        Assert.Equal(0, domain.BrowserDepth);
        Assert.True(domain.TryAutoSelectUniqueVisibleLeaf(out uint autoId));
        Assert.Equal(77u, autoId);
        Assert.Equal(3, domain.BrowserDepth);
        Assert.Equal(1, domain.SelectedPrimaryIndex);
        Assert.Equal(4, domain.SelectedCategoryIndex);
        Assert.Equal(3, domain.SelectedSubcategoryIndex);

        Apply(world,
            ("ManufacturingLab.PrimaryCategories.0.Categories.0.Subcategories.0.ID", I32(88)),
            ("ManufacturingLab.PrimaryCategories.0.Categories.0.Subcategories.0.Visible", U8(1)));
        Assert.False(domain.TryResolveUniqueVisibleLeaf(out _));
    }

    [Fact]
    public void VisibleLeafFailsClosedUntilItsAuthoritativeIdIsMaterializedAndDoesNotInventPositivityRules()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        Apply(world, ("ManufacturingLab.PrimaryCategories.0.Categories.0.Subcategories.0.Visible", U8(1)));

        Assert.True(domain.SelectPrimaryCategory(0));
        Assert.True(domain.SelectCategory(0));
        Assert.False(domain.TrySelectSubcategory(0, out _));

        // Absence is the blocker; it does not add a positivity
        // rule to the materialized signed ID value.
        Apply(world, ("ManufacturingLab.PrimaryCategories.0.Categories.0.Subcategories.0.ID", I32(-2)));
        Assert.True(domain.TrySelectSubcategory(0, out uint id));
        Assert.Equal(0xFFFFFFFEu, id);
    }

    [Fact]
    public void ActiveObjectBindingSafelyTracksMissingCreateRemoveAndReplacementWithoutChangingWireId()
    {
        var world = new Net7WorldState();
        var domain = new Net7ManufacturingDomain(id => world.Objects.FindFirst(id));
        domain.SetActiveObject(LabId);
        Assert.Null(domain.ActiveObject);
        Assert.Null(domain.CurrentLab);

        world.MaterializeCreate(new Net7CreateUpdate(LabId, 1f, 0, 0x27, 0, 0, 0));
        Assert.NotNull(domain.CurrentLab);

        world.MaterializeRemove(LabId);
        Assert.Equal(LabId, domain.ActiveObjectId);
        Assert.Null(domain.CurrentLab);

        world.MaterializeCreate(new Net7CreateUpdate(LabId, 1f, 0, 0x27, 0, 0, 0));
        Assert.NotNull(domain.CurrentLab);

        domain.SetActiveObject(0);
        Assert.Null(domain.ActiveObject);
    }

    [Fact]
    public void ManufactureAndRefinerySentinelsAreSuppressedBeforeWireConstruction()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world,
            ("ManufacturingLab.KnownFormula.0.ItemID", I32(0)),
            ("ManufacturingLab.PreviousAttempts.0.ItemID", I32(1)));
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        Assert.False(domain.CanSelectManufactureItem(uint.MaxValue));
        Assert.True(domain.CanSelectManufactureItem(0));
        Assert.True(domain.CanSelectManufactureItem(1));

        domain.RecordModeSent(Net7ManufacturingMode.Refining);
        Assert.False(domain.CanSelectRefineryItem(uint.MaxValue));
        Assert.True(domain.CanSelectRefineryItem(0));
        Assert.False(domain.CanSelectRefineryItem(1));
    }

    [Fact]
    public void TerminalSpecificIntentFailsClosedOutsideItsOwningMode()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world,
            ("ManufacturingLab.KnownFormula.0.ItemID", I32(500)),
            ("ManufacturingLab.PrimaryCategories.0.Categories.0.Subcategories.0.ID", I32(77)),
            ("ManufacturingLab.PrimaryCategories.0.Categories.0.Subcategories.0.Visible", U8(1)),
            ("ManufacturingLab.TechLevelFilterBitfield", I32(0x1FE)));

        Assert.False(domain.SelectPrimaryCategory(0));
        Assert.False(domain.CanSelectManufactureItem(500));
        Assert.False(domain.CanSelectRefineryItem(500));
        Assert.False(domain.ShouldSendFilter(true, 0x001));

        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        Assert.True(domain.SelectPrimaryCategory(0));
        Assert.True(domain.CanSelectManufactureItem(500));
        Assert.False(domain.CanSelectRefineryItem(500));
        Assert.True(domain.ShouldSendFilter(true, 0x001));

        domain.RecordModeSent(Net7ManufacturingMode.Refining);
        Assert.False(domain.SelectPrimaryCategory(0));
        Assert.False(domain.CanSelectManufactureItem(500));
        Assert.True(domain.CanSelectRefineryItem(500));
        Assert.False(domain.ShouldSendFilter(true, 0x001));
    }

    [Fact]
    public void SelectionPendingClearsWhenManufactureComponentsBecomeUsableNotOnUnrelatedAuxData()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world,
            ("ManufacturingLab.KnownFormula.0.ItemID", I32(500)),
            ("ManufacturingLab.ManufactureComponents.0.ItemTemplateID", I32(111)));
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        _ = domain.ReconcileAuthoritativeState();
        domain.RecordManufactureItemSent(500);
        Assert.True(domain.ManufactureSelectionPending);

        Apply(world, ("ManufacturingLab.NegotiatedCost", U64(123)));
        Net7ManufacturingReconcileResult unrelated = domain.ReconcileAuthoritativeState();
        Assert.False(unrelated.ManufactureSelectionReady);
        Assert.True(domain.ManufactureSelectionPending);

        Apply(world, ("ManufacturingLab.ManufactureComponents.0.ItemTemplateID", I32(222)));
        Net7ManufacturingReconcileResult ready = domain.ReconcileAuthoritativeState();
        Assert.True(ready.ManufactureSelectionReady);
        Assert.False(domain.ManufactureSelectionPending);
    }

    [Theory]
    [InlineData(Net7ManufacturingMode.Manufacture)]
    [InlineData(Net7ManufacturingMode.Refining)]
    public void ComponentPanelNeedsPendingSelectionReceiptAndStaysExpandedUntilClose(Net7ManufacturingMode mode)
    {
        var (world,domain)=CreateLab();
        domain.RecordModeSent(mode);
        Apply(world,("ManufacturingLab.ManufactureComponents.0.ItemTemplateID",I32(111)));
        domain.ReconcileAuthoritativeState();
        Assert.False(domain.ComponentPanelExpanded); // Populated slots alone do not start the request.
        if(mode==Net7ManufacturingMode.Manufacture)domain.RecordManufactureItemSent(500);
        else domain.RecordRefineryItemSent(500);
        Apply(world,("ManufacturingLab.NegotiatedCost",U64(123)));
        domain.ReconcileAuthoritativeState();
        Assert.False(domain.ComponentPanelExpanded);
        Apply(world,("ManufacturingLab.ManufactureComponents.0.ItemTemplateID",I32(-1)));
        domain.ReconcileAuthoritativeState();
        Assert.True(domain.ComponentPanelExpanded); // An empty dirty container is a receipt too.
        ulong first=domain.ComponentPanelRevision;
        Assert.NotEqual(0ul,first);
        if(mode==Net7ManufacturingMode.Manufacture)domain.RecordManufactureItemSent(501);
        else domain.RecordRefineryItemSent(501);
        Apply(world,("ManufacturingLab.ManufactureComponents.0.ItemTemplateID",I32(222)));
        domain.ReconcileAuthoritativeState();
        Assert.True(domain.ComponentPanelExpanded);
        Assert.Equal(first,domain.ComponentPanelRevision);
        domain.RecordModeSent(Net7ManufacturingMode.None);
        Assert.False(domain.ComponentPanelExpanded);
        domain.RecordModeSent(mode);
        domain.ReconcileAuthoritativeState();
        Assert.False(domain.ComponentPanelExpanded);
        if(mode==Net7ManufacturingMode.Manufacture)domain.RecordManufactureItemSent(502);
        else domain.RecordRefineryItemSent(502);
        Apply(world,("ManufacturingLab.ManufactureComponents.0.ItemTemplateID",I32(-1)));
        domain.ReconcileAuthoritativeState();
        Assert.Equal(first+1,domain.ComponentPanelRevision);
        domain.ResetForSectorBoundary();
        Assert.False(domain.ComponentPanelExpanded);
    }

    [Fact]
    public void FilterPolicyKeepsLocalUserIntentSeparateFromAuthoritativeMaterialization()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world, ("ManufacturingLab.TechLevelFilterBitfield", I32(0x1FE)));
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        Assert.Equal(0x1FEu, domain.LocalTechLevelFilterBitfield);

        Assert.True(domain.ShouldSendFilter(true, 0x001));
        domain.RecordFilterSent(true, 0x001);
        Assert.Equal(0x1FFu, domain.LocalTechLevelFilterBitfield);
        Assert.False(domain.ShouldSendFilter(true, 0x001));

        // User input is intention. A reverse toggle must still be
        // sendable before the first request has materialized through AuxData.
        Assert.True(domain.ShouldSendFilter(false, 0x001));
        domain.RecordFilterSent(false, 0x001);
        Assert.Equal(0x1FEu, domain.LocalTechLevelFilterBitfield);

        // Server-authored materialization synchronizes local checkbox state
        // without creating another request.
        Apply(world, ("ManufacturingLab.TechLevelFilterBitfield", I32(0x1FF)));
        _ = domain.ReconcileAuthoritativeState();
        Assert.Equal(0x1FFu, domain.LocalTechLevelFilterBitfield);
        // INV-D58: a repeated authoritative value that is marked updated restores the checks.
        domain.RecordFilterSent(false, 0x002);
        _ = domain.ReconcileAuthoritativeState();
        Assert.Equal(0x1FDu, domain.LocalTechLevelFilterBitfield);
        Apply(world, ("ManufacturingLab.TechLevelFilterBitfield", I32(0x1FF)));
        _ = domain.ReconcileAuthoritativeState();
        Assert.Equal(0x1FFu, domain.LocalTechLevelFilterBitfield);
        Assert.False(domain.ShouldSendFilter(true, 0x001));
        Assert.True(domain.ShouldSendFilter(false, 0x100));
    }

    [Fact]
    public void ManufactureActionUsesOneSecondPresentationGateAndResultRefreshInsteadOfRetransmission()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world, ("ManufacturingLab.KnownFormula.0.ItemID", I32(500)));
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        domain.RecordManufactureItemSent(500);
        Assert.True(domain.CanSendManualAction(2));
        domain.RecordActionSent(2);
        Assert.Equal(Net7ManufacturingActionContext.Manufacture, domain.ActionContext);
        Assert.Equal(1000u, domain.ActionCountdownMilliseconds);

        Apply(world, ("ManufacturingLab.Validity", I32(0x10)));
        Net7ManufacturingReconcileResult early = domain.ReconcileAuthoritativeState();
        Assert.False(early.RefreshManufactureSelection);
        Assert.Equal(Net7ManufacturingActionContext.Manufacture, domain.ActionContext);

        Assert.False(domain.AdvanceActionTimer(999));
        Assert.Equal(1u, EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain,null).ActionCountdownMilliseconds);
        Assert.False(domain.AdvanceActionTimer(1));
        Assert.Equal(0u, EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain,null).ActionCountdownMilliseconds);
        Net7ManufacturingReconcileResult result = domain.ReconcileAuthoritativeState();
        Assert.True(result.RefreshManufactureSelection);
        Assert.Equal(Net7ManufacturingActionContext.Idle, domain.ActionContext);
    }
    [Fact]
    public void TerminalPresentationRequiresValidLabAndDisablesActionsDuringAnAttempt()
    {
        var (world,domain)=CreateLab();
        Apply(world,("ManufacturingLab.KnownFormula.0.ItemID",I32(500)));
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        domain.RecordManufactureItemSent(500);
        Assert.False(EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain,null).CanAct);
        Apply(world,("ManufacturingLab.Validity",I32(13)));
        Assert.False(EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain,null).CanAct); // +BA4 = 1 until components update
        Apply(world,("ManufacturingLab.ManufactureComponents.0.ItemTemplateID",I32(1)));
        domain.ReconcileAuthoritativeState();
        Assert.True(EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain,null).CanAct);
        domain.RecordActionSent(2);
        Assert.False(EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain,null).CanAct);
    }

    [Fact]
    public void RefineryOnceAndStackStayDistinctAndRefreshRememberedRefinerySelectionOnAttemptResult()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world, ("ManufacturingLab.KnownFormula.0.ItemID", I32(600)));
        domain.RecordModeSent(Net7ManufacturingMode.Refining);
        Assert.False(domain.CanSendManualAction(2));
        Assert.False(domain.CanSendManualAction(3));
        domain.RecordRefineryItemSent(600);
        Assert.True(domain.CanSendManualAction(2));
        Assert.True(domain.CanSendManualAction(3));

        domain.RecordActionSent(3);
        Assert.Equal(Net7ManufacturingActionContext.Refinery, domain.ActionContext);
        Assert.Equal(0u, domain.ActionCountdownMilliseconds);
        Apply(world, ("ManufacturingLab.Validity", I32(0x0E)));
        Net7ManufacturingReconcileResult result = domain.ReconcileAuthoritativeState();
        Assert.True(result.RefreshRefinerySelection);
        Assert.False(result.RefreshManufactureSelection);
    }

    [Fact]
    public void AnalysisNearSuccessHonorsInitialGateAndResultRearmWithoutTurningActionOneIntoRetry()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Analyze);
        Apply(world, ("ManufacturingLab.Validity", I32(0x0D)));
        Assert.True(domain.CanSendManualAction(2));
        Assert.False(domain.CanSendManualAction(1));
        domain.RecordActionSent(2);
        Assert.Equal(Net7ManufacturingActionContext.Analyze, domain.ActionContext);
        Assert.Equal(2000u, domain.ActionCountdownMilliseconds);
        Assert.False(domain.AnalysisFollowUpArmed);
        // INV-D73/D74: busy analysis refuses commands and disables RESULT/OVER transfers.
        Assert.True(domain.AnalysisTransfersInhibited);
        Assert.False(domain.CanSendManualAction(0));
        Assert.False(domain.CanSendManualAction(2));

        // An early materialized result does not bypass the initial
        // two-second analysis/dismantle presentation gate.
        Apply(world, ("ManufacturingLab.Validity", I32(0x0F)));
        _ = domain.ReconcileAuthoritativeState();
        Assert.Equal(Net7ManufacturingActionContext.Analyze, domain.ActionContext);
        Assert.Equal(2000u, domain.ActionCountdownMilliseconds);
        Assert.False(domain.AnalysisFollowUpArmed);

        Assert.False(domain.AdvanceActionTimer(1999));
        Assert.True(domain.AdvanceActionTimer(1));
        Assert.Equal(0u, domain.ActionCountdownMilliseconds);
        Assert.True(domain.AnalysisTransfersInhibited);

        _ = domain.ReconcileAuthoritativeState();
        Assert.Equal(Net7ManufacturingActionContext.Idle, domain.ActionContext);
        Assert.True(domain.AnalysisFollowUpArmed);
        Assert.Equal(2000u, domain.ActionCountdownMilliseconds);
        // 00545D30: an idle Inventory reset hides the result but keeps timer and follow-up.
        Assert.True(domain.CanSendManualAction(0));
        domain.RecordActionSent(0);
        Assert.Null(domain.LastResult);
        Assert.True(domain.AnalysisFollowUpArmed);
        Assert.Equal(2000u, domain.ActionCountdownMilliseconds);

        domain.RecordActionSent(1);
        Assert.Equal(2000u, domain.ActionCountdownMilliseconds);
        Assert.True(domain.AdvanceActionTimer(2000));
        Assert.Equal(0u, domain.ActionCountdownMilliseconds);
        Assert.False(domain.AnalysisTransfersInhibited);
        domain.RecordActionSent(1);
        Assert.Equal(0u, domain.ActionCountdownMilliseconds);
        Assert.False(domain.AdvanceActionTimer(2000));

        Apply(world, ("ManufacturingLab.Validity", I32(0x10)));
        _ = domain.ReconcileAuthoritativeState();
        Assert.False(domain.AdvanceActionTimer(2000));
    }

    [Fact]
    public void MaterializedModeControlsOperationContextIncludingConsumedDismantleModeThree()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Analyze);
        Apply(world,
            ("ManufacturingLab.Mode", I32(3)),
            ("ManufacturingLab.Validity", I32(0x0D)));
        Assert.True(domain.CurrentLab!.ModeMaterialized);
        Assert.Equal(3, domain.CurrentLab.Mode);
        Assert.True(domain.CanSendManualAction(2));
        domain.RecordActionSent(2);
        Assert.Equal(Net7ManufacturingActionContext.Analyze, domain.ActionContext);
    }

    [Theory]
    [InlineData(0x0D, false)]
    [InlineData(0x0E, true)]
    [InlineData(0x0F, true)]
    [InlineData(0x10, true)]
    [InlineData(0x11, true)]
    [InlineData(0x12, false)]
    public void AttemptResultClassificationIsExactly0EThrough11(int validity, bool expected)
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world, ("ManufacturingLab.Validity", I32(validity)));
        Assert.Equal(expected, domain.CurrentLab!.IsAttemptResult);
    }

    [Fact]
    public void ModeCloseResetsLocalTerminalIntentButRetainsExplicitActiveObjectSelection()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world, ("ManufacturingLab.KnownFormula.0.ItemID", I32(123)));
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        domain.RecordManufactureItemSent(123);
        domain.RecordActionSent(2);
        domain.RecordModeSent(Net7ManufacturingMode.None);

        Assert.False(domain.IsTerminalOpen);
        Assert.Equal(LabId, domain.ActiveObjectId);
        Assert.Null(domain.RememberedManufactureItemId);
        Assert.Equal(Net7ManufacturingActionContext.Idle, domain.ActionContext);
    }

    [Fact]
    public void Gameplay007FUsesSameDynamicManufacturingBindingAndResetsAtSectorBoundary()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(LabId, 1f, 0, 0x27, 0, 0, 0));
        var gameplay = new Net7GameplayState(resolveWorldObject: id => world.Objects.FindFirst(id));
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, LabId);
        Assert.True(gameplay.Apply(Packet.ForOpcode(0x007F, payload)));
        Assert.NotNull(gameplay.ManufacturingDomain.CurrentLab);

        gameplay.ResetForServerHandoff();
        Assert.Equal(0u, gameplay.Manufacturing.ActiveObjectId);
        Assert.Null(gameplay.ManufacturingDomain.CurrentLab);
    }

    [Fact]
    public void OutOfRangeValidityAndDifficultyKeepTheLabAndCompareUnequalLikeRetail()
    {
        // 005B2480 compares Validity with 13 and IsManufacturingAttemptResult tests
        // its range; 00846090/00846160 show "error" outside their tables. No
        // rejection: the lab stays presented and nothing is enabled.
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world, ("ManufacturingLab.KnownFormula.0.ItemID", I32(500)));
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        domain.RecordManufactureItemSent(500);
        Apply(world, ("ManufacturingLab.Validity", I32(0x13)), ("ManufacturingLab.Difficulty", I32(7)),
            ("ManufacturingLab.ManufactureComponents.0.ItemTemplateID", I32(1)));
        domain.ReconcileAuthoritativeState();
        Net7ManufacturingLabProjection lab = domain.CurrentLab!;
        Assert.Equal(0x13, (int)lab.Validity);
        Assert.False(lab.IsAttemptResult);
        Assert.False(EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain, null).CanAct);

        Apply(world, ("ManufacturingLab.Validity", I32(13)));
        Assert.True(EnbNext.Presentation.EnbStationTerminalSnapshot.Build(domain, null).CanAct);
    }

    private static (Net7WorldState World, Net7ManufacturingDomain Domain) CreateLab()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(LabId, 1f, 0, 0x27, 0, 0, 0));
        var domain = new Net7ManufacturingDomain(id => world.Objects.FindFirst(id));
        domain.SetActiveObject(LabId);
        return (world, domain);
    }

    private static void Apply(Net7WorldState world, params (string Path, byte[] Wire)[] values)
    {
        Net7AuxDataSchema schema = world.Objects.FindFirst(LabId)!.AuxData!;
        using var delta = new MemoryStream();
        delta.WriteByte(0);
        WriteU32(delta, checked((uint)values.Length));
        foreach ((string path, byte[] wire) in values)
        {
            Net7AuxDataProperty property = Assert.Single(schema.Properties, p => p.Path == path);
            WriteU32(delta, checked((uint)property.Id));
            delta.Write(wire);
        }
        WriteU32(delta, 0); // state-two list
        WriteU32(delta, 0); // state-zero list

        byte[] encoded = delta.ToArray();
        byte[] envelope = new byte[6 + encoded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(0, 4), LabId);
        BinaryPrimitives.WriteUInt16LittleEndian(envelope.AsSpan(4, 2), checked((ushort)encoded.Length));
        encoded.CopyTo(envelope, 6);
        _ = world.Apply(Packet.ForOpcode(0x001B, envelope), 0);
    }

    private static byte[] I32(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] U64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] F32(float value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
        return bytes;
    }

    private static byte[] U8(byte value) => [value];

    private static byte[] Lp(string value)
    {
        byte[] text = Encoding.Latin1.GetBytes(value);
        byte[] bytes = new byte[2 + text.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)text.Length));
        text.CopyTo(bytes, 2);
        return bytes;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    [Fact]
    public void TerminalUiTriggersUseEachTerminalsRetailActions()
    {
        // INV-D65/D72: 005B0F14/005B1195, 005EC495/005EC662, 005441C0/0054437F.
        Assert.Equal(0x17u, EnbNext.Client.EnbClient.TerminalUiTrigger(2, true));
        Assert.Equal(0x18u, EnbNext.Client.EnbClient.TerminalUiTrigger(2, false));
        Assert.Equal(0x1Du, EnbNext.Client.EnbClient.TerminalUiTrigger(0, true));
        Assert.Equal(0x1Eu, EnbNext.Client.EnbClient.TerminalUiTrigger(0, false));
        Assert.Equal(0x19u, EnbNext.Client.EnbClient.TerminalUiTrigger(1, true));
        Assert.Equal(0x1Au, EnbNext.Client.EnbClient.TerminalUiTrigger(1, false));
    }
}
