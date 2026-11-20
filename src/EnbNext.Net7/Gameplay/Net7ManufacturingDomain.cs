using EnbNext.Net7.World;

namespace EnbNext.Net7.Gameplay;

public enum Net7ManufacturingValidity : int
{
    NoTarget = 0x00,
    NotManufacturable = 0x01,
    NotDismantleable = 0x02,
    RecipeNotKnown = 0x03,
    InsufficientCredits = 0x04,
    MissingComponents = 0x05,
    InsufficientCargo = 0x06,
    NotQualified = 0x07,
    TooDifficult = 0x08,
    LowQualitiy = 0x09,
    BadStackSize = 0x0A,
    PlayerMade = 0x0B,
    DuplicateUniqueItem = 0x0C,
    Valid = 0x0D,
    AttemptTotalFailure = 0x0E,
    AttemptNearSuccess = 0x0F,
    AttemptNormalSuccess = 0x10,
    AttemptCriticalSuccess = 0x11,
    Invalid = 0x12,
}

public enum Net7ManufacturingDifficulty : int
{
    Invalid = 0,
    Automatic = 1,
    Easy = 2,
    Moderate = 3,
    Hard = 4,
    VeryHard = 5,
    Impossible = 6,
}

public enum Net7ManufacturingActionContext
{
    Idle,
    Manufacture,
    Analyze,
    Refinery,
}
public sealed record Net7ManufacturingAttemptResult(ulong Revision, int Mode, Net7ManufacturingValidity Validity);

public sealed record Net7ManufacturingFormula(string ItemName, int ItemId, int TechLevel)
{
    public bool HasItem => ItemId != -1;
}

public sealed record Net7ManufacturingInventoryItem(
    int ItemTemplateId,
    int StackCount,
    ulong Price,
    float AverageCost,
    float Structure,
    float Quality,
    string InstanceInfo,
    string InstanceActivatedEffectInfo,
    string InstanceEquipEffectInfo,
    string BuilderName,
    bool IsMaterialized)
{
    public bool IsQualitySentinel => Quality == -1.0f;
    public float? QualityPercent => IsQualitySentinel ? null : Quality * 100.0f;
}

public sealed record Net7ManufacturingSubcategory(string Name, int Id, bool IdMaterialized, bool Visible);
public sealed record Net7ManufacturingCategory(
    string Name,
    int Id,
    IReadOnlyList<Net7ManufacturingSubcategory> Subcategories);
public sealed record Net7ManufacturingPrimaryCategory(
    string Name,
    IReadOnlyList<Net7ManufacturingCategory> Categories);

public sealed record Net7ManufacturingLabProjection(
    uint ObjectId,
    string Name,
    int Mode,
    bool ModeMaterialized,
    Net7ManufacturingValidity Validity,
    string FailureMessage,
    Net7ManufacturingDifficulty Difficulty,
    IReadOnlyList<Net7ManufacturingInventoryItem> ManufactureTargets,
    IReadOnlyList<Net7ManufacturingInventoryItem> ManufactureComponents,
    IReadOnlyList<Net7ManufacturingInventoryItem> ManufactureOverride,
    ulong NegotiatedCost,
    ulong BaseCost,
    IReadOnlyList<Net7ManufacturingPrimaryCategory> PrimaryCategories,
    IReadOnlyList<Net7ManufacturingFormula> KnownFormula,
    IReadOnlyList<Net7ManufacturingFormula> PreviousAttempts,
    int CurrentItemCategory,
    float SuccessProbability,
    float CriticalSuccessProbability,
    float ExpectedQuality,
    float MinimumQuality,
    float MaximumQuality,
    int AdditionalIterations,
    int TechLevelFilterBitfield,
    int MaterializedComponentCount)
{
    public int KnownFormulaState { get; init; }
    public ulong KnownFormulaRevision { get; init; }
    public int PreviousAttemptsState { get; init; }
    public ulong PreviousAttemptsRevision { get; init; }
    public bool ComponentsUpdated { get; init; }
    public ulong CreationOrdinal { get; init; }
    public ulong TargetInventoryRevision { get; init; }
    public ulong ComponentInventoryRevision { get; init; }
    public ulong TechLevelFilterRevision { get; init; }
    // 005B5320 rewrites the quality labels and reports an unusable selection
    // only for properties updated this frame (AuxData state 2; SClientRunFrame
    // 00742B60 -> 006F5120 normalizes every object's AuxData each frame).
    public ulong ValidityRevision { get; init; }
    public int ValidityState { get; init; }
    public int SuccessProbabilityState { get; init; }
    public int CriticalSuccessProbabilityState { get; init; }
    public ulong SuccessProbabilityRevision { get; init; }
    public ulong CriticalSuccessProbabilityRevision { get; init; }
    public int MinimumQualityState { get; init; }
    public int MaximumQualityState { get; init; }
    public int ExpectedQualityState { get; init; }
    public ulong MinimumQualityRevision { get; init; }
    public ulong MaximumQualityRevision { get; init; }
    public ulong ExpectedQualityRevision { get; init; }
    public bool IsMapped(int itemId)
        => itemId != -1 && KnownFormula.Any(formula => formula.ItemId == itemId);

    public bool IsAttemptResult
        => Validity is >= Net7ManufacturingValidity.AttemptTotalFailure and <= Net7ManufacturingValidity.AttemptCriticalSuccess;

    public float SuccessPercent => SuccessProbability * 100.0f;
    public float CriticalSuccessPercent => CriticalSuccessProbability * 100.0f;

    // Keep authoritative quality floats raw, but match the current
    // retail manufacturing presentation. -1 is the unavailable sentinel; other
    // expected/min/max values are presentation-clamped to 0..3 before ×100.
    // The range is a pair: if either side is unavailable the whole range is, and
    // if normalized min > max retail collapses min to max. Comparisons are kept
    // literal rather than using Math.Clamp so NaN follows current x86 compare
    // behavior and remains untouched.
    public float? ExpectedQualityPercent
        => ExpectedQuality == -1.0f ? null : NormalizeQuality(ExpectedQuality) * 100.0f;

    public (float MinimumPercent, float MaximumPercent)? QualityRangePercent
    {
        get
        {
            if (MinimumQuality == -1.0f || MaximumQuality == -1.0f)
                return null;
            float minimum = NormalizeQuality(MinimumQuality);
            float maximum = NormalizeQuality(MaximumQuality);
            if (minimum > maximum)
                minimum = maximum;
            return (minimum * 100.0f, maximum * 100.0f);
        }
    }

    public float? MinimumQualityPercent => QualityRangePercent?.MinimumPercent;
    public float? MaximumQualityPercent => QualityRangePercent?.MaximumPercent;

    private static float NormalizeQuality(float value)
    {
        if (value < 0.0f)
            return 0.0f;
        if (value > 3.0f)
            return 3.0f;
        return value;
    }
}

