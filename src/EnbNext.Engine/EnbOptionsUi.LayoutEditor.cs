using System.Collections.Immutable;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbOptionsUi
{
    internal EnbUiLayoutEditor? LayoutEditor {get;set;}
    private EnbQuitPanel? _layoutArt;
    internal EnbUiRect LayoutEntry=>_quitUi?.Panel.ScreenEdit(_size)??default;
    internal void LayoutClickSound()=>InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
    private void BeginLayoutEditor(){LayoutClickSound();Close();LayoutEditor!.Begin();Publish();}
    private void PublishLayoutEditor(ImmutableList<EnbUiModelDraw>.Builder list)
    {
        if(LayoutEditor is not {} edit||!edit.Layout.Editing||_assets is null)return;
        _layoutArt??=new(_assets,_quitPalette);
        if(_layoutArt.ResolveFont("FONT_SMALL",_size,_scaling) is {} font)
            edit.Unit=Math.Min(Math.Max(1,Math.Max(font.MeasureWidth("Group Finder")*font.Scale/128,font.CellHeight*font.Scale/24)),Math.Min((_size.Width-16)/700,(_size.Height-48)/640));
        edit.DisplayScale=_scaling;
        double u=edit.Unit;
        if(UiLayout!.Preview(EnbUiGroup.Options)&&!NativeOptionsOpen)foreach(var draw in _mainPanel!.DrawFrame(_size,_scaling,_cameraState))list.Add(draw with {LayoutGroup=EnbUiGroup.Options});
        int slot=0,toolOwner=-1;
        void Control(EnbUiRect rect,string label,bool button=true,bool heading=false,bool selected=false,bool enabled=true,bool ellipsis=false)
        {
            bool hover=button&&enabled&&!edit.ConfirmDelete&&rect.Contains(edit.Pointer)&&(toolOwner==4||(edit.Managing?toolOwner==3:edit.ToolAt(edit.Pointer)==toolOwner));
            var draw=_layoutArt.Control("layout-"+slot++,button?"quit/BTTN02":"quit/FRAME",rect,label,_size,_scaling,hover,selected&&!hover||hover&&edit.Pressed,"FONT_SMALL",heading,enabled,ellipsis);
            if(!button&&label.Length>0)list[^1]=list[^1] with {Images=list[^1].Images.AddRange(draw.Images)};else list.Add(draw);
        }
        foreach(var pair in edit.Groups)
        {
            var r=edit.ScaleBar(pair.Key);Control(r,"",false);
            Control(edit.DragHandle(pair.Key),$"{UiLayout.Placement(pair.Key).Scale*100:0}%",false);
            Control(edit.ScaleAction(pair.Key,false),"-");Control(edit.ScaleAction(pair.Key,true),"+");Control(edit.ResetAction(pair.Key),"R",enabled:!UiLayout.Locked);
            if(EnbUiLayoutEditor.HasOptions(pair.Key))Control(edit.OptionsAction(pair.Key),"Options",selected:edit.OptionsOpen==pair.Key);
        }
        // The open panel's layout options under its scale bar, each with its state.
        foreach(var (key,label,_,row) in edit.OptionRows())Control(row,label+": "+(UiLayout.Option(key)?"On":"Off"),selected:UiLayout.Option(key));
        foreach(int tool in edit.ToolOrder)
        {
            if(!edit.ToolVisible(tool))continue;toolOwner=tool;var t=edit.ToolRect(tool);Control(t,"",false);
            Control(new(t.X+24*u,t.Y+12*u,t.Width-(tool==0?48:84)*u,30*u),tool==0?"Edit mode - "+(UiLayout.Docked?"Docked":"Space / Planets"):tool==1?"Panels":"Grid",false,true);
            if(tool==0)
            {
                Control(new(t.X+24*u,t.Y+50*u,t.Width-356*u,34*u),(UiLayout.Selected?.Name??"Default (locked)")+"  v",ellipsis:true);
                Control(edit.Action(10),"Manage");Control(edit.Action(4),"Save");Control(edit.Action(5),"Cancel");
                Control(edit.Action(13),"Panels",selected:edit.PanelsOpen);Control(edit.Action(14),"Grid",selected:edit.GridToolsOpen);
                Control(new(t.X+24*u,t.Bottom-32*u,t.Width-48*u,20*u),edit.Message.Length>0?edit.Message:UiLayout.Locked?"Default is locked. Use Manage to make a profile.":"Drag panels or percentages to move. R resets.",false);
            }
            else if(tool==1)
            {
                Control(edit.Action(19),"X");
                for(int row=0;row<edit.Elements.Length;row++)
                {var group=edit.Elements[row];Control(edit.ElementRow(row),EnbUiLayoutEditor.ElementName(group),selected:UiLayout.Visible(group));}
            }
            else
            {
                Control(edit.Action(20),"X");Control(edit.Action(15),"Show grid",selected:edit.GridVisible);Control(edit.Action(16),"Snap to grid",selected:edit.SnapToGrid);
                Control(new(t.X+58*u,t.Y+152*u,t.Width-116*u,34*u),edit.GridSpacing+" px",false);
                Control(edit.Action(17),"-",enabled:edit.GridSpacing>8);Control(edit.Action(18),"+",enabled:edit.GridSpacing<64);
                Control(new(t.X+24*u,t.Y+200*u,t.Width-48*u,20*u),"Hold Shift to bypass snapping.",false);
            }
        }
        if(edit.Managing)
        {
            toolOwner=3;var m=edit.Manager;Control(m,"",false);
            Control(new(m.X+24*u,m.Y+12*u,m.Width-48*u,30*u),"Manage profiles",false,true);
            Control(edit.Action(0),(UiLayout.Selected?.Name??"Default (locked)")+"  v",ellipsis:true);
            Control(edit.Action(1),"New");Control(edit.Action(2),"Duplicate");
            Control(edit.Action(3),"Rename",enabled:!UiLayout.Locked);
            Control(edit.Action(11),"Delete",enabled:!UiLayout.Locked);
            if(edit.Naming!=0){Control(edit.Action(8),edit.Name+"|");Control(edit.Action(9),"Apply");}
            Control(new(m.X+24*u,m.Bottom-90*u,m.Width-48*u,24*u),edit.Message.Length>0?edit.Message:UiLayout.Locked?"Default cannot be renamed or deleted.":"Save the layout to keep profile changes.",false);
            Control(edit.Action(12),"Done");
        }
        if(edit.ConfirmDelete&&_layoutDeletePanel is {} dialog)list.AddRange(dialog.DrawFrame(_layoutDeleteState,_size,_scaling));
        if(edit.Dropdown){toolOwner=4;for(int row=0;row<edit.ProfileRows;row++)Control(edit.ProfileRow(row),edit.ProfileName(row+edit.ProfileScroll),ellipsis:true);}
    }
    private readonly EnbNext.Client.EnbConfirmationState _layoutDeleteState=new();
    private readonly EnbUiGadgetContext _layoutDeleteContext=new();
    private EnbConfirmationPanel? _layoutDeletePanel;
    internal void ShowLayoutDelete(string name)
    {
        _layoutDeletePanel??=new(_assets!,_quitPalette);
        _layoutDeletePanel.AttachInput(_layoutDeleteContext);
        _layoutDeleteState.Show("Delete profile \""+name+"\"?",false,wrapWidth:280);
        _layoutDeletePanel.Prepare(_layoutDeleteState,_size,_scaling);
    }
    internal void RetireLayoutDeleteInput()=>_layoutDeletePanel?.RetireInput();
    internal void CloseLayoutDelete(){_layoutDeleteState.Clear();_layoutDeletePanel?.RetireInput();}
    internal EnbUiRect LayoutDeleteBounds(int index)
    {
        var r=_layoutDeletePanel?.Bounds[index]??default;
        return new(r.X*_size.Width/640,r.Y*_size.Height/480,r.Width*_size.Width/640,r.Height*_size.Height/480);
    }
    internal void LayoutDeleteInput(EnbUiPoint point,ushort code,ushort modifiers,bool down)
    {
        if(_layoutDeletePanel is not {} dialog||LayoutEditor?.ConfirmDelete!=true)return;
        dialog.PrepareInput(_layoutDeleteState,_size,_scaling,(float)(point.X/_size.Width),(float)(point.Y/_size.Height));
        var frame=new EnbUiPointerFrame(code,modifiers,down,false);_layoutDeleteContext.Dispatch(ref frame);
        int answer=dialog.CompleteInput(frame);
        if(answer!=0){LayoutClickSound();LayoutEditor.AnswerDelete(answer==1);}
    }
    private readonly EnbUiGadgetPointer _layoutPointer=new(1);
    private readonly EnbUiInputQueue _layoutInput=new();
    private EnbPanelTooltip? _layoutTooltip;
    private string? _layoutTip;
    private void ShowLayoutTooltip()
    {
        if(LayoutEditor is not {} edit||_layoutArt is null||_assets is null)return;
        string? tip=null;
        if(!edit.Dropdown&&!edit.Dragging&&!edit.ConfirmDelete)
        {
            for(int i=0;i<=20;i++)if(edit.ActiveAction(i)&&edit.ActionTool(i)==(edit.Managing?3:edit.ToolAt(edit.Pointer))&&edit.Action(i).Contains(edit.Pointer))tip=i switch {
                0=>"Select a saved layout. Default is locked.",1=>"Create a profile from Default.",2=>"Copy this profile, including both scenes.",3=>"Rename the selected profile.",4=>"Save all changes and leave edit mode.",5=>"Discard changes and leave edit mode.",8=>"Ctrl+A clears the name; Enter applies it.",9=>"Apply this profile name.",10=>"Create, duplicate, rename or delete profiles.",11=>"Delete the selected profile. Default cannot be deleted.",12=>"Return to layout editing.",13=>"Show or hide the Panels tool.",14=>"Show or hide the Grid tool.",15=>"Show grid guides behind the UI.",16=>"Snap a moved panel to the grid. Hold Shift to bypass.",17 or 18=>"Change grid spacing in screen pixels.",19 or 20=>"Hide this tool. Reopen it from Edit mode.",_=>null};
            if(!edit.Managing&&tip is null)
            {
                if(edit.ToolAt(edit.Pointer)==1)for(int row=0;row<edit.Elements.Length;row++)if(edit.ElementRow(row).Contains(edit.Pointer))tip="Show or hide "+EnbUiLayoutEditor.ElementName(edit.Elements[row])+" during editing only.";
                if(tip is null&&edit.ToolAt(edit.Pointer)<0)foreach(var (_,_,optionTip,row) in edit.OptionRows())if(row.Contains(edit.Pointer))tip=optionTip+" Applies to every profile.";
                if(tip is null&&edit.ToolAt(edit.Pointer)<0)foreach(var pair in edit.Groups)if(EnbUiLayoutEditor.HasOptions(pair.Key)&&edit.OptionsAction(pair.Key).Contains(edit.Pointer))tip="Layout options for "+EnbUiLayoutEditor.ElementName(pair.Key)+".";
                if(tip is null&&edit.ToolAt(edit.Pointer)<0)foreach(var pair in edit.Groups)if(edit.ScaleBar(pair.Key).Contains(edit.Pointer))tip=edit.Layout.Locked?"Default is locked. Make a profile in Manage first.":edit.ResetAction(pair.Key).Contains(edit.Pointer)?"Reset "+pair.Key+" to its original position and 100% size.":"Resize "+pair.Key+" with - / +. Drag the percentage to move.";
                if(tip is null&&edit.ToolAt(edit.Pointer)>=0)tip="Drag this title to move the tool window.";
            }
        }
        if(tip!=_layoutTip){_layoutPointer.Dispatch(new(0,0,false,false),0);_layoutTip=tip;}
        _layoutPointer.Dispatch(new(0,0,edit.Pressed,false),tip is null?0u:1u);
        _layoutInput.SetPosition((float)(edit.Pointer.X*_scaling/Math.Max(1,_size.Width*_scaling-1)),(float)(edit.Pointer.Y*_scaling/Math.Max(1,_size.Height*_scaling-1)));
        _layoutTooltip??=new(_assets,_layoutArt.ResolveFont);_layoutTooltip.Immediate=_immediateTooltips;
        PublishTooltip(_layoutTooltip.Draw(_layoutPointer,tip is null?null:"layout/"+tip,"",_layoutInput,_size,_scaling,Environment.TickCount64,_owner,tip));
    }
}
