using EnbNext.Assets;
using EnbNext.Client.Models;
using EnbNext.Net7;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.LocalData;
using EnbNext.Net7.World;
using EnbNext.Net7.Diagnostics;
using EnbNext.Protocol.Net;
using EnbNext.Presentation;
using EnbNext.Net7.Gameplay;
using System.Collections.Immutable;

namespace EnbNext.Client;

/// <summary>UI-independent direct Net7 session facade.</summary>
public sealed partial class EnbClient : IAsyncDisposable
{
    // Current Net7Proxy keeps accepted server gate-cache permission in
    // process-global storage with no clear until exit. MainWindow deliberately
    // replaces EnbClient instances on a fresh login, so this owner must outlive
    // any one facade/session object to preserve that exact lifetime.
    private static readonly Net7ProcessCompatibilityState ProcessCompatibilityState = new();

    private readonly EnbClientOptions _options;
    private readonly Net7WireCapture _wireCapture;
    private readonly object _automaticRecoverySync = new();
    private readonly object _wireCaptureStopSync = new();
    private readonly EnbPresentationSnapshotPublisher _presentationSnapshots = new();
    private Net7Session? _net7;
    private Task? _automaticHandoffRecoveryTask;
    private Task? _postInWorldWireCaptureStopTask;
    private CancellationTokenSource? _postInWorldWireCaptureStopCts;
    private bool _automaticSectorTransitionActive;

    public EnbClient(EnbClientOptions options)
    {
        Autopilot = new(RequestTargetCoreAsync, RequestWarpAsync, ExecuteTargetVerbCoreAsync, Trace);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _wireCapture = new Net7WireCapture(message => Trace(message));
        _wireCapture.EnableEmoteReceipts(options.WireCaptureDirectory);
    }

    public EnbSectorAutopilot Autopilot { get; }

    public ClientLifecycleState State { get; private set; } = ClientLifecycleState.Disconnected;
    public IReadOnlyList<CharacterSummary> Characters { get; private set; } = Array.Empty<CharacterSummary>();
    public SectorRouteResult? Route { get; private set; }
    public CharacterSummary? SelectedCharacter { get; private set; }
    /// <summary>Called when a character is selected, before its saved settings are read (the character and its session).</summary>
    public Action<CharacterSummary, uint>? CharacterSettingsPreparing { get; set; }
    public uint? SelectedFixedSessionId { get; private set; }
    internal Net7WorldState? World => _net7?.World;
    public EnbSceneSnapshot? LatestPresentationSnapshot => _presentationSnapshots.Latest;
    public EnbRenderSceneSubscription CreateRenderSceneSubscription() => _presentationSnapshots.RenderScene.Subscribe();
    public Net7GalaxyMapData? GalaxyMap => _net7?.GalaxyMap;
    /// <summary>The server's GalaxyMap command 4: the system and sector the character is in, and docked the station's name.</summary>
    public Net7GalaxyMapLocation? GalaxyMapLocation => _net7?.GalaxyMapLocation;
    public async Task<bool> ToggleGalaxyMapAsync()
    {
        if (State != ClientLifecycleState.InWorld || _net7 is not { } session) return false;
        uint owner = session.FixedSessionId;
        bool sent = await session.ToggleGalaxyMapAsync().ConfigureAwait(false);
        if (sent && ReferenceEquals(_net7, session) && session.FixedSessionId == owner && State == ClientLifecycleState.InWorld)
            RetailAssets?.SaveGalaxyMapShowAll(owner, session.GalaxyMapShowAll);
        return sent;
    }

    public Task<bool> UpdateFlightInputAsync(Net7FlightInput input, CancellationToken cancellationToken = default)
    {
        if (_isRecordedReplay || State != ClientLifecycleState.InWorld || _net7 is not { } session) return Task.FromResult(false);
        return session.UpdateFlightInputAsync(input, cancellationToken);
    }

