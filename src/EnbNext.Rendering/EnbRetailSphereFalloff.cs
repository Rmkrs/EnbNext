using System.Numerics;

namespace EnbNext.Rendering;

// Stateless numeric portion of client 0090E640; not production sphere admission.
public static class EnbRetailSphereFalloff
{
    public static Vector4 Evaluate(Vector3 normal,Quaternion orientation,float strength,bool inverted,bool additive)
    {
        float x=orientation.X,y=orientation.Y,z=orientation.Z,w=orientation.W;
        // Native calculation rotates unit X without normalizing the quaternion.
        var direction=new Vector3(w*w+x*x-y*y-z*z,2*x*y+2*z*w,2*x*z-2*y*w);
        float value=MathF.Min(MathF.Abs(Vector3.Dot(direction,normal)*strength),1f);
        if(!inverted)value=1f-value;
        return additive?new(value,value,value,0):new(1,1,1,value);
    }
}
