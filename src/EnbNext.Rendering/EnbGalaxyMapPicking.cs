using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Galaxy map's finite ray and link queries (007A7560,
/// 008C9370, 008C9500, 007AD5B0). Bounds are supplied by render objects.</summary>
public static class EnbGalaxyMapPicking
{
    public static bool Intersect(Vector3 origin, Vector3 end, Vector4 sphere,
        Vector3 minimum, Vector3 maximum, out Vector3 point)
    {
        point = default;
        var delta = end-origin;
        float length = delta.Length();
        if (length == 0) return false;
        var offset = new Vector3(sphere.X,sphere.Y,sphere.Z)-origin;
        float projected = Vector3.Dot(offset,delta/length);
        float discriminant = sphere.W*sphere.W-(offset.LengthSquared()-projected*projected);
        if (discriminant < 0) return false;
        float root = MathF.Sqrt(discriminant), fraction = (projected-root)/length;
        if (fraction < 0) fraction = (projected+root)/length;
        if (fraction < 0 || fraction >= 1) return false;
        // Retail's candidate-plane AABB query uses inclusive faces and a
        // strict finite-ray end. An inside origin returns its zeroed point.
        Vector3 candidate = default, times = new(-1);
        bool inside = true;
        for (int axis=0;axis<3;axis++)
        {
            if (origin[axis] < minimum[axis]) candidate[axis]=minimum[axis];
            else if (origin[axis] > maximum[axis]) candidate[axis]=maximum[axis];
            else continue;
            inside=false;
            if (delta[axis]!=0) times[axis]=(candidate[axis]-origin[axis])/delta[axis];
        }
        if (inside) return true;
        int plane=0;
        if(times.Y>times.X) plane=1;
        if(times.Z>times[plane]) plane=2;
        fraction=times[plane];
        if(fraction<0 || fraction>=1) return false;
        point=origin+delta*fraction; point[plane]=candidate[plane];
        for(int axis=0;axis<3;axis++)
            if(axis!=plane && (point[axis]<minimum[axis] || point[axis]>maximum[axis])) return false;
        return true;
    }

    public static float LinkDistance(Vector3 point,Vector3 from,Vector3 to)
    {
        if(point.X<Math.Min(from.X,to.X)-1.5f || point.X>Math.Max(from.X,to.X)+1.5f ||
            point.Y<Math.Min(from.Y,to.Y)-1.5f || point.Y>Math.Max(from.Y,to.Y)+1.5f) return -1;
        var delta=new Vector2(to.X-from.X,to.Y-from.Y);
        var offset=new Vector2(point.X-from.X,point.Y-from.Y);
        float length=delta.Length();
        return length<.0001f ? offset.Length() : Math.Abs(delta.Y*offset.X-delta.X*offset.Y)/length;
    }
}
