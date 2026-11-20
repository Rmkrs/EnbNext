using System.Numerics;
namespace EnbNext.Net7.World;

/// <summary>ClientBase+10D4..10E8, consumed by6F4C20/6F4610.
/// Provider IDs here are retail virtual+4 results, not packet/model enums.</summary>
public readonly record struct Net7ObjectSpatialBounds(float MinimumZ,float MaximumZ,
    float MinimumX,float MinimumY,float MaximumX,float MaximumY)
{
    public static Net7ObjectSpatialBounds Initial => new(-2500,2500,-1_000_000,-1_000_000,1_000_000,1_000_000);
    public Vector3 Position(Vector3 position,byte objectType,int retailProviderKind)
    {
        if(objectType!=1 || retailProviderKind is 12 or 13 or 14)return position;
        // Preserve ordered comparisons, including the lower-first branch for
        // inverted bounds; Math.Clamp would reject that native input.
        static float Axis(float value,float low,float high)=>value<low?low:value>high?high:value;
        return new(Axis(position.X,MinimumX,MaximumX),Axis(position.Y,MinimumY,MaximumY),Axis(position.Z,MinimumZ,MaximumZ));
    }
    public Matrix4x4 Transform(Matrix4x4 matrix,byte objectType,int retailProviderKind)
    {
        matrix.Translation=Position(matrix.Translation,objectType,retailProviderKind);
        return matrix;
    }
}
