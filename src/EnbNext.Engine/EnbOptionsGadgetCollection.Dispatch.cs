using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal enum EnbOptionsInputCommandKind { Options, Information, Controls, Graphics, Settings, AnswerPrompt, CommitLogPage, FollowCaret, Sound }
internal readonly record struct EnbOptionsInputCommand(EnbOptionsInputCommandKind Kind, string? Key = null,
    bool Answer = false, EnbControlsCommandResult Controls = default);

internal sealed partial class EnbOptionsGadgetCollection
{
    public event Action<EnbOptionsInputCommand>? Command;

    public void Advance(int page, EnbUiSize size, double scaling, int width, int height, long now,
        EnbUtilityHudPanel? utility, EnbInformationPanel? information, EnbControlsPanel? controls,
        EnbGraphicsPanel? graphics, EnbSettingsPanel? settings, EnbCameraOptionsState camera,EnbUiLayoutProfiles? layout=null)
    {
        var input = Input.TakeFrame();
        var frame = new EnbUiPointerFrame(input.Code,input.Modifiers,Input.LeftDown,Input.RightDown);
        if (Utility is not null) Utility.Prepare(utility!.HitMask(input.X,input.Y));
        var originalPoint=new EnbUiPoint(Input.X,Input.Y);
        if(layout is not null)
        {
            EnbUiPoint Map(float x,float y){var p=layout.Inverse(EnbUiGroup.Options,new(x*(width-1)/scaling,y*(height-1)/scaling));return new(p.X*scaling/Math.Max(1,width-1),p.Y*scaling/Math.Max(1,height-1));}
            var eventPoint=Map(input.X,input.Y);input=input with {X=(float)eventPoint.X,Y=(float)eventPoint.Y};
            var point=Map(Input.X,Input.Y);Input.SetPosition((float)point.X,(float)point.Y);
        }
        string? utilityKey=null;
        try {
        if (Browser is {} browser) browser.PrepareInput(input);
        else if (Petition is {} petition) petition.PrepareInput(input,size,scaling,now);
        else if (Log is {} log) log.PrepareInput(input,size,now,input.Character);
        else if (information is not null) information.PrepareInput(input);
        else if (controls is not null) controls.PrepareInput(input,Input.HeldModifiers,size,scaling,now);
        else if (graphics is not null) graphics.PrepareInput(input,now);
        else if (settings is not null) settings.PrepareInput(input,now);
        else Panel?.PrepareInput(input);
        Context.Dispatch(ref frame);
        int utilityCommand=Utility?.Command(frame) ?? -1;
        utilityKey=utilityCommand is >=3 and <=6 ? EnbUtilityHudPanel.InputKey(utilityCommand) : null;
        if (Browser is {} activeBrowser)
        {
            if(activeBrowser.CompleteInput(frame) is {} command)Command?.Invoke(new(EnbOptionsInputCommandKind.Options,command));
        }
        else if (information is not null)
        {
            string? command;
            if (Petition is {} petitionInput)
            {
                command=petitionInput.CompleteInput(frame);
                if (petitionInput.Answer!=0) Command?.Invoke(new(EnbOptionsInputCommandKind.AnswerPrompt,Answer:petitionInput.Answer==1));
                if (petitionInput.Editor.FollowCaret) Command?.Invoke(new(EnbOptionsInputCommandKind.FollowCaret));
            }
            else if (Log is {} logInput)
            {
                command=logInput.CompleteInput(frame);
                if (logInput.CommitPage) Command?.Invoke(new(EnbOptionsInputCommandKind.CommitLogPage));
                if (logInput.FollowCaret) Command?.Invoke(new(EnbOptionsInputCommandKind.FollowCaret));
            }
            else command=information.CompleteInput(frame);
            if (command is not null) Command?.Invoke(new(EnbOptionsInputCommandKind.Information,command));
        }
        else if (controls is not null)
        {
            var result=controls.CompleteInput(frame,Input,size,scaling,now);
            Command?.Invoke(new(EnbOptionsInputCommandKind.Controls,Controls:result));
        }
        else if (graphics is not null)
        {
            string? command=graphics.CompleteInput(frame,Input,size,scaling,now);
            Command?.Invoke(new(EnbOptionsInputCommandKind.Graphics,command));
        }
        else if (settings is not null)
        {
            string? command=settings.CompleteInput(frame,Input,size,scaling,now);
            Command?.Invoke(new(EnbOptionsInputCommandKind.Settings,command));
        }
        else if (Panel is {} panel)
        {
            string? command=panel.CompleteInput(frame,Input.X,Input.Y,width,height,now,camera);
            if (command is null && frame.Code==0x1b && frame.Modifiers==0)
                command=page==1 ? "main/EXIT" : "camera/EXIT";
            if (command is not null)
            {
                Command?.Invoke(new(EnbOptionsInputCommandKind.Sound,command.StartsWith("camera/CHECK") ? "fontclick.wav" : "Roll_Select02b.wav"));
                if (!command.StartsWith("camera/CHECK")) Command?.Invoke(new(EnbOptionsInputCommandKind.Options,command));
            }
        }
        } finally {if(layout is not null)Input.SetPosition((float)originalPoint.X,(float)originalPoint.Y);}
        if (utilityKey is not null) Command?.Invoke(new(EnbOptionsInputCommandKind.Options,utilityKey));
    }
}
