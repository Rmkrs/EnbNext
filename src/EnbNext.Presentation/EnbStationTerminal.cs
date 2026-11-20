using System.Collections.Immutable;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Presentation;

public sealed record EnbStationTerminalOwner(uint Character, uint StarbaseId, ulong HandoffVersion, uint Room, uint Slot, uint Type);
public enum EnbTerminalCommand { Open, Close, Primary, Category, Subcategory, Formula, Action, Tick, FilterOn, FilterOff, Browse, Move, JobDescription, JobAccept, Customize, Recent }
public sealed record EnbStationTerminalRequest(EnbStationTerminalOwner Owner, EnbTerminalCommand Command, uint Value = 0)
{
    public EnbInventoryMoveRequest? Move { get; init; }
    public EnbNext.Net7.Protocol.Net7AvatarData? Avatar { get; init; }
    public EnbNext.Net7.Presentation.Net7ShipCustomization? Ship { get; init; }
}
public sealed record EnbStationTerminalSnapshot(Net7ManufacturingMode Mode, Net7ManufacturingLabProjection? Lab,
    int Depth, int Primary, int Category, uint Filter, bool CanAct, bool CanRefineStack,
    ImmutableArray<EnbSceneItemSnapshot> Items)
{
    public int Subcategory { get; init; } = -1;
    public Net7ManufacturingAttemptResult? Result { get; init; }
    public Net7ManufacturingActionContext ActionContext { get; init; }
    public uint ActionCountdownMilliseconds { get; init; }
    public bool AnalysisTransfersInhibited { get; init; }
    public bool ComponentPanelExpanded { get; init; }
    public ulong ComponentPanelRevision { get; init; }
    public ulong SelectionReadyRevision { get; init; }
    // SendManufactureSetItemId 005B39B0 sets +BA4 = 1 until 005B1230 sees the
    // updated components.
    public bool SelectionPending { get; init; }
    // GetManufacturingTargetItemName 005B5CD0: ManufactureTarget[0]'s template
    // name, "Unknown Item" (00B81CC4) without a target, id or template.
    public string TargetItemName { get; init; } = UnknownItem;
    public const string UnknownItem = "Unknown Item";
    public static Net7ManufacturingMode ModeFor(uint facility) => facility switch
    {
        0 => Net7ManufacturingMode.Refining, 1 => Net7ManufacturingMode.Analyze,
        2 => Net7ManufacturingMode.Manufacture, _ => Net7ManufacturingMode.None
    };
    public static EnbStationTerminalSnapshot Build(Net7ManufacturingDomain domain, Net7ItemTemplateCatalog? templates)
    {
        var lab = domain.CurrentLab;
        var items = ImmutableArray.CreateBuilder<EnbSceneItemSnapshot>();
        void Add(byte inventory, IReadOnlyList<Net7ManufacturingInventoryItem> collection)
        {
            for (int slot = 0; slot < collection.Count; slot++)
            {
                var i = collection[slot];
                // 0068B860 and 0068CBA0 select by template, independently of quantity.
                if (!i.IsMaterialized || i.ItemTemplateId < 0) continue;
                Net7ItemTemplate? t = null;
                templates?.TryGet((uint)i.ItemTemplateId, out t);
                var (attributes,effects)=EnbSceneInventorySnapshotBuilder.ProjectItemInformation(t,i.InstanceInfo,i.InstanceActivatedEffectInfo,i.InstanceEquipEffectInfo);
                items.Add(new(inventory, slot, i.ItemTemplateId, t?.Name.EffectiveText ?? "Null Item",
                    t?.Category ?? uint.MaxValue, t?.Subcategory ?? uint.MaxValue, t?.ItemType ?? uint.MaxValue, t?.TechLevel ?? 0, (int?)t?.IconBaseAsset ?? -1,
                    i.StackCount, i.Structure, i.Quality, null, t?.StringFieldB.EffectiveText ?? "", i.BuilderName, attributes, effects, t?.Flags ?? 0)
                    {Price=i.Price,MaxStack=t?.MaxStack??1,AverageCost=i.AverageCost});
            }
        }
        if (lab is not null)
        {
            Add(12, lab.ManufactureTargets); Add(13, lab.ManufactureComponents); Add(14, lab.ManufactureOverride);
        }
        // 005ECEF0 (refinery) and 005B2480 enable the action only with no
        // pending selection or attempt (+AF0 / +BA4 == 0) and Validity 13.
        bool ready=lab?.Validity==Net7ManufacturingValidity.Valid && domain.ActionContext==Net7ManufacturingActionContext.Idle &&
            !domain.RefinerySelectionPending && !domain.ManufactureSelectionPending;
        return new(domain.RequestedMode, lab, domain.BrowserDepth, domain.SelectedPrimaryIndex, domain.SelectedCategoryIndex,
            domain.LocalTechLevelFilterBitfield, ready && domain.CanSendManualAction(2),
            // 005ECEF0 raw 005ED013..10A: Stack also needs unsigned AdditionalIterations > 0 (INV-D71).
            ready && domain.CanSendManualAction(3) && unchecked((uint)lab!.AdditionalIterations) > 0, items.ToImmutable())
            {Subcategory=domain.SelectedSubcategoryIndex, Result=domain.LastResult, ActionContext=domain.ActionContext,
                ActionCountdownMilliseconds=domain.ActionCountdownMilliseconds, AnalysisTransfersInhibited=domain.AnalysisTransfersInhibited, ComponentPanelExpanded=domain.ComponentPanelExpanded,
                ComponentPanelRevision=domain.ComponentPanelRevision, SelectionReadyRevision=domain.SelectionReadyRevision, SelectionPending=domain.ManufactureSelectionPending,
                TargetItemName=lab?.ManufactureTargets is [var target, ..] && lab.ManufactureTargets.Any(i => i.IsMaterialized) && target.ItemTemplateId != -1 &&
                    templates?.TryGet((uint)target.ItemTemplateId, out var targetTemplate) == true ? targetTemplate!.Name.EffectiveText : UnknownItem};
    }
}