public readonly record struct Net7ManufacturingReconcileResult(
    bool ManufactureSelectionReady,
    bool RefinerySelectionReady,
    bool RefreshManufactureSelection,
    bool RefreshRefinerySelection);

/// <summary>
/// Clean manufacturing-domain state. Authoritative rich terminal data stays
/// in the object-targeted Type39 ManufacturingLab AuxData tree; this controller
/// projects it on demand and owns only local browser/request/action intent.
/// </summary>
public sealed class Net7ManufacturingDomain
{
    private readonly Func<uint, Net7WorldObject?> _resolveObject;
    private int _browserDepth;
    private int _primaryIndex = -1;
    private int _categoryIndex = -1;
    private int _subcategoryIndex = -1;
    private uint? _rememberedManufactureItemId;
    private uint? _rememberedRefineryItemId;
    private bool _manufactureSelectionPending;
    private bool _refinerySelectionPending;
    // Filter widgets are client-local intention state. The authoritative
    // TechLevelFilterBitfield arrives separately through object AuxData and
    // synchronizes these bits without emitting another 0x0080.
    private uint _localTechLevelFilterBitfield = 0x1FF;
    private uint? _lastMaterializedTechLevelFilterBitfield;
    private ulong _lastTechLevelFilterRevision;
    public bool InitialCategoryFilterPending { get; private set; }
    private Net7ManufacturingActionContext _actionContext;
    private uint _actionCountdownMilliseconds;
    private bool _analysisFollowUpArmed;
    // 005458B0/0062F5B0: analysis start disables RESULT12/OVE drag and drop;
    // 005443F0 re-enables them on timer expiry only while analysis is not busy.
    private bool _analysisTransfersInhibited;

    public Net7ManufacturingDomain(Func<uint, Net7WorldObject?>? resolveObject = null)
        => _resolveObject = resolveObject ?? (_ => null);

    public uint ActiveObjectId { get; private set; }
    public Net7ManufacturingMode RequestedMode { get; private set; }
    public bool IsTerminalOpen => RequestedMode != Net7ManufacturingMode.None;
    public int BrowserDepth => _browserDepth;
    public int SelectedPrimaryIndex => _primaryIndex;
    public int SelectedCategoryIndex => _categoryIndex;
    public int SelectedSubcategoryIndex => _subcategoryIndex;
    public uint? RememberedManufactureItemId => _rememberedManufactureItemId;
    public uint? RememberedRefineryItemId => _rememberedRefineryItemId;
    public bool ManufactureSelectionPending => _manufactureSelectionPending;
    public bool RefinerySelectionPending => _refinerySelectionPending;
    public bool ComponentPanelExpanded { get; private set; }
    public ulong ComponentPanelRevision { get; private set; }
    // 005B1230 calls 005B2480 whenever a pending selection consumes updated
    // components (+BA4 1 -> 0), not only when the panel first expands.
    public ulong SelectionReadyRevision { get; private set; }
    public Net7ManufacturingActionContext ActionContext => _actionContext;
    private ulong _resultRevision;
    public Net7ManufacturingAttemptResult? LastResult { get; private set; }
    public uint ActionCountdownMilliseconds => _actionCountdownMilliseconds;
    public bool AnalysisFollowUpArmed => _analysisFollowUpArmed;
    public bool AnalysisTransfersInhibited => _analysisTransfersInhibited;
    public uint LocalTechLevelFilterBitfield => _localTechLevelFilterBitfield;

