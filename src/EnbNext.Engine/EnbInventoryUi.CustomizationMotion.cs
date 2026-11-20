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
    private long _customMotionTick;
    private float _customDistance=10;
    private Vector2 _customDragVelocity;
    private long _customDragTick;
    private float _customPanOrigin=2.5f;
    private float _customShipSpin;
    private Quaternion _customRotation=Quaternion.Identity;
    private void RotateCustomization(float yaw,float pitch)
    {
        _customHeading+=yaw;_customPitch+=pitch;
        // The renderer composes baseCamera * orbit. Postmultiply the increment
        // so its axes are the current camera's up/left, not the initial camera's.
        _customRotation=Quaternion.Normalize(_customRotation*Quaternion.CreateFromAxisAngle(Vector3.UnitZ,yaw)*Quaternion.CreateFromAxisAngle(Vector3.UnitY,pitch));
    }
    private readonly Dictionary<string,(long Tick,float Angle)> _customSwatchTurns=[];

    private float SwatchAngle(string mesh,bool selected)
    {
        long now=Environment.TickCount64;
        var previous=_customSwatchTurns.GetValueOrDefault(mesh,(now,0));
        float angle=previous.Angle+Math.Clamp((now-previous.Tick)*.001f,0,.1f)*(selected?2:.3f); // 00550A10 / 00AED028
        _customSwatchTurns[mesh]=(now,angle);return angle;
    }
    private static bool CustomizationContinuous(string? id)=>id is
        "custom/TURN_LEFT" or "custom/TURN_RIGHT" or "custom/TURN_UP" or "custom/TURN_DOWN" or
        "custom/PAN_LEFT" or "custom/PAN_RIGHT" or "custom/PAN_UP" or "custom/PAN_DOWN" or
        "custom/ZOOM_IN" or "custom/ZOOM_OUT" or "custom/SCALE_IN" or "custom/SCALE_OUT";

    private void TickCustomization()
    {
        long now=Environment.TickCount64;
        double seconds=_customMotionTick==0?0:Math.Clamp((now-_customMotionTick)*.001,0,.1);
        _customMotionTick=now;
        if(!_customConfirm&&!_busy)AdvanceCustomization(seconds,_pressed);
    }

    // 0054E3C0 / 0080CE10: held controls feed a time-based camera update.
    internal void AdvanceCustomization(double seconds,string? held)
    {
        float dt=(float)seconds;
        _customShipSpin+=dt*.05f;
        bool avatar=_terminal?.Type==6;
        float move=(avatar?_customDistance*.6f:60)*dt;
        float turn=MathF.Asin(Math.Clamp(dt*(avatar?2:1)/.8f,-1,1)); // retail virtual trackball radius .8
        switch(held)
        {
            case "custom/TURN_LEFT":RotateCustomization(turn,0);break;
            case "custom/TURN_RIGHT":RotateCustomization(-turn,0);break;
            case "custom/TURN_UP":RotateCustomization(0,-turn);break;
            case "custom/TURN_DOWN":RotateCustomization(0,turn);break;
            case "custom/PAN_LEFT":_customView.X-=move*.75f;break;
            case "custom/PAN_RIGHT":_customView.X+=move*.75f;break;
            case "custom/PAN_UP":_customView.Y-=move*.75f;break;
            case "custom/PAN_DOWN":_customView.Y+=move*.75f;break;
            case "custom/ZOOM_IN":_customView.Z*=Math.Max(.01f,1-move/Math.Max(.01f,_customDistance));break;
            case "custom/ZOOM_OUT":_customView.Z*=1+move/Math.Max(.01f,_customDistance);break;
            case "custom/SCALE_IN":ScaleCustomizationTattoo(false,seconds);break;
            case "custom/SCALE_OUT":ScaleCustomizationTattoo(true,seconds);break;
        }
        _customView.X=Math.Clamp(_customView.X,avatar?-2:-70,avatar?2:70);
        _customView.Y=Math.Clamp(_customView.Y,avatar?-4.75f-_customPanOrigin:-47,avatar?4.75f-_customPanOrigin:53);
        _customView.Z=Math.Clamp(_customView.Z,avatar?(_customTab==1?.25f:1):.5f/2.1f,avatar?(_customTab==1?1:4):1);
        if(held is null)
        {
            RotateCustomization(_customDragVelocity.X*dt,avatar?0:_customDragVelocity.Y*dt);
            
            _customDragVelocity*=MathF.Pow(.99f,dt*60); // 0080CE10 release inertia
        }
        else if(CustomizationContinuous(held))_customDragVelocity=Vector2.Zero;
    }

    private void DragCustomization(Point point)
    {
        var delta=point-_lastDrag;_lastDrag=point;
        // Pointer positions are normalized to the authored camera aperture.
        float dx=(float)(2*delta.X/_size.Width),dy=(float)(2*delta.Y/_size.Height);
        float yaw=-MathF.Asin(Math.Clamp(dx/.8f,-1,1)),pitch=MathF.Asin(Math.Clamp(dy/.8f,-1,1));
        RotateCustomization(yaw,_terminal?.Type==5?pitch:0);
        long now=Environment.TickCount64;
        float dt=Math.Max(.001f,(now-_customDragTick)*.001f);_customDragTick=now;
        _customDragVelocity=new(yaw/dt,pitch/dt);
        PreviewCursor("c_handgrab.tga");
    }

    private void ScaleCustomizationTattoo(bool increase,double seconds)
    {
        if(_customAvatar is not {} a)return;
        var options=TattooOptions(a.TattooTextureNum);if(options.Scale<=1)return;
        var p=a.TattooOffset.ToArray();
        p[2]=Math.Clamp(p[2]*MathF.Pow(increase?31f/30:29f/30,(float)seconds*60),.5f,options.Scale);
        _customAvatar=a with {TattooOffset=p};
    }
}

