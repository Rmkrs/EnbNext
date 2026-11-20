using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>006E6030: displace the local-ship endpoint when a beam crosses
/// the camera near plane inside the width/noise-expanded projection bounds.</summary>
public static class EnbRetailLineCameraDisplacement
{
    public static void Apply(EnbResolvedRenderCamera camera,float width,float noise,
        bool sourceIsLocal,bool targetIsLocal,ref Vector3 start,ref Vector3 end)
    {
        if(!sourceIsLocal&&!targetIsLocal)return;
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        if(camera.Projection.StoredProjectionBounds is not {} bounds)
            throw new InvalidDataException("Line camera displacement requires stored projection bounds");
        var right=camera.Basis.Right;var up=camera.Basis.Up;var back=-camera.Basis.Forward;
        //00944EB0/008C2BD0: transpose the rigid basis and store its translated
        //inverse. Keep the native addition order at both transform consumers.
        float Translation(Vector3 axis,bool first)=>-(float)(first
            ? (R)axis.Z*camera.Position.Z+(R)axis.Y*camera.Position.Y+(R)axis.X*camera.Position.X
            : (R)axis.Z*camera.Position.Z+(R)axis.X*camera.Position.X+(R)axis.Y*camera.Position.Y);
        float tx=Translation(right,true),ty=Translation(up,false),tz=Translation(back,false);
        static float Transform(Vector3 p,Vector3 axis,float translation,bool start,bool first)
            =>(float)((start?(R)axis.Y*p.Y+(R)axis.Z*p.Z+(R)axis.X*p.X
                :first?(R)axis.Z*p.Z+(R)axis.Y*p.Y+(R)axis.X*p.X
                :(R)axis.Z*p.Z+(R)axis.X*p.X+(R)axis.Y*p.Y)+translation);
        float ax=Transform(start,right,tx,true,true),ay=Transform(start,up,ty,true,false);
        float az=(float)((R)Transform(start,back,tz,true,false)+camera.Projection.ClipRange.NearPlane);
        float bx=Transform(end,right,tx,false,true),by=Transform(end,up,ty,false,false);
        R bzRegister=(R)Transform(end,back,tz,false,false)+camera.Projection.ClipRange.NearPlane;
        float bz=(float)bzRegister;
        if(bzRegister*az>0)return;
        float halfX=(float)((R)bounds.Z*camera.Projection.ClipRange.NearPlane);
        float halfY=(float)((R)bounds.W*camera.Projection.ClipRange.NearPlane);
        float padding=(float)((R)width+noise);
        R dx=(R)bx-ax;float dy=(float)((R)by-ay),dz=(float)((R)bz-az);
        float squareX=(float)(dx*dx+(R)dz*dz),squareY=(float)((R)dy*dy+(R)dz*dz);
        float cosineX=squareX==0?dz:(float)((R)EnbRetailLineCoalescing.ReciprocalSquareRoot(squareX)*dz);
        R cosineY=squareY==0?dz:(R)EnbRetailLineCoalescing.ReciprocalSquareRoot(squareY)*dz;
        halfX=(float)((R)padding/MathF.Abs(cosineX)+halfX);
        halfY=(float)((R)padding/Math.Abs((double)cosineY)+halfY);
        float inverseX=(float)((R)1/halfX),inverseY=(float)((R)1/halfY);
        R normalizedAX=(R)ax*inverseX,normalizedBX=(R)bx*inverseX;
        float normalizedAY=(float)((R)ay*inverseY),normalizedBY=(float)((R)by*inverseY);
        float difference=(float)((R)az-bz),x,y;
        //The native double constant is the exact double representation of .001f.
        if(MathF.Abs(difference)>=BitConverter.Int64BitsToDouble(0x3f50624de0000000))
        {
            float fraction=(float)(((R)1/difference)*az);
            x=(float)((normalizedBX-normalizedAX)*fraction+normalizedAX);
            y=(float)(((R)normalizedBY-normalizedAY)*fraction+normalizedAY);
        }
        else {x=(float)normalizedAX;y=normalizedAY;}
        if(MathF.Abs(x)>=1||MathF.Abs(y)>=1)return;
        R reciprocal=(R)1/MathF.Max(MathF.Abs(x),MathF.Abs(y));
        R moveX=(R)(float)(reciprocal*x)-x;
        float moveY=(float)(reciprocal*y-y);
        R factor=Math.Abs((double)((R)difference/(sourceIsLocal?bz:az)));
        float storedFactor=(float)factor;
        float worldX=(float)(factor*moveX*halfX);
        float worldY=(float)((R)(float)((R)storedFactor*moveY)*halfY);
        R shiftX=(R)worldY*up.X+(R)worldX*right.X;
        float shiftY=(float)((R)worldX*right.Y+(R)worldY*up.Y);
        float shiftZ=(float)((R)worldX*right.Z+(R)worldY*up.Z);
        var original=sourceIsLocal?start:end;
        var displaced=new Vector3((float)(shiftX+original.X),(float)((R)shiftY+original.Y),(float)((R)shiftZ+original.Z));
        //Retail divides by zero for a beam through the exact projection center.
        //Contain that proven invalid geometry locally; never invent a direction.
        if(!float.IsFinite(displaced.X)||!float.IsFinite(displaced.Y)||!float.IsFinite(displaced.Z))
            throw new InvalidDataException("Line camera displacement produced non-finite geometry");
        if(sourceIsLocal)start=displaced;else end=displaced;
    }
}
