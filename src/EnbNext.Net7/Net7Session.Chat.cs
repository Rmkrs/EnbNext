using EnbNext.Net7.Social;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7;

public sealed partial class Net7Session
{
    private System.Collections.Immutable.ImmutableArray<System.Numerics.Vector3> _chatColors=[];
    public void SetChatColors(uint character,System.Collections.Immutable.ImmutableArray<System.Numerics.Vector3> colors)
    {
        if(colors.IsDefault || colors.Length!=24)throw new ArgumentException("Expected the complete chat palette.",nameof(colors));
        lock(_worldStateSync)if(FixedSessionId==character)_chatColors=colors;
    }
    /// <summary>A mission slot announced as new (retail's "Mission_New" branch); the first batch after entry is silent.</summary>
    public event Action<int>? NewMissionStarted;
    public void AdvanceLocalMissionPresentation()
    {
        IReadOnlyList<Net7MissionNotification> notifications;
        lock (_worldStateSync)
        {
            if (World.IsTransitioning) return;
            notifications = PlayerSemantics.ConsumeMissionNotifications(0);
            if (notifications.Count == 0) return;
            foreach (var notification in notifications)
            {
                foreach (var line in notification.Lines) Gameplay.Social.ApplyScreenLine(line);
                if (notification.Sound is { } sound) Gameplay.Social.ApplyInterfaceSound(sound);
            }
            NotifyPresentationStateChanged();
        }
        foreach (var notification in notifications)
            if (notification.Kind == Net7MissionNotificationKind.New) NewMissionStarted?.Invoke(notification.SlotIndex);
    }

