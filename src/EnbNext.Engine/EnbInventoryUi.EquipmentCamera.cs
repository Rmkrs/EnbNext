using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Numerics;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private bool _equipmentDragArmed;
    private Point _equipmentRotatePoint;
    internal Action<Point>? EquipmentPointerWarpRequested { get; set; }

    private void BeginEquipmentDrag(bool right)
    {
        if(_pressed!="shipPreview")_equipmentDragArmed=false;
        PreviewCursor(right?"c_zoomin.tga":"c_handgrab.tga");
    }

    private void MoveEquipmentPointer(Point point,bool left,bool right)
    {
        if(!left&&!right)return;
        // 66DAE0 ->66AE60: a captured drag wraps at the window boundary.
        // The coordinate writer updates the comparison baseline after wrapping,
        // suppressing this event's camera mutation (including its large jump).
        if(_equipmentDragArmed)
        {
            Point wrapped=point;
            if(point.X<=0)wrapped=new(.99*_size.Width,point.Y);
            else if(point.X>=_size.Width)wrapped=new(.01*_size.Width,point.Y);
            else if(point.Y<=0)wrapped=new(point.X,.99*_size.Height);
            else if(point.Y>=_size.Height)wrapped=new(point.X,.01*_size.Height);
            if(wrapped!=point)
            {
                _lastDrag=wrapped;
                if(EquipmentPointerWarpRequested is {} warp)warp(wrapped);
                return;
            }
        }
        if(point==_lastDrag)return;
        // 65F780 produces both held bits;66DB10 gives left precedence and seeds
        // the first nonzero rotation.66DED0 arms zoom without moving its baseline.
        if(left)
        {
            if(_equipmentDragArmed){RotateEquipment(_equipmentRotatePoint,point);_lastDrag=point;}
            _equipmentRotatePoint=point;
        }
        else if(_equipmentDragArmed)
        {
            double delta=point.Y-_lastDrag.Y;
            ZoomEquipment((float)(delta/Screen(Native("equip/SHIP")).Height));
            PreviewCursor(delta<0?"c_zoomin.tga":"c_zoomout.tga");
            _lastDrag=point;
        }
        _equipmentDragArmed=true;
    }

    // Current retail 0066DB10 / 00542EE0: additive distance in units of
    // the 75-unit preview scale (0079B6B0), clamped to half/twice that scale.
    private void ZoomEquipment(float delta) => _previewView.Z=Math.Clamp(_previewView.Z+delta,.5f,2f);

    private void RotateEquipment(Point previous,Point current)
    {
        // 00543200 / 008D1CE0: full-window normalized virtual trackball,
        // sphere joined to a hyperbolic sheet at radius/sqrt(2).
        Vector3 Project(Point point)
        {
            float x=(float)(2*point.X/_size.Width-1), y=(float)(1-2*point.Y/_size.Height);
            float d=MathF.Sqrt(x*x+y*y);
            float z=d<.8f*MathF.Sqrt(.5f)?MathF.Sqrt(.64f-d*d):.32f/d;
            return new(x,y,z);
        }
        var from=Project(previous);var to=Project(current);
        var axis=Vector3.Cross(to,from);
        if(axis.LengthSquared()<1e-16f)return;
        float angle=2*MathF.Asin(Math.Clamp(Vector3.Distance(from,to)/1.6f,0,1));
        // Retail camera right/up/back -> our left/up/forward basis.
        axis=Vector3.Normalize(new Vector3(-axis.Z,-axis.X,axis.Y));
        _previewRotation=Quaternion.Normalize(_previewRotation*Quaternion.CreateFromAxisAngle(axis,angle));
    }
}
