using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Shared RenderObj ownership/traversal queries inherited by camera.
/// Virtual targets are read at their native call sites; callbacks remain explicit.</summary>
public static class EnbCurrentRetailRenderObjectQueryStorage
{
    public static float AddFloatingCost(float sum,double cost,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {using var arithmetic=new EnbRetailLineArithmetic.Scope(precision,true);return (float)((R)sum+(R)cost);}
}