    public Net7WorldObject? ActiveObject
        => ActiveObjectId == 0 ? null : _resolveObject(ActiveObjectId);

    public Net7ManufacturingLabProjection? CurrentLab
    {
        get
        {
            Net7WorldObject? obj = ActiveObject;
            if (obj?.ObjectType != 0x27 || obj.AuxData?.Name != "ManufacturingLab")
                return null;
            return Project(obj);
        }
    }

    public void SetActiveObject(uint objectId)
    {
        // The inbound active-object update owns exactly one state
        // mutation. Retail only overwrites that object ID; it does not clear
        // local terminal/browser/selection/action/filter intent. Lifecycle
        // resets remain owned by explicit interface/sector-boundary paths.
        ActiveObjectId = objectId;
    }

    public void RecordModeSent(Net7ManufacturingMode mode)
    {
        LastResult = null;
        ComponentPanelExpanded = false;
        RequestedMode = mode;
        InitialCategoryFilterPending = mode == Net7ManufacturingMode.Manufacture;
        if (mode == Net7ManufacturingMode.None)
        {
            ResetLocalTerminalState(clearActiveObject: false);
            ResetLocalFilterState();
            return;
        }

        if (mode == Net7ManufacturingMode.Manufacture)
            SynchronizeFilterFromAuthoritative(CurrentLab, force: true);

        _browserDepth = 0;
        _primaryIndex = -1;
        _categoryIndex = -1;
        _subcategoryIndex = -1;
        _actionContext = Net7ManufacturingActionContext.Idle;
        _actionCountdownMilliseconds = 0;
        _analysisFollowUpArmed = false;
        _analysisTransfersInhibited = false;
    }

    public void ResetForSectorBoundary()
    {
        ActiveObjectId = 0;
        RequestedMode = Net7ManufacturingMode.None;
        ResetLocalTerminalState(clearActiveObject: false);
        ResetLocalFilterState();
    }

    public bool SelectPrimaryCategory(int index)
    {
        Net7ManufacturingLabProjection? lab = CurrentLab;
        if (RequestedMode != Net7ManufacturingMode.Manufacture
            || lab is null
            || _browserDepth != 0
            || (uint)index >= (uint)lab.PrimaryCategories.Count)
            return false;
        _primaryIndex = index;
        _categoryIndex = -1;
        _subcategoryIndex = -1;
        _browserDepth = 1;
        return true;
    }
    // Current retail 005B1C20 changes browser depth locally, preserving indices.
    public bool NavigateBrowser(int depth)
    {
        if (RequestedMode != Net7ManufacturingMode.Manufacture || CurrentLab is null || depth < 0 || depth > _browserDepth) return false;
        // 005B1CAC..1D27: the tab of the level already shown returns to the formula list (depth 3) (INV-D56).
        _browserDepth = depth == _browserDepth && depth < 3 ? 3 : depth;
        return true;
    }

    public bool SelectCategory(int index)
    {
        Net7ManufacturingLabProjection? lab = CurrentLab;
        if (RequestedMode != Net7ManufacturingMode.Manufacture || lab is null || _browserDepth != 1 || (uint)_primaryIndex >= (uint)lab.PrimaryCategories.Count)
            return false;
        IReadOnlyList<Net7ManufacturingCategory> categories = lab.PrimaryCategories[_primaryIndex].Categories;
        if ((uint)index >= (uint)categories.Count)
            return false;
        _categoryIndex = index;
        _subcategoryIndex = -1;
        _browserDepth = 2;
        return true;
    }

    public bool TrySelectSubcategory(int index, out uint leafId)
    {
        leafId = 0;
        Net7ManufacturingLabProjection? lab = CurrentLab;
        if (RequestedMode != Net7ManufacturingMode.Manufacture || lab is null || _browserDepth != 2 || (uint)_primaryIndex >= (uint)lab.PrimaryCategories.Count)
            return false;
        IReadOnlyList<Net7ManufacturingCategory> categories = lab.PrimaryCategories[_primaryIndex].Categories;
        if ((uint)_categoryIndex >= (uint)categories.Count)
            return false;
        IReadOnlyList<Net7ManufacturingSubcategory> leaves = categories[_categoryIndex].Subcategories;
        if ((uint)index >= (uint)leaves.Count)
            return false;
        Net7ManufacturingSubcategory leaf = leaves[index];
        if (!leaf.Visible || !leaf.IdMaterialized)
            return false;
        _subcategoryIndex = index;
        _browserDepth = 3;
        leafId = unchecked((uint)leaf.Id);
        return true;
    }

    public bool IsAuthoritativeLeafId(uint leafId)
    {
        Net7ManufacturingLabProjection? lab = CurrentLab;
        if (RequestedMode != Net7ManufacturingMode.Manufacture || lab is null)
            return false;
        return lab.PrimaryCategories
            .SelectMany(primary => primary.Categories)
            .SelectMany(category => category.Subcategories)
            .Any(leaf => leaf.Visible && leaf.IdMaterialized && unchecked((uint)leaf.Id) == leafId);
    }

