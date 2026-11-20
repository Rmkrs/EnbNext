using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using Size = EnbNext.Presentation.EnbUiSize;
using EnbNext.Presentation;
using EnbNext.Rendering;
using EnbNext.Assets;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldController
{
    public Func<EnbNext.Net7.Social.Net7ChatInputRequest, Task<bool>>? ChatRequested { get; set; }
    private async Task<bool> SubmitSceneChat(EnbNext.Net7.Social.Net7ChatInputRequest request)
    {
        if(_ui.Chat.TryColorCommand(request.Text))return true;
        if(EnbRetailSelfNameCommand.Parse(request.Text) is {} showSelf)
        {_ui.Chat.SetSelfName(showSelf);return true;}
        // 0074AF82..D5 consumes this exact case-insensitive command in station.
        if (request.Text.Equals("/starbase reset", StringComparison.OrdinalIgnoreCase) &&
            _stationActive && _stationKey is {} key && _stationLoadedRoom is not null &&
            _w3dWorld.ResetStationAvatar() && _w3dWorld.StationAvatarPose is {} pose)
        {
            if (StationMovementRequest is {} send)
                return await send(new(key.Id, key.Version, pose.Position, pose.Heading, pose.Run) { RoomType = _stationRoom });
            return true;
        }
        var scene=_snapshotAccessor();var assets=_retailAssetsAccessor();
        request=request with {AvatarProducer=text=>scene is null || assets is null?null:_stationActive
            ?_w3dWorld.ProduceStationAvatarChat(text):_ui.Inventory.ProduceAvatarChat(scene,assets,text)};
        return ChatRequested is {} submit && await submit(request);
    }
    private bool _stationActive;
    private uint _stationRoom;
    private bool _stationRoomChanging;
    private uint? _stationFailedDoor;
    private EnbCurrentRetailStationInterior? _stationLoadedRoom;
    public Func<EnbStationRoomRequest,Task<bool>>? StationRoomRequest { get; set; }
    private string? _stationDepartureError;
    private (uint Id, ulong Version)? _stationKey;
    private bool _stationLeavePending;
    private (uint Definition, uint Flags)? _stationDefinitionKey;
    private string? _stationLoadError;
    private bool _stationArrivalReported;
    private bool _stationArrivalPublished;
    private bool _stationArrivalSettled;
    private string? _stationArrivalError;
    public Func<EnbStationMovementRequest, Task<bool>>? StationArrivalCompletion { get; set; }
    public Func<uint, ulong, Task<bool>>? StationLeaveRequest { get; set; }
    public Func<EnbStationMovementRequest,Task<bool>>? StationDepartureRequest { get; set; }
    internal readonly EnbRetailText _stationShipHint=new();
    internal EnbUiPoint StationHintPosition;
    private EnbRetailAssetBundle? _stationHintAssets;
    private EnbInterfaceLayoutCatalog? _stationHintLayout;
    private EnbRetailIniDocument? _stationHintColors;
    private readonly Dictionary<string,EnbRetailFontFace> _stationHintFonts=[];
    public bool StationShipHovered {get;private set;}
    public bool StationDoorHovered {get;private set;}
    public bool StationNpcHovered(Point point) => CanControlStationAt(point) && Bounds.Width>0 && Bounds.Height>0 &&
        _w3dWorld.PickStationNpc(point.X/Bounds.Width,point.Y/Bounds.Height) is not null;
    public bool StationPlayerHovered(Point point)=>CanControlStationAt(point) && Bounds.Width>0 && Bounds.Height>0 &&
        _w3dWorld.PickStationPlayer(point.X/Bounds.Width,point.Y/Bounds.Height) is not null;
    public bool ActivateStationPlayer(Point point)
    {
        if(!CanControlStationAt(point) || _ui.Inventory.StationPlayerMenuOpen || _stationKey is not {} key || Bounds.Width<=0 || Bounds.Height<=0 ||
            _w3dWorld.PickStationPlayer(point.X/Bounds.Width,point.Y/Bounds.Height) is not {Busy:false} peer)return false;
        return _ui.Inventory.OpenStationPlayer(key.Id,key.Version,_stationRoom,peer.Id,point);
    }
    public bool ActivateStationNpc(Point point)
    {
        if(!CanControlStationAt(point) || _stationKey is not {} key || Bounds.Width<=0 || Bounds.Height<=0 ||
            _w3dWorld.PickStationNpc(point.X/Bounds.Width,point.Y/Bounds.Height) is not {} npc)return false;
        _w3dWorld.QueueInteractionSound("ButtonClick02.wav");
        EndStationWalk();StopStationMovement();_= _ui.Inventory.OpenStationNpc(key.Id,key.Version,_stationRoom,npc);return true;
    }
    public bool StationTerminalHovered(Point point) => CanControlStationAt(point) && Bounds.Width>0 && Bounds.Height>0 &&
        _w3dWorld.PickStationTerminal(point.X/Bounds.Width,point.Y/Bounds.Height) is not null;
    private EnbStationInteractionRequest? _stationConversationPresentationOwner;
    private async Task<bool> MoveToStationNpc(EnbStationInteractionRequest owner)
    {
        await SynchronizeStationConversation(owner);
        while (!_w3dWorld.StationConversationCameraReady(owner))
        {
            await Task.Delay(16);
            if (_stopped || !_stationActive || _stationKey != (owner.StarbaseId, owner.HandoffVersion) ||
                _stationRoom != owner.Room || _ui.Inventory.ConversationCameraOwner != owner || _w3dWorld.StationRenderFailed) return false;
        }
        return true;
    }
    private Task SynchronizeStationConversation(EnbStationInteractionRequest? owner)
    {
        _w3dWorld.StationConversationPreviousTree=_ui.Inventory.ConversationPreviousTree;
        _w3dWorld.SetStationConversation(owner);
        if (_stationConversationPresentationOwner == owner) return Task.CompletedTask;
        var previous=_stationConversationPresentationOwner;
        _stationConversationPresentationOwner=owner;
        var identity=owner??previous;
        if (identity?.StarbaseId is not uint station || _stationKey != (station, identity.HandoffVersion) ||
            _stationRoom != identity.Room || _w3dWorld.StationAvatarPose is not {} pose) return Task.CompletedTask;
        _w3dWorld.TakeStationMovementUpdate();
        return SendStationMovement(new(station,identity.HandoffVersion,pose.Position,pose.Heading,pose.Run)
            {RoomType=identity.Room,Interaction=owner is not null?EnbStationInteractionPose.Npc:EnbStationInteractionPose.Normal});
    }
    public bool ActivateStationTerminal(Point point)
    {
        if(!CanControlStationAt(point) || _stationKey is not {} key || Bounds.Width<=0 || Bounds.Height<=0 ||
            _w3dWorld.PickStationTerminal(point.X/Bounds.Width,point.Y/Bounds.Height) is not {} terminal)return false;
        _w3dWorld.QueueInteractionSound("ButtonClick02.wav");
        EndStationWalk();StopStationMovement();_= _ui.Inventory.OpenStationTerminal(key.Id,key.Version,_stationRoom,terminal);return true;
    }
    private async Task<bool> MoveToStationTerminal(EnbStationTerminalOwner owner)
    {
        await SynchronizeStationTerminal(owner);
        while (!_w3dWorld.StationTerminalCameraReady(owner))
        {
            await Task.Delay(16);
            if (_stopped || !_stationActive || _stationKey != (owner.StarbaseId, owner.HandoffVersion) ||
                _stationRoom != owner.Room || _ui.Inventory.TerminalCameraOwner != owner ||
                _w3dWorld.StationRenderFailed) return false;
        }
        return true;
    }
    private EnbStationTerminalOwner? _stationTerminalPresentationOwner;
    private Task SynchronizeStationTerminal(EnbStationTerminalOwner? owner)
    {
        if(owner is not null)_w3dWorld.StationTerminalCompact=_ui.Inventory.TerminalCompact;
        _w3dWorld.SetStationTerminal(owner);
        if (_stationTerminalPresentationOwner == owner) return Task.CompletedTask;
        var previous = _stationTerminalPresentationOwner;
        _stationTerminalPresentationOwner = owner;
        var identity = owner ?? previous;
        if (identity is null || _stationKey != (identity.StarbaseId, identity.HandoffVersion) ||
            _stationRoom != identity.Room || _w3dWorld.StationAvatarPose is not {} pose) return Task.CompletedTask;
        // Selection faces the avatar before publishing16. Closing publishes0
        // before the return camera completes (00638DA0). Retire an older walk
        // report so it cannot immediately overwrite the interaction state.
        _w3dWorld.TakeStationMovementUpdate();
        return SendStationMovement(new(identity.StarbaseId, identity.HandoffVersion, pose.Position, pose.Heading, pose.Run)
            { RoomType = identity.Room, Interaction = owner is not null ? EnbStationInteractionPose.Terminal : EnbStationInteractionPose.Normal });
    }
    // 00639630 keeps station interaction active behind chat history; the
    // pane rectangle itself must not suppress ship/door/NPC picking.
    // A map window over the point owns it, as in flight (CanControlFlight): station picking and Next free movement
    // must not take a click meant for the galaxy map's controls.
    public bool CanControlStationAt(Point point) => CanControlStation && !_ui.Inventory.StationPlayerMenuOpen && !_ui.Chat.ContainsControl(point) && !_ui.Options.BlocksFlight(point) && !_ui.Inventory.BlocksFlight(point) && _ui.MapAt(point) is null;
    public bool StationRoomTransitioning => _stationRoomChanging || (_stationActive && _stationLoadedRoom is {PlayDocking:false} &&
        (!_w3dWorld.StationFrameReady || _awaitingWorld));
    public void UpdateStationShipHover(Point? point)
    {
        try
        {
        StationShipHovered=point is {} p && CanControlStationAt(p) &&
            Bounds.Width>0 && Bounds.Height>0 && _w3dWorld.PickStationShip(p.X/Bounds.Width,p.Y/Bounds.Height);
        string? hint=StationShipHovered?"Exit Starbase":point is {} doorPoint && CanControlStationAt(doorPoint) && Bounds.Width>0 && Bounds.Height>0
            ?_w3dWorld.PickStationDoor(doorPoint.X/Bounds.Width,doorPoint.Y/Bounds.Height):null;
        StationDoorHovered=hint is not null && !StationShipHovered;
        if(hint is null && point is {} terminalPoint && CanControlStationAt(terminalPoint) && Bounds.Width>0 && Bounds.Height>0 &&
            _w3dWorld.PickStationTerminal(terminalPoint.X/Bounds.Width,terminalPoint.Y/Bounds.Height) is {} facility)
            //639ABC indexes the current binary's B90BF8 display-name table.
            hint=facility.Type switch {0=>"Refining",1=>"Analyze",2=>"Manufacturing",3=>"Jobs",4=>"Intergalactic Net",
                5=>"Customize Ship",6=>"Customize Avatar",7=>"Training",_=>null};
        if(hint is null && point is {} playerPoint && CanControlStationAt(playerPoint) && Bounds.Width>0 && Bounds.Height>0 &&
            _w3dWorld.PickStationPlayer(playerPoint.X/Bounds.Width,playerPoint.Y/Bounds.Height) is {Busy:true})hint="Busy";
        if(hint is null && point is {} npcPoint && CanControlStationAt(npcPoint) && Bounds.Width>0 && Bounds.Height>0 &&
            _w3dWorld.PickStationNpc(npcPoint.X/Bounds.Width,npcPoint.Y/Bounds.Height) is {Role:7,Variant:0})hint="Black Market";
        _stationShipHint.IsVisible=hint is not null;
        if(!_stationShipHint.IsVisible || point is not {} at || _retailAssetsAccessor() is not {} assets)return;
        if(!ReferenceEquals(_stationHintAssets,assets))
        {
            _stationHintAssets=assets; _stationHintFonts.Clear();
            _stationHintLayout=EnbInterfaceLayoutCatalog.Load(System.IO.Path.Combine(assets.Paths.ClientIniDirectory,"fa_sbase.ini"));
            _stationHintColors=EnbRetailIniDocument.Load(System.IO.Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
        }
        double scale=_host.Scale;
        if(_stationHintLayout?.TryResolve("MouseText_SBASE",out var definition)!=true ||
            !EnbRetailFontFactory.TryResolveDefinition(assets,definition.Properties["FontType"],
                (int)(Bounds.Width*scale),(int)(Bounds.Height*scale),out var fontDefinition) || fontDefinition.Resource is null)return;
        if(!_stationHintFonts.TryGetValue(fontDefinition.Resource,out var font))
        {
            if(!EnbRetailFontFactory.TryCreate(assets,fontDefinition,out font))return;
            _stationHintFonts.Add(fontDefinition.Resource,font);
        }
        var tint=new EnbRgb(1,1,0);
        foreach(string section in _stationHintColors!.SectionNames)
            if(_stationHintColors.TryGetSection(section,out var values) && values.GetValueOrDefault("ColorID")==definition.Properties["Color"] &&
                values.GetValueOrDefault("RGB")?.Split(',') is {Length:3} rgb)
            { tint=new(float.Parse(rgb[0],System.Globalization.CultureInfo.InvariantCulture)/255,
                float.Parse(rgb[1],System.Globalization.CultureInfo.InvariantCulture)/255,
                float.Parse(rgb[2],System.Globalization.CultureInfo.InvariantCulture)/255); break; }
        _stationShipHint.SetText(hint!,font,tint);
        _stationShipHint.SetDisplayScale(1,1);
        StationHintPosition=new(Math.Clamp(at.X-_stationShipHint.Width/2,0,Math.Max(0,Bounds.Width-_stationShipHint.Width)),
            Math.Max(0,at.Y-_stationShipHint.Height-4/scale));
        }
        finally { _host.PresentationChanged(this); }
    }
    public void ActivateStationShip()
    { if(StationShipHovered && CanControlStation) _=DepartStationAsync(); }
    /// <summary>An alt follows its leader's launch through the same departure as a
    /// click on the hangar ship, when station control is currently possible.</summary>
    public bool RequestStationDeparture()
    {
        if(!CanControlStation || _stationRoom!=0)return false;
        _=DepartStationAsync();return true;
    }

    private async Task DepartStationAsync()
    {
        if (_stationLeavePending || _stationKey is not { } key) return;
        _stationLeavePending = true;
        _stationDepartureError = null;
        try
        {
            if(StationDepartureRequest is {} start && _w3dWorld.StationAvatarPose is {} pose)
            {
                if(!await start(new(key.Id,key.Version,pose.Position,pose.Heading,pose.Run)))
                    throw new InvalidOperationException("Departure notification was not sent.");
                if(_stationKey!=key)return;
                _w3dWorld.BeginStationDeparture();
                // Retail 00638736 routes this through LoadAudioResource, outside
                // the station scene lifetime. Keep the full clip across loading.
                if(_stationLoadedRoom is {} departingRoom)
                    _hudGpu.QueueInteractionSound(departingRoom.UndockSound);

                while(!_w3dWorld.StationDepartureComplete)
                {
                    await Task.Delay(16);
                    if(_stationKey!=key || _stopped)return;
                    // A station presentation failure is contained to the presentation:
                    // the undock animation cannot finish, so stop waiting for it and
                    // still send the leave request instead of trapping the player.
                    if(_w3dWorld.StationRenderFailed)
                        break;
                }
                // Rendering completion precedes the host's final present.
                // Keep the empty hangar visible through that commit before loading.
                await _host.WaitForPresentationAsync();
                if(_stationKey!=key || _stopped)return;
            }
            bool sent = StationLeaveRequest is { } leave && await leave(key.Id, key.Version);
            if (_stationKey != key) return;
            _stationLeavePending = sent;
            _stationDepartureError = sent ? null : "Unable to undock. Please try again.";
        }
        catch (Exception ex)
        {
            if (_stationKey != key) return;
            _stationLeavePending = false;
            _stationDepartureError = "Unable to undock: " + ex.Message;
        }
    }

    /// <summary>A text box has the keyboard (chat, a search, a name or log entry): keys type, they do not walk or fly.</summary>
    public bool TextEntryActive => Inventory.FriendsEditing || Inventory.ModernSearchEditing || Inventory.ModernCommandSearchEditing ||
        Inventory.ModernLocalSearchEditing || Inventory.ModernItemsSearchEditing || Inventory.ModernJobSearchEditing || Inventory.ModernVaultSearchEditing ||
        Inventory.ModernLogEditing || NavigationMap.ModernFindEditing || GalaxyMap.ModernFindEditing || Inventory.GuildEditing ||
        Inventory.CustomizationNameEditing || Options.InformationEditing || Chat.Editing;
    public bool CanControlStation => !_ui.Layout.Editing && !_host.IssuePanelOpen && _stationArrivalPublished && !_stationRoomChanging && !_stopped && !_awaitingWorld && _stationActive && _w3dWorld.StationFrameReady && _w3dWorld.StationArrivalComplete && !_w3dWorld.StationTerminalCameraMoving && !_w3dWorld.StationConversationCameraMoving && !_stationLeavePending && !_ui.Options.SuspendsGameplayMovement && !_ui.Inventory.BlocksStationMovement && !_ui.Chat.Editing;
    public Func<EnbStationMovementRequest,Task<bool>>? StationMovementRequest { get; set; }
    private bool _stationMovementSending;
    public void SetStationMovement(int thrust,float turn,bool run) {if(AcceptStationInput(thrust,turn))_w3dWorld.SetStationMovement(thrust,turn,run);}
    public void SetStationThrust(int thrust,bool run) {if(AcceptStationInput(thrust,0))_w3dWorld.SetStationThrust(thrust,run);}
    /// <summary>Next option: free station movement (keys move the way the camera faces; see EnbStationMovement.Free).</summary>
    public bool StationFreeMovement { get; set; }
    /// <summary>Docked: the station, its sector and its system as the server's GalaxyMap command 4 names them.</summary>
    internal Func<(string Station, string Sector, string System)?>? DockedPlaceAccessor { get; set; }
    /// <summary>Next option: free station movement's mouse turn, as a share of the standard turn (1 = standard).</summary>
    public float StationLookSpeed { get; set; } = 1;
    /// <summary>Next free movement: the mouse turns the view (yaw, pitch in radians); with the right button the avatar turns along.</summary>
    public void TurnStationFree(float yaw,float pitch,bool turnAvatar) {if(CanControlStation)_w3dWorld.TurnStationFree(yaw,pitch,turnAvatar);}
    public void SetStationFreeMovement(System.Numerics.Vector2 wish,float turn,bool run)
    {if(AcceptStationInput(wish==default?0:1,0))_w3dWorld.SetStationFreeMovement(wish,turn,run);} // Turning the view alone leaves a walk going.
    public void StopStationMovement() => _w3dWorld.StopStationMovement();
    public void SetStationLook(float look) => _w3dWorld.SetStationLook(look);
    public void SetStationCameraTurn(float turn) => _w3dWorld.SetStationCameraTurn(turn);
    private async Task SendStationMovement(EnbStationMovementRequest request)
    {
        _stationMovementSending=true;
        try { if(StationMovementRequest is {} send) await send(request); }
        catch(Exception ex) { if(_stationKey==(request.StarbaseId,request.HandoffVersion)) _stationArrivalError="Movement: "+ex.Message; }
        finally { _stationMovementSending=false; }
    }

    private void ShowStation(IEnbSceneFrame snapshot)
    {
        _ui.Cockpit.PauseBuffFrame();
        GutterVisible = false;
        var key = (snapshot.ActiveStarbaseId!.Value, snapshot.LocationTransition?.Version ?? 0);
        if (_stationKey != key)
        {
            // Clear the previous sector's GPU/HUD state and suspend flight input.
            ShowEmpty("Station entry complete.");
            _stationKey = key;
            _w3dWorld.StationThirdPersonMode=_retailAssetsAccessor()?.ResolvePlayerOptions(_fixedSessionIdAccessor()).Options.StarbaseThirdPersonMode??true;
            ClearStationPreloads();
            _stationRoom=0; _stationRoomChanging=false; _stationLoadedRoom=null; _stationFailedDoor=null;
            _stationDefinitionKey = null;
            _w3dWorld.SetStationInterior(null);

            _stationLeavePending = false;
            _stationArrivalReported = false;
            _stationArrivalPublished = false;
            _stationArrivalSettled = false;
            _stationArrivalError = null;
            _stationDepartureError = null;
        }
        _ui.Scanner.IsVisible = false;
        if (snapshot.StationDefinition is { } definition && definition.Rooms.FirstOrDefault(r => r.RoomType == _stationRoom) is { } room)
        {
            var definitionKey = (definition.DefinitionValue, room.Flags);
            if (_stationDefinitionKey != definitionKey)
            {
                _stationDefinitionKey = definitionKey;
                try
                {
                    _stationLoadedRoom=EnbCurrentRetailStationInterior.Load(_retailAssetsAccessor()!, definition,_stationRoom);
                    _w3dWorld.SetStationInterior(_stationLoadedRoom);
                    _stationLoadError = null;
                }
                catch (Exception ex)
                {
                    _w3dWorld.SetStationInterior(null);
                    _stationLoadError = "Interior unavailable: " + ex.Message;
                }
            }
        }
        else _stationLoadError = "Waiting for station definitionâ€¦";
        if(_w3dWorld.StationFrameReady && snapshot.StationDefinition is {} preloadDefinition && _stationLoadedRoom is not null)PrepareStationRooms(preloadDefinition);
        // Network entry and decoded assets precede the first GPU frame. Keep
        // the loading artwork over the renderer until this station has drawn.
        bool ready=_w3dWorld.StationFrameReady;
        Autopilot?.Cancel("Entered station");
        _awaitingWorld=!ready;
        LoadingVisible=!ready && !_presentationAssetFailed;
        HudVisible=true;
        double scaling=_host.Scale;
        _ui.Options.SuppressUtilityHud=_ui.Inventory.TerminalOwner?.Type is 5 or 6;
        _ui.Options.Update(snapshot,_retailAssetsAccessor(),Bounds.Size,scaling,ready);
        // Next Modern UI Local: inside a station it lists the current room.
        _ui.Inventory.ModernLocalFrame=null; _ui.Inventory.ModernLocalStation=_w3dWorld.StationRoster; UpdateStationMap(snapshot.StationDefinition);
        // The station map names where it is: the server's GalaxyMap command 4 (station, sector, system), else the last
        // handoff's names as retail's sector map shows them docked (007C8E50: starbase, sector, system).
        _ui.Inventory.ModernStationMapPlace=DockedPlaceAccessor?.Invoke() ?? (snapshot.LocationTransition is {DestinationSectorName.Length:>0} place ? (place.DestinationSectorName,place.SourceSectorName,place.SourceSystemName) : null);
        _ui.Inventory.Update(snapshot,_retailAssetsAccessor(),Bounds.Size,scaling,ready);
        _ui.Chat.Update(snapshot,_retailAssetsAccessor(),Bounds.Size,scaling,ready);
        _w3dWorld.StationChatColors = _ui.Chat.SpeakerColors;
        _w3dWorld.UpdateStationFrame(snapshot);
        _ui.Map.Update(snapshot,_retailAssetsAccessor(),Bounds.Size,scaling,ready);
        _ui.UpdateModernStation(snapshot,_retailAssetsAccessor());
        if(_ui.GalaxyMap.Modern)_ui.GalaxyMap.Update(snapshot,_retailAssetsAccessor(),Bounds.Size,scaling,ready);
        _hudGpu.SetMapPreparation(_ui.Map.Preparation);
        _ = SynchronizeStationConversation(_ui.Inventory.ConversationCameraOwner);
        _w3dWorld.StationDialogueSpeechPage=_ui.Inventory.DialogueSpeechPage;
        _w3dWorld.StationLabelInteractionOpen=_ui.Inventory.StationLabelInteractionOpen;
        _w3dWorld.StationLabelServiceOpen=_ui.Inventory.StationLabelServiceOpen;
        _w3dWorld.StationShowSelfName=_ui.Chat.ShowSelfName;
        _ = SynchronizeStationTerminal(_ui.Inventory.TerminalCameraOwner);
        if(ready) _hudGpu.SetLoadingPresentation(null);
        else UpdateLoadingScreen();
        UpdateStationFollow(snapshot.StationDefinition);
        UpdateStationWalk(snapshot.StationDefinition);
        if(_stationArrivalPublished && !_stationRoomChanging && !_stationMovementSending && !_stationLeavePending && _w3dWorld.TakeStationMovementUpdate() is {} movement)
            _=SendStationMovement(new(key.Item1,key.Item2,movement.Position,movement.Heading,movement.Run) {RoomType=_stationRoom});
        if(CanControlStation && _stationLoadedRoom is {} loaded && snapshot.StationDefinition is {} station && _w3dWorld.StationAvatarPose is {} pose)
        {
            if(EnbStationRooms.FindExit(loaded,station,pose.Position) is {} exit)
            { if(_stationFailedDoor!=exit.Room)_=ChangeStationRoom(snapshot,exit.Room,exit.Offset,pose.Heading); }
            else _stationFailedDoor=null;
        }
        if (!_stationArrivalReported && _w3dWorld.StationArrivalComplete &&
            _w3dWorld.StationAvatarPose is { } arrivalPose && StationArrivalCompletion is { } complete)
        {
            _stationArrivalReported = true;
            _ = CompleteStationArrival(key, new(key.Item1, key.Item2, arrivalPose.Position, arrivalPose.Heading, arrivalPose.Run), complete);
        }
        // 0063FF80: once the arrival teleport state ends and no starbase interface is active
        // (0069CBE0), retail clears the 0x40 arrival flag and republishes the pose. The server
        // keeps the last avatar change, so without this a standing avatar beams in again for
        // every player who later enters the room.
        if (_stationArrivalPublished && !_stationArrivalSettled && _w3dWorld.StationArrivalBeamComplete &&
            !_stationRoomChanging && !_stationMovementSending && !_stationLeavePending &&
            _ui.Inventory.TerminalCameraOwner is null && _ui.Inventory.ConversationCameraOwner is null &&
            _w3dWorld.StationAvatarPose is { } settledPose)
        {
            _stationArrivalSettled = true;
            _ = SendStationMovement(new(key.Item1, key.Item2, settledPose.Position, settledPose.Heading, settledPose.Run) { RoomType = _stationRoom });
        }
        _stationActive = true;
        string interiorStatus = _stationLoadError ?? _stationDepartureError ?? _stationArrivalError ?? _w3dWorld.StationRenderStatus;
        Volatile.Write(ref _diagnosticsText, $"LIVE WORLD VIEW · Station\nStation entry complete · StarbaseID 0x{key.Item1:X8} · handoff {key.Item2}\n{interiorStatus} · undock {(_stationLeavePending ? "requested" : "ready")}");
    }

    private async Task ChangeStationRoom(IEnbSceneFrame snapshot,uint destination,System.Numerics.Vector3 offset,float heading)
    {
        if(_stationRoomChanging || _stationKey is not {} key)return;
        var stationDefinition=snapshot.StationDefinition!;
        _stationRoomChanging=true; _w3dWorld.StationMovementPaused=true;
        try
        {
            // Load and validate the destination before committing the room-change message.
            var next=await _stationRoomLoads[destination];
            if(_stationKey!=key)return;
            string pivot=EnbStationRooms.EntryPivot(destination,_stationRoom);
            var spawn=System.Numerics.Matrix4x4.CreateRotationZ(heading)*System.Numerics.Matrix4x4.CreateTranslation(next.PivotTransform(pivot).Translation+offset);
            next=next with {Arrival=null,PlayDocking=false,SpawnPivot=pivot,AvatarSpawn=spawn};
            if(StationRoomRequest is not {} send || !await send(new(key.Id,key.Version,_stationRoom,destination)))
                throw new InvalidOperationException("Room change was not sent.");
            if(_stationKey!=key)return;
            _stationRoom=destination; _stationLoadedRoom=next;
            _stationDefinitionKey=(stationDefinition.DefinitionValue,stationDefinition.Rooms.Single(r=>r.RoomType==destination).Flags);
            _w3dWorld.SetStationInterior(next); _stationLoadError=null;
        }
        catch(Exception ex){if(_stationKey==key){_stationLoadError="Room change: "+ex.Message;_stationFailedDoor=destination;}}
        finally {if(_stationKey==key){_stationRoomChanging=false;_w3dWorld.StationMovementPaused=false;}}
    }

    private async Task CompleteStationArrival((uint Id, ulong Version) key, EnbStationMovementRequest request,
        Func<EnbStationMovementRequest, Task<bool>> complete)
    {
        try
        {
            bool sent = await complete(request);
            if (_stationKey == key) _stationArrivalPublished = sent;
            if (_stationKey == key && !sent) _stationArrivalError = "Room entry notification was not sent.";
        }
        catch (Exception ex)
        {
            if (_stationKey == key) _stationArrivalError = "Room entry: " + ex.Message;
        }
    }
}