    public void SetChatListWindow(Guid stream, bool ignore, bool members, string channel)
        => MutateWorldAndPublish(() => Gameplay.Social.SetChatListWindow(stream, ignore, members, channel));
    public void SetFriendsWindow(bool active) => MutateWorldAndPublish(() => Gameplay.Social.SetFriendsWindow(active));
    public void ReportCameraMessage(uint character, string message)
    {
        // HandleClientHotkey 007436A0: local type 23, no outgoing chat.
        MutateWorldAndPublish(() =>
        {
            if (FixedSessionId == character) Gameplay.Social.ApplyBaseMessage(new(23, message));
        });
    }
    public void ReportScreenshotMessage(uint character, bool complete)
    {
        // 007436A0: local type 23, never an outgoing chat request.
        MutateWorldAndPublish(() =>
        {
            if (FixedSessionId == character)
                Gameplay.Social.ApplyBaseMessage(new(23, complete ? "*SNAP* Done." : "Taking Screenshot..."));
        });
    }
    public void ReportLocalInterfaceMessage(uint character, string message)
    {
        MutateWorldAndPublish(() =>
        {
            if (FixedSessionId == character) Gameplay.Social.ApplyBaseMessage(new(0, message));
        });
    }
    public void ReportLocalChatMessage(uint character, byte type, string message, bool suppressPrefix = false)
    {
        if (type >= 24) throw new ArgumentOutOfRangeException(nameof(type));
        MutateWorldAndPublish(() =>
        {
            if (FixedSessionId == character) Gameplay.Social.ApplyBaseMessage(new(type, message), suppressPrefix);
        });
    }
    public void ReportQuickMessageSaved(uint character, int slot)
    {
        // UI persistence result, never an outgoing chat request.
        MutateWorldAndPublish(() =>
        {
            if (FixedSessionId == character)
                Gameplay.Social.ReportQuickMessageSaved(slot, "chat.ini");
        });
    }
    public Task<bool> SendGroupSymbolAsync(string asset, bool contextSymbol, CancellationToken cancellationToken=default)
    {
        // 00596E20: local avatar Owner, SClient+112C, literal symbol asset from symbolchatdef.ini.
        var schema=World.Objects.FindFirst(RequireLocalShipObjectId())?.AuxData;
        var owner=schema?.Properties.FirstOrDefault(p=>p.Path=="ShipAuxData.Owner")?.Value as EnbNext.Net7.World.Net7AuxDataString;
        if(owner is null)return Task.FromResult(false);
        var plan=Net7SocialChatPolicy.PlanGroupDialogSignal(new(RequireLocalAvatarObjectId(),Net7SelectedChatChannel.Group,false,true),
            contextSymbol,owner.Text,asset);
        return TrySendGameplayAsync(plan.Opcode,plan.Payload,cancellationToken);
    }
    public async Task<bool> InitializeChatAsync(IEnumerable<string> monitoredChannels, bool restrictedStatus,
        CancellationToken cancellationToken = default)
    {
        // Retail 0056B700: join enabled named channels, then synchronize privacy.
        foreach (string channel in monitoredChannels)
            if (!await SendChatControlRequestAsync(Net7ClientChatRequestKind.EnterChannel,
                channel: channel, value: " ", cancellationToken: cancellationToken).ConfigureAwait(false)) return false;
        return await SetPresenceVisibilityAsync(restrictedStatus, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>00747510 disband/remove: the action with self as sender and target; handled whatever the send returns.</summary>
    private async Task<bool> GroupSlashSelfActionAsync(Net7PlayerAction action, CancellationToken cancellationToken)
    {
        await SendSelfGroupActionAsync(action, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>00747510 invite/kick: the selected target, or the local notice when there is none.</summary>
    private async Task<bool> GroupSlashTargetActionAsync(Net7PlayerAction action, CancellationToken cancellationToken)
    {
        Net7GroupRequestPlan plan;
        lock (_worldStateSync)
            plan = Net7SocialGroupPolicy.PlanTargetAction(RequireLocalAvatarObjectId(), Gameplay.LocalSelectedTargetId, action);
        if (!plan.ShouldSend && !string.IsNullOrEmpty(plan.LocalError))
        {
            string error = plan.LocalError;
            MutateWorldAndPublish(() => Gameplay.Social.ApplyBaseMessage(new(17, error)));
            return true;
        }
        await SendGroupRequestPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SubmitChatAsync(Net7ChatInputRequest input, CancellationToken cancellationToken = default)
    {
        // 00747510 HandleGroupSlashCommand: each exact command is handled (the editor retires) even
        // when nothing is sent. disband is always 0D and remove always 0E, self/self, with no
        // leadership test (GROUP-D15/D16); a missing target is a local type-17 notice (GROUP-D17).
        switch (input.Text.Trim())
        {
            case "/group invite": return await GroupSlashTargetActionAsync(Net7PlayerAction.GroupInvite, cancellationToken).ConfigureAwait(false);
            case "/group accept": return await AcceptGroupInviteAsync(cancellationToken).ConfigureAwait(false);
            case "/group refuse": return await RefuseGroupInviteAsync(cancellationToken).ConfigureAwait(false);
            case "/group disband": return await GroupSlashSelfActionAsync(Net7PlayerAction.GroupDisband, cancellationToken).ConfigureAwait(false);
            case "/group remove": return await GroupSlashSelfActionAsync(Net7PlayerAction.GroupRemoveOrLeaveSelf, cancellationToken).ConfigureAwait(false);
            case "/leavegroup": return await LeaveGroupAsync(cancellationToken).ConfigureAwait(false);
            case "/group kick": return await GroupSlashTargetActionAsync(Net7PlayerAction.GroupKick, cancellationToken).ConfigureAwait(false);
            case "/group help":
                // 0074785E..0074787D: two type-6 lines (00B98CE8, then 00B98C9C), no packet.
                MutateWorldAndPublish(() =>
                {
                    Gameplay.Social.ApplyBaseMessage(new(6, "Usage: /group [invite | accept | refuse | disband | kick | help]"));
                    Gameplay.Social.ApplyBaseMessage(new(6, "For commands that need an object to act on, the target is used."));
                });
                return true;
        }
        Net7ChatSubmission plan;
        lock (_worldStateSync)
        {
            var selected = input.Destination switch
            {
                "Broadcast" => Net7SelectedChatChannel.Broadcast,
                "Guild" => Net7SelectedChatChannel.Guild,
                "Group" => Net7SelectedChatChannel.Group,
                _ => Net7SelectedChatChannel.Local,
            };
            plan = Net7ChatInput.Plan(input, new(RequireLocalAvatarObjectId(), selected,
                PlayerSemantics.GetGuildMembership().IsInGuild, PlayerSemantics.GetGroupState().IsInGroup),
                RequireStartSessionValue(), Gameplay.Social.ReplyIdentity, PlayerSemantics.IsAvatarViewActive,
                RequireLocalAvatarObjectId());
            if (plan.LocalError is { } error)
            {
                Gameplay.Social.ApplyBaseMessage(new(Net7ChatInput.LocalErrorType(error), error));
                NotifyWorldStateChanged();
            }
        }
        bool selectsSecure=plan.Private is {PromoteSecureTarget:true,LocalError:null,TargetIdentity:{Length:>0}};
        if (plan.Packets.Count == 0)
        {
            // 749590 still calls 5658B0 when its local avatar lookup fails.
            if(selectsSecure)MutateWorldAndPublish(()=>Gameplay.Social.SelectSecureRecipient(plan.Private!.TargetIdentity!));
            // A retail local refusal (usage, no reply recipient, not in a group/guild, no channel) is a handled
            // submission: 0065CCD0 records and retires the input (CHAT-D01).
            return selectsSecure || plan.LocalError is { } handled && Net7ChatInput.LocalErrorType(handled) != 19;
        }
        foreach (var (opcode, payload) in plan.Packets)
            if (!await TrySendGameplayAsync(opcode, payload, cancellationToken).ConfigureAwait(false)) return false;
        if (plan.Private is { } privatePlan)
            MutateWorldAndPublish(() =>
            {
                if (selectsSecure)
                    Gameplay.Social.SelectSecureRecipient(privatePlan.TargetIdentity!);
                if (privatePlan.CreateLocalEcho)
                    Gameplay.Social.AddHistory(new(16, $"To {privatePlan.TargetIdentity}: {plan.EchoText}"));
            });
        return true;
    }
}