    public bool TryResolveUniqueVisibleLeaf(out uint leafId)
        => TryFindUniqueVisibleLeaf(updateSelection: false, out leafId);

    public bool TryAutoSelectUniqueVisibleLeaf(out uint leafId)
        => TryFindUniqueVisibleLeaf(updateSelection: true, out leafId);

    private bool TryFindUniqueVisibleLeaf(bool updateSelection, out uint leafId)
    {
        leafId = 0;
        Net7ManufacturingLabProjection? lab = CurrentLab;
        if (RequestedMode != Net7ManufacturingMode.Manufacture || lab is null)
            return false;

        (int Primary, int Category, int Subcategory, Net7ManufacturingSubcategory Leaf)? found = null;
        for (int p = 0; p < lab.PrimaryCategories.Count; p++)
        {
            IReadOnlyList<Net7ManufacturingCategory> categories = lab.PrimaryCategories[p].Categories;
            for (int c = 0; c < categories.Count; c++)
            {
                IReadOnlyList<Net7ManufacturingSubcategory> leaves = categories[c].Subcategories;
                for (int sub = 0; sub < leaves.Count; sub++)
                {
                    Net7ManufacturingSubcategory leaf = leaves[sub];
                    if (!leaf.Visible || !leaf.IdMaterialized)
                        continue;
                    if (found is not null)
                        return false;
                    found = (p, c, sub, leaf);
                }
            }
        }

        if (found is not { } unique)
            return false;
        if (updateSelection)
        {
            _primaryIndex = unique.Primary;
            _categoryIndex = unique.Category;
            _subcategoryIndex = unique.Subcategory;
            _browserDepth = 3;
        }
        leafId = unchecked((uint)unique.Leaf.Id);
        return true;
    }

    public bool CanSelectManufactureItem(uint itemId)
    {
        if (RequestedMode != Net7ManufacturingMode.Manufacture
            || itemId == uint.MaxValue
            || CurrentLab is not Net7ManufacturingLabProjection lab)
            return false;
        int signed = unchecked((int)itemId);
        return lab.KnownFormula.Any(formula => formula.ItemId == signed)
            || lab.PreviousAttempts.Any(formula => formula.ItemId == signed);
    }

    public bool CanSelectRefineryItem(uint itemId)
    {
        if (RequestedMode != Net7ManufacturingMode.Refining
            || itemId == uint.MaxValue
            || CurrentLab is not Net7ManufacturingLabProjection lab)
            return false;
        int signed = unchecked((int)itemId);
        return lab.KnownFormula.Any(formula => formula.ItemId == signed);
    }

    public void RecordManufactureItemSent(uint itemId)
    {
        if (itemId == uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(itemId));
        _rememberedManufactureItemId = itemId;
        _manufactureSelectionPending = true;
        // SendManufactureSetItemId 005B39B0 / SendRefinerySetItemId 005ED530
        // overwrite the single pending-action field (+BA4 / +AF0) with 1, so a
        // selection also ends an attempt still waiting for its result.
        _actionContext = Net7ManufacturingActionContext.Idle;
    }

    public void RecordRefineryItemSent(uint itemId)
    {
        if (itemId == uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(itemId));
        _rememberedRefineryItemId = itemId;
        _refinerySelectionPending = true;
        _actionContext = Net7ManufacturingActionContext.Idle;
    }

    public bool CanSendManualAction(uint actionId)
    {
        if (actionId > 3 || !IsTerminalOpen)
            return false;
        Net7ManufacturingActionContext context = ResolveOperationContext();
        Net7ManufacturingLabProjection? lab = CurrentLab;
        bool analysisValid = lab?.Validity == Net7ManufacturingValidity.Valid;
        return actionId switch
        {
            // 00544BB0: analysis busy (+110) refuses every command, Inventory included.
            0 => _actionContext != Net7ManufacturingActionContext.Analyze,
            1 => false, // generated only by the analysis countdown state machine
            2 when context == Net7ManufacturingActionContext.Manufacture => _rememberedManufactureItemId is uint manufacture && manufacture != uint.MaxValue,
            2 when context == Net7ManufacturingActionContext.Refinery => _rememberedRefineryItemId is uint refinery && refinery != uint.MaxValue,
            2 when context == Net7ManufacturingActionContext.Analyze => analysisValid && _actionContext != Net7ManufacturingActionContext.Analyze,
            3 => context == Net7ManufacturingActionContext.Refinery && _rememberedRefineryItemId is uint stack && stack != uint.MaxValue,
            _ => false,
        };
    }

