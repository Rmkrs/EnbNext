using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Social;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Gameplay;

/// <summary>
/// Native gameplay-domain state. Persistent authoritative gameplay data
/// remains in World/AuxData; this layer owns transient effect/chat/trade/
/// manufacturing/presentation state and local-service products.
/// </summary>
public sealed class Net7GameplayState
{
    private readonly Dictionary<uint, Net7EffectState> _effects = [];
    private ulong _nextEffectInstanceGeneration;
    private readonly Guid _effectPoseContext = Guid.NewGuid();
    private ImmutableDictionary<Net7EffectPoseKey, EnbNext.Net7.Gameplay.Net7RetainedEffectPosition> _retainedEffectPositions = ImmutableDictionary<Net7EffectPoseKey, EnbNext.Net7.Gameplay.Net7RetainedEffectPosition>.Empty;
    public void PublishRetainedEffectPositions(ImmutableDictionary<Net7EffectPoseKey, EnbNext.Net7.Gameplay.Net7RetainedEffectPosition> positions)
        => Volatile.Write(ref _retainedEffectPositions, positions);
    private readonly List<(byte Message, uint GameId)> _cameraEvents = [];
    private readonly List<Net7CameraEvent> _nativeCameraEvents = [];
    private ulong _nativeCameraSequence;
    public IReadOnlyList<Net7CameraEvent> NativeCameraEvents => _nativeCameraEvents;
    public ulong? TakeoffRevision { get; private set; }
    public void BeginTakeoffCamera()
    { QueueNativeCameraEvent(17); TakeoffRevision = _nativeCameraSequence; }
    public void ClearTakeoffCamera() => TakeoffRevision = null;
    public void QueueNativeCameraEvent(int value, uint target = 0)
        => _nativeCameraEvents.Add(new(++_nativeCameraSequence, value, target));
    private readonly List<Net7ClientSoundUpdate> _clientSounds = [];
    private readonly List<Net7ClientSoundCommandUpdate> _soundCommands = [];
    private readonly List<Net7MessageLineUpdate> _messageLines = [];
    private readonly List<Net7GadgetControlUpdate> _gadgetControls = [];
    private readonly Dictionary<uint, IReadOnlyList<uint>> _verbLists = [];
    private readonly Dictionary<uint, Net7ClickIndicatorUpdate> _clickIndicators = [];
    private readonly Dictionary<(uint Type, uint Id), Net7ConfirmedActionPending> _confirmedActions = [];
    private readonly List<uint> _confirmedMessageIds = [];
    private readonly List<uint> _confirmedMessageTimeouts = [];
    public Net7ConfirmedActionPending? IncomingMessage { get; private set; }
    private readonly List<uint> _confirmedDialogIds = [];
    private readonly List<uint> _confirmedDialogTimeouts = [];
    /// <summary>
    /// The type-0 offer dialog (CONFIRMED_ACTION_DIALOG) while it is open: 0074E710 queues every type-0 id and opens the
    /// dialog with the newest caption; 0074F5C0 answers the oldest queued id, and 0074FA30 closes the open dialog when
    /// that oldest id has timed out. Id is the id an answer goes to; null while the dialog is closed.
    /// </summary>
    public Net7ConfirmedActionPending? ConfirmedDialog { get; private set; }
    private readonly Dictionary<uint, uint> _emotes = [];
    private long _shipEmoteSequence;
    private readonly List<Net7ShipEmoteEvent> _shipEmoteEvents = [];
    public IReadOnlyList<Net7ShipEmoteEvent> ShipEmoteEvents => _shipEmoteEvents;
    private readonly Net7PresentationCatalogs _presentation;
    private readonly Func<uint, Net7WorldObject?> _resolveWorldObject;
    private readonly bool _canObserveEffectEndpoints;
    private readonly Func<uint, uint> _convertServerTimestamp;
    private readonly Func<uint, Vector3?>? _resolveEffectSourcePosition;
    private readonly Func<bool> _isAvatarViewActive;
    private readonly Func<bool> _isGroupLeader;
    private readonly Func<uint> _clientTime;

    public Net7GameplayState(
        Net7PresentationCatalogs? presentation = null,
        Func<uint, string?>? resolveObjectName = null,
        Func<uint?>? localPlayerId = null,
        Func<uint, Net7WorldObject?>? resolveWorldObject = null,
        Func<uint, uint>? convertServerTimestamp = null,
        Func<uint, Vector3?>? resolveEffectSourcePosition = null,
        Func<string, uint?>? resolveCaller = null,
        Func<bool>? isAvatarViewActive = null,
        Func<bool>? isGroupLeader = null,Func<uint,ulong?>? resolveAvatarCache=null, Func<uint>? clientTime = null)
    {
        _presentation = presentation ?? new Net7PresentationCatalogs();
        _resolveWorldObject = resolveWorldObject ?? (_ => null);
        _canObserveEffectEndpoints = resolveWorldObject is not null;
        _convertServerTimestamp = convertServerTimestamp ?? (timestamp => timestamp);
        _resolveEffectSourcePosition = resolveEffectSourcePosition;
        _isAvatarViewActive = isAvatarViewActive ?? (()=>false);
        _isGroupLeader = isGroupLeader ?? (()=>false);
        _clientTime = clientTime ?? (() => 0);
        Social = new Net7SocialState(resolveObjectName, localPlayerId,
            id => _resolveWorldObject(id) is {} obj && (resolveAvatarCache is not null || obj.AvatarDescription is not null) ? obj.CreationOrdinal : null,
            resolveCaller, resolveWorldObject is null ? resolveObjectName : id => _resolveWorldObject(id)?.ChatIdentity,resolveAvatarCache);
        ManufacturingDomain = new Net7ManufacturingDomain(resolveWorldObject);
        TradeDomain = new Net7TradeDomain();
    }

