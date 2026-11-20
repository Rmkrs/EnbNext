using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>63CA50 initial placement, 63D1F0 received targets, 788900/783B50 motion.</summary>
public sealed class EnbStationRemoteMotion
{
    private EnbStationAmbientNpcMotion? controller;
    private (Vector3 Position,float Heading,bool Run)? lastReceived;
    private double previousTime;
    private long lastRevision;
    public bool Initialized=>controller is not null;
    public Matrix4x4 Transform=>controller?.Transform??Matrix4x4.Identity;
    public bool AcceptsReceivedSpeech=>controller?.AcceptsReceivedSpeech??true;
    public bool RequestIdle=>controller?.RequestIdle??false;
    public bool RequestWalk=>controller?.RequestWalk??false;
    public float Speed=>controller?.Speed??0;
    public int Remaining=>controller?.Remaining??0;
    public string AnimationTag=>controller is not {} motion || motion.MovementState==2?"breathe1":
        motion.Backward?"walk_backward":motion.Run?"run_forward":"walk_forward";

    public void Receive(Vector3 position,float heading,bool run,double milliseconds,long revision=0)
    {
        var sample=(position,heading,run);
        if(revision!=0?revision<=lastRevision:lastReceived==sample)return;
        lastRevision=revision;
        lastReceived=sample;
        var transform=Matrix4x4.CreateRotationZ(heading)*Matrix4x4.CreateTranslation(position);
        if(controller is null)
        {
            controller=new(transform){Run=run};previousTime=milliseconds;
            return;
        }
        controller.Append([transform],trimHistory:true);
        controller.Run=run;
    }
    public void Advance(double milliseconds,bool actionAllowsMovement=true)
    {
        float seconds=(float)Math.Max(0,milliseconds-previousTime)*.001f;
        previousTime=milliseconds;
        controller?.Advance(seconds,actionAllowsMovement);
    }
    public void WalkAnimationSelected()=>controller?.WalkAnimationSelected();
}