    public void RecordActionSent(uint actionId)
    {
        if (actionId is 0 or 2 or 3) LastResult = null;
        if (actionId > 3)
            throw new ArgumentOutOfRangeException(nameof(actionId));
        Net7ManufacturingActionContext context = ResolveOperationContext();
        switch (actionId)
        {
            case 0 when context == Net7ManufacturingActionContext.Analyze:
                // 00545D30 hides results only; busy +110 and timer +114 are unchanged (INV-D74).
                break;
            case 0:
                _actionContext = Net7ManufacturingActionContext.Idle;
                _actionCountdownMilliseconds = 0;
                _analysisFollowUpArmed = false;
                break;
            case 1:
                // Sending the analysis near-success follow-up does NOT
                // itself write/re-arm the 2.0-second retail countdown. Result
                // presentation (and other separately proven UI-state paths) own
                // any later re-arm, so do not turn ActionID 1 into a retry loop.
                break;
            case 2 when context == Net7ManufacturingActionContext.Manufacture:
                _actionContext = Net7ManufacturingActionContext.Manufacture;
                _actionCountdownMilliseconds = 1000;
                _analysisFollowUpArmed = false;
                break;
            case 2 when context == Net7ManufacturingActionContext.Analyze:
                _actionContext = Net7ManufacturingActionContext.Analyze;
                _actionCountdownMilliseconds = 2000;
                _analysisFollowUpArmed = false;
                _analysisTransfersInhibited = true;
                break;
            case 2 when context == Net7ManufacturingActionContext.Refinery:
            case 3 when context == Net7ManufacturingActionContext.Refinery:
                _actionContext = Net7ManufacturingActionContext.Refinery;
                _actionCountdownMilliseconds = 0;
                _analysisFollowUpArmed = false;
                break;
            default:
                throw new InvalidOperationException($"ActionID {actionId} is not valid in current manufacturing context {context}.");
        }
    }

    /// <summary>
    /// Advances only the client-owned presentation/follow-up countdowns. The
    /// returned true means the proven analysis ActionID-1 follow-up is due now;
    /// it is not a retransmission ACK timer and carries no fixed retry budget.
    /// </summary>
    public bool AdvanceActionTimer(uint elapsedMilliseconds)
    {
        if (_actionCountdownMilliseconds == 0)
            return false;

        if (elapsedMilliseconds < _actionCountdownMilliseconds)
        {
            _actionCountdownMilliseconds -= elapsedMilliseconds;
            return false;
        }

        _actionCountdownMilliseconds = 0;
        if (_actionContext != Net7ManufacturingActionContext.Analyze)
            _analysisTransfersInhibited = false;
        Net7ManufacturingLabProjection? lab = CurrentLab;
        if (_actionContext == Net7ManufacturingActionContext.Manufacture)
            return false; // one-second presentation gate, never a resend timer

        // Retail checks AttemptNearSuccess exactly when an analysis
        // countdown crosses below zero. This applies both to the initial
        // ActionID-2 gate (ActionContext Analyze) and to a later countdown
        // explicitly re-armed by result presentation. Expiry itself does not
        // re-arm the timer.
        if (_actionContext == Net7ManufacturingActionContext.Analyze)
            return lab?.Validity == Net7ManufacturingValidity.AttemptNearSuccess;

        if (!_analysisFollowUpArmed)
            return false;

        _analysisFollowUpArmed = false;
        return lab?.Validity == Net7ManufacturingValidity.AttemptNearSuccess;
    }

    public Net7ManufacturingReconcileResult ReconcileAuthoritativeState()
    {
        Net7ManufacturingLabProjection? lab = CurrentLab;
        SynchronizeFilterFromAuthoritative(lab, force: false);
        // 005B1230 tests ManufactureComponents AuxData state (+70) > 1.
        // It does not require two populated component slots.
        bool componentsUsable = lab?.ComponentsUpdated == true;
        bool manufactureReady = _manufactureSelectionPending && componentsUsable;
        bool refineryReady = _refinerySelectionPending && componentsUsable;
        // 005B1230/005EC6D0 enqueue the one-shot panel animation only when
        // a pending selection consumes a dirty component container. Empty
        // containers count too; later inventory changes never retract it.
        if (!ComponentPanelExpanded && (manufactureReady || refineryReady))
        {
            ComponentPanelExpanded = true;
            ComponentPanelRevision++;
        }
        if (manufactureReady || refineryReady)
            SelectionReadyRevision++;
        if (manufactureReady)
            _manufactureSelectionPending = false;
        if (refineryReady)
            _refinerySelectionPending = false;

        bool refreshManufacture = false;
        bool refreshRefinery = false;
        if (lab?.IsAttemptResult == true)
        {
            if (_actionContext == Net7ManufacturingActionContext.Idle
                && _actionCountdownMilliseconds == 0
                && lab.Validity != Net7ManufacturingValidity.AttemptNearSuccess)
            {
                _analysisFollowUpArmed = false;
            }

            if (_actionContext == Net7ManufacturingActionContext.Manufacture)
            {
                // The one-second manufacture countdown gates result
                // processing. An early materialized result waits behind it.
                if (_actionCountdownMilliseconds == 0)
                {
                    LastResult = new(++_resultRevision, lab.Mode, lab.Validity);
                    _actionContext = Net7ManufacturingActionContext.Idle;
                    refreshManufacture = _rememberedManufactureItemId is not null;
                }
            }
            else if (_actionContext == Net7ManufacturingActionContext.Refinery)
            {
                LastResult = new(++_resultRevision, lab.Mode, lab.Validity);
                _actionContext = Net7ManufacturingActionContext.Idle;
                refreshRefinery = _rememberedRefineryItemId is not null;
            }
            else if (_actionContext == Net7ManufacturingActionContext.Analyze)
            {
                // Analysis/dismantle keeps its in-progress state until
                // the initial 2.0-second countdown has expired. Only then may a
                // materialized 0x0E..0x11 result be consumed. Result presentation
                // re-arms a separate 2.0-second countdown; NearSuccess is the
                // only result that can emit ActionID 1 on that later expiry.
                if (_actionCountdownMilliseconds == 0)
                {
                    LastResult = new(++_resultRevision, lab.Mode, lab.Validity);
                    _actionContext = Net7ManufacturingActionContext.Idle;
                    _analysisFollowUpArmed = lab.Validity == Net7ManufacturingValidity.AttemptNearSuccess;
                    _actionCountdownMilliseconds = 2000;
                }
            }
        }
        else if (_actionContext == Net7ManufacturingActionContext.Analyze && _actionCountdownMilliseconds == 0
            && lab is { ValidityState: not 0 } && lab.Validity != Net7ManufacturingValidity.Valid)
        {
            // 005443F0 ends an analysis for any materialized Validity other
            // than 13 (+110 = 0, +114 = 2.0 s); outside 0x0E..0x11 it reports
            // an assertion and presents no result text or sound.
            _actionContext = Net7ManufacturingActionContext.Idle;
            _analysisFollowUpArmed = false;
            _actionCountdownMilliseconds = 2000;
        }

        if (lab is not null && ActiveObject?.AuxData is {} aux)
        {
            var components = aux.Root.Children.Single(p => p.Path == "ManufacturingLab.ManufactureComponents");
            Net7AuxDataCodec.Normalize(components);
        }
        return new Net7ManufacturingReconcileResult(manufactureReady, refineryReady, refreshManufacture, refreshRefinery);
    }

