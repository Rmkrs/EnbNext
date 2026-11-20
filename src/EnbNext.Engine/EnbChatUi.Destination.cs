using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Presentation;
namespace EnbNext.Engine;

internal sealed partial class EnbChatUi
{
    //7F4316/19 initialize current/default false;74B03E/A2 set it locally.
    public bool ShowSelfName => _options.GetValueOrDefault("Show_Self_Name","no").Equals("yes",StringComparison.OrdinalIgnoreCase);
    public void SetSelfName(bool enabled)
    {
        _options["Show_Self_Name"]=enabled?"yes":"no";
        SaveSettings();
        if(_session() is {} character && _assets is not null)
            LocalChatMessageRequested?.Invoke(character,23,EnbNext.Assets.EnbRetailSelfNameCommand.Feedback(_assets.Paths.ClientExecutablePath,enabled));
    }
    // Retail HUD+144/+1F4 are the saved default; +148/+238 belong to the
    // active input. 00564F50 copies the default only when opening normal chat.
    private int _defaultDestination = 1;
    private string _defaultLabel = "Local";
    private bool _reconcileDefault;
    internal string DefaultDestinationLabel => _defaultLabel;

    private void SetDefaultDestination(int kind, string label)
    {
        _defaultDestination = kind;
        _defaultLabel = label;
        _options["Chat_Default_Channel"] = kind.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _options["Channel_Label"] = label;
    }

    private int DestinationKind(string destination, string target) => destination switch
    {
        "Broadcast" => 0, "Local" => 1, "Guild" => 2, "Group" => 3,
        "Channel" => PublicChannels.Contains(target, StringComparer.OrdinalIgnoreCase) ? 5 : 4,
        _ => -1
    };

    private void DestinationMessage(byte type, string text)
    {
        if (_session() is not { } character) return;
        bool sound = type == 17 ? !_hiddenTypes.Contains(17)
            : !_options.GetValueOrDefault("Chat_Errors_System", "yes").Equals("no", StringComparison.OrdinalIgnoreCase);
        if (sound) InteractionSoundRequested?.Invoke("Text_Error");
        LocalChatMessageRequested?.Invoke(character, type, text);
    }

    private bool ReconcileDefaultDestination()
    {
        // 005689D0 -> 0057B440 forces the old Chat combo through its close
        // callback before replacing entries, even during an opening animation.
        if (_menu == "Chat")
        {
            if (_comboState is 1 or 2 or 3) PlayCommandMenuOpen();
            _menu = _comboMenu = "";
            _comboState = 0; _comboOpening = 0; _comboShown = false;
        }
        // 005689D0 walks the complete B7C530 table, remembering its first
        // admitted item and whether the saved label names an excluded item.
        // Unknown nonempty labels are not rejected by this path.
        var candidates = new List<(int Kind, string Label, bool Enabled)>();
        string[] categories = ["Broadcast", "Local", "Guild", "Group"];
        for (int i = 0; i < categories.Length; i++)
        {
            string category = categories[i];
            candidates.Add((i, category,
                _options.GetValueOrDefault("Channel_" + category, "yes").Equals("yes", StringComparison.OrdinalIgnoreCase) &&
                (i != 2 || _inGuild) && (i != 3 || _inGroup)));
        }
        foreach (var channel in NamedChannelOptions)
            candidates.Add((5, channel.Channel, _options.GetValueOrDefault(channel.Option,
                channel.Option == "Channel_Newbie" ? "yes" : "no").Equals("yes", StringComparison.OrdinalIgnoreCase)));
        bool invalid = candidates.Any(c => !c.Enabled && c.Label.Equals(_defaultLabel, StringComparison.OrdinalIgnoreCase));
        bool privateAvailable = _data?.PrivateChannel is { Length: > 0 } &&
            _options.GetValueOrDefault("Channel_Private", "yes").Equals("yes", StringComparison.OrdinalIgnoreCase);
        if (privateAvailable) candidates.Add((4, "Private " + _data!.PrivateChannel, true));
        else invalid |= _defaultLabel.StartsWith("Private", StringComparison.Ordinal); // native strncmp(...,7)
        var first = candidates.FirstOrDefault(c => c.Enabled);
        if (!invalid && !(first.Enabled && _defaultLabel.Length == 0)) return false;
        SetDefaultDestination(first.Enabled ? first.Kind : -1, first.Enabled ? first.Label : "");
        DestinationMessage(19, first.Enabled ? "Defaulting chat channel to " + first.Label + "."
            : "No default destination available for chat.");
        return true;
    }

    private bool ApplyDefaultToInput()
    {
        // 005646A0 cases -1/4/5 fail locally before input activation.
        string? error = _defaultDestination switch
        {
            -1 => "No valid default channel set.",
            2 when !_inGuild => "Not in a guild.",
            3 when !_inGroup => "Not in a group.",
            4 when string.IsNullOrEmpty(_data?.PrivateChannel) => "Not subscribed to any private channel.",
            5 when _defaultLabel.Length == 0 => "Not monitoring any public channel.",
            _ => null
        };
        if (error is not null) { DestinationMessage(17, error); return false; }
        _destination = _defaultDestination switch { 0 => "Broadcast", 2 => "Guild", 3 => "Group", 4 or 5 => "Channel", _ => "Local" };
        _target = _defaultDestination == 4 ? _data!.PrivateChannel : _defaultDestination == 5 ? _defaultLabel : "";
        _targets[_destination] = _target;
        return true;
    }

    private void OpenExplicitDestination(string destination, bool privateChannel)
    {
        // 0065DE90 actions 1/2/3 call 005646A0 cases 4/3/2 and test
        // its result before activating input. Radio always selects the joined
        // private channel, independently of the last public-channel draft.
        string? error = destination switch
        {
            "Guild" when !_inGuild => "Not in a guild.",
            "Group" when !_inGroup => "Not in a group.",
            _ when privateChannel && string.IsNullOrEmpty(_data?.PrivateChannel) => "Not subscribed to any private channel.",
            _ => null
        };
        if (error is not null)
        {
            // Unlike cases 2/3, retail's failed case 4 retains the selected
            // kind but leaves the caption/target and input visibility intact.
            if (privateChannel) _destination = "Channel";
            DestinationMessage(17, error); return;
        }
        SelectInputDestination(destination, privateChannel ? _data!.PrivateChannel : null);
    }
}
