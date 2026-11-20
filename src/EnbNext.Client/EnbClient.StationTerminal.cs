using EnbNext.Net7;
using EnbNext.Presentation;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    private readonly SemaphoreSlim _terminalCommands = new(1, 1);
    private (Net7Session Session, EnbStationTerminalOwner Owner)? _terminalOwner;
    private EnbNext.Net7.Protocol.Net7AvatarData? _customizationAvatar;
    private long _terminalTick;
    private string? _terminalDiagnosticPath;
    private string? _terminalDiagnosticState;
    private readonly object _jobDiagnosticLock = new();
    private string? _jobDiagnosticPath;
    private int _jobDiagnosticCharacters;

    // Refinery 1D/1E, analysis 19/1A, manufacture 17/18 (INV-D65/D72).
    internal static uint TerminalUiTrigger(uint facility, bool open) => (facility switch { 0 => 0x1Du, 1 => 0x19u, _ => 0x17u }) + (open ? 0u : 1u);
    private void BeginJobDiagnostic()
    {
        lock (_jobDiagnosticLock)
        {
            _jobDiagnosticPath = null;
            if (string.IsNullOrWhiteSpace(_options.WireCaptureDirectory)) return;
            try
            {
                Directory.CreateDirectory(_options.WireCaptureDirectory);
                _jobDiagnosticPath = Path.Combine(_options.WireCaptureDirectory, $"terminal-jobs-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.jsonl");
                _jobDiagnosticCharacters = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }

    private void RecordJobDiagnostic(string message)
    {
        // Bounded passive terminal evidence, independent of the short login capture.
        // Only job packet prefixes and command outcomes; no credentials or extra traffic.
        lock (_jobDiagnosticLock)
        {
            if (_jobDiagnosticPath is null || _jobDiagnosticCharacters >= 524288) return;
            try
            {
                string line = System.Text.Json.JsonSerializer.Serialize(new { Time = DateTimeOffset.UtcNow, Message = message }) + Environment.NewLine;
                File.AppendAllText(_jobDiagnosticPath, line);
                _jobDiagnosticCharacters += line.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { _jobDiagnosticPath = null; }
        }
    }

    private void RecordTerminalDiagnostic(Net7Session session, EnbStationTerminalRequest request, bool accepted)
    {
        if(request.Owner.Type>=3 || string.IsNullOrWhiteSpace(_options.WireCaptureDirectory))return;
        try
        {
            if(request.Command==EnbTerminalCommand.Open)
            {
                Directory.CreateDirectory(_options.WireCaptureDirectory);
                _terminalDiagnosticPath=Path.Combine(_options.WireCaptureDirectory,$"terminal-state-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.jsonl");
                _terminalDiagnosticState=null;
            }
            if(_terminalDiagnosticPath is null)return;
            // Passive decoded state only. No traffic, retries, credentials, or browser URLs.
            string state=session.ReadPresentationFrame((_,_)=>
            {
                var domain=session.Gameplay.ManufacturingDomain;
                var lab=domain.CurrentLab;
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    domain.ActiveObjectId,domain.RequestedMode,domain.BrowserDepth,
                    domain.SelectedPrimaryIndex,domain.SelectedCategoryIndex,domain.SelectedSubcategoryIndex,
                    domain.LocalTechLevelFilterBitfield,
                    lab?.Mode,lab?.CurrentItemCategory,lab?.Validity,
                    lab?.PrimaryCategories,lab?.KnownFormula,lab?.PreviousAttempts,
                    Components=lab?.ManufactureComponents.Select(i=>new {i.ItemTemplateId,i.StackCount,i.IsMaterialized}),
                    States=domain.ActiveObject?.AuxData?.Root.Children.Select(p=>new {p.Path,p.State})
                });
            });
            if(request.Command!=EnbTerminalCommand.Tick || state!=_terminalDiagnosticState)
            {
                var line=System.Text.Json.JsonSerializer.Serialize(new {Time=DateTimeOffset.UtcNow,request.Command,request.Value,Accepted=accepted,State=System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(state)});
                File.AppendAllText(_terminalDiagnosticPath,line+Environment.NewLine);
                _terminalDiagnosticState=state;
            }
            if(request.Command==EnbTerminalCommand.Close)_terminalDiagnosticPath=null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _terminalDiagnosticPath=null;Trace("Terminal diagnostic unavailable: "+ex.Message); }
    }

    public string? GetStationBrowserQuery(EnbStationTerminalOwner owner)
    {
        if (_net7 is not {} session) return null;
        return session.ReadPresentationFrame((_, _) =>
            _terminalOwner == (session, owner) && owner.Type == 4 &&
            State == ClientLifecycleState.InWorld && !session.World.IsTransitioning &&
            session.World.PresentationHandoffVersion == owner.HandoffVersion &&
            session.PlayerSemantics.ActiveStarbaseId == owner.StarbaseId &&
            session.CharacterSelection is {} selected
                ? Net7StationBrowser.BuildQuery(selected.SectorJoinData) : null);
    }

    public async Task<bool> StationTerminalAsync(EnbStationTerminalRequest request)
    {
        await _terminalCommands.WaitAsync();
        try
        {
            if (_net7 is not {} session) return false;
            var o = request.Owner;
            bool current = session.ReadPresentationFrame((_, _) => ReferenceEquals(_net7, session) &&
                State == ClientLifecycleState.InWorld && session.FixedSessionId == o.Character && !session.World.IsTransitioning &&
                session.World.PresentationHandoffVersion == o.HandoffVersion && session.PlayerSemantics.ActiveStarbaseId == o.StarbaseId &&
                session.Gameplay.LoungeDefinition?.Facilities.Any(f => f.RoomType == o.Room && f.Slot == o.Slot && f.FacilityType == o.Type) == true);
            if (!current || o.Type > 6) return false;
            if (request.Command != EnbTerminalCommand.Open && _terminalOwner != (session, o)) return false;
            if (o.Type == 3)
            {
                if (request.Command == EnbTerminalCommand.Open) BeginJobDiagnostic();
                RecordJobDiagnostic($"Command {request.Command} job={request.Value} pending={session.Gameplay.StationJobRequestPending}");
            }
            bool result = await session.ReadPresentationFrame((_, _) => Execute());
            if (o.Type == 3)
            {
                RecordJobDiagnostic($"Command {request.Command} completed={result} pending={session.Gameplay.StationJobRequestPending}");
                if (request.Command == EnbTerminalCommand.Close && result)
                    lock (_jobDiagnosticLock) _jobDiagnosticPath = null;
            }
            session.ReadPresentationFrame((_, time) => { OnNet7PresentationStateChanged(time); return true; });
            RecordTerminalDiagnostic(session,request,result);
            return result;

            async Task<bool> Execute()
            {
                if (o.Type is 5 or 6)
                {
                    switch (request.Command)
                    {
                        case EnbTerminalCommand.Open:
                            session.Gameplay.ClearRecustomization();
                            _customizationAvatar = session.World.ClientAvatarId is {} avatarId
                                ? session.World.AvatarDescriptions.GetValueOrDefault(avatarId) : null;
                            if (o.Type == 6 && _customizationAvatar is null) return false;
                            if (!await session.SendStarbaseRequestAsync(0, (byte)(o.Type == 6 ? 10 : 11))) return false;
                            _terminalOwner = (session, o); return true;
                        case EnbTerminalCommand.Close:
                            // Retail Cancel/Escape closes the local interface; no Done packet.
                            _terminalOwner = null; _customizationAvatar = null;
                            session.Gameplay.ClearRecustomization(); return true;
                        case EnbTerminalCommand.Customize:
                            if (o.Type == 6 && _customizationAvatar is {} original && request.Avatar is {} edited &&
                                session.Gameplay.AvatarRecustomizationStart is {} avatarCosts)
                            {
                                // Only recustomizable fields may change. Identity, physique, mood,
                                // face mesh and equipment are retained from the opening appearance.
                                var allowed = original with { HairNum=edited.HairNum, BeardNum=edited.BeardNum,
                                    HairColor=edited.HairColor, BeardColor=edited.BeardColor, SkinColor=edited.SkinColor, EyeColor=edited.EyeColor,
                                    TattooTextureNum=edited.TattooTextureNum, TattooOffset=edited.TattooOffset,
                                    GoggleNum=edited.GoggleNum, EarNum=edited.EarNum, BodyType=edited.BodyType, PantsType=edited.PantsType,
                                    ShirtPrimaryColor=edited.ShirtPrimaryColor, ShirtSecondaryColor=edited.ShirtSecondaryColor,
                                    PantsPrimaryColor=edited.PantsPrimaryColor, PantsSecondaryColor=edited.PantsSecondaryColor,
                                    ShirtPrimaryMetal=edited.ShirtPrimaryMetal, ShirtSecondaryMetal=edited.ShirtSecondaryMetal,
                                    PantsPrimaryMetal=edited.PantsPrimaryMetal, PantsSecondaryMetal=edited.PantsSecondaryMetal };
                                int cost=EnbNext.Net7.Gameplay.Net7Recustomization.AvatarCost(original,allowed,avatarCosts.OptionDwords);
                                if (cost<=0 || LatestPresentationSnapshot?.Hud.Inventory?.Credits is not {} credits || credits<(ulong)cost) return false;
                                if (!await session.SubmitAvatarRecustomizationAsync(EnbNext.Net7.Protocol.Net7AvatarCreateCodec.EncodeRecustomizationData(allowed))) return false;
                                session.ReadPresentationFrame((_,_) => {
                                    if (ReferenceEquals(_net7,session) && session.World.PresentationHandoffVersion==o.HandoffVersion && session.World.ClientAvatarId is {} id)
                                        session.World.ReplaceAvatarAppearance(id,allowed);
                                    return true;
                                });
                            }
                            else if (o.Type == 5 && request.Ship is {} ship && session.Gameplay.ShipRecustomization is {} start)
                            {
                                ship=ship with { Profession=start.Customization.Profession, TechLevel=start.Customization.TechLevel };
                                int cost=EnbNext.Net7.Gameplay.Net7Recustomization.ShipCost(start.Customization,ship,start.AuxiliaryDwords);
                                if (cost<=0 || LatestPresentationSnapshot?.Hud.Inventory?.Credits is not {} credits || credits<(ulong)cost) return false;
                                if (!await session.SubmitShipRecustomizationAsync(EnbNext.Net7.Gameplay.Net7Recustomization.EncodeShip(ship))) return false;
                            }
                            else return false;
                            _terminalOwner=null; _customizationAvatar=null;
                            session.ReadPresentationFrame((_,_) => { session.Gameplay.ClearRecustomization(); return true; }); return true;
                        default: return request.Command == EnbTerminalCommand.Tick;
                    }
                }
                if (o.Type == 4)
                {
                    // BrowserLink action 3 is local UI; opening/closing emits no station packet.
                    if (request.Command == EnbTerminalCommand.Open) { _terminalOwner = (session, o); return true; }
                    if (request.Command == EnbTerminalCommand.Close) { _terminalOwner = null; return true; }
                    return false;
                }
                if (o.Type == 3)
                {
                    switch (request.Command)
                    {
                        case EnbTerminalCommand.Open:
                            session.Gameplay.BeginStationJobs();
                            if (!await session.SendStarbaseRequestAsync(0, 6)) return false;
                            _terminalOwner = (session, o); return true;
                        case EnbTerminalCommand.Close:
                            if (!await session.SendStarbaseRequestAsync(0, 9)) return false;
                            session.Gameplay.EndStationJobRequest();
                            _terminalOwner = null; return true;
                        case EnbTerminalCommand.JobDescription:
                            if (session.Gameplay.StationJobRequestPending) return false;
                            if (!session.Gameplay.JobList.Any(j => j.Field0 == request.Value)) return false;
                            session.Gameplay.BeginStationJobDescription();
                            return await SendJobRequest(false);
                        case EnbTerminalCommand.JobAccept:
                            return !session.Gameplay.StationJobRequestPending && session.Gameplay.JobList.Any(j => j.Field0 == request.Value) &&
                                session.Gameplay.JobDescription is {StillAvailable: not 0} description && description.JobId == request.Value &&
                                session.Gameplay.LastJobAcceptReply?.JobId != request.Value && await SendJobRequest(true);
                        default: return true;
                    }
                }
                async Task<bool> SendJobRequest(bool accept)
                {
                    session.Gameplay.BeginStationJobRequest(request.Value, accept);
                    bool sent = false;
                    try { return sent = await session.SendStarbaseRequestAsync(request.Value, (byte)(accept ? 8 : 7)); }
                    finally { if (!sent) session.Gameplay.EndStationJobRequest(); }
                }
                switch (request.Command)
                {
                    case EnbTerminalCommand.Open:
                        if (!await session.SetManufacturingModeAsync(EnbStationTerminalSnapshot.ModeFor(o.Type))) return false;
                        _terminalOwner = (session, o); _terminalTick = Environment.TickCount64;
                        // 005EC495 / 005441C0 / 005B0F14: gated 0065 after the terminal's setup.
                        _ = await session.SendUiTriggerAsync(TerminalUiTrigger(o.Type, true)); return true;
                    case EnbTerminalCommand.Close:
                        if (!await session.CloseManufacturingTerminalAsync()) return false;
                        _terminalOwner = null;
                        // 005EC662 / 0054437F / 005B1195.
                        _ = await session.SendUiTriggerAsync(TerminalUiTrigger(o.Type, false)); return true;
                    case EnbTerminalCommand.Primary: return session.SelectManufacturingPrimaryCategory((int)request.Value);
                    case EnbTerminalCommand.Category: return session.SelectManufacturingCategoryLevel((int)request.Value);
                    // 005B1C20 and 005B16C0 case 5 end in 005B4850 (ManufactureAction 0).
                    case EnbTerminalCommand.Browse:
                        return session.Gameplay.ManufacturingDomain.NavigateBrowser((int)request.Value) && await session.SendManufacturingSelectionResetAsync();
                    case EnbTerminalCommand.Recent: return await session.SendManufacturingSelectionResetAsync();
                    case EnbTerminalCommand.Move:
                        if (request.Move is not {} move || move.Source.Character != o.Character || move.DestroyConfirmed || move.Count <= 0) return false;
                        var snapshot = LatestPresentationSnapshot;
                        if (snapshot?.Objects.FirstOrDefault(s => s.IsLocalShip)?.RenderIdentity != move.Source.ShipOwner) return false;
                        var terminal = EnbStationTerminalSnapshot.Build(session.Gameplay.ManufacturingDomain, session.ItemTemplates);
                        var ship = session.World.LocalShipObjectId is uint shipId ? session.World.Objects.FindFirst(shipId)?.AuxData : null;
                        var inventory = EnbSceneInventorySnapshotBuilder.Build(ship, session.World.LocalPlayerAuxData, session.ItemTemplates, null);
                        var source = move.Source.Inventory == 1 ? inventory.Find(1, move.Source.Slot) :
                            terminal.Items.FirstOrDefault(i => i.Inventory == move.Source.Inventory && i.Slot == move.Source.Slot);
                        if (source is null || source.TemplateId != move.Source.TemplateId || source.Count < 1 || move.Count > move.SourceCount) return false;
                        if (move.Destination == 1 && move.DestinationSlot != -1 && !inventory.CargoSlotKeys.Contains(move.DestinationSlot)) return false;
                        if(move.DestinationSlot>=0)
                        {
                            var destination=move.Destination==1?inventory.Find(1,move.DestinationSlot):
                                terminal.Items.FirstOrDefault(i=>i.Inventory==move.Destination && i.Slot==move.DestinationSlot);
                            if(destination?.TemplateId!=move.DestinationTemplate)return false;
                        }
                        return await session.TransferManufacturingItemAsync(source.Inventory, (uint)source.Slot, move.Destination, unchecked((uint)move.DestinationSlot), (uint)move.Count);
                    case EnbTerminalCommand.Subcategory: return await session.SelectManufacturingSubcategoryAsync((int)request.Value);
                    case EnbTerminalCommand.Formula:
                        return o.Type == 0 ? await session.SelectRefineryItemAsync(request.Value) : await session.SelectManufactureItemAsync(request.Value);
                    case EnbTerminalCommand.Action:
                        if(request.Value is 2 or 3)
                        {
                            var actionState=EnbStationTerminalSnapshot.Build(session.Gameplay.ManufacturingDomain,session.ItemTemplates);
                            // Manufacturing Go is the retained 005B2480 widget state (the UI
                            // latch); its event 005B3A70 only needs a selected formula, which
                            // SendManufacturingActionAsync checks, and no attempt in flight.
                            if(o.Type==2 && request.Value==2){if(actionState.ActionContext!=EnbNext.Net7.Gameplay.Net7ManufacturingActionContext.Idle)return false;}
                            else if(request.Value==2?!actionState.CanAct:!actionState.CanRefineStack)return false;
                        }
                        return await session.SendManufacturingActionAsync(request.Value);
                    case EnbTerminalCommand.FilterOn: case EnbTerminalCommand.FilterOff:
                        return await session.SetManufacturingTechFilterAsync(request.Command == EnbTerminalCommand.FilterOn, request.Value);
                    case EnbTerminalCommand.Tick:
                        long now = Environment.TickCount64;
                        uint elapsed = (uint)Math.Clamp(now - _terminalTick, 0, uint.MaxValue); _terminalTick = now;
                        await session.AdvanceManufacturingActionAsync(elapsed); return true;
                    default: return false;
                }
            }
        }
        finally { _terminalCommands.Release(); }
    }
}


