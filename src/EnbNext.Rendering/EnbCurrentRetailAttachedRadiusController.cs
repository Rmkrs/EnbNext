using System.Numerics;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>The retained +60 value of729FC0/72A6C0, refreshed by72CC10.</summary>
public sealed class EnbCurrentRetailAttachedRadiusController
{
    public float Radius { get; private set; }
    public Action? Changed { get; set; }

    // Callers supply the currently mounted direct entries, including an entry
    // being removed until72C1D0 erases it. Reading Radius never queries a pose.
    public void Refresh(float? ownRadius, bool hasModel, Vector3 origin,
        IEnumerable<(float Radius, Vector3 Pivot)> children)
    {
        if (ownRadius is null)
        {
            if (Radius == 0f) return;
            Radius = 0f;
            Changed?.Invoke();
            return;
        }
        float radius = ownRadius.Value;
        if (hasModel)
        {
            using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            foreach (var child in children)
            {
                R x = (R)origin.X - child.Pivot.X;
                R y = (R)origin.Y - child.Pivot.Y;
                R z = (R)origin.Z - child.Pivot.Z;
                float square = (float)(z * z + y * y + x * x);
                float distance = (float)R.Sqrt((R)square);
                float expanded = (float)((R)child.Radius + distance);
                if (expanded > radius) radius = expanded;
            }
        }
        Radius = radius;
        //72CC10 publishes even an unchanged nonnull BoneMap result.
        Changed?.Invoke();
    }
}
