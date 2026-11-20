using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Presentation;
using EnbNext.Assets;

namespace EnbNext.Engine;

internal sealed partial class EnbChatUi
{
    private bool _colorsChanged;
    public bool TryColorCommand(string text)
    {
        if(EnbRetailChatColorCommand.Parse(text) is not {} command)return false;
        if(_assets is null || _session() is not {} character)return true;
        // Flush existing delivered rows with their old color before changing the palette.
        // 570FF0 captures at insertion; 6818C0 preserves RGB during reflow.
        Publish();
        if(command.Reset)SpeakerColors=EnbRetailChatColors.Defaults;
        else if(command.Color is {} color)SpeakerColors=SpeakerColors.SetItem(command.Type,color);
        if(command.Reset || command.Color is not null)ChatColorsChanged?.Invoke(character,SpeakerColors);
        foreach(var feedback in command.Feedback)
        {
            if(feedback.Type is 17 or 19 &&
                _options.GetValueOrDefault(feedback.Type==17?"Chat_Errors_IC":"Chat_Errors_System","yes").Equals("yes",StringComparison.OrdinalIgnoreCase))
                InteractionSoundRequested?.Invoke("Text_Error");
            if(feedback.Type==21 && _hiddenTypes.Contains(21))InteractionSoundRequested?.Invoke("Text_Warning");
            (feedback.SuppressPrefix?LocalChatPreviewRequested:LocalChatMessageRequested)?.Invoke(character,feedback.Type,feedback.Read(_assets.Paths.ClientExecutablePath));
        }
        if(command.Store){_colorsChanged=true;SaveSettings();}
        Publish();
        return true;
    }
}
