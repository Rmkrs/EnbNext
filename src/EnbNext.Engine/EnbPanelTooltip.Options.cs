using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbPanelTooltip
{
    public EnbUiTooltipDraw? DrawOptions(EnbOptionsGadgetCollection collection,int page,
        EnbInformationPanel? informationPanel,EnbControlsPanel? controls,EnbSceneExperienceSnapshot experience,
        EnbUiSize size,double scaling,uint? character,Func<string?>? characterName,long? milliseconds=null)
    {
        var target = collection.Context.Tooltip;
        if (target.Owner is not { } pointer) { Reset(); return null; }
        int index = target.Index;
        string? targetKey = null, nativeMessage = null;
        string nativeSection = "UI_OPTION_CHECK";
        if (pointer == collection.Utility)
        {
            targetKey = EnbUtilityHudPanel.InputKey(index);
            nativeSection = index < 3 ? "UI_EXPERIENCE" : "UI_CPIT";
            if (index < 3)
            {
                float value = index == 0 ? experience.Explore : index == 1 ? experience.Trade : experience.Combat;
                nativeMessage = FormattableString.Invariant($"Progress: {value * 100:0.00}%");
            }
        }
        else if(informationPanel is {} information)
        {
            targetKey=collection.Petition?.InputKey(pointer,index) ?? (pointer==information.Pointer?information.InputKey(index):page==10 && pointer==collection.Log?.Body.Pointer?"log/MESSAGE":null);nativeSection=page==10?"UI_LOG":page==12?"UI_PETITION_PLAYER":"UI_PETITION";
            string? infoName=targetKey?[(targetKey.IndexOf('/')+1)..];
            if(targetKey?.StartsWith("petition/")==true)nativeSection="UI_PETITION_ENTRY";
            else if(targetKey?.StartsWith("player/")==true){nativeSection="UI_PETITION_PLAYER_ENTRY";infoName=infoName=="ENTRY_FIELD01"?"EDIT":"IMPORT";}
            if(infoName=="MESSAGE")infoName="DETAILS";
            if(targetKey?.StartsWith("complaint/")==true)infoName=infoName switch {"BUTTON_01"=>"NAME","BUTTON_02"=>"HARASS","BUTTON_03"=>"GAME","BUTTON_04"=>"SPAM","BUTTON_05"=>"CHEATING","EXIT2"=>"EXIT",_=>infoName};
            nativeMessage=infoName is not null&&resources.Strings.TryGetSection(nativeSection,out var infoStrings)==true?infoStrings.GetValueOrDefault(infoName,"").Replace("\\n","\n"):"";
        }
        else if (page == 3) { targetKey = controls!.InputKey(index); nativeSection = "UI_OPTION_CONTROL_KEYS"; }
        else if (page is 7 or 8 or 9) targetKey = EnbSettingsPanel.InputKey(index);
        else if (page == 6)
        {
            targetKey = index is 12 or 13 or >=20 and <=23 ? EnbGraphicsPanel.InputKey(index) : null;
            nativeMessage = index == 12 ? "Default display settings used before login" : index == 13 ? "Display settings for " + DisplayName(characterName) : null;
            nativeSection = "UI_OPTION_SLIDER";
        }
        else if (collection.Panel is { } native) { targetKey = native.Key(index); nativeSection = page == 1 ? "UI_OPTION_MAIN" : "UI_OPTION_CHECK"; }
        return Draw(pointer,targetKey,nativeSection,collection.Input,size,scaling,
            milliseconds ?? Environment.TickCount64,character,nativeMessage,index);
    }

    private static string DisplayName(Func<string?>? requested)
    {
        string? name=requested?.Invoke()?.Trim();
        return string.IsNullOrWhiteSpace(name)?"Character":name;
    }
}