    public bool ShouldSendFilter(bool enable, uint bitField)
    {
        if (RequestedMode != Net7ManufacturingMode.Manufacture || CurrentLab is null)
            return false;

        // USER_INPUT is intention, not authoritative state. Compare to
        // the local checkbox state so a user can toggle back before the server's
        // prior AuxData update arrives. Repeated identical UI state is suppressed
        // because the retail callback would not represent a new toggle either.
        uint next = enable
            ? _localTechLevelFilterBitfield | bitField
            : _localTechLevelFilterBitfield & ~bitField;
        return next != _localTechLevelFilterBitfield;
    }

    public void RecordFilterSent(bool enable, uint bitField)
    {
        InitialCategoryFilterPending = false;
        _localTechLevelFilterBitfield = enable
            ? _localTechLevelFilterBitfield | bitField
            : _localTechLevelFilterBitfield & ~bitField;
        _localTechLevelFilterBitfield &= 0x1FF;
    }

    private void SynchronizeFilterFromAuthoritative(Net7ManufacturingLabProjection? lab, bool force)
    {
        if (lab is null)
            return;

        uint authoritative = unchecked((uint)lab.TechLevelFilterBitfield) & 0x1FF;
        // 005B5320 raw 005B5A31..5A58: an updated field restores the local checks even
        // when it repeats the same bits (INV-D58).
        if (!force && _lastMaterializedTechLevelFilterBitfield == authoritative && _lastTechLevelFilterRevision == lab.TechLevelFilterRevision)
            return;

        _lastMaterializedTechLevelFilterBitfield = authoritative;
        _lastTechLevelFilterRevision = lab.TechLevelFilterRevision;
        _localTechLevelFilterBitfield = authoritative;
    }

    private void ResetLocalFilterState()
    {
        InitialCategoryFilterPending = false;
        _localTechLevelFilterBitfield = 0x1FF;
        _lastMaterializedTechLevelFilterBitfield = null;
    }

    private Net7ManufacturingActionContext ResolveOperationContext()
    {
        Net7ManufacturingLabProjection? lab = CurrentLab;
        if (lab?.ModeMaterialized == true)
        {
            return lab.Mode switch
            {
                1 => Net7ManufacturingActionContext.Manufacture,
                2 or 3 => Net7ManufacturingActionContext.Analyze,
                4 => Net7ManufacturingActionContext.Refinery,
                _ => Net7ManufacturingActionContext.Idle,
            };
        }

        return RequestedMode switch
        {
            Net7ManufacturingMode.Manufacture => Net7ManufacturingActionContext.Manufacture,
            Net7ManufacturingMode.Analyze => Net7ManufacturingActionContext.Analyze,
            Net7ManufacturingMode.Refining => Net7ManufacturingActionContext.Refinery,
            _ => Net7ManufacturingActionContext.Idle,
        };
    }