    public Task<bool> RequestWarpAsync(uint shipId, ulong owner, IReadOnlyList<uint> route, CancellationToken cancellationToken = default)
    {
        if (State != ClientLifecycleState.InWorld || _net7 is not { } session) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
        {
            var snapshot = LatestPresentationSnapshot;
            if (!ReferenceEquals(session, _net7) || State != ClientLifecycleState.InWorld ||
                snapshot is null || snapshot.IsTransitioning || world.LocalShipObjectId != shipId ||
                snapshot.Objects.FirstOrDefault(o => o.GameId == shipId)?.RenderIdentity != owner)
                return Task.FromResult(false);
            if (Net7WarpStatus.Read(world.Objects.FindFirst(shipId)?.AuxData, session.Gameplay.PresentationMode).CanRequestRescue)
                return session.RequestIncapacitationDetailsAsync(cancellationToken);
            return session.RequestWarpAsync(shipId, route, cancellationToken);
        });
    }

    public Task<bool> FireAllWeaponsAsync(uint shipId, ulong owner, CancellationToken cancellationToken = default)
        => PublishMultiboxInput("fire",0,FireAllCoreAsync(shipId,owner,cancellationToken));

    private Task<bool> FireAllCoreAsync(uint shipId, ulong owner, CancellationToken cancellationToken = default)
    {
        if (State != ClientLifecycleState.InWorld || _net7 is not { } session) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
        {
            var snapshot = LatestPresentationSnapshot;
            if (!ReferenceEquals(session, _net7) || State != ClientLifecycleState.InWorld ||
                snapshot is null || snapshot.IsTransitioning || world.LocalShipObjectId != shipId ||
                snapshot.LocalShipObjectId != shipId ||
                snapshot.Objects.FirstOrDefault(o => o.GameId == shipId)?.RenderIdentity != owner)
                return Task.FromResult(false);
            return session.FireAllWeaponsAsync(cancellationToken);
        });
    }

    public Task<bool> ToggleAutoLevelAsync(uint shipId, ulong owner, CancellationToken cancellationToken = default)
    {
        if (State != ClientLifecycleState.InWorld || _net7 is not { } session) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
        {
            var snapshot = LatestPresentationSnapshot;
            if (!ReferenceEquals(session, _net7) || State != ClientLifecycleState.InWorld ||
                snapshot is null || snapshot.IsTransitioning || world.LocalShipObjectId != shipId ||
                snapshot.LocalShipObjectId != shipId ||
                snapshot.Objects.FirstOrDefault(o => o.GameId == shipId)?.RenderIdentity != owner)
                return Task.FromResult(false);
            return session.ToggleAutoLevelAsync(cancellationToken);
        });
    }

    public Task<bool> ToggleAutofollowAsync(uint shipId, ulong owner, CancellationToken cancellationToken = default)
    {
        if (State != ClientLifecycleState.InWorld || _net7 is not { } session) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
        {
            var snapshot = LatestPresentationSnapshot;
            if (!ReferenceEquals(session, _net7) || State != ClientLifecycleState.InWorld ||
                snapshot is null || snapshot.IsTransitioning || world.LocalShipObjectId != shipId ||
                snapshot.LocalShipObjectId != shipId ||
                snapshot.Objects.FirstOrDefault(o => o.GameId == shipId)?.RenderIdentity != owner)
                return Task.FromResult(false);
            return session.ToggleAutofollowAsync(cancellationToken);
        });
    }

    /// <summary>006120E0: a keyboard shortcut press while the avatar is
    /// incapacitated activates nothing and adds local message type 0x11.</summary>
    public bool ReportIncapacitatedShortcutPress()
        => ReportLocalGameplayMessage("Cannot perform action while incapacitated.");

    private bool ReportLocalGameplayMessage(string text)
    {
        if (_net7 is not { } session || State != ClientLifecycleState.InWorld)
            return false;
        return session.ReadPresentationFrame((_, clientTime) =>
        {
            if (!ReferenceEquals(session, _net7)) return false;
            session.Gameplay.AddMessage(new(0x11, text));
            OnNet7PresentationStateChanged(clientTime);
            return true;
        });
    }

    public Task<bool> ActivateShortcutSkillAsync(EnbShortcutSkillRequest request, CancellationToken cancellationToken = default)
    {
        if (_net7 is not { } session || RetailAssets is not { } assets || State != ClientLifecycleState.InWorld)
            return Task.FromResult(false);
        // Resolve again at dispatch under the packet-materialization boundary.
        // Never carry an old click into a replacement character/ship or handoff.
        return session.ReadPresentationFrame((world, _) =>
        {
            var snapshot = LatestPresentationSnapshot;
            if (!ReferenceEquals(session, _net7) || State != ClientLifecycleState.InWorld ||
                snapshot is null || snapshot.IsTransitioning || snapshot.LocalShipObjectId != world.LocalShipObjectId)
                return Task.FromResult(false);
            var owner = snapshot.Objects.FirstOrDefault(o => o.GameId == world.LocalShipObjectId)?.RenderIdentity ?? 0;
            var ship = world.LocalShipObjectId is uint id ? world.Objects.FindFirst(id) : null;
            if (ship?.AuxData is null) return Task.FromResult(false);
            var current = EnbSceneShortcutSnapshotBuilder.Build(assets.ResolveShortcuts(session.FixedSessionId),
                assets.ResolveShortcutSkills(), ship.AuxData, world.LocalPlayerAuxData, session.ItemTemplates);
            return EnbShortcutSkillActivation.TryResolve(request, current, owner, out uint ability)
                ? session.ActivateSkillAsync(ability, cancellationToken) : Task.FromResult(false);
        });
    }

    public Task<bool> ActivateShortcutEquipmentAsync(EnbShortcutEquipmentRequest request, CancellationToken cancellationToken = default)
    {
        if (_net7 is not { } session || RetailAssets is not { } assets || State != ClientLifecycleState.InWorld)
            return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
        {
            var snapshot = LatestPresentationSnapshot;
            if (!ReferenceEquals(session, _net7) || State != ClientLifecycleState.InWorld ||
                snapshot is null || snapshot.IsTransitioning || snapshot.LocalShipObjectId != world.LocalShipObjectId)
                return Task.FromResult(false);
            ulong owner = snapshot.Objects.FirstOrDefault(o => o.GameId == world.LocalShipObjectId)?.RenderIdentity ?? 0;
            var ship = world.LocalShipObjectId is uint id ? world.Objects.FindFirst(id) : null;
            if (ship?.AuxData is null) return Task.FromResult(false);
            var current = EnbSceneShortcutSnapshotBuilder.Build(assets.ResolveShortcuts(session.FixedSessionId),
                assets.ResolveShortcutSkills(), ship.AuxData, world.LocalPlayerAuxData, session.ItemTemplates);
            return EnbShortcutEquipmentActivation.TryResolve(request, current, owner, session.ClientShipTransferId, out var use)
                ? session.UseInventoryItemAsync(use.TargetGameId, use.Inventory, use.Slot, cancellationToken)
                : Task.FromResult(false);
        });
    }

    public Task<bool> SetInventoryAutoFireAsync(EnbInventoryItemRequest request, bool enabled, CancellationToken cancellationToken = default)
    {
        if (_net7 is not { } session || State != ClientLifecycleState.InWorld) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
        {
            var snapshot = LatestPresentationSnapshot;
            var ship = world.LocalShipObjectId is uint id ? world.Objects.FindFirst(id) : null;
            if (!ReferenceEquals(session, _net7) || request.Character != session.FixedSessionId || ship?.AuxData is null ||
                snapshot is null || snapshot.IsTransitioning || snapshot.LocalShipObjectId != world.LocalShipObjectId ||
                snapshot.Objects.FirstOrDefault(o => o.IsLocalShip)?.RenderIdentity != request.ShipOwner || request.Inventory != 2 || request.Slot is < 0 or > 19)
                return Task.FromResult(false);
            var current = EnbSceneInventorySnapshotBuilder.Build(ship.AuxData, world.LocalPlayerAuxData, session.ItemTemplates, null).Find(2, request.Slot);
            // 0052B160: SClient+1130, inventory 2, slot, mask 0x2000, requested boolean. Never schedule repeated UseItem calls locally.
            return current is { HasAutoFire: true, State: not null } && current.TemplateId == request.TemplateId
                ? session.SetItemStateFlag2000Async(session.ClientShipTransferId, (byte)request.Slot, enabled, cancellationToken)
                : Task.FromResult(false);
        });
    }

    public Task<bool> TrainInventorySkillAsync(EnbCharacterActionRequest request, CancellationToken cancellationToken=default)
        => CharacterAction(request,false,cancellationToken);
    public Task<bool> ForfeitInventoryMissionAsync(EnbCharacterActionRequest request, CancellationToken cancellationToken=default)
        => CharacterAction(request,true,cancellationToken);
    public Task<bool> ClearInventoryMissionAsync(EnbCharacterActionRequest request, CancellationToken cancellationToken=default)
        => CharacterAction(request,true,cancellationToken,clear:true);
    private Task<bool> CharacterAction(EnbCharacterActionRequest request,bool forfeit,CancellationToken cancellationToken,bool clear=false)
    {
        if(_net7 is not {} session||State!=ClientLifecycleState.InWorld)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,time)=>
        {
            var snapshot=LatestPresentationSnapshot;
            if(!ReferenceEquals(session,_net7)||request.Character!=session.FixedSessionId||snapshot is not {IsTransitioning:false}||
               snapshot.LocalShipObjectId!=world.LocalShipObjectId||snapshot.Objects.FirstOrDefault(o=>o.IsLocalShip)?.RenderIdentity!=request.ShipOwner)
                return Task.FromResult(false);
            if(!forfeit)return request.Index is >=0 and <64 ? session.TrainSkillAsync(request.Index,cancellationToken):Task.FromResult(false);
            if(request.Index is <0 or >=12)return Task.FromResult(false);
            var mission=session.PlayerSemantics.GetMission(request.Index,time);
            if(clear)return mission.InternalMissionId==request.Identity&&mission.CanClear
                ?session.ClearMissionAsync(request.Index,cancellationToken):Task.FromResult(false);
            return mission.InternalMissionId==request.Identity&&mission.CanForfeit
                ?session.ForfeitMissionAsync(request.Index,true,cancellationToken):Task.FromResult(false);
        });
    }

    public Task<bool> SortCargoAsync(uint character, ulong owner, int preset, bool reverse, CancellationToken cancellationToken = default)
    {
        if (_net7 is not { } session || State != ClientLifecycleState.InWorld || preset is < 0 or > 255) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
        {
            var snapshot = LatestPresentationSnapshot;
            return ReferenceEquals(session, _net7) && character == session.FixedSessionId && snapshot is { IsTransitioning: false } &&
                snapshot.LocalShipObjectId == world.LocalShipObjectId && snapshot.Objects.FirstOrDefault(o => o.IsLocalShip)?.RenderIdentity == owner
                ? SortInventoryWithOptions(session,character,1,preset,reverse,cancellationToken) : Task.FromResult(false);
        });
    }

    public Task<bool> SortVaultAsync(uint character, ulong owner, int preset, bool reverse, CancellationToken cancellationToken = default)
    {
        if (_net7 is not { } session || State != ClientLifecycleState.InWorld || preset is < 0 or > 255) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
        {
            var snapshot = LatestPresentationSnapshot;
            return ReferenceEquals(session, _net7) && character == session.FixedSessionId && snapshot is { IsTransitioning: false, ActiveStarbaseId: not null } &&
                snapshot.LocalShipObjectId == world.LocalShipObjectId && snapshot.Objects.FirstOrDefault(o => o.IsLocalShip)?.RenderIdentity == owner
                ? SortInventoryWithOptions(session,character,3,preset,reverse,cancellationToken) : Task.FromResult(false);
        });
    }

    private Task<bool> SortInventoryWithOptions(Net7Session session,uint character,uint target,int mode,bool reverse,CancellationToken cancellationToken)
    {
        if(RetailAssets is not {} assets)return Task.FromResult(false);
        var preset=assets.ResolvePlayerOptions(character).Options.InventorySort(mode);
        return session.SortInventoryAsync(target,preset.Primary,preset.Secondary,preset.Tertiary,reverse,cancellationToken);
    }

    public EnbSceneFrame? SampleLivePresentationFrame()
    {
        if (_net7 is not { } session) return null;
        // Retail 005A69B0 -> 005A8350 runs before the frame consumes message lines.
        // The live owner path must advance this just like diagnostic snapshot sampling.
        if (State == ClientLifecycleState.InWorld) session.AdvanceLocalMissionPresentation();
        return session.ReadPresentationFrame((world,time) =>
            _presentationSnapshots.AcquireLiveFrame(world,time,session.Gameplay.PresentationMode));
    }
    public uint? CurrentClientTime => _net7?.CurrentClientTime;
    /// <summary>Next: the session's connection reading (null without a session).</summary>
    public EnbNext.Net7.Net7ConnectionSample? Connection => _net7?.Connection;
    public void SaveCinematicOptions(bool warp, bool gate)
    {
        if (_net7 is not {} session || RetailAssets is not {} assets || State != ClientLifecycleState.InWorld)
            throw new InvalidOperationException("Enter the world before changing character camera settings.");
        assets.SaveCinematicOptions(session.FixedSessionId, warp, gate);
        OnNet7PresentationStateChanged(session.CurrentClientTime);
    }
    public void SaveCinematicOptions(EnbRetailCameraOptions options)
    {
        if (_net7 is not {} session || RetailAssets is not {} assets || State != ClientLifecycleState.InWorld)
            throw new InvalidOperationException("Enter the world before changing character camera settings.");
        assets.SaveCinematicOptions(session.FixedSessionId, options);
        OnNet7PresentationStateChanged(session.CurrentClientTime);
    }
    public void SaveGraphicsOptions(EnbRetailGraphicsOptions options)
    {
        if (_net7 is not {} session || RetailAssets is not {} assets || State != ClientLifecycleState.InWorld)
            throw new InvalidOperationException("Enter the world before changing character graphics settings.");
        assets.SaveGraphicsOptions(session.FixedSessionId, options);
        OnNet7PresentationStateChanged(session.CurrentClientTime);
    }
    public void PreviewGraphicsOptions(EnbRetailGraphicsOptions? options)
    {
        if (_net7 is not {} session || RetailAssets is not {} assets || State != ClientLifecycleState.InWorld)
        {
            if (options is null) return;
            throw new InvalidOperationException("Enter the world before changing character graphics settings.");
        }
        assets.SetGraphicsPreview(session.FixedSessionId, options);
    }
    public Task<bool> CompleteGateCameraAsync(ulong shipOwner, ulong sequence, uint targetId)
    {
        if (_net7 is not {} session || State != ClientLifecycleState.InWorld) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
            ReferenceEquals(session, _net7) && !world.IsTransitioning &&
            LatestPresentationSnapshot?.Objects.FirstOrDefault(o => o.GameId == world.LocalShipObjectId)?.RenderIdentity == shipOwner
                ? session.CompleteGateCameraAsync(sequence, targetId) : Task.FromResult(false));
    }
    public Task<bool> CompleteDockCameraAsync(ulong shipOwner, ulong revision, uint targetId, bool departure = false)
    {
        if (_net7 is not {} session || State != ClientLifecycleState.InWorld) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
            ReferenceEquals(session, _net7) && !world.IsTransitioning &&
            LatestPresentationSnapshot?.Objects.FirstOrDefault(o => o.GameId == world.LocalShipObjectId)?.RenderIdentity == shipOwner
                ? session.CompleteDockCameraAsync(revision, targetId, departure: departure) : Task.FromResult(false));
    }
    public Task<bool> CompleteLandCameraAsync(ulong shipOwner,ulong revision,uint targetId,Net7LandCameraPhase phase)
    {
        if(_net7 is not {} session || State!=ClientLifecycleState.InWorld)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>ReferenceEquals(session,_net7) && !world.IsTransitioning &&
            LatestPresentationSnapshot?.Objects.FirstOrDefault(o=>o.GameId==world.LocalShipObjectId)?.RenderIdentity==shipOwner
                ?session.CompleteLandCameraAsync(revision,targetId,phase):Task.FromResult(false));
    }
    public Task<bool> CompleteTakeoffCameraAsync(ulong shipOwner,ulong revision)
    {
        if (_net7 is not {} session || State != ClientLifecycleState.InWorld) return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>ReferenceEquals(session,_net7) && !world.IsTransitioning &&
            LatestPresentationSnapshot?.Objects.FirstOrDefault(o=>o.GameId==world.LocalShipObjectId)?.RenderIdentity==shipOwner
                ?session.CompleteTakeoffCameraAsync(revision):Task.FromResult(false));
    }
    public bool AssignShortcutSkill(EnbShortcutAssignmentRequest request)
    {
        if(_net7 is not {} session||RetailAssets is not {} assets||State!=ClientLifecycleState.InWorld)return false;
        return session.ReadPresentationFrame((world,time)=>
        {
            var snapshot=LatestPresentationSnapshot;
            if(!ReferenceEquals(session,_net7)||request.Character!=session.FixedSessionId||world.IsTransitioning||snapshot is null||
                snapshot.Objects.FirstOrDefault(o=>o.GameId==world.LocalShipObjectId)?.RenderIdentity!=request.ShipOwner)return false;
            var ability=assets.ResolveShortcutSkills()?.Abilities.FirstOrDefault(a=>a.Id==request.AbilityId);
            if(ability is null||!assets.AssignSessionShortcutAbility(request.Character,request.Expected,request.Address,ability,persist:true))return false;
            OnNet7PresentationStateChanged(time);return true;
        });
    }

    public bool MoveShortcut(EnbShortcutMoveRequest request)
    {
        if(_net7 is not {} session||RetailAssets is not {} assets||State!=ClientLifecycleState.InWorld)return false;
        return session.ReadPresentationFrame((world,time)=>
        {
            var snapshot=LatestPresentationSnapshot;
            if(!ReferenceEquals(session,_net7)||State!=ClientLifecycleState.InWorld||request.Character!=session.FixedSessionId||
                world.IsTransitioning||snapshot is not {IsTransitioning:false}||snapshot.LocalShipObjectId!=world.LocalShipObjectId||
                snapshot.Objects.FirstOrDefault(o=>o.IsLocalShip)?.RenderIdentity!=request.ShipOwner)return false;
            if(!assets.MoveSessionShortcut(request.Character,request.Expected,request.Source,request.Destination,persist:true))return false;
            OnNet7PresentationStateChanged(time);return true;
        });
    }
    public bool AssignShortcutItem(EnbShortcutItemAssignmentRequest request)
    {
        if(_net7 is not {} session||RetailAssets is not {} assets||State!=ClientLifecycleState.InWorld)return false;
        return session.ReadPresentationFrame((world,time)=>
        {
            var snapshot=LatestPresentationSnapshot;var item=request.Item;
            if(!ReferenceEquals(session,_net7)||State!=ClientLifecycleState.InWorld||item.Character!=session.FixedSessionId||world.IsTransitioning||
                snapshot is not {IsTransitioning:false}||snapshot.LocalShipObjectId!=world.LocalShipObjectId||
                snapshot.Objects.FirstOrDefault(o=>o.IsLocalShip)?.RenderIdentity!=item.ShipOwner||item.AmmoLayer||item.Inventory is not (1 or 2)||
                snapshot.Hud.Inventory?.Find(item.Inventory,item.Slot)?.TemplateId!=item.TemplateId)return false;
            if(!assets.AssignSessionShortcutItem(item.Character,request.Expected,request.Address,item.Inventory,item.Slot,persist:true))return false;
            OnNet7PresentationStateChanged(time);return true;
        });
    }
    public Task<bool> LeaveStarbaseAsync(uint starbaseId, ulong handoffVersion)
    {
        if (_net7 is not { } session || State != ClientLifecycleState.InWorld)
            return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
            ReferenceEquals(session, _net7) && !world.IsTransitioning &&
            world.PresentationHandoffVersion == handoffVersion &&
            session.PlayerSemantics.ActiveStarbaseId == starbaseId
                ? session.LeaveStarbaseAsync() : Task.FromResult(false));
    }

    public Task<bool> BeginStarbaseDepartureAsync(EnbStationMovementRequest request)
        => PublishMultiboxInput("launch", request.StarbaseId, BeginStarbaseDepartureCoreAsync(request), station: true);

    private async Task<bool> BeginStarbaseDepartureCoreAsync(EnbStationMovementRequest request)
    {
        if(_net7 is not {} session || State!=ClientLifecycleState.InWorld)return false;
        bool Current()=>ReferenceEquals(session,_net7) && State==ClientLifecycleState.InWorld &&
            !session.World.IsTransitioning && session.World.PresentationHandoffVersion==request.HandoffVersion &&
            session.PlayerSemantics.ActiveStarbaseId==request.StarbaseId;
        // 00638500 action 1: avatar presentation 4, then room (-1,0).
        if(!await session.ReadPresentationFrame((_,_)=>Current()
            ? session.SendStarbaseAvatarChangeAsync(0,request.Heading,request.Position,4u|(request.Run?1u:0u))
            : Task.FromResult(false)))return false;
        return await session.ReadPresentationFrame((_,_)=>Current()
            ? session.SendStarbaseRoomChangeAsync(uint.MaxValue,0) : Task.FromResult(false));
    }

    public async Task<bool> CompleteStarbaseArrivalAsync(EnbStationMovementRequest request)
    {
        if (_net7 is not { } session || State != ClientLifecycleState.InWorld)
            return false;
        bool Current() => ReferenceEquals(session, _net7) && State == ClientLifecycleState.InWorld &&
            !session.World.IsTransitioning && session.World.PresentationHandoffVersion == request.HandoffVersion &&
            session.PlayerSemantics.ActiveStarbaseId == request.StarbaseId && request.RoomType == 0;
        // 006390A0 / 0063C080: enter room, then publish the spawned avatar with
        // presentation flag 40. 0063E2B0 also includes the authored run option.
        if (!await session.ReadPresentationFrame((_, _) => Current()
            ? session.SendStarbaseRoomChangeAsync(0, uint.MaxValue) : Task.FromResult(false))) return false;
        return await session.ReadPresentationFrame((_, _) => Current()
            ? session.SendStarbaseAvatarChangeAsync(0, request.Heading, request.Position, 0x40u | (request.Run ? 1u : 0u))
            : Task.FromResult(false));
    }

    public Task<bool> MoveStationAvatarAsync(EnbStationMovementRequest request)
    {
        if (_net7 is not { } session || State != ClientLifecycleState.InWorld) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
            ReferenceEquals(session,_net7) && !world.IsTransitioning && world.PresentationHandoffVersion == request.HandoffVersion &&
            session.PlayerSemantics.ActiveStarbaseId == request.StarbaseId
                // 0063B84C / 0063B90B / 00638F05 select terminal16 / NPC32 /
                // normal0; 0063E2B0
                // publishes the pose with the independent default-run bit.
                  ? session.SendStarbaseAvatarChangeAsync(request.RoomType,request.Heading,request.Position,
                      (uint)request.Interaction|(request.Run?1u:0u)) : Task.FromResult(false));
    }

    public Task<bool> ChangeStationRoomAsync(EnbStationRoomRequest request)
    {
        if(_net7 is not {} session || State!=ClientLifecycleState.InWorld)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>ReferenceEquals(session,_net7) && !world.IsTransitioning &&
            world.PresentationHandoffVersion==request.HandoffVersion && session.PlayerSemantics.ActiveStarbaseId==request.StarbaseId &&
            EnbStationRoomLayout.AreConnected(request.Source,request.Destination) && LatestPresentationSnapshot?.StationDefinition is {} definition &&
            definition.Rooms.Any(r=>r.RoomType==request.Source) && definition.Rooms.Any(r=>r.RoomType==request.Destination)
            ? session.SendStarbaseRoomChangeAsync(request.Destination,request.Source) : Task.FromResult(false));
    }

    public bool SelectShortcutSkillLevel(EnbShortcutSkillRequest request, int abilityId)
    {
        if (_net7 is not { } session || RetailAssets is not { } assets || assets.ResolveShortcutSkills() is not { } skills ||
            State != ClientLifecycleState.InWorld) return false;
        return session.ReadPresentationFrame((world, time) =>
        {
            var snapshot = LatestPresentationSnapshot;
            if (!ReferenceEquals(session, _net7) || State != ClientLifecycleState.InWorld || snapshot is null ||
                snapshot.IsTransitioning || snapshot.LocalShipObjectId != world.LocalShipObjectId) return false;
            ulong owner = snapshot.Objects.FirstOrDefault(o => o.GameId == world.LocalShipObjectId)?.RenderIdentity ?? 0;
            var ship = world.LocalShipObjectId is uint id ? world.Objects.FindFirst(id) : null;
            if (ship?.AuxData is null) return false;
            var current = EnbSceneShortcutSnapshotBuilder.Build(assets.ResolveShortcuts(session.FixedSessionId),
                skills, ship.AuxData, world.LocalPlayerAuxData, session.ItemTemplates);
            var choice = EnbShortcutSkillActivation.ResolveSelection(request, abilityId, current, owner, skills);
            if (choice is null || !assets.SelectSessionShortcutAbility(session.FixedSessionId, current.Saved, request.Assignment, choice,persist:true)) return false;
            OnNet7PresentationStateChanged(time);
            return true;
        });
    }
    public string? LastWireCapturePath { get; private set; }
    public EnbInstalledAssetPaths? InstalledAssets { get; private set; }
    public EnbClientBaseAssetCatalog? ClientBaseAssets { get; private set; }
    public EnbClientBaseAssetMountCatalog? ClientBaseAssetMounts { get; private set; }
    public EnbRetailPresentationCatalog? RetailPresentationCatalog { get; private set; }
    public EnbRetailRenderStateCatalog? RetailRenderStates { get; private set; }
    public EnbRetailRslResourceResolver? RetailRslResources { get; private set; }
    public EnbRetailBaseAssetResolver? RetailBaseAssetResolver { get; private set; }
    public EnbMixResourceCatalog? MixResources { get; private set; }
    public EnbRetailAssetBundle? RetailAssets { get; private set; }

    public event Action<string>? Diagnostic;
    /// <summary>A new mission arrived in this mission slot (Next mission tracker auto-track).</summary>
    public event Action<int>? NewMissionStarted;
    public event Action<ClientLifecycleState>? StateChanged;
    public event Action<Packet>? PacketReceived;
    public event Action<Packet>? PacketSent;

    public async Task<IReadOnlyList<CharacterSummary>> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (State != ClientLifecycleState.Disconnected)
            throw new InvalidOperationException($"Cannot login from state {State}.");

        try
        {
            SetState(ClientLifecycleState.ResolvingServer);
            Trace($"direct Net7 server -> {_options.ServerHost}");

            multiboxSessionIdentity=Guid.NewGuid();
            _net7 = new Net7Session(
                new Net7ServerOptions
                {
                    Host = _options.ServerHost,
                    MasterControlUdpPort = _options.MasterControlUdpPort,
                    AccountTcpPort = _options.AccountTcpPort,
                    MasterHandoffUdpPort = _options.MasterHandoffUdpPort,
                },
                Trace,
                wireCapture: _wireCapture,
                processCompatibilityState: ProcessCompatibilityState,
                useCameraController: true);
            ConfigureQualityOfLife(qualityOfLife);
            _net7.SetCDataCatalog(Net7LocalDataAssets.CurrentCData);
            _presentationSnapshots.Clear();
            LoadInstalledPresentationAssets();

            _net7.PresentationStateChanged += OnNet7PresentationStateChanged;
            _net7.PresentationStateDeferred += OnNet7PresentationStateDeferred;
            _net7.NewMissionStarted += slot => NewMissionStarted?.Invoke(slot);
            _net7.GameplayPacketReceived += packet =>
            {
                if (!Autopilot.Active) return;
                if (packet.Header.Opcode == 0x001D)
                {
                    var message=Net7GameplayWire.DecodeMessageString(packet.Payload.Span);
                    Autopilot.ObserveMessage(message.Type,message.Text);
                }
            };
            _net7.TcpPacketReceived += p => PacketReceived?.Invoke(p);
            _net7.TcpPacketSent += p => PacketSent?.Invoke(p);
            _net7.LoginStateChanged += OnNet7LoginStateChanged;
            var logoffSession = _net7;
            logoffSession.LogoffConfirmed += destination => OnNet7LogoffConfirmed(logoffSession, destination);
            _net7.UdpFrameSent += (p, channel) => Trace($"UDP OUT [{channel}] 0x{p.Opcode:X4} len={p.TotalLength} fixed=0x{p.FixedSessionId:X8} seq={p.Sequence}");
            _net7.UdpFrameReceived += (p, channel) => Trace($"UDP IN  [{channel}] 0x{p.Opcode:X4} len={p.TotalLength} fixed=0x{p.FixedSessionId:X8} seq={p.Sequence}");

            Net7AvatarRoster roster = await _net7.LoginAsync(username, password, cancellationToken).ConfigureAwait(false);
            _loginCredentials = (username, password);

            Characters = MapCharacters(roster);
            Trace($"character roster ready: {Characters.Count(c => !c.IsEmpty)} occupied / {Characters.Count} slots");
            SetState(ClientLifecycleState.CharacterSelection);
            return Characters;
        }
        catch
        {
            SetState(ClientLifecycleState.Faulted);
            throw;
        }
    }

    /// <summary>
    /// Selects an occupied roster slot, validates the current account response,
    /// and commits the fixed/session ID. Call <see cref="EnterGameAsync"/> next
    /// to continue through Master handoff and sector entry.
    /// </summary>
    /// <summary>00769400 route gate, evaluated before the selection request.</summary>
    public bool IsCharacterGalaxyAvailable(CharacterSummary character)
        => !character.IsEmpty && State == ClientLifecycleState.CharacterSelection &&
            _net7?.Roster?.TryResolveUsableRouteForSlot(character.Slot) is not null;

    public async Task<uint> SelectCharacterAsync(
        CharacterSummary character,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (character.IsEmpty)
            throw new InvalidOperationException("Cannot select an empty character slot.");
        if (State != ClientLifecycleState.CharacterSelection || _net7 is null)
            throw new InvalidOperationException($"Cannot select a character from state {State}.");

        try
        {
            TryStartSectorEntryCapture(character);
            _wireCapture.Mark(
                "character-selection-start",
                $"slot={character.Slot};avatar=0x{character.AvatarId:X8};name={character.FullName}");
            SetState(ClientLifecycleState.AvatarLogin);
            Net7CharacterSelectionResult result = await _net7.SelectCharacterAsync(character.Slot, cancellationToken).ConfigureAwait(false);

            SelectedCharacter = character;
            SelectedFixedSessionId = result.FixedSessionId;
            // Before this character's settings are read: the host may prepare them (Next: same settings for all characters).
            try { CharacterSettingsPreparing?.Invoke(character, result.FixedSessionId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Trace("character settings not prepared: " + ex.Message); }
            _ = RetailAssets?.ResolveCameraModes(result.FixedSessionId);
            _net7.GalaxyMapShowAll = RetailAssets?.ResolvePlayerOptions(result.FixedSessionId).Options.GalaxyMapShowAll ?? false;
            if(RetailAssets is {} colorAssets)
                SetChatColors(result.FixedSessionId,EnbRetailChatColors.LoadFile(Path.Combine(colorAssets.Paths.ClientOutputDirectory,
                    $"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(result.FixedSessionId)}_options.ini")));
            SetState(ClientLifecycleState.CharacterSelected);
            Trace($"character selected: slot {character.Slot}, fixed/session ID 0x{result.FixedSessionId:X8}; ready to enter the game");
            _wireCapture.Mark("character-selection-complete", $"fixedSessionId=0x{result.FixedSessionId:X8}");
            return result.FixedSessionId;
        }
        catch (Net7AvatarSelectionRejectedException ex)
        {
            // A complete explicit failure response is retry-safe at the Net7 layer.
            _wireCapture.Mark("character-selection-rejected", ex.Message);
            await StopWireCaptureAsync("character-selection-rejected").ConfigureAwait(false);
            SetState(ClientLifecycleState.CharacterSelection);
            throw;
        }
        catch (Exception ex)
        {
            _wireCapture.Mark("character-selection-fault", ex.ToString());
            await StopWireCaptureAsync("character-selection-fault").ConfigureAwait(false);
            SetState(ClientLifecycleState.Faulted);
            throw;
        }
    }

    /// <summary>
    /// Enters the selected character into the current Net7 gameplay sector.
    /// The complete Master/sector/login/reliability path is offline-conformant;
    /// current local data is embedded and validated automatically.
    /// </summary>
    public async Task<SectorRouteResult> EnterGameAsync(
        CharacterSummary character,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(character);
        cancellationToken.ThrowIfCancellationRequested();

        if (_net7 is null || State != ClientLifecycleState.CharacterSelected)
            throw new InvalidOperationException($"Cannot enter the game from state {State}; select a character first.");

        CharacterSummary selected = SelectedCharacter
            ?? throw new InvalidOperationException("A committed character selection is required before entering the game.");
        if (character.Slot != selected.Slot || character.AvatarId != selected.AvatarId)
        {
            throw new InvalidOperationException(
                "EnterGameAsync may only enter the exact character committed by SelectCharacterAsync.");
        }

        TryStartSectorEntryCapture(selected);
        _wireCapture.Mark("sector-entry-start", $"slot={selected.Slot};sector={selected.SectorId}");
        Trace($"entering game: beginning Master/sector entry for slot {selected.Slot}; session owns the pinned GalaxyMap asset");

        try
        {
            Net7RouteResult route = await _net7.EnterSelectedCharacterAsync(cancellationToken).ConfigureAwait(false);
            var clientRoute = new SectorRouteResult(
                selected,
                route.FixedSessionId,
                route.RetainedMasterResponseDword,
                route.GameplaySectorEndPoint);
            Route = clientRoute;
            // MainHudUpdateStateAndMenus 0056310D -> 0056B700 synchronizes the
            // saved channel subscriptions and visibility once at initial chat startup.
            // Incoming responses supply confirmation; never synthesize success locally.
            if (RetailAssets is { } chatAssets)
            {
                var chatOptions = chatAssets.ResolvePlayerOptions(route.FixedSessionId).Options;
                await _net7.InitializeChatAsync(chatOptions.MonitoredChatChannels, chatOptions.RestrictedStatus, cancellationToken).ConfigureAwait(false);
            }
            Trace($"gameplay sector ready at {route.GameplaySectorEndPoint}");
            _wireCapture.Mark("sector-entry-inworld", route.GameplaySectorEndPoint.ToString());
            SchedulePostInWorldWireCaptureStop();
            return clientRoute;
        }
        catch (Exception ex)
        {
            _wireCapture.Mark("sector-entry-fault", ex.ToString());
            await StopWireCaptureAsync("sector-entry-fault").ConfigureAwait(false);

            // This is a recoverable return-to-login path, not a terminal
            // application fault. Net7Session has already sent the one-shot 0x1008,
            // invalidated gameplay authority and retired account/gameplay transports.
            // Retire the now-spent session object and expose a genuinely reusable
            // login state instead of leaving a character-selection screen backed by
            // a closed account connection.
            if (_net7?.LoginState == Net7LoginState.CharacterSelection)
                await ReturnToLoginAfterSectorEntryFailureAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Selects or clears the in-world target through the established Net7 target
    /// request path. A target ID of zero is Retail's clear-target request.
    /// Authoritative target state still arrives through ShipAuxData.
    /// </summary>
    public Task<bool> RequestTargetAsync(uint targetId, CancellationToken cancellationToken = default)
        => PublishMultiboxInput("target",targetId,RequestTargetCoreAsync(targetId,cancellationToken));

    private async Task<bool> RequestTargetCoreAsync(uint targetId, CancellationToken cancellationToken = default)
    {
        Net7Session session = _net7
            ?? throw new InvalidOperationException("No Net7 gameplay session is active.");
        if (State != ClientLifecycleState.InWorld)
            throw new InvalidOperationException($"Cannot select a world target from state {State}.");
        if (_isRecordedReplay) return false;
        if (_wireCapture.IsActive) _wireCapture.Mark("replay-target-input-v1", targetId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await session.RequestTargetAsync(targetId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes one enabled server-published target verb. The upper word is the
    /// retail VerbEnum; a nonzero lower word is the server-authored disabled
    /// status and is never emitted as an action.
    /// </summary>
    public Task<bool> ExecuteTargetVerbAsync(
        uint targetId,
        uint verb,
        CancellationToken cancellationToken = default)
    {
        string? follow = (verb & 0xFFFF0000u) switch { 0x00080000 => "dock", 0x00020000 => "land", _ => null };
        if (follow is null || MultiboxLeadAsync is null || MultiboxContext is not { } context || (verb & 0xFFFF) != 0 ||
            (_net7?.Gameplay.HasLocalTargetSelection == true ? _net7.Gameplay.LocalSelectedTargetId : _net7?.World.CurrentTargetId) != targetId)
            return ExecuteTargetVerbCoreAsync(targetId, verb, cancellationToken);
        return LeadAsync(context);
        // As in N7CM, following alts press their own Dock/Land first,
        // one at a time; the leader's own request goes last.
        async Task<bool> LeadAsync(EnbMultiboxContext leader)
        {
            await MultiboxLeadAsync!(new(Guid.NewGuid(), follow, targetId, leader));
            return await ExecuteTargetVerbCoreAsync(targetId, verb, cancellationToken);
        }
    }

    /// <summary>Dispatches a leader Dock/Land to following alts and completes after
    /// the last alt was addressed plus the stagger. Supplied by the multibox host.</summary>
    public Func<EnbMultiboxInput, Task>? MultiboxLeadAsync { get; set; }

    private Task<bool> ExecuteTargetVerbCoreAsync(
        uint targetId,
        uint verb,
        CancellationToken cancellationToken = default)
    {
        Net7Session session = _net7
            ?? throw new InvalidOperationException("No Net7 gameplay session is active.");
        uint? selectedTargetId = session.Gameplay.HasLocalTargetSelection
            ? session.Gameplay.LocalSelectedTargetId
            : session.World.CurrentTargetId;
        if (State != ClientLifecycleState.InWorld ||
            targetId == 0 ||
            selectedTargetId != targetId ||
            (verb & 0xFFFF) != 0)
            return Task.FromResult(false);

        // 00658430: every verb Action is built with SClient+112C (0037's
        // assigned object); Scan and Tractor also address that object.
        uint self = session.World.ClientAvatarId ?? session.World.LocalPlayerId ?? 0;
        return (verb & 0xFFFF0000u) switch
        {
            0x00010000 => session.SendActionAsync(
                EnbNext.Net7.Gameplay.Net7PlayerAction.Scan,
                self,
                cancellationToken: cancellationToken),
            0x00020000 => session.RequestLandAsync(targetId, cancellationToken,
                completeWithoutCamera: RetailAssets?.ResolvePlayerOptions(session.FixedSessionId).Options.CameraLand != true,
                startCamera: RetailAssets?.ResolvePlayerOptions(session.FixedSessionId).Options.CameraLand == true, useCameraController: true),
            0x00030000 => session.RequestHulkOpenAsync(cancellationToken),
            0x00040000 => session.InviteCurrentTargetToGroupAsync(cancellationToken),
            // 00744E70/00744F22: non-player targets send Action 2; the
            // server's 0053/0054 reply supplies the portrait and shared dialog.
            0x00050000 when session.World.Objects.FindFirst(targetId) is { ObjectType: not 1 } =>
                session.SendActionAsync(EnbNext.Net7.Gameplay.Net7PlayerAction.MessageNonAvatar,
                    targetId, cancellationToken: cancellationToken),
            // 00658430 Trade: the own object reports local type 0x11 and
            // still takes the shared 00658A70 cue; no Action 0x14 is queued.
            0x00060000 when targetId == self => Task.FromResult(ReportLocalGameplayMessage("Cannot trade with yourself.")),
            0x00060000 => session.RequestTradeAsync(targetId, cancellationToken),
            0x00070000 => session.SendActionAsync(
                EnbNext.Net7.Gameplay.Net7PlayerAction.Tractor,
                self,
                cancellationToken: cancellationToken),
            0x00080000 => session.RequestDockAsync(targetId, cancellationToken,
                completeWithoutCamera: RetailAssets?.ResolvePlayerOptions(session.FixedSessionId).Options.CameraDock != true, useCameraController: true),
            0x00090000 => session.Gameplay.CurrentHarvestableId is not null ? session.CloseHarvestableAsync(cancellationToken) : session.SendActionAsync(
                EnbNext.Net7.Gameplay.Net7PlayerAction.Prospect,
                targetId,
                cancellationToken: cancellationToken),
            0x000A0000 => session.RequestGateAsync(
                targetId,
                cameraDerivedOptionalVar: RetailAssets?.ResolvePlayerOptions(session.FixedSessionId).Options.CameraGate == true ? 0u : 1u,
                cancellationToken: cancellationToken, useCameraController: true),
            0x000B0000 => session.SendActionAsync(
                EnbNext.Net7.Gameplay.Net7PlayerAction.Register,
                targetId,
                cancellationToken: cancellationToken),
            0x000C0000 => session.SendActionAsync(
                EnbNext.Net7.Gameplay.Net7PlayerAction.Jumpstart,
                targetId,
                cancellationToken: cancellationToken),
            // 00658430 Follow: the own object reports local type 0x11; no Move 12.
            0x000D0000 when targetId == self => Task.FromResult(ReportLocalGameplayMessage("Cannot autofollow self.")),
            0x000D0000 => session.SendMoveAsync(12, cancellationToken),
            // Player-target Message uses a separate local private-chat branch.
            _ => Task.FromResult(false),
        };
    }


    private void LoadInstalledPresentationAssets()
    {
        InstalledAssets = EnbInstalledAssetLocator.TryLocate(
            _options.EarthAndBeyondRoot,
            _options.EarthAndBeyondClientPath);
        ClientBaseAssets = null;
        ClientBaseAssetMounts = null;
        RetailPresentationCatalog = null;
        RetailRenderStates = null;
        RetailRslResources = null;
        RetailBaseAssetResolver = null;
        MixResources = null;
        RetailAssets = null;

        if (InstalledAssets is null)
        {
            Trace(
                $"ASSET-2C installed retail art not found; set {EnbInstalledAssetLocator.RootEnvironmentVariable} " +
                $"or {EnbInstalledAssetLocator.ClientEnvironmentVariable} to enable native presentation assets");
            return;
        }

        Trace($"ASSET-2C retail assets -> {InstalledAssets.RootDirectory}");

        try
        {
            _net7!.LoadBaseAssetCatalog(InstalledAssets.BassetIniPath);
            Trace(
                $"ASSET-2C basset catalog -> {_net7.Presentation.BaseAssetCount} BASE-N definitions " +
                $"from {InstalledAssets.BassetIniPath}");
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            System.Security.SecurityException)
        {
            Trace($"ASSET-2C basset catalog unavailable: {ex.GetType().Name}: {ex.Message}");
        }

        EnbRetailAssetBundle retailAssets;
        try
        {
            retailAssets = EnbRetailAssetBundle.Load(InstalledAssets, _options.TextureOverrideDirectory);
            retailAssets.SelectTexturePack(_options.TexturePack??(_options.TextureOverridesEnabled?null:""));
            if(retailAssets.TextureOverrides is {} pack)Trace($"Texture overrides: {pack.DirectoryPath} (restart after changes)");
            RetailAssets = retailAssets;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            System.Security.SecurityException)
        {
            Trace($"SCENE08-R2B retail asset bundle unavailable: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        ClientBaseAssets = retailAssets.ClientBaseAssets;
        ClientBaseAssetMounts = retailAssets.ClientBaseAssetMounts;
        RetailPresentationCatalog = retailAssets.PresentationCatalog;
        RetailRenderStates = retailAssets.RenderStates;
        RetailRslResources = RetailRenderStates is null ? null : retailAssets.RslResources;
        RetailBaseAssetResolver = retailAssets.BaseAssetResolver;
        MixResources = retailAssets.MixResources;

        Trace("SCENE08-R2B retail asset bundle -> immutable presentation snapshot provenance ready");
        if (ClientBaseAssets is null)
        {
            Trace($"ASSET-2D cbasset geometry catalog unavailable -> {InstalledAssets.CbassetIniPath}");
        }
        else
        {
            Trace(
                $"ASSET-2D cbasset geometry catalog -> {ClientBaseAssets.Count} BASE-N geometry definitions " +
                $"from {InstalledAssets.CbassetIniPath}");
        }

        if (ClientBaseAssetMounts is null)
        {
            Trace($"SCENE08-R1D cbasset mount catalog unavailable -> {InstalledAssets.CbassetIniPath}");
        }
        else
        {
            Trace(
                $"SCENE08-R1D cbasset mounts -> {ClientBaseAssetMounts.MountCount} mounts across " +
                $"{ClientBaseAssetMounts.BaseAssetCount} BaseAssets from {InstalledAssets.CbassetIniPath}");
        }

        if (RetailPresentationCatalog is null)
        {
            Trace($"SCENE08-R1I asset-native presentation catalog unavailable -> {InstalledAssets.BassetIniPath}");
        }
        else
        {
            Trace(
                $"SCENE08-R1I asset-native presentation catalog -> {RetailPresentationCatalog.BaseAssetCount} BaseAssets, " +
                $"{RetailPresentationCatalog.EffectDefinitionCount} effects");
        }

        if (RetailRenderStates is null)
        {
            Trace($"SCENE08-R1J retail render-state catalog unavailable -> {InstalledAssets.RsIniPath}");
        }
        else
        {
            Trace(
                $"SCENE08-R1J rs.ini -> {RetailRenderStates.LibraryCount} RSL libraries, " +
                $"{RetailRenderStates.StateCount} states from {InstalledAssets.RsIniPath}");
            Trace("SCENE08-R1K RSL state -> installed resource graph resolver ready");
        }

        if (MixResources is null)
        {
            Trace($"ASSET-2C MIX1 art catalog unavailable -> {InstalledAssets.MixFilesDirectory}");
        }
        else
        {
            Trace(
                $"ASSET-2C MIX1 art catalog -> {MixResources.ResourceCount} named resources " +
                $"from {MixResources.ArchiveCount} archive(s)");
        }

        if (RetailBaseAssetResolver is not null)
            Trace("SCENE08-R1K BaseAsset -> retail W3D/RSL resource resolver ready");
    }

    private async Task ReturnToLoginAfterSectorEntryFailureAsync()
    {
        Net7Session? session = _net7;
        _net7 = null;

        try
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Preserve the original sector-entry failure while still making the
            // product workflow reusable. The session was already unpublished and
            // Gameplay/account authority was invalidated before this point.
            Trace($"MASTER-09 spent-session disposal warning: {ex.GetType().Name}: {ex.Message}");
        }

        _presentationSnapshots.Clear();
        Route = null;
        _automaticSectorTransitionActive = false;
        SelectedCharacter = null;
        SelectedFixedSessionId = null;
        Characters = Array.Empty<CharacterSummary>();
        SetState(ClientLifecycleState.Disconnected);
        Trace("MASTER-09 login-failure recovery -> returned to reusable login state");
    }

    public async ValueTask DisposeAsync()
    {
        _loginCredentials = null;
        if (State != ClientLifecycleState.Disconnected)
            SetState(ClientLifecycleState.Disconnecting);

        Task? pendingCaptureStop = CancelPostInWorldWireCaptureStop();
        if (_wireCapture.IsActive)
            await StopWireCaptureAsync("client-dispose").ConfigureAwait(false);
        if (pendingCaptureStop is not null)
            await pendingCaptureStop.ConfigureAwait(false);

        if (_net7 is not null)
            await _net7.DisposeAsync().ConfigureAwait(false);

        await _wireCapture.DisposeAsync().ConfigureAwait(false);

        _net7 = null;
        _presentationSnapshots.Clear();
        Route = null;
        _automaticSectorTransitionActive = false;
        SelectedCharacter = null;
        SelectedFixedSessionId = null;
        Characters = Array.Empty<CharacterSummary>();
        SetState(ClientLifecycleState.Disconnected);
    }

    private void TryStartSectorEntryCapture(CharacterSummary character, string prefix = "sector-entry")
    {
        if (_wireCapture.IsActive)
            return;

        LastWireCapturePath = null;
        string? directory = _options.WireCaptureDirectory;
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            string safeName = SanitizeCaptureFileName(character.FullName);
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string path = Path.Combine(directory, $"{prefix}-{safeName}-{stamp}.enbcap.jsonl");
            _wireCapture.Start(path);
            if(RecordedViewBootstrap is {} viewBootstrap)_wireCapture.Mark("replay-view-bootstrap-v1",viewBootstrap);
            LastWireCapturePath = Path.GetFullPath(path);
            Trace($"sector-entry wire capture -> {LastWireCapturePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace($"sector-entry wire capture unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SchedulePostInWorldWireCaptureStop()
    {
        if (!_wireCapture.IsActive)
            return;

        TimeSpan duration = _options.SectorEntryPostInWorldCaptureDuration;
        if (duration <= TimeSpan.Zero)
        {
            _ = StopWireCaptureAsync("sector-entry-inworld");
            return;
        }

        var cts = new CancellationTokenSource();
        Task task = StopWireCaptureAfterPostInWorldWindowAsync(duration, cts);
        lock (_wireCaptureStopSync)
        {
            _postInWorldWireCaptureStopCts?.Cancel();
            _postInWorldWireCaptureStopCts = cts;
            _postInWorldWireCaptureStopTask = task;
        }
        Trace($"sector-entry wire capture retaining post-InWorld window for {duration.TotalSeconds:0.###}s");
    }

    private Task? CancelPostInWorldWireCaptureStop()
    {
        lock (_wireCaptureStopSync)
        {
            _postInWorldWireCaptureStopCts?.Cancel();
            Task? task = _postInWorldWireCaptureStopTask;
            _postInWorldWireCaptureStopCts = null;
            _postInWorldWireCaptureStopTask = null;
            return task;
        }
    }

    private async Task StopWireCaptureAfterPostInWorldWindowAsync(
        TimeSpan duration,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(duration, cancellation.Token).ConfigureAwait(false);
            _wireCapture.Mark("sector-entry-post-inworld-window", $"{duration.TotalSeconds:0.###}s");
            await StopWireCaptureAsync("sector-entry-post-inworld-window").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_wireCaptureStopSync)
            {
                if (ReferenceEquals(_postInWorldWireCaptureStopCts, cancellation))
                {
                    _postInWorldWireCaptureStopCts = null;
                    _postInWorldWireCaptureStopTask = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task StopWireCaptureAsync(string reason)
    {
        if (!_wireCapture.IsActive)
            return;

        await _wireCapture.StopAsync(reason).ConfigureAwait(false);
        if (_wireCapture.LastCompletedPath is string path)
            LastWireCapturePath = path;
    }

    private static string SanitizeCaptureFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "character" : sanitized;
    }

    private static CharacterSummary MapCharacter(Net7AvatarSlot slot, int slotIndex)
    {
        return new CharacterSummary(
            Slot: slotIndex,
            AvatarId: slot.Meta.AvatarId,
            AccountId: slot.Meta.AccountId,
            FirstName: slot.Avatar.FirstName,
            LastName: slot.Avatar.LastName,
            Race: slot.Avatar.Race,
            Profession: slot.Avatar.Profession,
            Gender: slot.Avatar.Gender,
            CombatLevel: slot.Meta.CombatLevel,
            ExploreLevel: slot.Meta.ExploreLevel,
            TradeLevel: slot.Meta.TradeLevel,
            SectorId: slot.Meta.Sector,
            SectorName: slot.Meta.SectorName,
            ShardId: slot.Meta.ShardId,
            SaveCount: slot.Meta.SaveCount,
            AdminLevel: slot.Meta.AdminLevel,
            GmFlag: slot.Meta.GmFlag) { Appearance = EnbAvatarCustomizationProjection.ToScene(slot.Avatar) };
    }

    private static IReadOnlyList<CharacterSummary> MapCharacters(Net7AvatarRoster roster)
    {
        return roster.Slots.Select((slot, index) =>
        {
            CharacterSummary character = MapCharacter(slot, index);
            Net7GalaxyRoutingRecord? route = roster.RoutingRecords.LastOrDefault(candidate =>
                candidate.ShardId == slot.Meta.ShardId);
            return route is null ? character : character with
            {
                GalaxyName = route.GalaxyName,
                GalaxyCurrentLoad = route.CurrentLoad,
                GalaxyCapacity = route.Capacity,
            };
        }).ToArray();
    }

    private void StartAutomaticHandoffFailureRecovery()
    {
        lock (_automaticRecoverySync)
        {
            if (_automaticHandoffRecoveryTask is { IsCompleted: false })
                return;
            _automaticHandoffRecoveryTask = ReturnToLoginAfterAutomaticHandoffFailureAsync();
        }
    }

    private async Task ReturnToLoginAfterAutomaticHandoffFailureAsync()
    {
        await Task.Yield();
        await ReturnToLoginAfterSectorEntryFailureAsync().ConfigureAwait(false);
    }

    private void OnNet7PresentationStateDeferred(uint clientTime)
    {
        if (_net7 is not {} session) return;
        try
        {
            _presentationSnapshots.CaptureDeferredEffects(session.World, session.Gameplay.Effects,
                RetailAssets, clientTime, session.ItemTemplates);
        }
        catch (Exception ex)
        {
            Trace($"Effect source capture skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnNet7PresentationStateChanged(uint clientTime)
    {
        Net7Session? session = _net7;
        if (session is null)
            return;

        try
        {
            _presentationSnapshots.Publish(
                session.World,
                session.Gameplay.Effects,
                RetailAssets,
                clientTime,
                session.FixedSessionId == 0 ? null : session.FixedSessionId,
                session.Gameplay.PresentationMode,
                session.Gameplay,
                session.ItemTemplates,
                session.PlayerSemantics.ActiveStarbaseId);
        }
        catch (Exception ex)
        {
            // Presentation is downstream of authoritative session/world state. A
            // snapshot bug must never turn a valid Net7 packet into a protocol or
            // session failure; retain the last complete snapshot and diagnose it.
            Trace($"SCENE08-R2 presentation snapshot publication skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnNet7LoginStateChanged(Net7LoginState state)
    {
        // Can fire while an automatic JOIN is in progress.
        // The initial EnterGameAsync path has a caller awaiting its failure, but
        // an automatic handoff does not. Preserve the same product-level return
        // to a reusable login screen without treating graceful in-world logoff as
        // a failure. The handoff-owned transition flag distinguishes this path
        // from initial entry and graceful in-world logoff. Yield before disposal
        // so a recovery event raised by the watchdog cannot synchronously await
        // that same task.
        if (state == Net7LoginState.CharacterSelection
            && _automaticSectorTransitionActive
            && State is ClientLifecycleState.Redirecting
                or ClientLifecycleState.MasterHandoff
                or ClientLifecycleState.SectorJoining)
        {
            StartAutomaticHandoffFailureRecovery();
            return;
        }
        // Server handoff means the previous route is no longer active.
        // Mirror Net7Session's route invalidation immediately, then republish the
        // new route only once the automatic JOIN/Redirect/sector-entry cycle is
        // actually SectorReady.
        if (state == Net7LoginState.SectorTransition)
        {
            _automaticSectorTransitionActive = true;
            Route = null;
            // Each automatic handoff gets the same bounded capture as the first entry,
            // so a post-handoff state question can be answered from the wire.
            if (SelectedCharacter is CharacterSummary handoffCharacter)
            {
                TryStartSectorEntryCapture(handoffCharacter, "sector-handoff");
                _wireCapture.Mark("sector-handoff-start", $"slot={handoffCharacter.Slot}");
            }
        }

        if (state == Net7LoginState.SectorReady
            && _net7?.Route is Net7RouteResult current
            && SelectedCharacter is CharacterSummary selected)
        {
            Route = new SectorRouteResult(
                selected,
                current.FixedSessionId,
                current.RetainedMasterResponseDword,
                current.GameplaySectorEndPoint);
            if (_automaticSectorTransitionActive)
            {
                _wireCapture.Mark("sector-handoff-inworld", current.GameplaySectorEndPoint.ToString());
                SchedulePostInWorldWireCaptureStop();
            }
            _automaticSectorTransitionActive = false;
        }

        ClientLifecycleState? mapped = state switch
        {
            Net7LoginState.Bootstrapping => ClientLifecycleState.Bootstrapping,
            Net7LoginState.AccountConnecting => ClientLifecycleState.AccountConnecting,
            Net7LoginState.Authenticating => ClientLifecycleState.Authenticating,
            Net7LoginState.LoadingRoster => ClientLifecycleState.LoadingCharacters,
            Net7LoginState.CharacterSelection => ClientLifecycleState.CharacterSelection,
            Net7LoginState.SelectingCharacter => ClientLifecycleState.AvatarLogin,
            Net7LoginState.CharacterSelected => ClientLifecycleState.CharacterSelected,
            Net7LoginState.MasterHandoff => ClientLifecycleState.MasterHandoff,
            Net7LoginState.SectorJoining => ClientLifecycleState.SectorJoining,
            Net7LoginState.SectorReady => ClientLifecycleState.InWorld,
            Net7LoginState.SectorTransition => ClientLifecycleState.Redirecting,
            Net7LoginState.Faulted => ClientLifecycleState.Faulted,
            _ => null,
        };

        if (mapped is ClientLifecycleState clientState)
            SetState(clientState);
    }

    private void SetState(ClientLifecycleState state)
    {
        if (State == state)
            return;
        if (state is ClientLifecycleState.Disconnected or ClientLifecycleState.Disconnecting or ClientLifecycleState.Faulted or ClientLifecycleState.CharacterSelection) Autopilot.Cancel("Session ended");
        State = state;
        StateChanged?.Invoke(state);
        Trace($"state -> {state}");
    }

    private void Trace(string message)
    {
        if (message.StartsWith("Station jobs ", StringComparison.Ordinal)) RecordJobDiagnostic(message);
        Diagnostic?.Invoke(message);
    }
}
