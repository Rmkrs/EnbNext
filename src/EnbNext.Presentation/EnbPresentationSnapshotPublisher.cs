using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;

namespace EnbNext.Presentation;

/// <summary>
/// Builds complete immutable presentation snapshots while the caller owns the
/// Net7 presentation-mutation boundary and publishes them through one atomic
/// reference exchange. Readers never lock
/// and can only observe a previously completed snapshot or the newly completed
/// snapshot, never a partially-built scene.
/// </summary>
public sealed class EnbPresentationSnapshotPublisher
{
    private Net7IncapacitationDetails? _recoveryDetails;
    private EnbSceneAvatarSnapshot? _recoveryAvatar;
    private EnbNext.Net7.Protocol.Net7AvatarData? _talkPortraitSource;
    private EnbSceneAvatarSnapshot? _talkPortrait;
    private EnbSceneAvatarSnapshot? TalkPortrait(Net7WorldState world)
    {
        var id = world.LocalPlayerAuxData.Properties.FirstOrDefault(p => p.Path == "LocalPlayerAuxData.PIPAvatarID")?.Value;
        var source = id is int avatarId && world.AvatarDescriptions.TryGetValue(unchecked((uint)avatarId), out var avatar) ? avatar : null;
        if (!ReferenceEquals(source, _talkPortraitSource))
        {
            _talkPortraitSource = source;
            _talkPortrait = EnbPresentationSnapshotBuilder.BuildAvatar(source);
        }
        return _talkPortrait;
    }
    private EnbSceneAvatarSnapshot? RecoveryAvatar(Net7IncapacitationDetails? details)
    {
        if (ReferenceEquals(details,_recoveryDetails)) return _recoveryAvatar;
        _recoveryDetails=details;
        _recoveryAvatar=details is null ? null : EnbPresentationSnapshotBuilder.BuildAvatar(
            EnbNext.Net7.Protocol.Net7AvatarRosterCodec.DecodeAvatarData(details.AvatarData,"Recovery avatar").Avatar);
        return _recoveryAvatar;
    }
    private readonly EnbPresentationSnapshotBuilder _builder;
    private EnbSceneSnapshot? _latest;
    private readonly EnbPresentationFrameSampler _frameSampler=new();
    private readonly object _frameSamplingGate=new();
    /// <summary>Caller holds the session world boundary. The returned frame carries one consumer lease.</summary>
    public EnbSceneFrame? AcquireLiveFrame(Net7WorldState world,uint time,int? presentationMode)
    {
        lock(_frameSamplingGate)
        {
            var snapshot=Latest;
            return snapshot is null?null:_frameSampler.Acquire(world,snapshot,time,presentationMode,RenderScene);
        }
    }
    private EnbNext.Net7.Presentation.Net7TalkTreeUpdate? _sourceTalkTree, _publishedTalkTree;

    public EnbPresentationSnapshotPublisher(EnbPresentationSnapshotBuilder? builder = null)
    {
        _builder = builder ?? new EnbPresentationSnapshotBuilder();
    }

    // Caller owns the session mutation boundary; this never publishes a frame.
    public void CaptureDeferredEffects(Net7WorldState world, IReadOnlyDictionary<uint, Net7EffectState> effects,
        EnbRetailAssetBundle? assets, uint clientTime, Net7ItemTemplateCatalog? itemTemplates = null)
        => _builder.CaptureEffectSources(world, effects, assets, clientTime, itemTemplates);

    public EnbRenderSceneFeed RenderScene { get; } = new();
    public EnbSceneSnapshot? Latest => Volatile.Read(ref _latest);

    public EnbSceneSnapshot Publish(
        Net7WorldState world,
        EnbRetailAssetBundle? assets,
        uint clientTime,
        uint? fixedSessionId = null,
        int? presentationMode = null)
        => Publish(world, effects: null, assets, clientTime, fixedSessionId, presentationMode);

