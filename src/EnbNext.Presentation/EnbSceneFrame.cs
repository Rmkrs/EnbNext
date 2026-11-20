using System.Collections.Immutable;
namespace EnbNext.Presentation;

/// <summary>Reusable physical scene storage sampled under the network lock.
/// Metadata is adopted on publication; sampled owners are mutated in place.
/// A lease must protect this frame until all queued consumers release it.</summary>
public sealed partial class EnbSceneFrame : IEnbSceneFrame
{
    public ulong Version {get;set;} = default!;
    public uint ClientTime {get;set;} = default!;
    public uint? LocalPlayerId {get;set;} = default!;
    public uint? LocalShipObjectId {get;set;} = default!;
    public uint? ClientAvatarId {get;set;} = default!;
    public bool IsTransitioning {get;set;} = default!;
    public EnbSceneEnvironmentSnapshot? Environment {get;set;} = default!;
    public EnbSceneCameraStateSnapshot Camera {get;set;} = default!;
    public ImmutableArray<EnbSceneEffectSnapshot> Effects {get;set;} = default!;
    public EnbHudFrame Hud {get;} = new();
    IEnbHudFrame IEnbSceneFrame.Hud=>Hud;
    public EnbSceneLightingSnapshot Lighting {get;set;} = default!;
    public IReadOnlyList<IEnbRenderObject> Objects {get;set;} = Array.Empty<IEnbRenderObject>();
    public ImmutableArray<EnbSceneStationAvatarSnapshot> StationAvatars {get;set;} = default!;
    public EnbSceneZBandSnapshot? ZBand {get;set;} = default!;
    public EnbSceneBoundingBoxSnapshot? BoundingBox {get;set;} = default!;
    public ImmutableArray<byte> SplineData {get;set;} = default!;
    public ImmutableArray<EnbSceneSplineUpdateSnapshot> SplineUpdates {get;set;} = default!;
    public ImmutableDictionary<uint,string> AvatarFirstNames {get;set;} = default!;
    public ImmutableDictionary<uint,EnbSceneAvatarSnapshot>? AvatarDescriptions {get;set;} = default!;
    public ImmutableDictionary<uint,ulong> AvatarCacheLifetimes {get;set;} = default!;
    public Guid StationAvatarStreamId {get;set;} = default!;
    public ImmutableList<EnbNext.Net7.Presentation.Net7StationAvatarEvent> StationAvatarEvents {get;set;} = default!;
    public ImmutableArray<EnbNext.Net7.Gameplay.Net7ShipEmoteEvent> ShipEmotes {get;set;} = default!;
    public long ServerTimeCorrectionMilliseconds {get;set;} = default!;
    public ulong MeshBufferResetRevision {get;set;} = default!;
    public ulong RenderSceneSequence {get;set;} = default!;
    public ulong SourceMembershipRevision {get;set;} = default!;
    public EnbRenderPoseSet RenderPoses {get;set;} = default!;
    public ulong WorldRegistryRevision {get;set;} = default!;
    public EnbSceneLocationTransition? LocationTransition {get;set;} = default!;
    public EnbSceneMusicContext? Music {get;set;} = default!;
    public uint? ActiveStarbaseId {get;set;} = default!;
    public EnbStationConversation? StationConversation {get;set;} = default!;
    public EnbNext.Net7.Gameplay.Net7IncapacitationDetails? IncapacitationDetails {get;set;} = default!;
    public EnbSceneAvatarSnapshot? RecoveryAvatar {get;set;} = default!;
    public EnbStationTerminalSnapshot? StationTerminal {get;set;} = default!;
    public EnbNext.Net7.Presentation.Net7AvatarRecustomizeStart? AvatarCustomization {get;set;} = default!;
    public EnbNext.Net7.Presentation.Net7ShipRecustomizeStart? ShipCustomization {get;set;} = default!;
    public System.Collections.Immutable.ImmutableArray<EnbNext.Net7.Presentation.Net7JobListEntry> StationJobs {get;set;} = default!;
    public System.Collections.Immutable.ImmutableArray<uint> StationJobsWithdrawn {get;set;} = default!;
    public EnbNext.Net7.Presentation.Net7JobDescriptionUpdate? StationJobDescription {get;set;} = default!;
    public EnbNext.Net7.Presentation.Net7JobAcceptReplyUpdate? StationJobReply {get;set;} = default!;
    public bool StationJobRequestPending {get;set;} = default!;
    public EnbNext.Net7.Gameplay.Net7DamageStream? DamageEvents {get;set;}
    public Guid ClientSoundStreamId {get;set;} = default!;
    public ImmutableList<EnbNext.Net7.Presentation.Net7ClientSoundEvent> ClientSoundEvents {get;set;} = default!;
    public ulong DockingSoundRevision {get;set;} = default!;
    public EnbSceneStationDefinition? StationDefinition {get;set;} = default!;
    public uint? LocalAvatarObjectId => ClientAvatarId ?? LocalPlayerId;
    public int GetOrbitalTimeMilliseconds(uint renderClientTime) =>
        (int)Math.Clamp((long)renderClientTime+ServerTimeCorrectionMilliseconds,int.MinValue,int.MaxValue);
    public void AdoptMetadata(IEnbSceneFrame source)
    {
        if(IsRetained)throw new InvalidOperationException("Cannot mutate a queued scene context");
        RenderPoses=source.RenderPoses;
        Objects=source.Objects;
        Version=source.Version;
        ClientTime=source.ClientTime;
        LocalPlayerId=source.LocalPlayerId;
        LocalShipObjectId=source.LocalShipObjectId;
        ClientAvatarId=source.ClientAvatarId;
        IsTransitioning=source.IsTransitioning;
        Environment=source.Environment;
        Camera=source.Camera;
        Effects=source.Effects;
        Hud.Adopt(source.Hud);
        Lighting=source.Lighting;
        StationAvatars=source.StationAvatars;
        ZBand=source.ZBand;
        BoundingBox=source.BoundingBox;
        SplineData=source.SplineData;
        SplineUpdates=source.SplineUpdates;
        AvatarFirstNames=source.AvatarFirstNames;
        AvatarDescriptions=source.AvatarDescriptions;
        AvatarCacheLifetimes=source.AvatarCacheLifetimes;
        StationAvatarStreamId=source.StationAvatarStreamId;
        StationAvatarEvents=source.StationAvatarEvents;
        ShipEmotes=source.ShipEmotes;
        ServerTimeCorrectionMilliseconds=source.ServerTimeCorrectionMilliseconds;
        MeshBufferResetRevision=source.MeshBufferResetRevision;
        RenderSceneSequence=source.RenderSceneSequence;
        SourceMembershipRevision=source.SourceMembershipRevision;
        WorldRegistryRevision=source.WorldRegistryRevision;
        LocationTransition=source.LocationTransition;
        Music=source.Music;
        ActiveStarbaseId=source.ActiveStarbaseId;
        StationConversation=source.StationConversation;
        IncapacitationDetails=source.IncapacitationDetails;
        RecoveryAvatar=source.RecoveryAvatar;
        StationTerminal=source.StationTerminal;
        AvatarCustomization=source.AvatarCustomization;
        ShipCustomization=source.ShipCustomization;
        StationJobs=source.StationJobs;
        StationJobsWithdrawn=source.StationJobsWithdrawn;
        StationJobDescription=source.StationJobDescription;
        StationJobReply=source.StationJobReply;
        StationJobRequestPending=source.StationJobRequestPending;
        DamageEvents=source.DamageEvents;
        ClientSoundStreamId=source.ClientSoundStreamId;
        ClientSoundEvents=source.ClientSoundEvents;
        DockingSoundRevision=source.DockingSoundRevision;
        StationDefinition=source.StationDefinition;
    }
}
