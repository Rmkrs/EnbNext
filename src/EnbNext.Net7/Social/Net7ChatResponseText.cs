namespace EnbNext.Net7.Social;

/// <summary>Current retail 0073D170 response text; this class never changes protocol state.</summary>
internal static class Net7ChatResponseText
{
    public static string? Format(Net7ClientChatErrorUpdate response)
    {
        var (status, request, player, channel, value) = response;
        if (status == 0) return request switch
        {
            3 => "Message sent to all users.",
            5 => $"Removed channel {channel}.",
            20 => $"Kicked {player} from channel {channel}.",
            21 => $"Set channel {channel} access to {value}.",
            22 => $"A password has been set for channel {channel}.",
            _ => null,
        };
        string reason = Net7SocialState.DecodeChatErrorReason(status);
        // The literals at 00B97310..00B979AC (CHAT-D09): failures end ". (<reason>)"; the gag notice needs the reason to
        // equal "is_gagged" (00A2A9A0 folds case only), so the ordinary "is gagged" reason takes the could-not-speak line.
        bool gagged = reason.Equals("is_gagged", StringComparison.OrdinalIgnoreCase);
        return request switch
        {
            0 or 2 when gagged => "Chat disabled. You have been gagged by a GM",
            0 => $"Could not speak on {channel}. ({reason})",
            1 when status == 4 => $"{player} is not online at this time.",
            1 => $"Could not broadcast to {player}. ({reason})",
            2 => $"Could not speak locally. ({reason})",
            3 => $"Could not broadcast to all. ({reason})",
            4 => $"Could not insert channel {channel}. ({reason})",
            5 => $"Could not remove channel {channel}. ({reason})",
            6 when status == 10 => $"You have been banned from entering channel: {channel}",
            6 when status is 12 or 13 => null, // 0073D170 leaves its chat buffer empty.
            6 when channel.StartsWith("Sector", StringComparison.OrdinalIgnoreCase) => null,
            6 => $"Could not enter channel {channel}. ({reason})",
            7 => $"Could not exit channel {channel}. ({reason})",
            8 when status == 7 => $"{player} is already on your friend list.",
            8 when status == 17 => "You cannot add yourself to the friend list.",
            8 => $"Could not add {player} to your friend list. ({reason})",
            9 => $"Could not remove {player} from your friend list. ({reason})",
            10 when status == 7 => $"{player} is already on your ignore list.",
            10 when status == 17 => "You cannot ignore yourself.",
            10 => $"Could not add {player} to your ignore list. ({reason})",
            11 => $"Could not remove {player} from your ignore list. ({reason})",
            12 or 13 => $"Could not invite {player} to {channel}. ({reason})",
            14 => $"Could not ban {player} from {channel}. ({reason})",
            15 => $"Could not unban {player} from {channel}. ({reason})",
            // 0073D5A0: the reason "already" (folded case) is the already-gagged notice.
            16 when reason.Equals("already", StringComparison.OrdinalIgnoreCase) => $"That person is already gagged on {channel}.",
            16 => $"Could not gag {player} on {channel}. ({reason})",
            17 => $"That person is not gagged on {channel}",
            18 => $"Could not add {player} as owner of channel {channel}. ({reason})",
            19 => $"Could not remove {player} as owner of channel {channel}. ({reason})",
            20 => $"Could not kick {player} from {channel}. ({reason})",
            21 => $"Could not set access on channel {channel}. ({reason})",
            22 => $"Could not set password on channel {channel}. ({reason})",
            31 => "You do not have permission to GM Gag",
            32 => "You do not have permission to GM Ungag",
            _ => $"ERROR: error handling unknown request {unchecked((int)request)} ({reason})",
        };
    }
}
