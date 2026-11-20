using System.Numerics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public interface IEnbCurrentRetailHlodBoundsProvider<T> where T : class
{
    int LastLevelMemberCount { get; }
    T LastLevelMember(int index);
    int ClassId(T child);
    string Name(T child);
    bool IsBoundingBox(T child)
    {
        string name=Name(child);int dot=name.IndexOf('.');
        return string.Equals(dot<0?name:name[(dot+1)..],"BOUNDINGBOX",StringComparison.OrdinalIgnoreCase);
    }
    int ChildCount { get; }
    void EvaluateDefaultHierarchy();
    T RetainedChild(int index);
    int ChildPivot(T child);
    ReadOnlyMemory<float> PivotMatrix(int pivot);
    Vector4 LocalSphere(T child);
    (Vector3 Center, Vector3 Extent) LocalBox(T child);
    void Release(T child);
    uint Flags { get; set; }
    byte HierarchyUpdated { get; set; }
    void NotifyParentBounds();
}

/// <summary>0098F1D0 bounds over concrete retained hierarchy matrices.
/// The first box uses min/max transformation; subsequent boxes use center/extent.
/// Empty child bounds do not clear dirty flags or notify the parent.</summary>
public sealed class EnbCurrentRetailHlodBoundsRuntime
{
    private int _boundingBoxIndex=-1;
    internal Func<int>? LoadBoundingBoxIndex { get; init; }
    internal Action<int>? StoreBoundingBoxIndex { get; init; }
    public int BoundingBoxIndex { get=>LoadBoundingBoxIndex?.Invoke()??_boundingBoxIndex;set { _boundingBoxIndex=value;StoreBoundingBoxIndex?.Invoke(value); } }
    public EnbCurrentRetailCollectionBounds Bounds { get; set; }
    private static bool Less(R first, R second) => first < second || double.IsNaN((double)first) || double.IsNaN((double)second);
    private static bool Equal(R first, R second) => first == second || double.IsNaN((double)first) || double.IsNaN((double)second);

    public void Refresh<T>(IEnbCurrentRetailHlodBoundsProvider<T> owner) where T : class
    {
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        if (BoundingBoxIndex < 0 || BoundingBoxIndex >= owner.LastLevelMemberCount ||
            owner.ClassId(owner.LastLevelMember(BoundingBoxIndex)) != 0x1b) BoundingBoxIndex = -1;
        int count = owner.LastLevelMemberCount;
        for (int index = count - 1; index >= 0 && BoundingBoxIndex == -1; index--)
        {
            T child = owner.LastLevelMember(index);
            if (owner.ClassId(child) != 0x1b) continue;
            if (owner.IsBoundingBox(child))
                BoundingBoxIndex = index;
        }
        if (owner.ChildCount <= 0) { Bounds = default; return; }
        owner.EvaluateDefaultHierarchy();
        T first = owner.RetainedChild(0);
        var matrix = owner.PivotMatrix(owner.ChildPivot(first));
        Vector4 sphere = owner.LocalSphere(first);
        Vector3 position = TransformCenter(matrix.Span, new(sphere.X, sphere.Y, sphere.Z), true, out _);
        sphere = new(position, sphere.W);
        var box = owner.LocalBox(first);
        Vector3 minimum = default, maximum = default;
        for (int axis = 0; axis < 3; axis++)
        {
            minimum[axis] = (float)((R)box.Center[axis] - box.Extent[axis]);
            maximum[axis] = (float)((R)box.Extent[axis] + box.Center[axis]);
        }
        (minimum, maximum) = EnbCurrentRetailAffineBounds.MinMax(matrix.Span, minimum, maximum);
        owner.Release(first);
        for (int index = 1; index < owner.ChildCount; index++)
        {
            T child = owner.RetainedChild(index);
            matrix = owner.PivotMatrix(owner.ChildPivot(child));
            Vector4 other = owner.LocalSphere(child);
            position = TransformCenter(matrix.Span, new(other.X, other.Y, other.Z), false, out R liveX);
            other = new(position, other.W);
            sphere = MergeSphere(sphere, other, liveX);
            box = owner.LocalBox(child);
            box = EnbCurrentRetailAffineBounds.CenterExtent(matrix.Span, box.Center, box.Extent);
            if (!(Equal(box.Extent.X, 0) && Equal(box.Extent.Y, 0) && Equal(box.Extent.Z, 0)))
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    R lower = (R)box.Center[axis] - box.Extent[axis];
                    R upper = (R)box.Extent[axis] + box.Center[axis];
                    if (axis != 0) { lower = (float)lower; upper = (float)upper; }
                    if (Less(lower, minimum[axis])) minimum[axis] = (float)lower;
                    if (upper > maximum[axis]) maximum[axis] = (float)upper;
                }
            }
            owner.Release(child);
        }
        Vector3 center = default, extent = default;
        for (int axis = 0; axis < 3; axis++)
        {
            R sum = (R)maximum[axis] + minimum[axis], difference = (R)maximum[axis] - minimum[axis];
            if (axis != 0) { sum = (float)sum; difference = (float)difference; }
            center[axis] = (float)(sum * .5); extent[axis] = (float)(difference * .5);
        }
        Bounds = new(sphere, center, extent);
        owner.Flags &= 0xffffdfff; owner.HierarchyUpdated = 0;
        owner.NotifyParentBounds();
    }

    private static Vector3 TransformCenter(ReadOnlySpan<float> matrix, Vector3 center, bool first, out R liveX)
    {
        Vector3 result = default; liveX = 0;
        for (int row = 0; row < 3; row++)
        {
            R y = (R)center.Y * matrix[row * 4 + 1], z = (R)center.Z * matrix[row * 4 + 2];
            R value = ((first ? z + y : y + z) + (R)center.X * matrix[row * 4]) + matrix[row * 4 + 3];
            if (row == 0) liveX = value;
            result[row] = (float)value;
        }
        return result;
    }

    private static Vector4 MergeSphere(Vector4 sphere, Vector4 other, R liveX)
    {
        if (Equal(other.W, 0)) return sphere;
        R x = liveX - sphere.X, y = (R)other.Y - sphere.Y, z = (R)other.Z - sphere.Z;
        float distance = (float)R.Sqrt((float)((x * x + z * z) + y * y));
        if (Equal(distance, 0)) return sphere with { W = (float)((R)sphere.W > other.W ? (R)sphere.W : other.W) };
        R radius = (((R)sphere.W + other.W) + distance) * .5;
        if (Less(radius, sphere.W)) return sphere;
        if (Less(radius, other.W)) return other with { W = (float)(R)other.W };
        R fraction = (radius - sphere.W) / distance;
        float deltaY = (float)y, deltaZ = (float)z;
        float scaledX = (float)(((R)other.X - sphere.X) * fraction), scaledY = (float)((R)deltaY * fraction);
        return new((float)((R)scaledX + sphere.X), (float)((R)sphere.Y + scaledY),
            (float)((R)sphere.Z + fraction * deltaZ), (float)radius);
    }
}
