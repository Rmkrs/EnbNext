using System.Numerics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Local class23 bounds arithmetic009912B0. Callers query children
/// under the proven temporary identity transform and restore it afterward.
/// Degenerate and unordered inputs retain retail's instruction predicates.</summary>
public readonly record struct EnbCurrentRetailCollectionBounds(Vector4 Sphere, Vector3 BoxCenter, Vector3 BoxExtent)
{
    private static bool Unordered(R value) => double.IsNaN((double)value);
    private static bool LessOrUnordered(R left, R right) => left < right || Unordered(left) || Unordered(right);
    private static bool EqualOrUnordered(R left, R right) => left == right || Unordered(left) || Unordered(right);

    public static EnbCurrentRetailCollectionBounds Calculate(Func<int> childCount,
        Func<int, Vector4> childSphere, Func<int, (Vector3 Center, Vector3 Extent)> childBox)
        => childCount() <= 0 ? default : CalculateNonEmpty(childCount, childSphere, childBox);

    internal static EnbCurrentRetailCollectionBounds CalculateNonEmpty(Func<int> childCount,
        Func<int, Vector4> childSphere, Func<int, (Vector3 Center, Vector3 Extent)> childBox,
        Action<Vector4>? publishSphere = null)
    {
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Vector4 sphere = childSphere(0); // Raw first-child words, without FLD conversion.
        publishSphere?.Invoke(sphere);
        for (int index = 1; index < childCount(); index++)
        {
            Vector4 other = childSphere(index);
            if (EqualOrUnordered(other.W, 0)) continue;
            R x = (R)other.X - sphere.X, y = (R)other.Y - sphere.Y, z = (R)other.Z - sphere.Z;
            float squared = (float)((z * z + y * y) + x * x);
            float distance = (float)R.Sqrt(squared);
            if (EqualOrUnordered(distance, 0))
            {
                sphere.W = (float)((R)sphere.W > other.W ? (R)sphere.W : other.W);
                publishSphere?.Invoke(sphere);
                continue;
            }
            R radius = (((R)sphere.W + other.W) + distance) * .5;
            float storedRadius = (float)radius;
            if (LessOrUnordered(radius, sphere.W)) continue;
            if (LessOrUnordered(storedRadius, other.W))
            {
                sphere = other with { W = (float)(R)other.W };
                publishSphere?.Invoke(sphere);
                continue;
            }
            R fraction = ((R)storedRadius - sphere.W) / distance;
            float deltaY = (float)((R)other.Y - sphere.Y), deltaZ = (float)((R)other.Z - sphere.Z);
            float scaledX = (float)(((R)other.X - sphere.X) * fraction);
            float scaledY = (float)((R)deltaY * fraction);
            R scaledZ = fraction * deltaZ;
            sphere = new((float)((R)scaledX + sphere.X), (float)((R)scaledY + sphere.Y),
                (float)(scaledZ + sphere.Z), storedRadius);
            publishSphere?.Invoke(sphere);
        }

        var box=CalculateNonEmptyBox(childCount,childBox);
        return new(sphere,box.Center,box.Extent);
    }

    /// <summary>The independent box pass of009912B0. Empty owners clear the
    /// box; live nonempty owners preserve the raw degenerate predicates.</summary>
    public static (Vector3 Center,Vector3 Extent) CalculateBox(Func<int> childCount,
        Func<int,(Vector3 Center,Vector3 Extent)> childBox)
        =>childCount()<=0?default:CalculateNonEmptyBox(childCount,childBox);

    private static (Vector3 Center,Vector3 Extent) CalculateNonEmptyBox(Func<int> childCount,
        Func<int,(Vector3 Center,Vector3 Extent)> childBox)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        for (int index = 0; index < childCount(); index++)
        {
            var (center, extent) = childBox(index);
            if (EqualOrUnordered(extent.X, 0) && EqualOrUnordered(extent.Y, 0) && EqualOrUnordered(extent.Z, 0)) continue;
            R lowX = (R)center.X - extent.X;
            float lowY = (float)((R)center.Y - extent.Y), lowZ = (float)((R)center.Z - extent.Z);
            if (LessOrUnordered(lowX, minX)) minX = (float)lowX;
            if (LessOrUnordered(lowY, minY)) minY = lowY;
            if (LessOrUnordered(lowZ, minZ)) minZ = lowZ;
            R highX = (R)center.X + extent.X;
            float highY = (float)((R)extent.Y + center.Y), highZ = (float)((R)extent.Z + center.Z);
            if (highX > maxX) maxX = (float)highX;
            if ((R)highY > maxY) maxY = highY;
            if ((R)highZ > maxZ) maxZ = highZ;
        }
        // Y/Z sums/differences spill before halving; X retains the wide x87
        // exponent until after halving, including the all-degenerate case.
        Vector3 boxCenter = new((float)(((R)maxX + minX) * .5),
            (float)((R)(float)((R)maxY + minY) * .5), (float)((R)(float)((R)maxZ + minZ) * .5));
        Vector3 boxExtent = new((float)(((R)maxX - minX) * .5),
            (float)((R)(float)((R)maxY - minY) * .5), (float)((R)(float)((R)maxZ - minZ) * .5));
        return (boxCenter,boxExtent);
    }
}
