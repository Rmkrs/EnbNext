using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retail segment-versus-box test for dazzle visibility (Woo's slab method), for
/// axis-aligned boxes and boxes with an orientation. A segment starting inside the box sets
/// StartInside without changing the fraction.</summary>
public sealed class EnbRetailDazzleBoxRay
{
    public (float Fraction, bool StartInside) CastSegment(Vector3 center, Vector3 extent, Matrix4x4? orientation,
        Vector3 start, Vector3 end, float fraction = 1, bool startInside = false)
    {
        if (startInside) return (fraction, true);
        Vector3 direction = end - start;
        Vector3 min = center - extent, max = extent + center;
        Vector3 origin = start;
        if (orientation is { } m)
        {
            // Into box space: transpose of the axis rows, about the box center.
            float rx = start.X - center.X, ry = start.Y - center.Y, rz = start.Z - center.Z;
            origin = new(((m.M11 * rx + m.M13 * rz) + m.M12 * ry) + center.X,
                ((m.M21 * rx + m.M23 * rz) + m.M22 * ry) + center.Y,
                ((m.M31 * rx + m.M33 * rz) + m.M32 * ry) + center.Z);
            direction = new((m.M13 * direction.Z + m.M12 * direction.Y) + m.M11 * direction.X,
                (m.M23 * direction.Z + m.M22 * direction.Y) + m.M21 * direction.X,
                (m.M33 * direction.Z + m.M32 * direction.Y) + m.M31 * direction.X);
        }
        Span<float> o = stackalloc float[3] { origin.X, origin.Y, origin.Z };
        Span<float> d = stackalloc float[3] { direction.X, direction.Y, direction.Z };
        Span<float> low = stackalloc float[3] { min.X, min.Y, min.Z };
        Span<float> high = stackalloc float[3] { max.X, max.Y, max.Z };
        Span<float> candidate = stackalloc float[3];
        Span<bool> middle = stackalloc bool[3];
        bool inside = true;
        for (int i = 0; i < 3; i++)
        {
            if (!(o[i] >= low[i])) { candidate[i] = low[i]; inside = false; }
            else if (o[i] > high[i]) { candidate[i] = high[i]; inside = false; }
            else middle[i] = true;
        }
        if (inside) return (fraction, true);
        Span<float> distance = stackalloc float[3];
        for (int i = 0; i < 3; i++)
            distance[i] = !middle[i] && d[i] != 0 && !float.IsNaN(d[i]) ? (candidate[i] - o[i]) / d[i] : -1f;
        int plane = 0;
        for (int i = 1; i < 3; i++) if (distance[i] > distance[plane]) plane = i;
        float t = distance[plane];
        if (!(t >= 0)) return (fraction, false);
        for (int i = 0; i < 3; i++)
        {
            if (i == plane) continue;
            float coordinate = d[i] * t + o[i];
            if (!(coordinate >= low[i]) || coordinate > high[i]) return (fraction, false);
        }
        if (t < fraction || float.IsNaN(t) || float.IsNaN(fraction)) return (t, false);
        return (fraction, false);
    }
}
