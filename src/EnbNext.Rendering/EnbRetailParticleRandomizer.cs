using System.Numerics;
using EnbNext.Assets;
using NativeReal = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbRetailParticleRandomizer
{
    // Vtable+10:00A15B80/00A15DA0/00A15F60/00A16180. Unused words survive.
    public static EnbCurrentRetailParticleRandomizer Scale(EnbCurrentRetailParticleRandomizer randomizer,
        float scale, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        if (!(scale > 0)) scale = 0;
        Vector3 values = randomizer.Values;
        float x = (float)((NativeReal)scale * values.X);
        return randomizer.ClassId switch
        {
            0 => randomizer with { Values = new(x, (float)((NativeReal)scale * values.Y), (float)((NativeReal)scale * values.Z)) },
            1 or 2 => randomizer with { Values = new(x, values.Y, values.Z) },
            3 => randomizer with { Values = new(x, (float)((NativeReal)scale * values.Y), values.Z) },
            _ => throw new InvalidDataException($"Unknown retail particle randomizer class {randomizer.ClassId}")
        };
    }

    //00A15AE0/00A15C70/00A15E80/00A16070 consume the shared009D0250 stream.
    public static Vector3 Sample(EnbCurrentRetailParticleRandomizer randomizer,
        EnbRetailParticleBirthRandomStream stream, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        NativeReal Next() => (NativeReal)unchecked((int)stream.NextUInt32()) * BitConverter.UInt32BitsToSingle(0x30000000);
        Vector3 values = randomizer.Values;
        if (randomizer.ClassId == 0)
            return new((float)(Next() * values.X), (float)(Next() * values.Y), (float)(Next() * values.Z));
        if (randomizer.ClassId == 1)
        {
            float radiusSquared = (float)((NativeReal)values.X * values.X);
            while (true)
            {
                float x = (float)(Next() * values.X), y = (float)(Next() * values.X);
                NativeReal z = Next() * values.X;
                NativeReal square = (NativeReal)x * x + (NativeReal)y * y + z * z;
                if (!(square > radiusSquared)) return new(x, y, (float)z);
            }
        }
        if (randomizer.ClassId == 2)
        {
            while (true)
            {
                float x = (float)Next(), y = (float)Next();
                NativeReal z = Next();
                float square = (float)((NativeReal)x * x + (NativeReal)y * y + z * z);
                if (square > 1 || !(square > 0)) continue;
                NativeReal factor = EnbRetailLineCoalescing.ReciprocalSquareRoot(square) * values.X;
                return new((float)(factor * x), (float)(factor * y), (float)(factor * (float)z));
            }
        }
        if (randomizer.ClassId == 3)
        {
            float x = (float)(Next() * values.X);
            float radiusSquared = (float)((NativeReal)values.Y * values.Y);
            while (true)
            {
                float y = (float)(Next() * values.Y);
                NativeReal z = Next() * values.Y;
                if (!(z * z + (NativeReal)y * y > radiusSquared)) return new(x, y, (float)z);
            }
        }
        throw new InvalidDataException($"Unknown retail particle randomizer class {randomizer.ClassId}");
    }
}
