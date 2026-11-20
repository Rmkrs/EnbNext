using System.Collections.Immutable;
namespace EnbNext.Presentation;

/// <summary>Current scene inputs. Consumers must not assume snapshot identity or historical immutability.</summary>
public interface IEnbSceneFrame
{
    ulong Version {get;}
    uint ClientTime {get;}
    uint? LocalPlayerId {get;}
    uint? LocalShipObjectId {get;}
    uint? ClientAvatarId {get;}
    bool IsTransitioning {get;}
    EnbSceneEnvironmentSnapshot? Environment {get;}
    EnbSceneCameraStateSnapshot Camera {get;}
    ImmutableArray<EnbSceneEffectSnapshot> Effects {get;}
    IEnbHudFrame Hud {get;}
    EnbSceneLightingSnapshot Lighting {get;}
    IReadOnlyList<IEnbRenderObject> Objects {get;}
    ImmutableArray<EnbSceneStationAvatarSnapshot> StationAvatars {get;}
    EnbSceneZBandSnapshot? ZBand {get;}
    EnbSceneBoundingBoxSnapshot? BoundingBox {get;}
    ImmutableArray<byte> SplineData {get;}
    ImmutableArray<EnbSceneSplineUpdateSnapshot> SplineUpdates {get;}
    ImmutableDictionary<uint,string> AvatarFirstNames {get;}
    ImmutableDictionary<uint,EnbSceneAvatarSnapshot>? AvatarDescriptions {get;}
    ImmutableDictionary<uint,ulong> AvatarCacheLifetimes {get;}
    Guid StationAvatarStreamId {get;}
    ImmutableList<EnbNext.Net7.Presentation.Net7StationAvatarEvent> StationAvatarEvents {get;}
    ImmutableArray<EnbNext.Net7.Gameplay.Net7ShipEmoteEvent> ShipEmotes {get;}
    long ServerTimeCorrectionMilliseconds {get;}
    ulong MeshBufferResetRevision {get;}
    ulong RenderSceneSequence {get;}
    ulong SourceMembershipRevision {get;}
    EnbRenderPoseSet RenderPoses {get;}
    ulong WorldRegistryRevision {get;}
    EnbSceneLocationTransition? LocationTransition {get;}
    EnbSceneMusicContext? Music {get;}
    uint? ActiveStarbaseId {get;}
    EnbStationConversation? StationConversation {get;}
    EnbNext.Net7.Gameplay.Net7IncapacitationDetails? IncapacitationDetails {get;}
    EnbSceneAvatarSnapshot? RecoveryAvatar {get;}
    EnbStationTerminalSnapshot? StationTerminal {get;}
    EnbNext.Net7.Presentation.Net7AvatarRecustomizeStart? AvatarCustomization {get;}
    EnbNext.Net7.Presentation.Net7ShipRecustomizeStart? ShipCustomization {get;}
    System.Collections.Immutable.ImmutableArray<EnbNext.Net7.Presentation.Net7JobListEntry> StationJobs {get;}
    System.Collections.Immutable.ImmutableArray<uint> StationJobsWithdrawn {get;}
    EnbNext.Net7.Presentation.Net7JobDescriptionUpdate? StationJobDescription {get;}
    EnbNext.Net7.Presentation.Net7JobAcceptReplyUpdate? StationJobReply {get;}
    bool StationJobRequestPending {get;}
    EnbNext.Net7.Gameplay.Net7DamageStream? DamageEvents {get;}
    Guid ClientSoundStreamId {get;}
    ImmutableList<EnbNext.Net7.Presentation.Net7ClientSoundEvent> ClientSoundEvents {get;}
    ulong DockingSoundRevision {get;}
    EnbSceneStationDefinition? StationDefinition {get;}
    uint? LocalAvatarObjectId {get;}
    int GetOrbitalTimeMilliseconds(uint renderClientTime);
}
