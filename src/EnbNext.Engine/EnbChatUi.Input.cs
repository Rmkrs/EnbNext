using EnbNext.Presentation;
namespace EnbNext.Engine;
internal sealed partial class EnbChatUi
{
    private bool FocusInput(EnbUiTextInput input)
    {
        if(!input.IsVisible)return false;
        (ReferenceEquals(input,_editor)?_passwordVerifyEditor:_editor).SetFocused(false);
        input.SetFocused(true);return true;
    }
    private EnbUiRect Region(string id)=>TryRegion(id,out var bounds)?bounds:throw new KeyNotFoundException(id);
    private bool TryRegion(string id,out EnbUiRect bounds)
    {
        if(_hits.TryGetValue(id,out var hit)&&hit.Published){bounds=hit.Bounds;return true;}
        bounds=default;return false;
    }
    internal EnbUiTextInput MainInput=>_editor;
    internal EnbUiTextInput VerifyInput=>_passwordVerifyEditor;
    internal IReadOnlyDictionary<string,EnbChatHit> Hits=>_hits;
    internal event Action? TransportChanged;
    internal event Action<string?>? CaptureRequested;
    internal Action<Action> Defer {get;set;}=action=>action();
    internal void Detached(){if(IsChatList)CloseChatDialog();}
    internal void Hover(string id,EnbUiPoint point)
    {_hover=id;_hoverStarted=_chatDialog==ChatDialog.Commands?CommandsClock():Environment.TickCount64;_pingPointer=point;Publish();UpdatePingTooltip();}
    internal void ClearHover(){_hover="";Publish();UpdatePingTooltip();}
    internal void Press(string id,EnbUiPoint point,bool left,bool right)
    {
        _last=point;
        if(left&&!right&&_modernChat&&ModernPress(id,point)){}
        else if(IsHistorySlider(id)&&(left||right))BeginHistorySlider(id); // 0066F330 mask 11: left and right alike.
        else if(IsChatListSlider(id)&&left)
        {
            _drag=id;_listScrollAtGrab=ChatListScroll;_listCaptured=true;_listGrab=_last.Y-_listThumb.Top;
            _listRepeat=id.EndsWith("/up")?-1:id.EndsWith("/down")?1:id.EndsWith("/track")?(_last.Y<_listThumb.Top?-7:7):0;
            _listRepeatAt=CommandsClock()+500;CaptureRequested?.Invoke(id);
            if(_hits.TryGetValue(id,out var slider))slider.Left();
        }
        else if(id.EndsWith("/scrollthumb",StringComparison.Ordinal)&&left)
        {_drag=id;BeginMdScroll(id,point);CaptureRequested?.Invoke(id);}
        else if(id is "resize" or "split" or "chatdialog/block" or "chatdialog/ignore/entry/drag" &&
            (id!="chatdialog/block"||_chatDialog is ChatDialog.Ignore or ChatDialog.Commands||_modernDialogShown)&&left)
        {_drag=id;if(IsChatList)_listCaptured=true;CaptureRequested?.Invoke(id);}
        else if(_hits.TryGetValue(id,out var action)){if(right)action.Right?.Invoke();else action.Left();}
    }
    internal bool Move(string id,EnbUiPoint point)
    {
        _pingPointer=point;UpdatePingTooltip();if(_drag!=id)return false;
        if(ModernDragMove(id,point)){_last=point;Publish();return true;}
        double delta=(point.Y-_last.Y)*480/_size.Height;
        if(IsHistorySlider(id)){if(id.StartsWith("thumb"))DragHistoryThumb(point);}
        else if(IsChatListSlider(id)&&id.EndsWith("/thumb",StringComparison.Ordinal))DragChatListThumb(point);
        else if(IsChatListSlider(id)){}
        else if(id.EndsWith("/scrollthumb",StringComparison.Ordinal))DragMdScroll(id,point);
        else if(id=="chatdialog/block"&&_modernDialogShown)DragModernDialog(point-_last);
        else if(id=="chatdialog/block"&&_chatDialog==ChatDialog.Commands)DragCommands(point-_last);
        else if(id is "chatdialog/block" or "chatdialog/ignore/entry/drag")DragIgnoreDialog(id.EndsWith("/drag",StringComparison.Ordinal),point-_last);
        else if(id=="resize")_height=_layouts[_mode].Height(_layouts[_mode].HeightFrame(_height+delta));
        else if(id=="split")
        {
            var layout=_layouts[_mode];
            double desired=_mode==2?_split+(point.X-_last.X)*640/_size.Width*100/layout.Width:_split+delta;
            _split=layout.Split(layout.SplitFrame(desired,_mode==2),_mode==2);
        }
        if(id is "resize" or "split")PanesResized?.Invoke();
        _last=point;Publish();return true;
    }
    internal void Release(string id)
    {
        if(_drag!=id)return;
        if(_modernGrab==id){_drag=null;CaptureRequested?.Invoke(null);ModernRelease(id);return;}
        if(IsHistorySlider(id))EndHistorySlider();
        _drag=null;_listRepeat=0;_listCaptured=false;CaptureRequested?.Invoke(null);
        if(id is "resize" or "split")SaveSettings();
        if(id=="chatdialog/block"&&_modernDialogShown)SaveModernTabs();
    }
    internal void CaptureLost(string id)
    {
        if(_modernGrab==id){_drag=null;EndModernDrag();Publish();}
        if(IsHistorySlider(id)&&_drag==id)EndHistorySlider();
        if(IsChatList&&_drag==id){_drag=null;_listCaptured=false;_listRepeat=0;}
    }
    internal void Wheel(string id,double delta){if(_hits.TryGetValue(id,out var action))action.Wheel?.Invoke(delta);}
}
