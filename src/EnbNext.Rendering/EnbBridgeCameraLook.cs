using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Transient first-person bridge look, current client 0050ABC0/0053BD40.</summary>
public sealed class EnbBridgeCameraLook
{
    public float HeadingRadians { get; private set; }
    public float TiltRadians { get; private set; }
    private bool _modified;

    public void Reset()
    {
        HeadingRadians = TiltRadians = 0;
        _modified = false;
    }

    // Ctrl+Shift uses Ctrl here; Shift alone and any Alt chord do nothing.
    // currentState/targetState are the bridge's +18/+1C, not ship movement states.
    public bool TryAdjust(double normalizedDeltaX, double normalizedDeltaY, double elapsedMilliseconds,
        bool control, bool alt, bool resourceBound, int currentState, int targetState)
    {
        if (!control || alt || !resourceBound || currentState != 0 || targetState != 0 ||
            !double.IsFinite(normalizedDeltaX) || !double.IsFinite(normalizedDeltaY) ||
            !double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0) return false;
        var angles=ResolveInputAngles(new(HeadingRadians,TiltRadians),(float)(.5+normalizedDeltaX),
            (float)(.5+normalizedDeltaY),elapsedMilliseconds);
        HeadingRadians=angles.X;TiltRadians=angles.Y;
        _modified = true;
        return true;
    }

    public static Vector2 ResolveInputAngles(Vector2 current,float x,float y,double elapsedMilliseconds,
        bool initialGesture=false,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float heading=current.X,tilt=current.Y;
        if(!initialGesture)
        {
            //50ACC1/50ACED store both frame multipliers before the input
            //operations;53CA00/53C980 then clamp the stored caller values.
            float scale=(float)((R)elapsedMilliseconds*(R).1f);
            scale=(float)((R)scale*(R).5f);
            heading=(float)(((R).5f-(R)x)*(R)scale+(R)heading);
            R value=((R)y-(R).5f)*(R)scale+(R)tilt;
            tilt=(float)value;
            if((R.CompareFloatMemory(value,0xbfbde44f)&0x100)!=0)tilt=BitConverter.UInt32BitsToSingle(0xbfbde44f);
            else if(tilt>BitConverter.UInt32BitsToSingle(0x3fbde44f))tilt=BitConverter.UInt32BitsToSingle(0x3fbde44f);
        }
        return new(Math.Clamp(heading,-.25f,.25f),Math.Clamp(tilt,-.2f,.4f));
    }

    /// <summary>
    /// Applies look to the retail camera transform in row-vector storage, before
    /// conversion to the renderer's camera basis. The authored translation is
    /// rotated too; replacing only the orientation does not match 0053BD40.
    /// Call once per bridge update, after accepting this frame's mouse input.
    /// </summary>
    public Matrix4x4 ConsumeRetailTransform(Matrix4x4 authoredCamera, int currentState,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        var result=authoredCamera;
        if(currentState==0)
        {
            //53BE65..53BF97: cosine is stored to float; sine remains in ST.
            float cosine=(float)R.Cosine((R)HeadingRadians);
            R sine=R.Sine((R)HeadingRadians);
            RotateHeading(ref result.M11,ref result.M13,cosine,sine);
            RotateHeading(ref result.M21,ref result.M23,cosine,sine);
            RotateHeading(ref result.M31,ref result.M33,cosine,sine);
            RotateHeading(ref result.M41,ref result.M43,cosine,sine);
            cosine=(float)R.Cosine((R)TiltRadians);sine=R.Sine((R)TiltRadians);
            RotateTilt(ref result.M12,ref result.M13,cosine,sine);
            RotateTilt(ref result.M22,ref result.M23,cosine,sine);
            RotateTilt(ref result.M32,ref result.M33,cosine,sine);
            RotateTilt(ref result.M42,ref result.M43,cosine,sine);
        }
        // Retail consumes the current angles before recovering them. A setter
        // postpones recovery for this update, including a zero-delta setter.
        if (_modified) _modified = false;
        else
        {
            HeadingRadians = (float)((R)HeadingRadians*(R).85f);
            TiltRadians = (float)((R)TiltRadians*(R).85f);
        }
        return result;
    }

    private static void RotateHeading(ref float x,ref float z,float cosine,R sine)
    {
        float old=x;
        x=(float)((R)z*sine+(R)x*(R)cosine);
        z=(float)((R)z*(R)cosine-(R)old*sine);
    }

    private static void RotateTilt(ref float y,ref float z,float cosine,R sine)
    {
        float old=y;
        y=(float)((R)y*(R)cosine-(R)z*sine);
        z=(float)((R)old*sine+(R)z*(R)cosine);
    }
}
