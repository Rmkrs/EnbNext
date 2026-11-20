using System.Numerics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Ambient waypoint consumer 00788900 and translational part of 00783B50.
/// Animation state remains owned by the avatar: 0 starting, 1 walking, 2 stopped.</summary>
public sealed class EnbStationAmbientNpcMotion(Matrix4x4 initial)
{
    private readonly List<Matrix4x4> route=[];
    private List<Matrix4x4>? suspended;
    private uint flags;
    private float turn;
    private bool run,runChanged;
    public Matrix4x4 Transform {get;private set;}=initial;
    public float Speed {get;private set;}
    public int MovementState {get;private set;}=2;
    public bool RequestIdle {get;private set;}
    public bool RequestWalk {get;private set;}
    public bool Backward=>(flags&2)!=0;
    public bool AcceptsReceivedSpeech=>flags==0 && Speed==0;
    public bool Run
    {
        get=>run;
        set {if(run==value)return;run=value;if((flags&1)!=0)runChanged=true;}
    }
    public int Remaining=>route.Count;
    public void Append(IEnumerable<Matrix4x4> transforms,bool trimHistory)
    {
        foreach(var transform in transforms)
        {
            int old=route.Count;route.Add(transform);
            // 00788730 limits only when param2 is true; oldCount-3 removals.
            if(trimHistory && old>3)route.RemoveRange(0,old-3);
        }
    }
    public void BeginInteraction(Matrix4x4 facing)
    {
        // 0078A080 saves the pending path and clears movement bits, without
        // resetting the retained speed or the scheduler's idle wait.
        suspended=[..route];route.Clear();StopMovement();flags&=0xfffcu;
        Transform=facing;
    }
    public void EndInteraction()
    {
        if(suspended is not null){route.Clear();route.AddRange(suspended);suspended=null;}
    }
    public void WalkAnimationSelected(){if(MovementState==0)MovementState=1;}
    private void StopMovement()
    {
        if(MovementState==2)return;
        MovementState=2;RequestIdle=true;
    }
    public void Advance(float seconds,bool actionAllowsMovement=true)
    {
        RequestWalk=RequestIdle=false;
        // 784F60 restarts forward locomotion when the received run bit changes.
        if(runChanged)
        {
            StopMovement();runChanged=false;
            if(actionAllowsMovement){MovementState=0;RequestWalk=true;}
        }
        SelectPathInput();
        float dt=Math.Min(seconds,.25f);
        if(flags==0 && Speed==0)return;
        float rotation=seconds>.25f?0:2*dt;
        if(route.Count!=0 && MovementState!=2)rotation*=3;
        if(turn<.25f)rotation*=turn*4;
        if((flags&4)==0)rotation=(flags&8)!=0?-rotation:0;
        if(rotation!=0)
        {
            var translation=Transform.Translation;
            // Native rotates the local XY basis and retains all authored axes.
            Transform=Matrix4x4.CreateRotationZ(rotation)*Transform;
            var rotated=Transform;rotated.Translation=translation;Transform=rotated;
        }
        float acceleration=30*dt*(Speed>0 && run?2.5f:1);
        if((flags&3)==0)
            Speed=Speed>0?Math.Max(0,Speed-acceleration):Math.Min(0,Speed+acceleration);
        else
        {
            if(MovementState==2 && actionAllowsMovement)
            {MovementState=0;RequestWalk=true;}
            if(MovementState!=1)return;
            Speed=(flags&1)!=0?Math.Min(run?25:10,Speed+acceleration):Math.Max(-10,Speed-acceleration);
        }
        var transform=Transform;
        transform.Translation+=new Vector3(transform.M11,transform.M12,transform.M13)*(Speed*dt);
        Transform=transform;
    }
    private void SelectPathInput()
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        while(route.Count>0)
        {
            var target=route[0];var delta=target.Translation-Transform.Translation;
            // 00788961..77 and 00788AEB..B2F: Z/Y/X reduction, then
            // 00447C40's reciprocal-square-root, not a SIMD normalize.
            float square=(float)(((R)delta.Z*delta.Z+(R)delta.Y*delta.Y)+(R)delta.X*delta.X);
            float distance=MathF.Sqrt(square);
            var forward=new Vector3(Transform.M11,Transform.M12,Transform.M13);
            if(distance<3)
            {
                if(route.Count>1){route.RemoveAt(0);continue;}
                StopMovement();flags&=0xfffcu;
                float cross=(float)(((R)forward.Z*target.M23+(R)forward.Y*target.M22)+(R)forward.X*target.M21);
                turn=Math.Abs(cross);
                if(turn<(float)BitConverter.Int64BitsToDouble(0x3fa65544e0000000))
                {flags&=0xfff0u;route.RemoveAt(0);return;}
                flags=(flags&0xfff3u)|(cross<0?4u:8u);return;
            }
            R inverse=EnbRetailLineCoalescing.ReciprocalSquareRoot(square);
            R dx=inverse*delta.X,dy=(R)delta.Y*(float)inverse,dz=(R)delta.Z*(float)inverse;
            float lateral=(float)((dz*Transform.M23+dy*Transform.M22)+dx*Transform.M21);
            turn=Math.Abs(lateral);
            bool backward=((R)forward.Z*dz+(R)forward.Y*dy)+(R)forward.X*dx<=0 &&
                ((R)forward.Z*target.M13+(R)forward.Y*target.M12)+(R)forward.X*target.M11>=0 && distance<=10;
            if(backward)
            {
                bool reverseTurning=false;
                if(turn<.5f)
                {
                    if((flags&1)!=0)
                    {
                        if(distance>=10)reverseTurning=true;
                        else {StopMovement();flags&=0xfffcu;}
                    }
                    if(!reverseTurning)flags|=2;
                }
                if(reverseTurning)turn=1;
                flags=(flags&0xfff3u)|(lateral<0?4u:8u);
            }
            else
            {
                if(turn<.5f)
                {
                    if((flags&2)!=0){StopMovement();flags&=0xfffcu;}
                    flags|=1;
                }
                flags=(flags&0xfff3u)|(lateral<0?8u:4u);
            }
            return;
        }
    }
}
