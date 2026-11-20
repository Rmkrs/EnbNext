using System.Collections.Immutable;
namespace EnbNext.Presentation;

public interface IEnbHudFrame
{
    uint? CurrentTargetId {get;}
    int? ScanRange {get;}
    bool LocalShipLockSpeedActive {get;}
    bool ClientSendUiTriggersEnabled {get;}
    IEnbCockpitFrame? Cockpit {get;}
    int ReputationState {get;}
    ulong ReputationRevision {get;}
    EnbSceneExperienceSnapshot Experience {get;}
    EnbSceneInventorySnapshot? Inventory {get;}
    EnbSceneChatSnapshot? Chat {get;}
    EnbNext.Net7.Gameplay.Net7WarpRouteSnapshot? WarpRoute {get;}
    EnbSceneShortcutSnapshot? Shortcuts {get;}
    ImmutableArray<EnbSceneBuffSnapshot> Buffs {get;}
    /// <summary>The selected target's buffs, when the server sends them in that object's ShipAuxData BuffArray.</summary>
    ImmutableArray<EnbSceneBuffSnapshot> TargetBuffs {get;}
    ImmutableArray<uint> GroupMemberIds {get;}
    EnbSceneGroupSnapshot? Group {get;}
    EnbSceneGuildSnapshot? Guild {get;}
    uint? LocalSelectedTargetId {get;}
    bool HasLocalTargetSelection {get;}
    ulong TargetSelectionSequence {get;}
    int? ProspectSkillLevel {get;}
    ImmutableArray<uint> CurrentTargetVerbs {get;}
    string? TargetThreat {get;}
    string? TargetThreatSound {get;}
    int? TargetThreatLevel {get;}
}

public sealed class EnbHudFrame : IEnbHudFrame
{
    public uint? CurrentTargetId {get;set;} = default!;
    public int? ScanRange {get;set;} = default!;
    public bool LocalShipLockSpeedActive {get;set;} = default!;
    public bool ClientSendUiTriggersEnabled {get;set;} = default!;
    public IEnbCockpitFrame? Cockpit {get;set;} = default!;
    public int ReputationState {get;set;} = default!;
    public ulong ReputationRevision {get;set;} = default!;
    public EnbSceneExperienceSnapshot Experience {get;set;} = default!;
    public EnbSceneInventorySnapshot? Inventory {get;set;} = default!;
    public EnbSceneChatSnapshot? Chat {get;set;} = default!;
    public EnbNext.Net7.Gameplay.Net7WarpRouteSnapshot? WarpRoute {get;set;} = default!;
    public EnbSceneShortcutSnapshot? Shortcuts {get;set;} = default!;
    public ImmutableArray<EnbSceneBuffSnapshot> Buffs {get;set;} = default!;
    public ImmutableArray<EnbSceneBuffSnapshot> TargetBuffs {get;set;} = default!;
    public ImmutableArray<uint> GroupMemberIds {get;set;} = default!;
    public EnbSceneGroupSnapshot? Group {get;set;} = default!;
    public EnbSceneGuildSnapshot? Guild {get;set;} = default!;
    public uint? LocalSelectedTargetId {get;set;} = default!;
    public bool HasLocalTargetSelection {get;set;} = default!;
    public ulong TargetSelectionSequence {get;set;} = default!;
    public int? ProspectSkillLevel {get;set;} = default!;
    public ImmutableArray<uint> CurrentTargetVerbs {get;set;} = default!;
    public string? TargetThreat {get;set;} = default!;
    public string? TargetThreatSound {get;set;} = default!;
    public int? TargetThreatLevel {get;set;} = default!;
    internal void Adopt(IEnbHudFrame source)
    {
        CurrentTargetId=source.CurrentTargetId;
        ScanRange=source.ScanRange;
        LocalShipLockSpeedActive=source.LocalShipLockSpeedActive;
        ClientSendUiTriggersEnabled=source.ClientSendUiTriggersEnabled;
        Cockpit=source.Cockpit;
        ReputationState=source.ReputationState;
        ReputationRevision=source.ReputationRevision;
        Experience=source.Experience;
        Inventory=source.Inventory;
        Chat=source.Chat;
        WarpRoute=source.WarpRoute;
        Shortcuts=source.Shortcuts;
        Buffs=source.Buffs;
        TargetBuffs=source.TargetBuffs;
        GroupMemberIds=source.GroupMemberIds;
        Group=source.Group;
        Guild=source.Guild;
        LocalSelectedTargetId=source.LocalSelectedTargetId;
        HasLocalTargetSelection=source.HasLocalTargetSelection;
        TargetSelectionSequence=source.TargetSelectionSequence;
        ProspectSkillLevel=source.ProspectSkillLevel;
        CurrentTargetVerbs=source.CurrentTargetVerbs;
        TargetThreat=source.TargetThreat;
        TargetThreatSound=source.TargetThreatSound;
        TargetThreatLevel=source.TargetThreatLevel;
    }
}

public interface IEnbCockpitFrame
{
    float? EnergyPercent {get;}
    float? MaximumEnergyPower {get;}
    IEnbVitalsFrame? Vitals {get;}
    float? MaximumSpeed {get;}
    bool IsAutoFollowing {get;}
    EnbNext.Net7.Gameplay.Net7WarpStatus? Warp {get;}
    EnbSceneCockpitWarpCommand? WarpCommand {get;}
}

public sealed class EnbCockpitFrame : IEnbCockpitFrame
{
    public float? EnergyPercent {get;set;} = default!;
    public float? MaximumEnergyPower {get;set;} = default!;
    public IEnbVitalsFrame? Vitals {get;set;} = default!;
    public float? MaximumSpeed {get;set;} = default!;
    public bool IsAutoFollowing {get;set;} = default!;
    public EnbNext.Net7.Gameplay.Net7WarpStatus? Warp {get;set;} = default!;
    public EnbSceneCockpitWarpCommand? WarpCommand {get;set;} = default!;
}

public interface IEnbVitalsFrame
{
    float? ShieldPercent {get;}
    float? MaximumShieldPower {get;}
    float? HullPoints {get;}
    float? MaximumHullPoints {get;}
}

public sealed class EnbVitalsFrame : IEnbVitalsFrame
{
    public float? ShieldPercent {get;set;} = default!;
    public float? MaximumShieldPower {get;set;} = default!;
    public float? HullPoints {get;set;} = default!;
    public float? MaximumHullPoints {get;set;} = default!;
}