    private void ResetLocalTerminalState(bool clearActiveObject)
    {
        LastResult = null;
        ComponentPanelExpanded = false;
        if (clearActiveObject)
            ActiveObjectId = 0;
        _browserDepth = 0;
        _primaryIndex = -1;
        _categoryIndex = -1;
        _subcategoryIndex = -1;
        _rememberedManufactureItemId = null;
        _rememberedRefineryItemId = null;
        _manufactureSelectionPending = false;
        _refinerySelectionPending = false;
        _actionContext = Net7ManufacturingActionContext.Idle;
        _actionCountdownMilliseconds = 0;
        _analysisFollowUpArmed = false;
        _analysisTransfersInhibited = false;
    }

    private static Net7ManufacturingLabProjection Project(Net7WorldObject obj)
    {
        Net7AuxDataSchema schema = obj.AuxData!;
        Dictionary<string, Net7AuxDataProperty> byPath = schema.Properties.ToDictionary(p => p.Path, StringComparer.Ordinal);
        const string root = "ManufacturingLab";

        IReadOnlyList<Net7ManufacturingInventoryItem> targets = Enumerable.Range(0, 2)
            .Select(i => ReadItem(byPath, $"{root}.ManufactureTarget.{i}"))
            .ToArray();
        IReadOnlyList<Net7ManufacturingInventoryItem> components = Enumerable.Range(0, 6)
            .Select(i => ReadItem(byPath, $"{root}.ManufactureComponents.{i}"))
            .ToArray();
        IReadOnlyList<Net7ManufacturingInventoryItem> overrides =
            [ReadItem(byPath, $"{root}.ManufactureOverride.0")];

        List<Net7ManufacturingPrimaryCategory> primary = new(2);
        for (int p = 0; p < 2; p++)
        {
            List<Net7ManufacturingCategory> categories = new(5);
            string pbase = $"{root}.PrimaryCategories.{p}";
            for (int c = 0; c < 5; c++)
            {
                string cbase = $"{pbase}.Categories.{c}";
                List<Net7ManufacturingSubcategory> subcategories = new(5);
                for (int s = 0; s < 5; s++)
                {
                    string sbase = $"{cbase}.Subcategories.{s}";
                    subcategories.Add(new Net7ManufacturingSubcategory(
                        GetString(byPath, $"{sbase}.Name", string.Empty),
                        GetInt32(byPath, $"{sbase}.ID", -1),
                        byPath[$"{sbase}.ID"].State != 0,
                        GetByte(byPath, $"{sbase}.Visible", 0) != 0));
                }
                categories.Add(new Net7ManufacturingCategory(
                    GetString(byPath, $"{cbase}.Name", string.Empty),
                    GetInt32(byPath, $"{cbase}.ID", -1),
                    subcategories));
            }
            primary.Add(new Net7ManufacturingPrimaryCategory(
                GetString(byPath, $"{pbase}.Name", string.Empty), categories));
        }

        IReadOnlyList<Net7ManufacturingFormula> known = ReadFormulaList(byPath, $"{root}.KnownFormula", 500);
        IReadOnlyList<Net7ManufacturingFormula> previous = ReadFormulaList(byPath, $"{root}.PreviousAttempts", 15);
        int materializedComponentCount = components.Count(item => item.IsMaterialized);

        int validityRaw = GetInt32(byPath, $"{root}.Validity", 0);
        int difficultyRaw = GetInt32(byPath, $"{root}.Difficulty", 6);
        // Retail keeps the raw ints: 005B2480 compares Validity with 13 and
        // IsManufacturingAttemptResult tests its range; the text helpers
        // 00846090/00846160 render "error" outside 0..0x12 / 0..6. Out-of-range
        // values therefore compare unequal here; they are not rejected.

        return new Net7ManufacturingLabProjection(
            obj.GameId,
            GetString(byPath, $"{root}.Name", "Manufacturing Lab"),
            GetInt32(byPath, $"{root}.Mode", 1),
            byPath[$"{root}.Mode"].State != 0,
            (Net7ManufacturingValidity)validityRaw,
            GetString(byPath, $"{root}.FailureMessage", string.Empty),
            (Net7ManufacturingDifficulty)difficultyRaw,
            targets,
            components,
            overrides,
            GetUInt64(byPath, $"{root}.NegotiatedCost", 0),
            GetUInt64(byPath, $"{root}.BaseCost", 0),
            primary,
            known,
            previous,
            GetInt32(byPath, $"{root}.CurrentItemCategory", -1),
            GetFloat(byPath, $"{root}.SuccessProbability", 1.0f),
            GetFloat(byPath, $"{root}.CriticalSuccessProbability", 0.0f),
            GetFloat(byPath, $"{root}.ExpectedQuality", 1.0f),
            GetFloat(byPath, $"{root}.MinimumQuality", 1.0f),
            GetFloat(byPath, $"{root}.MaximumQuality", 1.0f),
            GetInt32(byPath, $"{root}.AdditionalIterations", 0),
            GetInt32(byPath, $"{root}.TechLevelFilterBitfield", 0x1FF),
            materializedComponentCount)
            { ComponentsUpdated = byPath[$"{root}.ManufactureComponents"].State > 1,
                CreationOrdinal=obj.CreationOrdinal,
                TargetInventoryRevision=byPath[$"{root}.ManufactureTarget"].UpdateRevision,
                ComponentInventoryRevision=byPath[$"{root}.ManufactureComponents"].UpdateRevision,
                TechLevelFilterRevision=byPath[$"{root}.TechLevelFilterBitfield"].UpdateRevision,
                KnownFormulaState=byPath[$"{root}.KnownFormula"].State,
                KnownFormulaRevision=byPath[$"{root}.KnownFormula"].UpdateRevision,
                PreviousAttemptsState=byPath[$"{root}.PreviousAttempts"].State,
                PreviousAttemptsRevision=byPath[$"{root}.PreviousAttempts"].UpdateRevision,
                ValidityRevision=byPath[$"{root}.Validity"].UpdateRevision,
                ValidityState=byPath[$"{root}.Validity"].State,
                SuccessProbabilityState=byPath[$"{root}.SuccessProbability"].State,
                CriticalSuccessProbabilityState=byPath[$"{root}.CriticalSuccessProbability"].State,
                SuccessProbabilityRevision=byPath[$"{root}.SuccessProbability"].UpdateRevision,
                CriticalSuccessProbabilityRevision=byPath[$"{root}.CriticalSuccessProbability"].UpdateRevision,
                MinimumQualityState=byPath[$"{root}.MinimumQuality"].State,
                MaximumQualityState=byPath[$"{root}.MaximumQuality"].State,
                ExpectedQualityState=byPath[$"{root}.ExpectedQuality"].State,
                MinimumQualityRevision=byPath[$"{root}.MinimumQuality"].UpdateRevision,
                MaximumQualityRevision=byPath[$"{root}.MaximumQuality"].UpdateRevision,
                ExpectedQualityRevision=byPath[$"{root}.ExpectedQuality"].UpdateRevision };
    }

