using EnbNext.Assets;

namespace EnbNext.Client;

public enum EnbCameraPointerAction { Unchanged, End, Apply }
public readonly record struct EnbCameraPointerSample(EnbCameraPointerAction Action,bool Initial=false,bool Distance=false,bool Control=false);

/// <summary>50ABC0 input latch/coordinate ordering; camera math remains in its camera owner.</summary>
public sealed class EnbCameraInputState
{
    private double lastX,lastY,wheelRemainder;
    public bool Active { get; private set; }
    public void End()=>Active=false;
    public void Accept()=>Active=true;
    public void Reset(){Active=false;lastX=lastY=wheelRemainder=0;}
    public void ResetWheel()=>wheelRemainder=0;

    public EnbCameraPointerSample Sample(double x,double y,EnbKeyModifiers modifiers)
    {
        if(x==lastX && y==lastY)return default;
        lastX=x;lastY=y;
        if(modifiers==EnbKeyModifiers.None || modifiers.HasFlag(EnbKeyModifiers.Alt))return new(EnbCameraPointerAction.End);
        return new(EnbCameraPointerAction.Apply,!Active,modifiers.HasFlag(EnbKeyModifiers.Shift),modifiers.HasFlag(EnbKeyModifiers.Control));
    }

    public bool ConsumeWheel(double delta,out bool forward)
    {
        forward=false;
        if(!double.IsFinite(delta))return false;
        wheelRemainder+=delta;
        double steps=Math.Truncate(wheelRemainder);
        if(steps==0)return false;
        wheelRemainder-=steps;forward=steps>0;return true;
    }

    public bool AdmitWheel(double x,double y,EnbKeyModifiers modifiers)
    {
        if(!Active && (modifiers.HasFlag(EnbKeyModifiers.Alt) || (modifiers&(EnbKeyModifiers.Shift|EnbKeyModifiers.Control))==0))return false;
        if(x==lastX && y==lastY)return false;
        lastX=x;lastY=y;return true;
    }
}