    public IReadOnlyDictionary<uint, Net7EffectState> Effects => _effects;
    public IReadOnlyList<Net7MessageState> Messages => Social.Messages;
    public Net7SocialState Social { get; }
    public IReadOnlyList<(byte Message, uint GameId)> CameraEvents => _cameraEvents;
    public IReadOnlyList<Net7ClientSoundUpdate> ClientSounds => _clientSounds;
    public Guid ClientSoundStreamId {get;private set;} = Guid.NewGuid();
    public ImmutableList<Net7ClientSoundEvent> ClientSoundEvents {get;private set;} = [];
    public ulong DockingSoundRevision {get;private set;}
    public void ObserveDockingCameraSound() => DockingSoundRevision++;
    public IReadOnlyList<Net7ClientSoundCommandUpdate> SoundCommands => _soundCommands;
    public IReadOnlyList<Net7MessageLineUpdate> MessageLines => _messageLines;
    public Net7SuperLineUpdate? SuperLine { get; private set; }
    public Net7MessageTimeUpdate? MessageTime { get; private set; }
    public Net7StarbaseSetUpdate? LastStarbaseSet { get; private set; }
    public Net7StarbaseTowUpdate? StarbaseTow { get; private set; }
    public Net7LoungeDefinitionUpdate? LoungeDefinition { get; private set; }
    public Net7TalkTreeUpdate? TalkTree { get; private set; }
    public Net7TalkTreeActionUpdate? TalkTreeAction { get; private set; }
    public ulong TalkTreeRevision { get; private set; }
    public ulong TalkTreeTextRevision { get; private set; }
    public ulong TalkTreeEndRevision { get; private set; }
    public ulong TalkTreeDoneRevision { get; private set; }
    public bool TalkTreeDoneCaption { get; private set; }
    public bool TalkTreeTradeAvailable { get; private set; }
    public bool TalkTreeSkillAvailable { get; private set; }
    public bool TalkTreeVendorAuthorized { get; private set; }
    public ulong TalkTreeTradeOpenRevision { get; private set; }
    public void BeginStationConversation()
    {
        TalkTree = null;
        TalkTreeTextRevision = 0;
        TalkTreeEndRevision = 0;
        TalkTreeDoneRevision = 0;
        TalkTreeDoneCaption = false;
        TalkTreeAction = null;
        TalkTreeTradeAvailable = false;
        TalkTreeSkillAvailable = false;
        TalkTreeVendorAuthorized = false;
        TalkTreeTradeOpenRevision = 0;
        TalkTreeRevision++;
    }
    public IReadOnlyDictionary<uint, IReadOnlyList<uint>> VerbLists => _verbLists;
    public Net7VerbUpdate? LastVerbUpdate { get; private set; }
    public Net7DialogUpdate? Dialog { get; private set; }
    public Net7OpenInterfaceUpdate? OpenInterface { get; private set; }
    public IReadOnlyList<Net7GadgetControlUpdate> GadgetControls => _gadgetControls;
    public IReadOnlyDictionary<uint, Net7ClickIndicatorUpdate> ClickIndicators => _clickIndicators;
    public Net7StarbaseRoomUpdate? StarbaseRoomUpdate { get; private set; }
    public IReadOnlyDictionary<(uint Type, uint Id), Net7ConfirmedActionPending> ConfirmedActions => _confirmedActions;
    public Net7TradeState? Trade { get; private set; }
    public Net7TradeDomain TradeDomain { get; }
    public Net7ManufacturingDomain ManufacturingDomain { get; }
    public Net7ManufacturingState Manufacturing => new(ManufacturingDomain.ActiveObjectId, ManufacturingDomain.RequestedMode);
    public uint? SectorType { get; private set; }
    // Retail SClient construction starts in presentation mode 2 before
    // the first server SetClientType establishes a sector-derived mode.
    public int? PresentationMode { get; private set; } = 2;
    public int PresentationRefreshCountdown { get; private set; }
    public bool PresentationNotificationPending { get; private set; }
    // Retail 00514010 -> 007511A0 and server handoff 00745750 both
    // clear mesh containers through 0097B2D0. Repeated valid modes count too.
    public ulong MeshBufferResetRevision { get; private set; }
    public Net7CDataRecord? LastCDataRecord { get; private set; }
    public Net7ProspectExtension? LastProspect { get; private set; }
    public Net7TractorExtension? LastTractor { get; private set; }
    public uint? LastProspectActivationTimestamp { get; private set; }
    public bool TractorVariantAValidityApplied { get; private set; }
    public Net7GuildPrompt? GuildPrompt { get; private set; }
    public Net7GuildMessage? GuildMessage { get; private set; }
    public Net7GuildPermissions? GuildPermissions { get; private set; }
    public IReadOnlyList<Net7GuildRankName> GuildRankNames { get; private set; } = Array.Empty<Net7GuildRankName>();
    public Action<Net7DamagePresentation>? DamageObserved {get;set;}
    public Net7DamagePresentation? LastDamagePresentation { get; private set; }
    public Net7DamageStream DamageEvents { get; private set; } = new();
    public Net7IncapacitationDetails? IncapacitationDetails { get; private set; }
    /// <summary>
    /// Semantic equivalent of retail SClient+0x1130.
    /// This is local UI/input selection state, deliberately separate from the
    /// server-authoritative ShipAuxData.TargetGameID exposed by World.CurrentTargetId.
    /// </summary>
    public uint? LocalSelectedTargetId { get; private set; }
    public bool HasLocalTargetSelection { get; private set; }
    private ulong _navigationSequence;
    public ulong TargetSelectionSequence { get; private set; }
    public uint? CurrentHulkId { get; private set; }
    public uint? CurrentHarvestableId { get; private set; }
    public Net7PresentationCatalogs PresentationCatalogs => _presentation;
    public Net7ShipRecustomizeStart? ShipRecustomization { get; private set; }
    public Net7AvatarRecustomizeStart? AvatarRecustomizationStart { get; private set; }
    public Net7AvatarRecustomizeUpdate? AvatarRecustomizationUpdate { get; private set; }
    public void ClearRecustomization()
    {
        ShipRecustomization = null;
        AvatarRecustomizationStart = null;
    }
    public void BeginStationJobs()
    {
        EndStationJobRequest();
        JobList = Array.Empty<Net7JobListEntry>();
        _withdrawnJobIds.Clear();
        JobDescription = null;
        LastJobAcceptReply = null;
    }
    public void BeginStationJobDescription()
    {
        JobDescription = null;
        LastJobAcceptReply = null;
    }
    private (uint Id, bool Accept)? _stationJobRequest;
    public bool StationJobRequestPending => _stationJobRequest is not null;
    // Retail 005A2730/005A36F0 enter pending states; 005A25B0 gates input.
    public void BeginStationJobRequest(uint id, bool accept) => _stationJobRequest = (id, accept);
    public void EndStationJobRequest() => _stationJobRequest = null;
    public IReadOnlyList<Net7JobListEntry> JobList { get; private set; } = Array.Empty<Net7JobListEntry>();
    public Net7JobDescriptionUpdate? JobDescription { get; private set; }
    public uint? LastDeletedJobId { get; private set; }
    /// <summary>The job ids the server deleted (0095) since the job list opened and has not listed again. A copy of a
    /// job the server listed twice stays in <see cref="JobList"/> (005A4430 unlinks only the first); Next's Modern
    /// Jobs window leaves those out.</summary>
    public IReadOnlyList<uint> WithdrawnJobIds => _withdrawnJobIds;
    private readonly List<uint> _withdrawnJobIds = [];
    public Net7JobAcceptReplyUpdate? LastJobAcceptReply { get; private set; }
    public Net7NoteListUpdate? NoteList { get; private set; }
    public Net7NoteContentUpdate? NoteContent { get; private set; }
    public Net7ReferralPointsUpdate? ReferralPoints { get; private set; }
    public Net7GmInventoryEditorUpdate? GmInventoryEditor { get; private set; }
    public Net7GmMissionLogUpdate? GmMissionLog { get; private set; }
    public Net7ModeRequestUpdate? LastModeRequest { get; private set; }
    public Net7ModeTransitionKind? LastModeTransition { get; private set; }
    public short? GlobalReferrerResult { get; private set; }
    public bool GlobalReferrerReadinessCleared { get; private set; }
    public uint? WarpIndex { get; private set; }
    public Net7WarpRouteSnapshot? WarpRoute { get; private set; }
    public IReadOnlyDictionary<uint, uint> Emotes => _emotes;