    public EnbSceneSnapshot Publish(
        Net7WorldState world,
        IReadOnlyDictionary<uint, Net7EffectState>? effects,
        EnbRetailAssetBundle? assets,
        uint clientTime,
        uint? fixedSessionId = null,
        int? presentationMode = null,
        Net7GameplayState? gameplay = null,
        Net7ItemTemplateCatalog? itemTemplates = null,
        uint? activeStarbaseId = null)
    {
        // A new snapshot is not a new dialogue page. Preserve page identity so
        // presentation consumers do not restart speech on every publication.
        if(!ReferenceEquals(_sourceTalkTree,gameplay?.TalkTree))
        {
            _sourceTalkTree=gameplay?.TalkTree;
            _publishedTalkTree=_sourceTalkTree is {} page ? page with {Responses=System.Collections.Immutable.ImmutableArray.CreateRange(page.Responses)} : null;
        }
        EnbSceneSnapshot snapshot = _builder.Build(
            world,
            effects,
            assets,
            clientTime,
            fixedSessionId,
            presentationMode,
            gameplay,
            itemTemplates) with
            {
                ActiveStarbaseId = activeStarbaseId,
                StationTerminal = gameplay is null ? null : EnbStationTerminalSnapshot.Build(gameplay.ManufacturingDomain, itemTemplates),
                AvatarCustomization = gameplay?.AvatarRecustomizationStart,
                ShipCustomization = gameplay?.ShipRecustomization,
                StationJobs = gameplay is null ? [] : System.Collections.Immutable.ImmutableArray.CreateRange(gameplay.JobList),
                StationJobsWithdrawn = gameplay is null || gameplay.WithdrawnJobIds.Count == 0 ? [] : System.Collections.Immutable.ImmutableArray.CreateRange(gameplay.WithdrawnJobIds),
                StationJobDescription = gameplay?.JobDescription,
                StationJobReply = gameplay?.LastJobAcceptReply,
                StationJobRequestPending = gameplay?.StationJobRequestPending == true,
                DamageEvents = gameplay?.DamageEvents,
                ClientSoundStreamId = gameplay?.ClientSoundStreamId ?? Guid.Empty,
                ClientSoundEvents = gameplay?.ClientSoundEvents ?? [],
                DockingSoundRevision = gameplay?.DockingSoundRevision ?? 0,
                IncapacitationDetails = gameplay?.IncapacitationDetails,
                RecoveryAvatar = RecoveryAvatar(gameplay?.IncapacitationDetails),
                StationConversation = gameplay is null ? null : new(gameplay.TalkTreeRevision,
                    _publishedTalkTree,
                    gameplay.TalkTreeAction, gameplay.TalkTreeTradeAvailable) { TradeOpenRevision = gameplay.TalkTreeTradeOpenRevision, TextRevision = gameplay.TalkTreeTextRevision, EndRevision = gameplay.TalkTreeEndRevision, DoneRevision = gameplay.TalkTreeDoneRevision, DoneCaption = gameplay.TalkTreeDoneCaption, SkillAvailable = gameplay.TalkTreeSkillAvailable,
                        Portrait = TalkPortrait(world) },
                StationDefinition = gameplay?.LoungeDefinition is { } station
                    ? new(station.StarbaseDefinitionValue, System.Collections.Immutable.ImmutableArray.CreateRange(
                        station.Rooms.Select(room => new EnbSceneStationRoom(room.RoomType, room.Flags,
                            System.Collections.Immutable.ImmutableArray.CreateRange(room.Parameters)))))
                    {
                        Npcs = System.Collections.Immutable.ImmutableArray.CreateRange(station.Npcs.Select(n =>
                            new EnbSceneStationNpc(n.RoomType,n.Slot,n.Field2,n.RoleCode,n.Variant,n.Name,EnbPresentationSnapshotBuilder.BuildAvatar(n.Avatar)!))),
                        Facilities = System.Collections.Immutable.ImmutableArray.CreateRange(station.Facilities.Select(f =>
                            new EnbSceneStationFacility(f.RoomType,f.Slot,f.FacilityType,f.FourthField)))
                    }
                    : null
            };
        snapshot = snapshot with { RenderSceneSequence = RenderScene.Publish(world,assets,itemTemplates,source=>_builder.BuildRenderDefinition(world,source,assets,clientTime,itemTemplates)), SourceMembershipRevision=world.Objects.MembershipRevision };
        lock(_frameSamplingGate)
            snapshot=_frameSampler.BindPublicationSlots(world,snapshot,RenderScene);
        Volatile.Write(ref _latest, snapshot);
        return snapshot;
    }

    public void Clear()
    {
        RenderScene.Clear();
        lock(_frameSamplingGate)
        {
            _frameSampler.Clear();
            Volatile.Write(ref _latest, null);
        }
    }
}
