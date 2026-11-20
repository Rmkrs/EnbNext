using System.Numerics;
using NativeReal = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public enum EnbRetailParticleRandomProperty { Opacity, Size, RotationRate, InitialOrientation, Frame, BlurTime }

public static class EnbRetailParticlePropertyRandomTables
{
    //0095EF40 and0095F550..00960920. Construction belongs to the buffer
    //owner, not the draw or birth. A zero table consumes no random words.
    public static float[] CreateScalar(EnbRetailParticleRandomProperty property, float amplitude, uint capacity,
        EnbRetailParticlePropertyRandomStream stream, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        uint threshold = property switch
        {
            EnbRetailParticleRandomProperty.Opacity => 0x3b79096c,
            EnbRetailParticleRandomProperty.Size => 0x2b8cbccc,
            EnbRetailParticleRandomProperty.RotationRate or EnbRetailParticleRandomProperty.InitialOrientation => 0x3991a2b4,
            EnbRetailParticleRandomProperty.Frame => 0x3dcccccd,
            EnbRetailParticleRandomProperty.BlurTime => 0x3727c5ac,
            _ => throw new ArgumentOutOfRangeException(nameof(property))
        };
        if (MathF.Abs(amplitude) < BitConverter.UInt32BitsToSingle(threshold)) return [0];
        float multiplier = BitConverter.UInt32BitsToSingle(property == EnbRetailParticleRandomProperty.RotationRate ? 0x2b03126fu : 0x30000000u);
        float factor = (float)((NativeReal)amplitude * multiplier);
        var table = new float[Count(capacity)];
        for (int i = 0; i < table.Length; i++) table[i] = (float)((NativeReal)unchecked((int)stream.NextUInt32()) * factor);
        return table;
    }

    public static Vector3[] CreateColor(Vector3 amplitude, uint capacity, EnbRetailParticlePropertyRandomStream stream,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        float threshold = BitConverter.UInt32BitsToSingle(0x3b79096c);
        if (MathF.Abs(amplitude.X) < threshold && MathF.Abs(amplitude.Y) < threshold && MathF.Abs(amplitude.Z) < threshold) return [Vector3.Zero];
        float unit = BitConverter.UInt32BitsToSingle(0x30000000);
        Vector3 factors = new((float)((NativeReal)unit * amplitude.X), (float)((NativeReal)unit * amplitude.Y), (float)((NativeReal)unit * amplitude.Z));
        var table = new Vector3[Count(capacity)];
        for (int i = 0; i < table.Length; i++)
        {
            int y = unchecked((int)stream.NextUInt32()), x = unchecked((int)stream.NextUInt32()), z = unchecked((int)stream.NextUInt32());
            table[i] = new((float)((NativeReal)x * factors.X), (float)((NativeReal)y * factors.Y), (float)((NativeReal)z * factors.Z));
        }
        return table;
    }

    private static int Count(uint capacity)
    {
        //008C8F30's arithmetic shift cannot terminate for a negative signed
        //capacity. Such a buffer cannot enter this bounded constructor path.
        if (capacity >= 0x80000000u) throw new InvalidDataException("Retail random-table capacity does not terminate");
        int count = 1;
        while (count < capacity && count < 32) count <<= 1;
        return count;
    }
}