    public bool Apply(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        switch (packet.Header.Opcode)
        {
            case 0x0009:
                ApplyObjectEffect(packet.Payload.Span);
                return true;
            case 0x000A:
                ApplyPointEffect(Net7EffectsRenderWire.DecodePointEffect(packet.Payload.Span));
                return true;
            case 0x000B:
                ApplyObjectToObjectEffect(packet.Payload.Span);
                return true;
            case 0x000C:
                ApplyObjectToEffectEffect(Net7EffectsRenderWire.DecodeObjectToEffectEffect(packet.Payload.Span));
                return true;
            case 0x000D:
                ApplyObjectToPointEffect(Net7EffectsRenderWire.DecodeObjectToPointEffect(packet.Payload.Span));
                return true;
            case 0x000E:
                ApplyObjectToObjectDurationLinkedEffect(Net7EffectsRenderWire.DecodeObjectToObjectDurationLinkedEffect(packet.Payload.Span));
                return true;
            case 0x000F:
                ApplyRemoveEffect(packet.Payload.Span);
                return true;
            case 0x001D:
                Social.ApplyBaseMessage(Net7GameplayWire.DecodeMessageString(packet.Payload.Span));
                return true;
            case 0x001E:
                Social.ApplyGroupAction(Net7SocialWire.DecodeGroupAction(packet.Payload.Span));
                return true;
            case 0x0020:
            case 0x0021:
            case 0x0022:
                var messageLine = Net7UiPresentationWire.DecodeMessageLine(packet.Payload.Span, packet.Header.Opcode);
                _messageLines.Add(messageLine);
                Social.ApplyScreenLine(messageLine);
                return true;
            case 0x0023:
                SuperLine = Net7UiPresentationWire.DecodeSuperLine(packet.Payload.Span);
                Social.ApplyNamedScreenLine(SuperLine.Text);
                return true;
            case 0x0024:
                Net7UiPresentationWire.DecodeClearMessageLine(packet.Payload.Span);
                _messageLines.Clear();
                Social.ClearScreenLines();
                return true;
            case 0x001F:
                Trade = Net7GameplayWire.DecodeTradeState(packet.Payload.Span);
                _ = TradeDomain.ApplyCoordinator(Trade);
                return true;
            case 0x003C:
                ApplySectorType(packet.Payload.Span);
                return true;
            case 0x004C:
                ApplyModeRequest(Net7ResidualInboundWire.DecodeModeRequest(packet.Payload.Span));
                return true;
            case 0x004F:
                LastStarbaseSet = Net7UiPresentationWire.DecodeStarbaseSet(packet.Payload.Span);
                return true;
            case 0x0050:
                StarbaseTow = Net7UiPresentationWire.DecodeStarbaseTow(packet.Payload.Span);
                return true;
            case 0x0052:
                LoungeDefinition = Net7UiPresentationWire.DecodeLoungeNpc(packet.Payload.Span);
                return true;
            case 0x0053:
                Social.ApplyFindMember(Net7SocialWire.DecodeFindMember(packet.Payload.Span));
                return true;
            case 0x0054:
                // 0064C800 resets More when a closed talk interface reopens.
                if (TalkTreeAction is {} ended && ended.EndsConversation) TalkTreeDoneCaption = false;
                TalkTree = Net7UiPresentationWire.DecodeTalkTree(packet.Payload.Span);
                TalkTreeRevision++;
                TalkTreeTextRevision = TalkTreeRevision;
                return true;
            case 0x0056:
                TalkTreeAction = Net7UiPresentationWire.DecodeTalkTreeAction(packet.Payload.Span);
                // 0064D230 -> 0064EDC0: both commands change More to Done.
                if (TalkTreeAction.Action is 6 or 7)
                { TalkTreeDoneCaption = true; TalkTreeDoneRevision = TalkTreeRevision + 1; }
                // Preserve the open command when later actions/text are published in the same frame.
                if (TalkTreeAction.Action == 5) TalkTreeTradeOpenRevision = TalkTreeRevision + 1;
                // Current retail 0064D230: actions 3/4 show/hide the Trade control.
                if (TalkTreeAction.Action is 1 or 2) TalkTreeSkillAvailable = TalkTreeAction.Action == 1;
                if (TalkTreeAction.Action is 3 or 4) TalkTreeTradeAvailable = TalkTreeAction.Action == 3;
                // 0064E430 opens the vendor without changing the Trade control.
                // Hiding that control does not close an already-open vendor.
                if (TalkTreeAction.Action is 3 or 5) TalkTreeVendorAuthorized = true;
                if (TalkTreeAction.EndsConversation)
                {
                    // 0064D230 retains +158 through subsequent ordinary actions;
                    // 0064D770 consumes it on update, not on packet receipt.
                    TalkTreeEndRevision = TalkTreeRevision + 1;
                    // 0064C8D0 hides Skill and Trade when the owner closes.
                    TalkTreeSkillAvailable = false;
                    TalkTreeTradeAvailable = false;
                    TalkTreeVendorAuthorized = false;
                }
                TalkTreeRevision++;
                return true;
            case 0x0059:
                // Retail 007396C0 consumes this notification without reading fields.
                return true;
            case 0x005B:
                ApplyVerbList(Net7UiPresentationWire.DecodeVerbList(packet.Payload.Span));
                return true;
            case 0x005C:
                ApplyVerbUpdate(Net7UiPresentationWire.DecodeVerbUpdate(packet.Payload.Span));
                return true;
            case 0x005F:
                Social.ApplyMessageChatstream(Net7SocialWire.DecodeMessageChatstream(packet.Payload.Span));
                return true;
            case 0x0060:
                MessageTime = Net7UiPresentationWire.DecodeMessageTime(packet.Payload.Span);
                return true;
            case 0x0062:
                Dialog = Net7UiPresentationWire.DecodeDialog(packet.Payload.Span);
                return true;
            case 0x0064:
                LastDamagePresentation = Net7GameplayWire.DecodeClientDamage(packet.Payload.Span);
                DamageEvents.Append(LastDamagePresentation, _clientTime());
                DamageObserved?.Invoke(LastDamagePresentation);
                return true;
            case 0x0066:
                OpenInterface = Net7UiPresentationWire.DecodeOpenInterface(packet.Payload.Span);
                // Retail 007397D0 -> 005820C0: type zero controls the
                // harvest window; change zero opens on the current target.
                if (OpenInterface.UiType == 0)
                    CurrentHarvestableId = OpenInterface.UiChange == 0 && LocalSelectedTargetId is uint harvestId &&
                        _resolveWorldObject(harvestId)?.ObjectType == 0x26 ? harvestId : null;
                return true;
            case 0x0067:
                _gadgetControls.Add(Net7UiPresentationWire.DecodeGadgetControl(packet.Payload.Span));
                return true;
            case 0x0069:
                Net7ClickIndicatorUpdate click = Net7UiPresentationWire.DecodeClickIndicator(packet.Payload.Span);
                if (_resolveWorldObject(click.GameId) is not null)
                    _clickIndicators[click.GameId] = click;
                return true;
            case 0x006A:
                var sound=Net7EffectsRenderWire.DecodeClientSound(packet.Payload.Span);
                _clientSounds.Add(sound);
                ClientSoundEvents=ClientSoundEvents.Add(new(sound.SoundName,sound.Channel,sound.Queue,null));
                return true;
            case 0x006B:
                var command=Net7EffectsRenderWire.DecodeClientSoundCommand(packet.Payload.Span);
                // 007384A0: an unknown command succeeds without touching sound state.
                if (command.Command > 2) return true;
                _soundCommands.Add(command);
                ClientSoundEvents=ClientSoundEvents.Add(new(null,command.Channel,0,command.Command));
                return true;
            case 0x0078:
                ApplyGlobalSetReferrerResponse(Net7ResidualInboundWire.DecodeGlobalSetReferrerResponse(packet.Payload.Span));
                return true;
            case 0x007F:
                ManufacturingDomain.SetActiveObject(Net7GameplayWire.DecodeActiveManufacturingObject(packet.Payload.Span));
                return true;
            case 0x0081:
                ShipRecustomization = Net7PresentationWire.DecodeShipRecustomizeStart(packet.Payload.Span);
                return true;
            case 0x0083:
                AvatarRecustomizationStart = Net7PresentationWire.DecodeAvatarRecustomizeStart(packet.Payload.Span);
                return true;
            case 0x0085:
                AvatarRecustomizationUpdate = Net7PresentationWire.DecodeAvatarRecustomizeUpdate(packet.Payload.Span);
                return true;
            case 0x0090:
                IncapacitationDetails = Net7GameplayWire.DecodeIncapacitationDetails(packet.Payload.Span);
                AddCameraEvent(0x19, 0);
                return true;
            case 0x0091:
                Social.AddHistory(new Net7MessageState(5, Net7GameplayWire.DecodeRescueConfirmation(packet.Payload.Span)));
                return true;
            case 0x0093:
                // 005A3BC0 appends each received batch to the open terminal's list.
                var batch = Net7JobsAdminWire.DecodeJobList(packet.Payload.Span);
                JobList = JobList.Concat(batch).ToArray();
                foreach (var job in batch) _withdrawnJobIds.Remove(job.Field0);
                return true;
            case 0x0094:
                JobDescription = Net7JobsAdminWire.DecodeJobDescription(packet.Payload.Span);
                if (_stationJobRequest == (JobDescription.JobId, false)) EndStationJobRequest();
                return true;
            case 0x0095:
                ApplyJobDelete(Net7JobsAdminWire.DecodeJobDelete(packet.Payload.Span));
                return true;
            case 0x0096:
                LastJobAcceptReply = Net7JobsAdminWire.DecodeJobAcceptReply(packet.Payload.Span);
                if (_stationJobRequest == (LastJobAcceptReply.JobId, true)) EndStationJobRequest();
                // Current retail 005A3FB0 removes success/unavailable jobs.
                if (LastJobAcceptReply.AcceptStatus is 0 or 1)
                    ApplyJobDelete(LastJobAcceptReply.JobId);
                return true;
            case 0x009C:
                WarpIndex = Net7ResidualInboundWire.DecodeWarpIndex(packet.Payload.Span);
                // Retail clears the route for ANY out-of-range unsigned index,
                // including count and uint.MaxValue. Keep the raw receipt above.
                if (WarpRoute is { } route)
                    WarpRoute = WarpIndex.Value < (uint)route.ObjectIds.Length
                        ? route with { ReportedIndex = WarpIndex.Value } : null;
                return true;
            case 0x00A0:
                StarbaseRoomUpdate = Net7UiPresentationWire.DecodeStarbaseRoomUpdate(packet.Payload.Span);
                return true;
            case 0x00A2:
                Net7NotifyEmoteUpdate emote = Net7ResidualInboundWire.DecodeNotifyEmote(packet.Payload.Span);
                if (_resolveWorldObject(emote.GameId) is { } emoteOwner)
                {
                    _emotes[emote.GameId] = emote.Emote;
                    _shipEmoteEvents.Add(new(++_shipEmoteSequence, emote.GameId, emoteOwner.CreationOrdinal, emote.Emote));
                    if (_shipEmoteEvents.Count > 256) _shipEmoteEvents.RemoveAt(0);
                }
                return true;
            case 0x00A4:
                Social.ApplyClientChatList(Net7SocialWire.DecodeClientChatList(packet.Payload.Span));
                return true;
            case 0x00A5:
                Social.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(packet.Payload.Span));
                return true;
            case 0x00A6:
                Social.ApplyClientChatError(Net7SocialWire.DecodeClientChatError(packet.Payload.Span));
                return true;
            case 0x00BD:
                Social.ApplyCtaResponse(Net7SocialWire.DecodeCtaResponse(packet.Payload.Span),_isAvatarViewActive(),_isGroupLeader());
                return true;
            case 0x00BE:
                ApplyConfirmedActionOffer(Net7UiPresentationWire.DecodeConfirmedActionOffer(packet.Payload.Span));
                return true;
            case 0x00B7:
                NoteList = Net7JobsAdminWire.DecodeNoteList(packet.Payload.Span);
                return true;
            case 0x00B8:
                NoteContent = Net7JobsAdminWire.DecodeNoteContent(packet.Payload.Span);
                return true;
            case 0x00BF:
                ApplyConfirmedActionTimeout(Net7UiPresentationWire.DecodeConfirmedActionTimeout(packet.Payload.Span));
                return true;
            case 0x00C8:
                GuildPrompt = Net7GameplayWire.DecodeGuildRecruitConfirm(packet.Payload.Span);
                return true;
            case 0x00CC:
                var prompt = Net7GameplayWire.DecodeGuildSimplePrompt(packet.Payload.Span);
                // 0073DD00 leaves the current prompt alone for unhandled types.
                if (prompt.Type is 0 or 1 or 3 or 5 or 7 or 9 or 11 or 13 or 16 or 18 or 20)
                    GuildPrompt = prompt;
                return true;
            case 0x00D0:
                GuildMessage = Net7GameplayWire.DecodeGuildMessage(packet.Payload.Span);
                Social.ApplyGuildMessage(GuildMessage);
                return true;
            case 0x00D2:
                GuildPermissions = Net7GameplayWire.DecodeGuildPermissions(packet.Payload.Span);
                return true;
            case 0x00D3:
                GuildRankNames = Net7GameplayWire.DecodeGuildRankNames(packet.Payload.Span);
                return true;
            case 0x00D8:
                GuildPrompt = Net7GameplayWire.DecodeGuildGmRenameConfirm(packet.Payload.Span);
                return true;
            case 0x00E9:
                ReferralPoints = Net7JobsAdminWire.DecodeReferralPoints(packet.Payload.Span);
                return true;
            case 0x00EA:
                GmInventoryEditor = Net7JobsAdminWire.DecodeGmEditPlayerInventory(packet.Payload.Span);
                return true;
            case 0x00EB:
                GmMissionLog = Net7JobsAdminWire.DecodeGmViewMissionLog(packet.Payload.Span);
                return true;
            default:
                return false;
        }
    }

    public void ClearGuildPrompt() => GuildPrompt = null;

    public void SetLocalSelectedTarget(uint targetId)
    {
        if(!HasLocalTargetSelection || LocalSelectedTargetId!=(targetId==0?(uint?)null:targetId))
            TargetSelectionSequence=++_navigationSequence;
        LocalSelectedTargetId = targetId == 0 ? null : targetId;
        HasLocalTargetSelection = true;
    }

    public void OpenHulk(uint gameId) => CurrentHulkId = gameId;
    public void CloseHulk() => CurrentHulkId = null;
    public void CloseHarvestable() => CurrentHarvestableId = null;

    public void SetRequestedManufacturingMode(Net7ManufacturingMode mode)
        => ManufacturingDomain.RecordModeSent(mode);

    public void RecordProspect(Net7ProspectExtension prospect, bool localSource)
    {
        LastProspect = prospect ?? throw new ArgumentNullException(nameof(prospect));
        if (localSource)
            LastProspectActivationTimestamp = prospect.Timestamp;
    }

    public void RecordTractor(Net7TractorExtension tractor, bool localSource)
    {
        LastTractor = tractor ?? throw new ArgumentNullException(nameof(tractor));
        TractorVariantAValidityApplied = localSource && tractor.Opcode == 0x2013;
    }

    public void RecordCData(Net7CDataRecord? record)
        => LastCDataRecord = record;

    public void AddSyntheticEffect(Net7EffectState effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        // Extension-owned synthetic effects are the semantic
        // equivalent of an ordinary successfully constructed effect packet.
        // EffectID zero is therefore still a real registry key.
        Net7EffectState resolved = effect.Presentation is null
            ? effect with { Presentation = _presentation.ResolveEffect(effect.DefinitionId) }
            : effect;
        RegisterEffect(resolved);
    }

    public bool RemoveEffect(uint effectId)
        => _effects.Remove(effectId);

    public void AddMessage(Net7MessageState message)
        => Social.AddHistory(message ?? throw new ArgumentNullException(nameof(message)));

    public void AddCameraEvent(byte message, uint gameId)
    {
        _cameraEvents.Add((message, gameId));
        QueueNativeCameraEvent(message == 3 ? 15 : message, gameId);
        if (message == 3) LootCamera = new(++_lootCameraSequence, gameId);
    }

    private ulong _lootCameraSequence;
    public Net7LootCameraRequest? LootCamera { get; private set; }
    public void ClearLootCamera() => LootCamera = null;

    private ulong _gateCameraSequence;
    public Net7GateCameraTransition? GateCamera { get; private set; }
    public void BeginGateCamera(uint targetId, uint clientTime, bool queueEvent = true)
    {
        GateCamera = new(++_gateCameraSequence, targetId, clientTime);
        if (queueEvent) QueueNativeCameraEvent(6, targetId);
    }
    public void ClearGateCamera() => GateCamera = null;
    public Net7DockCameraTransition? DockCamera { get; private set; }
    public void BeginDockCamera(ulong revision, uint target, uint time, bool queueEvent = true)
    { DockCamera = new(revision,target,time); if (queueEvent) QueueNativeCameraEvent(10, target); }
    private ulong _dockDepartureSequence;
    public void BeginDockDeparture(uint target, uint time, bool queueEvent = true)
    {
        if (DockCamera is { IsDeparture: true } active && active.TargetId == target) return;
        DockCamera = new(++_dockDepartureSequence, target, time, true);
        if (queueEvent) QueueNativeCameraEvent(12, target);
    }
    public void ClearDockCamera() => DockCamera = null;
    public Net7LandCameraTransition? LandCamera {get;private set;}
    private ulong _landArrivalSequence;
    public void BeginLandCamera(ulong revision,uint target,uint time)
    { LandCamera=new(revision,target,time,Net7LandCameraPhase.SpaceApproach); QueueNativeCameraEvent(8,target); }
    public void BeginLandArrival(uint target,uint time,bool queueEvent=true)
    {
        if(LandCamera is {Phase:Net7LandCameraPhase.SurfaceArrival} active && active.TargetId==target)return;
        LandCamera=new(++_landArrivalSequence,target,time,Net7LandCameraPhase.SurfaceArrival);
        if(queueEvent) QueueNativeCameraEvent(13,target);
    }
    public void ClearLandCamera()=>LandCamera=null;

    public bool ConsumePresentationNotification()
    {
        if (!PresentationNotificationPending)
            return false;
        PresentationNotificationPending = false;
        return true;
    }

    public void AdvancePresentationRefresh()
    {
        if (PresentationRefreshCountdown > 0)
            PresentationRefreshCountdown--;
    }

    public void ResetForServerHandoff()
    {
        MeshBufferResetRevision++;
        EndStationJobRequest();
        ClearGateCamera();
        ClearDockCamera();
        ClearLandCamera();
        _effects.Clear();
        _cameraEvents.Clear();
        _nativeCameraEvents.Clear();
        ClearTakeoffCamera();
        LootCamera = null;
        _clientSounds.Clear();
        _soundCommands.Clear();
        // These are client-owned audio commands, not sector objects. Keep the
        // ordered journal across handoff so an acknowledgement immediately before
        // it cannot disappear between presentation frames. Explicit 006B commands
        // still stop voices; character selection starts a new stream below.
        ResetUiPresentationState();
        ResetResidualSectorState();
        Trade = null;
        TradeDomain.Reset();
        ManufacturingDomain.ResetForSectorBoundary();
        LastProspect = null;
        LastTractor = null;
        LastCDataRecord = null;
        LastProspectActivationTimestamp = null;
        TractorVariantAValidityApplied = false;
        GuildPrompt = null;
        LastDamagePresentation = null;
        DamageEvents = new();
        LocalSelectedTargetId = null;
        HasLocalTargetSelection = false;
        CurrentHulkId = null;
        CurrentHarvestableId = null;
        IncapacitationDetails = null;
        ShipRecustomization = null;
        AvatarRecustomizationStart = null;
        AvatarRecustomizationUpdate = null;
        Social.ResetForHandoff();
    }

    public void ResetForCharacterSelection()
    {
        ClearGateCamera();
        ClearDockCamera();
        ClearLandCamera();
        _effects.Clear();
        _cameraEvents.Clear();
        _nativeCameraEvents.Clear();
        ClearTakeoffCamera();
        LootCamera = null;
        _clientSounds.Clear();
        _soundCommands.Clear();
        ClientSoundEvents=[]; ClientSoundStreamId=Guid.NewGuid(); DockingSoundRevision=0;
        ResetUiPresentationState();
        ResetResidualSectorState();
        GlobalReferrerResult = null;
        GlobalReferrerReadinessCleared = false;
        Trade = null;
        TradeDomain.Reset();
        ManufacturingDomain.ResetForSectorBoundary();
        SectorType = null;
        PresentationMode = null;
        PresentationRefreshCountdown = 0;
        PresentationNotificationPending = false;
        LastCDataRecord = null;
        LastProspect = null;
        LastTractor = null;
        LastProspectActivationTimestamp = null;
        TractorVariantAValidityApplied = false;
        GuildPrompt = null;
        GuildMessage = null;
        GuildPermissions = null;
        GuildRankNames = Array.Empty<Net7GuildRankName>();
        LastDamagePresentation = null;
        DamageEvents = new();
        IncapacitationDetails = null;
        LocalSelectedTargetId = null;
        HasLocalTargetSelection = false;
        CurrentHulkId = null;
        CurrentHarvestableId = null;
        ShipRecustomization = null;
        AvatarRecustomizationStart = null;
        AvatarRecustomizationUpdate = null;
        ResetJobsAdminState();
        Social.ResetForCharacterSelection();
    }

    private void ApplyVerbList(Net7VerbListUpdate update)
    {
        var values = new List<uint>();
        foreach (uint verb in new SortedSet<uint>(update.Verbs))
            SetVerb(values, verb);
        _verbLists[update.GameId] = values.ToArray();
    }

    private void ApplyVerbUpdate(Net7VerbUpdate update)
    {
        var values = _verbLists.TryGetValue(update.GameId, out IReadOnlyList<uint>? current)
            ? new List<uint>(current)
            : new List<uint>();
        // Retail 00659010: the first wire collection disables existing buttons
        // by high-word type; a zero reason becomes the generic 0xFFFF reason.
        foreach (uint verb in new SortedSet<uint>(update.Drop))
        {
            int index = values.FindIndex(value => (value & 0xFFFF0000) == (verb & 0xFFFF0000));
            if (index >= 0) values[index] = (verb & 0xFFFF) == 0 ? verb | 0xFFFFu : verb;
        }
        foreach (uint verb in new SortedSet<uint>(update.Add))
            SetVerb(values, verb);
        _verbLists[update.GameId] = values.ToArray();
        LastVerbUpdate = update;
    }

    private static void SetVerb(List<uint> values, uint verb)
    {
        // 00659240 updates an existing type in place, or appends a new slot.
        int index = values.FindIndex(value => (value & 0xFFFF0000) == (verb & 0xFFFF0000));
        if (index >= 0) values[index] = verb;
        else values.Add(verb);
    }

    private void ApplyConfirmedActionOffer(Net7ConfirmedActionOffer offer)
    {
        // 0074E710 ignores unknown types without adding a dialog or response ID.
        if (offer.Type > 1) return;
        _confirmedActions[(offer.Type, offer.Id)] = new Net7ConfirmedActionPending(offer.Type, offer.Id, offer.Message);
        // 0074E710: caption is replaced, while response IDs remain FIFO.
        if (offer.Type == 0)
        {
            _confirmedDialogIds.Add(offer.Id);
            ConfirmedDialog = new(0, _confirmedDialogIds[0], offer.Message);
            SettleConfirmedDialog();
        }
        if (offer.Type == 1)
        {
            _confirmedMessageIds.Add(offer.Id);
            IncomingMessage = new(1, _confirmedMessageIds[0], offer.Message);
            AdvanceIncomingMessageTimeout();
        }
    }

    private void ApplyJobDelete(uint jobId)
    {
        LastDeletedJobId = jobId;
        if (!_withdrawnJobIds.Contains(jobId)) _withdrawnJobIds.Add(jobId);
        // 005A4430 and 005A3FB0 unlink only the first list entry carrying the id.
        int index = -1;
        for (int i = 0; i < JobList.Count; i++) if (JobList[i].Field0 == jobId) { index = i; break; }
        if (index < 0) return;
        var remaining = JobList.ToList(); remaining.RemoveAt(index);
        JobList = remaining.ToArray();
    }

    private void ApplyConfirmedActionTimeout(Net7ConfirmedActionTimeout timeout)
    {
        _confirmedActions.Remove((timeout.Type, timeout.Id));
        if (timeout.Type == 0)
        {
            _confirmedDialogTimeouts.Add(timeout.Id);
            SettleConfirmedDialog();
        }
        if (timeout.Type == 1)
        {
            _confirmedMessageTimeouts.Add(timeout.Id);
            AdvanceIncomingMessageTimeout();
        }
    }

    // 0074FA30: only an open dialog whose oldest queued id has timed out is closed (its id leaves both lists).
    private void SettleConfirmedDialog()
    {
        if (ConfirmedDialog is null || _confirmedDialogIds.Count == 0 || !_confirmedDialogTimeouts.Remove(_confirmedDialogIds[0])) return;
        _confirmedDialogIds.RemoveAt(0);
        ConfirmedDialog = null;
    }

    private void AdvanceIncomingMessageTimeout()
    {
        // 0074FEC0 retires only visible FIFO heads. Settle pending expirations on
        // the session owner before publication, without a render-thread mutation.
        while (IncomingMessage is {} shown && _confirmedMessageTimeouts.Remove(shown.Id))
        {
            _confirmedMessageIds.RemoveAt(0);
            IncomingMessage = _confirmedMessageIds.Count == 0 ? null : shown with { Id = _confirmedMessageIds[0] };
        }
    }

    public bool TryConsumeConfirmedAction(uint type, uint id, out Net7ConfirmedActionPending? pending)
    {
        if (type == 1 && IncomingMessage is {} message && message.Id == id)
        {
            // 00750800 consumes one ID and hides the button even if more IDs remain.
            _confirmedMessageIds.RemoveAt(0);
            if (!_confirmedMessageIds.Contains(id)) _confirmedActions.Remove((type, id));
            IncomingMessage = null; pending = message; return true;
        }
        if (type == 1) { pending = null; return false; }
        if (type == 0)
        {
            // 0074F5C0: an answer closes the dialog and goes to the oldest queued id.
            if (ConfirmedDialog is { } dialog && _confirmedDialogIds.Count > 0 && _confirmedDialogIds[0] == id)
            {
                _confirmedDialogIds.RemoveAt(0); _confirmedDialogTimeouts.Remove(id); _confirmedActions.Remove((0, id));
                ConfirmedDialog = null; pending = dialog with { Id = id }; return true;
            }
            pending = null; return false;
        }
        if (_confirmedActions.Remove((type, id), out Net7ConfirmedActionPending? existing))
        {
            pending = existing;
            return true;
        }

        pending = null;
        return false;
    }

    private void ApplyModeRequest(Net7ModeRequestUpdate update)
    {
        LastModeRequest = update;
        LastModeTransition = update.Mode switch
        {
            0 => Net7ModeTransitionKind.Mode0,
            1 => Net7ModeTransitionKind.Mode1,
            _ => null,
        };
    }

    private void ApplyGlobalSetReferrerResponse(short result)
    {
        GlobalReferrerResult = result;
        if (result is 1 or 2)
            GlobalReferrerReadinessCleared = true;
    }

    /// <summary>Capture before sending, like retail's radar +0x40 route. This
    /// must not mark the ship as charging or moving; Aux/spatial updates own that.</summary>
    public void CaptureWarpRoute(IReadOnlyList<uint> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        WarpRoute = new(route.ToImmutableArray(), null) { CaptureSequence=++_navigationSequence };
    }

    private void ResetResidualSectorState()
    {
        LastModeRequest = null;
        LastModeTransition = null;
        WarpIndex = null;
        WarpRoute = null;
        _emotes.Clear();
        _shipEmoteEvents.Clear();
    }

    private void ResetUiPresentationState()
    {
        _messageLines.Clear();
        _gadgetControls.Clear();
        _verbLists.Clear();
        _clickIndicators.Clear();
        _confirmedActions.Clear();
        _confirmedMessageIds.Clear(); _confirmedMessageTimeouts.Clear(); IncomingMessage = null;
        _confirmedDialogIds.Clear(); _confirmedDialogTimeouts.Clear(); ConfirmedDialog = null;
        SuperLine = null;
        MessageTime = null;
        LastStarbaseSet = null;
        StarbaseTow = null;
        LoungeDefinition = null;
        BeginStationConversation();
        LastVerbUpdate = null;
        Dialog = null;
        OpenInterface = null;
        StarbaseRoomUpdate = null;
    }

    private void ResetJobsAdminState()
    {
        EndStationJobRequest();
        JobList = Array.Empty<Net7JobListEntry>();
        _withdrawnJobIds.Clear();
        JobDescription = null;
        LastDeletedJobId = null;
        LastJobAcceptReply = null;
        NoteList = null;
        NoteContent = null;
        ReferralPoints = null;
        GmInventoryEditor = null;
        GmMissionLog = null;
    }

    private void ApplySectorType(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new InvalidDataException("0x003C SetClientType requires the canonical four-byte SectorType prefix.");
        uint sectorType = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        int mode = sectorType switch
        {
            0 => 0,
            1 => 1,
            2 => 5,
            // A complete but unsupported SectorType reaches retail's
            // assertion/report path and does not call SClientSetMode. Consume it
            // without mutating the current presentation state; do not turn it into
            // a fatal network/session error in the memory-safe replacement.
            _ => -1,
        };
        if (mode < 0)
            return;

        SectorType = sectorType;
        PresentationMode = mode;
        MeshBufferResetRevision++;
        PresentationRefreshCountdown = 3;
        PresentationNotificationPending = true;
    }

    private void ApplyPointEffect(Net7PointEffectUpdate update)
    {
        RegisterEffect(new Net7EffectState(
            EffectId: update.EffectId, SourceId: 0, TargetId: 0, DefinitionId: update.EffectDefinitionId,
            Timestamp: _convertServerTimestamp(update.Timestamp), DurationMilliseconds: update.Duration, BoneName: string.Empty, Mask: 0, IsObjectToObject: false,
            Scale: update.Scale, Presentation: _presentation.ResolveEffect(update.EffectDefinitionId),
            GeometryKind: Net7EffectGeometryKind.Point, WorldPoint: update.Location, HsvShift: update.HsvShift));
    }

    private void ApplyObjectToEffectEffect(Net7ObjectToEffectEffectUpdate update)
    {
        _effects.TryGetValue(update.TargetGameId, out var referenced);
        // 00735670 resolves an effect, then +8 samples its position once.
        // Point-bound unary RObj/Sound getters copy matrix translation
        // (006EF120/006E9360). Neither has a binary descriptor for midpoint A.
        // Other targets require a retained production pose receipt; never
        // substitute an object lookup or a later reused effect instance.
        Vector3? captured = referenced is { GeometryKind: Net7EffectGeometryKind.Point } &&
            referenced.Presentation?.Definition?.EffectClassKind is
                Net7EffectClassKind.RObjEffect or Net7EffectClassKind.SoundEffect
            ? referenced.WorldPoint : null;
        // Native +8 reads the retained production matrix, not a new
        // simulation at the packet timestamp. Read one immutable publication
        // synchronously here; never defer capture until the next render frame.
        if (captured is null && referenced?.Presentation?.Definition?.EffectClassKind is
                Net7EffectClassKind.RObjEffect or Net7EffectClassKind.SoundEffect or
                Net7EffectClassKind.PassEffect or Net7EffectClassKind.AttachEffect or Net7EffectClassKind.WarpEffect or
                Net7EffectClassKind.RObjLookAtEffect or
                Net7EffectClassKind.LineEffect or Net7EffectClassKind.ParticleJetEffect or
                Net7EffectClassKind.StraightSeekEffect or Net7EffectClassKind.SpiralSeekEffect or
                Net7EffectClassKind.SplineRandomSeekEffect or Net7EffectClassKind.LinearRandomSeekEffect &&
            Volatile.Read(ref _retainedEffectPositions).TryGetValue(
                new(referenced.PoseContext, referenced.EffectId, referenced.InstanceGeneration), out var retained))
        {
            // 007356B9..72B halves each component before adding. Unary
            // descriptors have no endpoint and do not enter this branch.
            var kind=referenced.Presentation.Definition.EffectClassKind;
            bool segment=kind is Net7EffectClassKind.LineEffect or Net7EffectClassKind.ParticleJetEffect;
            Vector3? getterPosition=retained.Position;
            if(segment)
            {
                // 006E8F80: empty chain samples object +24; nonempty appends
                // EMITTER and reads the rendered hierarchy via 006FED80.
                getterPosition=null;
                Vector3? sourcePosition=null;
                if(string.IsNullOrEmpty(update.SourceBoneChain))sourcePosition=_resolveEffectSourcePosition?.Invoke(update.SourceGameId);
                else if(_resolveWorldObject(update.SourceGameId) is {} sourceObject && retained.SourceControllers is {} controllers &&
                    controllers.TryGetValue((update.SourceGameId,sourceObject.CreationOrdinal),out var controller))
                    sourcePosition=controller.ResolveEffectChain(update.SourceBoneChain).Translation;
                if(retained.BinaryEndpoint is {} segmentEnd && sourcePosition is {} sourcePoint)
                {
                    var delta=segmentEnd-retained.Position;float lengthSquared=delta.LengthSquared();
                    float projection=lengthSquared!=0?Vector3.Dot(sourcePoint-retained.Position,delta)/lengthSquared:0;
                    // 008DE5F4 also takes the start endpoint on an unordered comparison.
                    getterPosition=!(projection>0)?retained.Position:projection>=1?segmentEnd:retained.Position+delta*projection;
                }
            }
            bool binary = kind is Net7EffectClassKind.LineEffect or Net7EffectClassKind.ParticleJetEffect or
                Net7EffectClassKind.RObjLookAtEffect or
                Net7EffectClassKind.StraightSeekEffect or Net7EffectClassKind.SpiralSeekEffect or
                Net7EffectClassKind.SplineRandomSeekEffect or Net7EffectClassKind.LinearRandomSeekEffect;
            Vector3? position = binary && update.AuxiliaryEffectFieldA != 0
                ? retained.BinaryEndpoint is {} end && getterPosition is {} origin ? origin * .5f + end * .5f : null
                : getterPosition;
            // 00735520 copies the getter result without a finite-value admission check.
            captured = position;
        }
        RegisterEffect(new Net7EffectState(
            EffectId: update.EffectId, SourceId: update.SourceGameId, TargetId: update.TargetGameId, DefinitionId: update.EffectDefinitionId,
            Timestamp: _convertServerTimestamp(update.Timestamp), DurationMilliseconds: update.Duration, BoneName: update.SourceBoneChain, Mask: 0, IsObjectToObject: false,
            Scale: update.Scale, SpatialOffset: update.TargetOffset, EffectScalar: update.Speedup,
            Presentation: _presentation.ResolveEffect(update.EffectDefinitionId), GeometryKind: Net7EffectGeometryKind.ObjectToEffect,
            HsvShift: update.HsvShift, AuxiliaryEffectFieldA: update.AuxiliaryEffectFieldA, AuxiliaryEffectFieldB: update.AuxiliaryEffectFieldB)
        {
            TargetEffectGeneration = referenced?.InstanceGeneration,
            CapturedTargetEffectPoint = captured,
            EndpointReceipt = _canObserveEffectEndpoints
                ? new(_resolveWorldObject(update.SourceGameId)?.CreationOrdinal, null) : null
        });
    }

    private void ApplyObjectToPointEffect(Net7ObjectToPointEffectUpdate update)
    {
        RegisterEffect(new Net7EffectState(
            EffectId: update.EffectId, SourceId: update.SourceGameId, TargetId: 0, DefinitionId: update.EffectDefinitionId,
            Timestamp: _convertServerTimestamp(update.Timestamp), DurationMilliseconds: update.Duration, BoneName: update.SourceBoneChain, Mask: 0, IsObjectToObject: false,
            Scale: update.Scale, EffectScalar: update.Speedup, Presentation: _presentation.ResolveEffect(update.EffectDefinitionId),
            GeometryKind: Net7EffectGeometryKind.ObjectToPoint, WorldPoint: update.TargetPoint, HsvShift: update.HsvShift)
        {
            // 00735C26/00735C3C bind the source at packet processing time.
            EndpointReceipt = _canObserveEffectEndpoints
                ? new(_resolveWorldObject(update.SourceGameId)?.CreationOrdinal, null) : null
        });
    }

    private void ApplyObjectToObjectDurationLinkedEffect(Net7ObjectToObjectDurationLinkedEffectUpdate update)
    {
        RegisterEffect(new Net7EffectState(
            EffectId: update.EffectId, SourceId: update.SourceGameId, TargetId: update.TargetGameId, DefinitionId: update.EffectDefinitionId,
            Timestamp: _convertServerTimestamp(update.Timestamp), DurationMilliseconds: 0, BoneName: update.SourceBoneChain, Mask: 0, IsObjectToObject: true,
            Scale: update.Scale, SpatialOffset: update.SpatialOffset,
            EndpointShortA: unchecked((ushort)update.EndpointShortA), EndpointShortB: unchecked((ushort)update.EndpointShortB),
            DescriptorFlag: update.DescriptorFlag != 0, EffectScalar: update.Speedup,
            Presentation: _presentation.ResolveEffect(update.EffectDefinitionId), GeometryKind: Net7EffectGeometryKind.ObjectToObjectDurationLinked,
            DurationLink: update.DurationLink, HsvShift: update.HsvShift)
        {
            EndpointReceipt = _canObserveEffectEndpoints
                ? new(_resolveWorldObject(update.SourceGameId)?.CreationOrdinal, _resolveWorldObject(update.TargetGameId)?.CreationOrdinal)
                : null
        });
    }

    private void ApplyObjectEffect(ReadOnlySpan<byte> payload)
    {
        // 00882980 initializes fields before 004AACE0 copies available bytes.
        int offset = 0;
        byte mask = (byte)ReadInitializedField(payload, ref offset, 1);
        uint gameId = ReadInitializedField(payload, ref offset, 4);
        ushort definition = (ushort)ReadInitializedField(payload, ref offset, 2);
        uint effectId = ReadOptionalU32(payload, ref offset, (mask & 0x01) != 0);
        uint timestamp = ReadOptionalU32(payload, ref offset, (mask & 0x02) != 0);
        short duration = ReadOptionalI16(payload, ref offset, (mask & 0x04) != 0);
        float scale = ReadOptionalF32(payload, ref offset, (mask & 0x08) != 0, 1.0f);
        Vector3 effectOffset = new(
            ReadOptionalF32(payload, ref offset, (mask & 0x10) != 0, 0.0f),
            ReadOptionalF32(payload, ref offset, (mask & 0x20) != 0, 0.0f),
            ReadOptionalF32(payload, ref offset, (mask & 0x40) != 0, 0.0f));
        // 00882980 reads mask-selected fields; unused packet bytes do not reject the effect.
        // EffectID is optional/default-zero, but retail still registers
        // a successfully created ordinary effect under that numeric ID.
        RegisterEffect(new Net7EffectState(
            effectId, gameId, gameId, definition, _convertServerTimestamp(timestamp), duration, string.Empty, mask, false,
            Scale: scale, EffectOffset: effectOffset, Presentation: _presentation.ResolveEffect(definition))
        {
            // 00734F04 invokes the unary source setter immediately. Preserve
            // that lookup observation across later arrival or GameID reuse.
            EndpointReceipt = _canObserveEffectEndpoints
                ? new(_resolveWorldObject(gameId)?.CreationOrdinal, null)
                : null
        });
    }

    private void ApplyObjectToObjectEffect(ReadOnlySpan<byte> payload)
    {
        // 00883A60 retains initialized values on a short read, including optional fields.
        int offset = 0;
        ushort mask = (ushort)ReadInitializedField(payload, ref offset, 2);
        uint sourceId = ReadInitializedField(payload, ref offset, 4);
        uint targetId = ReadInitializedField(payload, ref offset, 4);
        ushort definition = (ushort)ReadInitializedField(payload, ref offset, 2);
        ReadOnlySpan<byte> remaining = payload[offset..];
        int nul = remaining.IndexOf((byte)0);
        int length = nul < 0 ? remaining.Length : nul;
        // 00414DC6 retains an unterminated nonempty suffix and adds a terminator.
        string bone = Encoding.Latin1.GetString(remaining[..length]);
        offset += length + (nul < 0 ? 0 : 1);
        uint effectId = ReadOptionalU32(payload, ref offset, (mask & 0x0001) != 0);
        uint timestamp = ReadOptionalU32(payload, ref offset, (mask & 0x0002) != 0);
        short duration = ReadOptionalI16(payload, ref offset, (mask & 0x0004) != 0);
        Vector3 spatialOffset = (mask & 0x0040) != 0
            ? new Vector3(ReadRequiredF32(payload, ref offset), ReadRequiredF32(payload, ref offset), ReadRequiredF32(payload, ref offset))
            : Vector3.Zero;
        ushort endpointShortA = ReadOptionalU16(payload, ref offset, (mask & 0x0008) != 0);
        ushort endpointShortB = ReadOptionalU16(payload, ref offset, (mask & 0x0010) != 0);
        bool descriptorFlag = (mask & 0x0020) != 0;
        float scale = ReadOptionalF32(payload, ref offset, (mask & 0x0080) != 0, 1.0f);
        Vector3 effectOffset = new(
            ReadOptionalF32(payload, ref offset, (mask & 0x0100) != 0, 0.0f),
            ReadOptionalF32(payload, ref offset, (mask & 0x0200) != 0, 0.0f),
            ReadOptionalF32(payload, ref offset, (mask & 0x0400) != 0, 0.0f));
        float effectScalar = ReadOptionalF32(payload, ref offset, (mask & 0x0800) != 0, 1.0f);
        // 00883A60 likewise ignores bytes following the selected fields.
        // Zero is likewise a real default EffectID for this family.
        RegisterEffect(new Net7EffectState(
            effectId, sourceId, targetId, definition, _convertServerTimestamp(timestamp), duration, bone, mask, true,
            scale, spatialOffset, endpointShortA, endpointShortB, descriptorFlag, effectOffset, effectScalar,
            _presentation.ResolveEffect(definition), GeometryKind: Net7EffectGeometryKind.ObjectToObject)
        {
            // Observe gameplay membership at packet receipt, independent of
            // spatial/asset loading and later presentation or render frames.
            EndpointReceipt = _canObserveEffectEndpoints
                ? new(_resolveWorldObject(sourceId)?.CreationOrdinal, _resolveWorldObject(targetId)?.CreationOrdinal)
                : null
        });
    }


    private void RegisterEffect(Net7EffectState effect)
    {
        ulong generation = checked(++_nextEffectInstanceGeneration);
        _effects[effect.EffectId] = effect with { InstanceGeneration = generation, PoseContext = _effectPoseContext };
    }

    private void ApplyRemoveEffect(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new InvalidDataException("0x000F Remove Effect payload is shorter than its effect ID.");
        _effects.Remove(BinaryPrimitives.ReadUInt32LittleEndian(payload));
    }

    private static uint ReadOptionalU32(ReadOnlySpan<byte> payload, ref int offset, bool present)
        => present ? ReadInitializedField(payload, ref offset, 4) : 0;

    private static short ReadOptionalI16(ReadOnlySpan<byte> payload, ref int offset, bool present)
        => unchecked((short)ReadOptionalU16(payload, ref offset, present));

    private static ushort ReadOptionalU16(ReadOnlySpan<byte> payload, ref int offset, bool present)
        => present ? (ushort)ReadInitializedField(payload, ref offset, 2) : (ushort)0;

    private static float ReadOptionalF32(ReadOnlySpan<byte> payload, ref int offset, bool present, float defaultValue)
        => present ? ReadRequiredF32(payload, ref offset, defaultValue) : defaultValue;

    private static float ReadRequiredF32(ReadOnlySpan<byte> payload, ref int offset, float initial = 0)
        => BitConverter.UInt32BitsToSingle(ReadInitializedField(payload, ref offset, 4,
            BitConverter.SingleToUInt32Bits(initial)));

    private static uint ReadInitializedField(ReadOnlySpan<byte> payload, ref int offset, int width, uint initial = 0)
    {
        // 004AACE0 advances by the copied count, leaving the remaining destination bytes intact.
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, initial);
        int count = Math.Min(width, payload.Length - offset);
        payload.Slice(offset, count).CopyTo(value);
        offset += count;
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }
}