    private static IReadOnlyList<Net7ManufacturingFormula> ReadFormulaList(
        IReadOnlyDictionary<string, Net7AuxDataProperty> byPath,
        string basePath,
        int count)
    {
        List<Net7ManufacturingFormula> values = [];
        for (int i = 0; i < count; i++)
        {
            string entry = $"{basePath}.{i}";
            Net7AuxDataProperty idProperty = byPath[$"{entry}.ItemID"];
            Net7AuxDataProperty nameProperty = byPath[$"{entry}.ItemName"];
            Net7AuxDataProperty levelProperty = byPath[$"{entry}.TechLevel"];
            if (idProperty.State == 0 && nameProperty.State == 0 && levelProperty.State == 0)
                continue;
            // 006FB760 initializes untouched entries to empty/-1/0. Keep
            // intervening slots: 005B3260/005EDA10 stop at the first empty
            // name, while item membership consumers still inspect the array.
            while (values.Count < i) values.Add(new Net7ManufacturingFormula(string.Empty, -1, 0));
            values.Add(new Net7ManufacturingFormula(
                GetString(byPath, $"{entry}.ItemName", string.Empty),
                GetInt32(byPath, $"{entry}.ItemID", -1),
                GetInt32(byPath, $"{entry}.TechLevel", 0)));
        }
        return values;
    }

    private static Net7ManufacturingInventoryItem ReadItem(
        IReadOnlyDictionary<string, Net7AuxDataProperty> byPath,
        string basePath)
    {
        bool materialized = byPath.Any(pair => pair.Key.StartsWith(basePath + ".", StringComparison.Ordinal) && pair.Value.State != 0);
        return new Net7ManufacturingInventoryItem(
            GetInt32(byPath, $"{basePath}.ItemTemplateID", -1),
            GetInt32(byPath, $"{basePath}.StackCount", 0),
            GetUInt64(byPath, $"{basePath}.Price", ulong.MaxValue),
            GetFloat(byPath, $"{basePath}.AveCost", 0),
            GetFloat(byPath, $"{basePath}.Structure", -1),
            GetFloat(byPath, $"{basePath}.Quality", -1),
            GetString(byPath, $"{basePath}.InstanceInfo", string.Empty),
            GetString(byPath, $"{basePath}.InstanceActivatedEffectInfo", string.Empty),
            GetString(byPath, $"{basePath}.InstanceEquipEffectInfo", string.Empty),
            GetString(byPath, $"{basePath}.BuilderName", string.Empty),
            materialized);
    }

    private static int GetInt32(IReadOnlyDictionary<string, Net7AuxDataProperty> byPath, string path, int fallback)
        => byPath[path].Value is int value ? value : fallback;

    private static ulong GetUInt64(IReadOnlyDictionary<string, Net7AuxDataProperty> byPath, string path, ulong fallback)
        => byPath[path].Value is ulong value ? value : fallback;

    private static float GetFloat(IReadOnlyDictionary<string, Net7AuxDataProperty> byPath, string path, float fallback)
        => byPath[path].Value is float value ? value : fallback;

    private static byte GetByte(IReadOnlyDictionary<string, Net7AuxDataProperty> byPath, string path, byte fallback)
        => byPath[path].Value is byte value ? value : fallback;

    private static string GetString(IReadOnlyDictionary<string, Net7AuxDataProperty> byPath, string path, string fallback)
        => byPath[path].Value is Net7AuxDataString value ? value.Text : fallback;
}
