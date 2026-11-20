using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

internal sealed partial class EnbNavigationMapUi
{
    internal Action<Point>? MapPointerWarpRequested { get; set; }
    private void SetMapPressCursor(bool right)
    {
        if(_pressedButton is not null)return;
        if(_galaxy)LocateGalaxy();
        // 7B47F0 /668F20: preserve the current cursor on an object press.
        bool hovered=_galaxy
            ? _galaxyFocusedSystem.HasValue||_galaxyFocusedSector.HasValue||_galaxyFocusedGate.HasValue
            : _hoveredObject.HasValue;
        if(!hovered)SetMapCursor(right?"c_zoomin.tga":"c_handgrab.tga");
    }

    internal bool PressPointer(Point point,bool right)
    {
        if(_captured || !BlocksFlight(point))return false;
        Begin(point,right);_captured=true;CaptureRequested?.Invoke(true);return true;
    }
    internal void ExitPointer(){if(!_captured)ClearHover();}
    internal void ReleasePointer(Point point,bool right,bool anyHeld)=>EndMapButton(point,right,_pressedButton is null&&anyHeld);
    internal void WheelPointer(double delta){if(_help || !_guidanceBounds.Contains(_pointer)) ZoomMap((float)delta*.1f);Publish();}
    internal bool RouteMapPointerMove(Point point,int transition,bool leftDown,bool rightDown,bool control)
    {
        if(_captured&&_pressedButton is null)
        {
            if(transition is 1 or 2)
            {Locate(point);_lastPointer=point;SetMapPressCursor(transition==2);return true;}
            if(transition is -1 or -2)
            {EndMapButton(point,transition==-2,leftDown||rightDown);return true;}
            _right=!leftDown&&rightDown;
        }
        MoveWithModifiers(point,control);return false;
    }

    private bool TryWrapMapPointer(Point point,out Point wrapped)
    {
        // Shared66AE60 coordinate producer: wrap one window axis, reset the
        // local baseline and suppress the wrapped event's camera mutation.
        wrapped=point;
        if(point.X<=0)wrapped=new(.99*_size.Width,point.Y);
        else if(point.X>=_size.Width)wrapped=new(.01*_size.Width,point.Y);
        else if(point.Y<=0)wrapped=new(point.X,.99*_size.Height);
        else if(point.Y>=_size.Height)wrapped=new(point.X,.01*_size.Height);
        if(wrapped==point)return false;
        if(MapPointerWarpRequested is {} warp)warp(wrapped);
        return true;
    }
}
