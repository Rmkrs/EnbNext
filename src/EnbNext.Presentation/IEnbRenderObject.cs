using System.Collections.Immutable;
using System.Numerics;
namespace EnbNext.Presentation;

/// <summary>Live render-owner input. Values may change between ordered update
/// and draw invocations; consumers must not apply record equality, with copies,
/// or assume a historical snapshot. Protocol snapshots implement this only for
/// explicit diagnostic/import callers while production binds persistent owners.</summary>
public interface IEnbRenderObject
{
    ulong RenderIdentity {get;}
    ulong VisualGeneration {get;}
    uint GameId {get;}
    bool IsLocalShip {get;}
    bool IsCurrentTarget {get;}
    byte ObjectType {get;}
    int BaseAsset {get;}
    float Scale {get;}
    float HsvH {get;}
    float HsvS {get;}
    float HsvV {get;}
    uint RelationshipRaw {get;}
    bool IsAttacking {get;}
    string? Name {get;}
    EnbSceneRadarSnapshot Radar {get;}
    IEnbRenderSpatial? Spatial {get;}
    EnbPresentationBaseAssetReference? BaseAssetReference {get;}
    EnbPresentationClientBaseAssetReference? ClientBaseAssetReference {get;}
    ImmutableArray<EnbSceneDecalSnapshot> Decals {get;}
    ImmutableArray<EnbSceneColorizationSnapshot> ColorizationEntries {get;}
    ImmutableArray<EnbSceneRenderStateLinkSnapshot> RenderStateLinks {get;}
    ImmutableArray<uint> ActiveRenderStates {get;}
    ImmutableArray<EnbSceneRenderStateActionSnapshot> RenderStateActions {get;}
    ImmutableArray<EnbSceneAttachmentSnapshot> Attachments {get;}
    EnbSceneNameDecalSnapshot? NameDecal {get;}
    ImmutableArray<EnbSceneSubPartSnapshot> SubParts {get;}
    EnbSceneAvatarSnapshot? Avatar {get;}
    uint SpecialTargetEndpointId {get;}
    float SpecialTargetTransition {get;}
    float? HarvestablePercentFull {get;}
    ulong? WorldCreationOrdinal {get;}
    ulong SpeakerRevision {get;}
    byte SpeakerType {get;}
    EnbNext.Assets.EnbRgb? SpeakerColor {get;}
    EnbNext.Assets.EnbRgb? SpeakingColor {get;}
    EnbSceneCreatedBaseAsset? CreatedBaseAsset {get;}
    int? HarvestableTechLevel {get;}
    EnbSceneShipLegoSnapshot? ShipLegos {get;}
    EnbSceneDisplayNameProperties? DisplayNameProperties {get;}
    ImmutableArray<EnbNext.Net7.World.Net7LegoControllerMutation> LegoControllerMutations {get;}
    ulong FixedShipVisualRevision {get;}
    ulong BaseAssetMaterializationRevision {get;}
    ulong? BaseVisualGeneration {get;}
    EnbSceneShipPresentationSnapshot? ShipPresentation {get;}
    ImmutableArray<EnbSceneDamageDecalPoolSnapshot> DamageDecalPools {get;}
    float? ShipVisualBankRadians {get;}
    int RenderPoseSlot {get;}
    EnbSceneShipEmotePlayback? ShipEmote {get;}
    EnbScenePlayerPresentationSnapshot? PlayerPresentation {get;}
    bool IsRescueBeaconActive {get;}
    ulong RelationshipNotificationRevision {get;}
    ulong PvpUpdateRevision {get;}
    bool PvpUpdated {get;}
    EnbSceneTargetVitalsSnapshot? TargetVitals {get;}
    EnbSceneInterruptionSnapshot? Interruption {get;}
    int ScannerFactionState {get;}
    int ScannerTechLevelState {get;}
    ulong ScannerTechLevelRevision {get;}
    int HarvestableResourcesState {get;}
    ImmutableArray<int> HarvestableResourceSlotStates {get;}
    ImmutableArray<EnbSceneHarvestableResourceSnapshot> HarvestableResources {get;}
    EnbSceneAuxDataMaterializationAuditSnapshot? ShipAuxDataAudit {get;}
    EnbSceneColorizationApplicationAuditSnapshot? ColorizationAudit {get;}
    int NameDecalApplyCount {get;}
    int? NameDecalColorizationApplyCount {get;}
    int DecalApplyCount {get;}
    ulong RenderObjectGeneration {get;}
}

public interface IEnbRenderSpatial
{
    EnbSceneSpatialProviderKind ProviderKind {get;}
    IEnbRenderTransform Transform {get;}
    EnbScenePlanetSnapshot? Planet {get;}
    EnbSceneAdvancedSpatialSnapshot? Advanced {get;}
    uint? FormationLeaderId {get;}
    string ModelAudit {get;}
    uint? PacketTime {get;}
    uint? ReceiptTime {get;}
    uint TransitionMilliseconds {get;}
}
public interface IEnbRenderTransform
{
    uint ClientTime {get;}
    Vector3 Position {get;}
    Quaternion Orientation {get;}
    Vector3 Velocity {get;}
    float Speed {get;}
    Vector3 Rotation {get;}
    Vector3 OrientationColumn0 {get;}
    Matrix4x4? ProviderTransform {get;}
    float BankHeadingTarget {get;}
}

/// <summary>Ordered object destruction, independent of visibility or queued-resource release.</summary>
public interface IEnbRenderObjectLifetime
{
    bool IsRetired {get;}
    event Action Retired;
}
