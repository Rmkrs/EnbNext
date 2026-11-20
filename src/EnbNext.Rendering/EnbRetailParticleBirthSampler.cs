using System.Numerics;
using EnbNext.Assets;
using Register = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbRetailParticleBirthSampler
{
    //0097E810/0097ECB0 consume already constructed/scaled randomizers and
    //velocity/outward values in units/ms. Null objects consume no words.
    public static (Vector3 Position, Vector3 VelocityPerMillisecond) Sample(
        EnbCurrentRetailParticleRandomizer? creation, EnbCurrentRetailParticleRandomizer? velocity,
        Vector3 constantVelocityPerMillisecond, float outwardVelocityPerMillisecond,
        bool localSpace, Quaternion rotation, Vector3 translation, Vector3 inheritedVelocityPerMillisecond,
        EnbRetailParticleBirthRandomStream stream, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        Vector3 position = creation is {} c ? EnbRetailParticleRandomizer.Sample(c, stream, precision) : Vector3.Zero;
        Vector3 randomVelocity = velocity is {} v ? EnbRetailParticleRandomizer.Sample(v, stream, precision) : Vector3.Zero;
        if (outwardVelocityPerMillisecond != 0 && !float.IsNaN(outwardVelocityPerMillisecond))
        {
            Register square = localSpace
                ? (Register)position.X * position.X + (Register)position.Y * position.Y + (Register)position.Z * position.Z
                : (Register)position.Y * position.Y + (Register)position.Z * position.Z + (Register)position.X * position.X;
            Register outwardX;
            float outwardY, outwardZ;
            if (square == 0 || double.IsNaN((double)square))
            { outwardX = outwardVelocityPerMillisecond; outwardY = outwardZ = 0; }
            else
            {
                Register factor = EnbRetailLineCoalescing.ReciprocalSquareRoot((float)square) * outwardVelocityPerMillisecond;
                outwardX = factor * position.X;
                outwardY = (float)(factor * position.Y);
                outwardZ = (float)(factor * position.Z);
            }
            randomVelocity = new((float)((Register)randomVelocity.X + outwardX),
                (float)((Register)randomVelocity.Y + outwardY), (float)((Register)randomVelocity.Z + outwardZ));
        }
        if (localSpace)
            return (position, new((float)((Register)constantVelocityPerMillisecond.X + randomVelocity.X),
                (float)((Register)constantVelocityPerMillisecond.Y + randomVelocity.Y),
                (float)((Register)constantVelocityPerMillisecond.Z + randomVelocity.Z)));
        //The world writer retains these additions into the quaternion product.
        return (EnbCurrentRetailParticleBirthTransform.TransformRetained(rotation,position.X,position.Y,position.Z,translation,false),
            EnbCurrentRetailParticleBirthTransform.TransformRetained(rotation,
                (Register)randomVelocity.X + constantVelocityPerMillisecond.X,
                (Register)randomVelocity.Y + constantVelocityPerMillisecond.Y,
                (Register)randomVelocity.Z + constantVelocityPerMillisecond.Z,inheritedVelocityPerMillisecond,true));
    }
}
