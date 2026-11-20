using EnbNext.Client;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal static class EnbWorldClientBinding
{
    internal static void Bind<TKey>(EnbWorldController world,EnbWorldKeyboard<TKey> keyboard,Func<EnbClient?> client) where TKey:notnull
    {
        world.Autopilot = client()?.Autopilot;
        world.Cockpit.AutopilotRequested += world.SetAutopilot;
        keyboard.FireAllRequested = (ship, owner) => client()?.FireAllWeaponsAsync(ship, owner) ?? Task.FromResult(false);
        keyboard.AutofollowRequested = (ship, owner) => client()?.ToggleAutofollowAsync(ship, owner) ?? Task.FromResult(false);
        keyboard.AutoLevelRequested = (ship, owner) => client()?.ToggleAutoLevelAsync(ship, owner) ?? Task.FromResult(false);
        world.EffectPositionsPublished = positions => client()?.PublishRetainedEffectPositions(positions);
        world.NavigationMap.GalaxyMapAccessor = () => client()?.GalaxyMap;
        // Where the character is, as the server's GalaxyMap command 4 says (docked: system, sector and station).
        world.NavigationMap.Guidance.Place = () => client()?.GalaxyMapLocation is { SectorName.Length: > 0 } at ? (at.SystemName, at.SectorName) : null;
        world.DockedPlaceAccessor = () => client()?.GalaxyMapLocation is { LocationDetail.Length: > 0 } at ? (at.LocationDetail, at.SectorName, at.SystemName) : null;
        world.NavigationMap.ToggleGalaxyMapRequested = () => client()?.ToggleGalaxyMapAsync() ?? Task.FromResult(false);
        world.Options.SaveCinematicOptions = options =>
        {
            var current = client() ?? throw new InvalidOperationException("The character session ended.");
            current.SaveCinematicOptions(options);
        };
        world.Options.SaveGraphicsOptions = options => world.ApplyGraphicsOptionChange(() =>
        {
            var current = client() ?? throw new InvalidOperationException("The character session ended.");
            current.SaveGraphicsOptions(options);
        });
        world.Options.PreviewGraphicsOptions = options => world.ApplyGraphicsOptionChange(() =>
        {
            if (client() is { } current) current.PreviewGraphicsOptions(options);
            else if (options is not null) throw new InvalidOperationException("The character session ended.");
        });
        world.GateCameraCompletion = (owner, sequence, target) =>
            client()?.CompleteGateCameraAsync(owner, sequence, target) ?? Task.FromResult(false);
        world.DockCameraCompletion = (owner, revision, target, departure) =>
            client()?.CompleteDockCameraAsync(owner, revision, target, departure) ?? Task.FromResult(false);
        world.LandCameraCompletion = (owner,revision,target,phase) =>
            client()?.CompleteLandCameraAsync(owner,revision,target,phase) ?? Task.FromResult(false);
        world.TakeoffCameraCompletion = (owner,revision) =>
            client()?.CompleteTakeoffCameraAsync(owner,revision) ?? Task.FromResult(false);
        world.StationLeaveRequest = (station, version) =>
            client()?.LeaveStarbaseAsync(station, version) ?? Task.FromResult(false);
        world.StationDepartureRequest = request => client()?.BeginStarbaseDepartureAsync(request) ?? Task.FromResult(false);
        world.StationArrivalCompletion = request =>
            client()?.CompleteStarbaseArrivalAsync(request) ?? Task.FromResult(false);
        world.StationMovementRequest = request => client()?.MoveStationAvatarAsync(request) ?? Task.FromResult(false);
        world.StationRoomRequest = request => client()?.ChangeStationRoomAsync(request) ?? Task.FromResult(false);
        world.AssignSkillRequested=request=>client()?.AssignShortcutSkill(request)??false;
        world.Cockpit.IncapacitatedShortcutPressed+=()=>client()?.ReportIncapacitatedShortcutPress();
        world.AssignItemShortcutRequested=request=>client()?.AssignShortcutItem(request)??false;
        world.MoveShortcutRequested=request=>client()?.MoveShortcut(request)??false;
        world.Inventory.TrainRequested=request=>client()?.TrainInventorySkillAsync(request)??Task.FromResult(false);
        world.Inventory.ConversationRequested=request=>client()?.BeginStationConversationAsync(request)??Task.FromResult(false);
        world.Inventory.TerminalRequested=request=>client()?.StationTerminalAsync(request)??Task.FromResult(false);
        world.Inventory.StationPlayerRequested=request=>client()?.StationPlayerAsync(request)??Task.FromResult(false);
        world.Inventory.StationBrowserQuery=owner=>client()?.GetStationBrowserQuery(owner);
        world.Options.SubmitPetition=(owner,handoff,category,subject,details,players)=>client()?.SubmitPetitionAsync(owner,handoff,category,subject,details,players) ?? Task.FromResult(false);
        world.Options.ManualBrowserQuery=(character,handoff)=>client()?.GetManualBrowserQuery(character,handoff);
        world.Options.LogoffRequested=(character,handoff,destination)=>client()?.RequestLogoffAsync(character,handoff,destination)??Task.FromResult(false);
        world.Inventory.ReplyRequested=request=>client()?.ReplyStationConversationAsync(request)??Task.FromResult(false);
        world.Inventory.RecoveryRequested=request=>client()?.ReplyRecoveryAsync(request)??Task.FromResult(false);
        world.Inventory.PurchaseRequested=request=>client()?.TradeStationItemAsync(request)??Task.FromResult(false);
        world.Inventory.ClearMissionRequested=request=>client()?.ClearInventoryMissionAsync(request)??Task.FromResult(false);
        world.Inventory.ForfeitRequested=request=>client()?.ForfeitInventoryMissionAsync(request)??Task.FromResult(false);
        world.Inventory.AutoFireRequested = (request, enabled) => client()?.SetInventoryAutoFireAsync(request, enabled) ?? Task.FromResult(false);
        world.Inventory.LootItemRequested=request=>client()?.LootItemAsync(request)??Task.FromResult(false);
        world.Inventory.LootCloseRequested=(id,owner)=>client()?.CloseLootAsync(id,owner)??Task.FromResult(false);
        world.Inventory.MoveItemRequested=request=>client()?.MoveInventoryItemAsync(request)??Task.FromResult(false);
        world.Inventory.PlayerTradeRequested=request=>client()?.PlayerTradeAsync(request)??Task.FromResult(false);
        world.Inventory.GroupAttentionPublished=p=>client()?.PublishGroupAttention(p);
        world.Inventory.GroupRequested=request=>client()?.GroupAsync(request)??Task.FromResult(false);
        world.Inventory.GuildRequested=request=>client()?.GuildAsync(request)??Task.FromResult(false);
        world.Inventory.FriendsRequested=request=>client()?.FriendsAsync(request)??Task.FromResult(false);
        world.Inventory.GroupFinderRequested=request=>client()?.GroupFinderAsync(request)??Task.FromResult(false);
        world.Chat.LocalChatMessageRequested = (character, type, message) => client()?.ReportLocalChatMessage(character, type, message);
        world.Chat.LocalChatPreviewRequested = (character, type, message) => client()?.ReportLocalChatMessage(character, type, message, true);
        world.Chat.ChatColorsChanged = (character, colors) => client()?.SetChatColors(character,colors);
        world.Chat.IncomingMessageRequested = (character, id) => client()?.AcceptIncomingMessageAsync(character, id) ?? Task.FromResult(false);
        world.Inventory.ConfirmedDialogAnswered = (character, id, accepted) => client()?.AnswerConfirmedDialogAsync(character, id, accepted) ?? Task.FromResult(false);
        world.Inventory.ConfirmedDialogAutomatic = question => client()?.AnswersItself(question) == true;
        world.Chat.ChannelMonitoringRequested = (channel, enabled) => client()?.SetMonitoredChatChannelAsync(channel, enabled) ?? Task.FromResult(false);
        world.Chat.PrivateChannelRequested = (kind, channel, value) => client()?.SendPrivateChannelControlAsync(kind, channel, value) ?? Task.FromResult(false);
        world.Chat.PrivateMemberRequested = (kind, player, channel) => client()?.SendPrivateMemberControlAsync(kind, player, channel) ?? Task.FromResult(false);
        world.Chat.ChatListWindowChanged = (stream, ignore, members, channel) => client()?.SetChatListWindow(stream, ignore, members, channel);
        world.Chat.IgnorePlayerRequested = (kind, player) => client()?.SendIgnorePlayerControlAsync(kind, player) ?? Task.FromResult(false);
        world.Chat.QuickMessageSaved = (character, slot) => client()?.ReportQuickMessageSaved(character, slot);
        world.Inventory.LocalMessageRequested = (character, message) => client()?.ReportLocalInterfaceMessage(character, message);
        world.CameraMessage = (character, message) => client()?.ReportCameraMessage(character, message);
        world.Inventory.VaultSortRequested = (character, owner, preset, reverse) => client()?.SortVaultAsync(character, owner, preset, reverse) ?? Task.FromResult(false);
        world.Inventory.SortRequested = (character, owner, preset, reverse) => client()?.SortCargoAsync(character, owner, preset, reverse) ?? Task.FromResult(false);
    }
    internal static void BindShortcuts(EnbWorldController world,
        Func<EnbShortcutSkillRequest,Task<bool>>? skillActivator,
        Func<EnbShortcutSkillRequest,int,bool>? skillLevelSelector,
        Func<EnbShortcutEquipmentRequest,Task<bool>>? equipmentActivator)
    {
        world.Cockpit.SkillRequested += async request =>
        {
            if (!world.CanControlFlight || skillActivator is null) return;
            try { await skillActivator(request); }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.IO.IOException)
            { /* A retired gameplay route cancels this click; no replay or optimistic activation. */ }
        };

        world.Cockpit.EquipmentRequested += async request =>
        {
            if (!world.CanControlFlight || equipmentActivator is null) return;
            try { await equipmentActivator(request); }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.IO.IOException)
            { /* No replay into a retired gameplay route. */ }
        };

        world.Cockpit.SkillLevelRequested += (request, ability) =>
        {
            if (world.CanControlFlight) skillLevelSelector?.Invoke(request, ability);
        };
    }
}
