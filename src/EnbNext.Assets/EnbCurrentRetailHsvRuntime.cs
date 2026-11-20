using System.Buffers.Binary;
using System.Numerics;

namespace EnbNext.Assets;

/// <summary>Current client 00490150 / 004902D0 / 004904B0. Despite the old
/// decompiler names these are HSV, not HSL. Texture and particle colors are
/// adjusted before interpolation, rather than tinting the final fragment.</summary>
public static class EnbCurrentRetailHsvRuntime
{
    public static EnbCurrentRetailParticleColorProperty ShiftParticleColor(
        EnbCurrentRetailParticleColorProperty color, Vector3 shift)
        => new(ShiftRgb(color.Random, shift), Array.AsReadOnly(color.Keys.Select(key =>
            key with { RuntimeRgb = ShiftRgb(key.NormalizedRgb, shift) }).ToArray())) {BeforeRuntimeSetter=color};

    public static Vector3 ShiftRgb(Vector3 rgb, Vector3 shift)
    {
        //004904C5/E5: only an ordered greater comparison selects the left
        //maximum.004904FF/517: less or unordered selects the left minimum.
        float maximum = rgb.X > rgb.Y ? rgb.X : rgb.Y;
        maximum = maximum > rgb.Z ? maximum : rgb.Z;
        float minimum = LessOrUnordered(rgb.X, rgb.Y) ? rgb.X : rgb.Y;
        minimum = LessOrUnordered(minimum, rgb.Z) ? minimum : rgb.Z;
        float delta = maximum - minimum;
        float saturation = EqualOrUnordered(maximum, 0) ? 0 : delta / maximum;
        float value = ClampNative(maximum + shift.Z);
        float hue = -1;
        //00490531/54: unordered equality takes the same sentinel branch as
        //achromatic input. Hue/S shifts are applied only by004905FD.
        if (!EqualOrUnordered(saturation, 0))
        {
            hue = EqualOrUnordered(maximum, rgb.X) ? (rgb.Y - rgb.Z) / delta :
                EqualOrUnordered(maximum, rgb.Y) ? (rgb.Z - rgb.X) / delta + 2 : (rgb.X - rgb.Y) / delta + 4;
            hue *= 60;
            if (LessOrUnordered(hue, 0)) hue += 360;
            if (!LessOrUnordered(hue, 0))
            {
                hue += shift.X;
                saturation += shift.Y;
            }
        }
        if (LessOrUnordered(hue, 0)) hue += 360;
        if (hue > 360) hue -= 360;
        saturation = ClampNative(saturation);
        if (EqualOrUnordered(saturation, 0)) return new(value);
        if (EqualOrUnordered(hue, 360)) hue = 0;
        float sector = hue * (1f / 60f);
        //00A264EC returns a signed64-bit integer; the switch consumes EAX.
        //Masked x87 invalid conversion yields8000000000000000, whose low
        //word is zero. A direct float->int conversion has different behavior.
        double integral = Math.Floor((double)sector);
        long converted = integral < -9223372036854775808d || integral >= 9223372036854775808d
            ? long.MinValue : (long)integral;
        int whole = unchecked((int)converted);
        float fraction = sector - whole;
        float p = (1 - saturation) * value;
        float q = (1 - fraction * saturation) * value;
        float t = (1 - (1 - fraction) * saturation) * value;
        return whole switch
        {
            0 => new(value, t, p), 1 => new(q, value, p), 2 => new(p, value, t),
            3 => new(p, q, value), 4 => new(t, p, value), 5 => new(value, p, q),
            // 004904B0's default switch arm leaves the input RGB unchanged.
            // A float just below 360 can round to sector 6 after multiplication.
            _ => rgb
        };
    }

    /// <summary>0094AC00 -> 009A0D30, same-format DDS mip branch. Adjust RGB565
    /// endpoints, retaining every selector and DXT5 alpha block. Recoloring
    /// decoded pixels would change the native interpolation/quantization order.</summary>
    public static bool TryRecolorDds(ReadOnlySpan<byte> source, Vector3 shift, out byte[] recolored)
    {
        recolored = [];
        if (source.Length < 128) return false;
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(84, 4));
        bool dxt1 = fourCc == 0x31545844;
        EnbDdsTexture decoded;
        if (dxt1 ? !EnbDdsTexture.TryDecodeDxt1(source, out decoded) :
            !EnbDdsTexture.TryDecodeDxt5(source, out decoded)) return false;
        recolored = source.ToArray();
        if (!ChangesDdsColor(shift)) return true;
        int offset = 128, blockSize = dxt1 ? 8 : 16;
        foreach (var mip in decoded.MipLevels)
        {
            int count = ((mip.Width + 3) / 4) * ((mip.Height + 3) / 4);
            for (int i = 0; i < count; i++, offset += blockSize)
            {
                int colors = offset + (dxt1 ? 0 : 8);
                for (int endpoint = 0; endpoint < 2; endpoint++)
                {
                    var target = recolored.AsSpan(colors + endpoint * 2, 2);
                    ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(target);
                    BinaryPrimitives.WriteUInt16LittleEndian(target, ShiftRgb565(packed, shift));
                }
            }
        }
        return true;
    }

    /// <summary>009A0D30's source20/destination21 equal-dimension branch.
    /// Every block receives opaque DXT2 alpha and the original selector word;
    /// nonzero HSV changes its two endpoints before their565 packing.</summary>
    public static bool TryConvertDxt1ToDxt2(ReadOnlySpan<byte> source,Vector3 shift,out byte[] converted)
    {
        converted=[];
        if(!EnbDdsTexture.TryDecodeDxt1(source,out var decoded))return false;
        long blockCount=decoded.MipLevels.Sum(mip=>(long)((mip.Width+3)/4)*((mip.Height+3)/4));
        if(blockCount>(int.MaxValue-128)/16)return false;
        converted=new byte[128+(int)blockCount*16];
        source[..128].CopyTo(converted);
        BinaryPrimitives.WriteUInt32LittleEndian(converted.AsSpan(84),0x32545844);
        int inputOffset=128,outputOffset=128;
        bool changes=ChangesDdsColor(shift);
        for(long block=0;block<blockCount;block++,inputOffset+=8,outputOffset+=16)
        {
            var output=converted.AsSpan(outputOffset,16);
            BinaryPrimitives.WriteUInt64LittleEndian(output,ulong.MaxValue);
            source.Slice(inputOffset,8).CopyTo(output[8..]);
            if(changes)
                for(int endpoint=0;endpoint<2;endpoint++)
                {
                    var word=output.Slice(8+endpoint*2,2);
                    BinaryPrimitives.WriteUInt16LittleEndian(word,ShiftRgb565(BinaryPrimitives.ReadUInt16LittleEndian(word),shift));
                }
        }
        return true;
    }

    internal static ushort ShiftRgb565(ushort packed, Vector3 shift)
    {
        // Native expands with zero low bits, not bit replication.
        var rgb = new Vector3((packed & 0xf800) >> 8, (packed & 0x7e0) >> 3, (packed & 31) << 3) * (1f / 255f);
        var changed = ShiftRgb(rgb, shift);
        //009A0E97..009A0F0E selects RC truncate before the PC24 FMUL,
        //FISTP dword and unmasked RGB word OR. The565 consumer extracts
        //bytes from that combined word, including overflow/negative bits.
        int packedRgb = TruncateColorByte(changed.X) << 16 |
            TruncateColorByte(changed.Y) << 8 | TruncateColorByte(changed.Z);
        return (ushort)(((packedRgb >> 16 & 248) << 8) |
            ((packedRgb >> 8 & 252) << 3) | ((packedRgb & 255) >> 3));
    }

    private static int TruncateColorByte(float value)
    {
        double exact = (double)value * 255;
        float product = (float)exact;
        if (product > 0 && product > exact) product = MathF.BitDecrement(product);
        else if (product < 0 && product < exact) product = MathF.BitIncrement(product);
        return !float.IsFinite(product) || product < -2147483648d || product >= 2147483648d
            ? int.MinValue : (int)product;
    }

    /// <summary>0048FFB0 uses the authored3B808081 reciprocal, not division.</summary>
    public static Vector4 UnpackMeshColor(uint color)
    {
        float reciprocal=BitConverter.UInt32BitsToSingle(0x3b808081);
        return new(((color>>16)&255)*reciprocal,((color>>8)&255)*reciprocal,
            (color&255)*reciprocal,(color>>24)*reciprocal);
    }

    /// <summary>00490070 followed by0048D38F..0048D3D2 at PC24 and RC
    /// truncate. The dispatch flag comes from CPUID.1:EDX bit15 at009D6296.</summary>
    public static uint PackMeshColor(Vector4 color,bool conditionalMoveClamp)
    {
        float Clamp(float value)
        {
            if(!conditionalMoveClamp)return LessOrUnordered(value,0)?0:value>1?1:value;
            uint bits=BitConverter.SingleToUInt32Bits(value);
            if((bits&0x80000000)!=0)bits=0;
            return BitConverter.UInt32BitsToSingle(Math.Min(bits,0x3f800000u));
        }
        return unchecked((uint)(TruncateColorByte(Clamp(color.W))<<24|TruncateColorByte(Clamp(color.X))<<16|
            TruncateColorByte(Clamp(color.Y))<<8|TruncateColorByte(Clamp(color.Z))));
    }

    public static uint ShiftMeshColor(uint color,Vector3 shift,bool conditionalMoveClamp)
    {
        var normalized=UnpackMeshColor(color);
        var changed=ShiftRgb(new(normalized.X,normalized.Y,normalized.Z),shift);
        return PackMeshColor(new(changed,normalized.W),conditionalMoveClamp);
    }

    internal static uint ShiftArgb8(uint color, Vector3 shift)
    {
        if (!ChangesDdsColor(shift)) return color;
        var rgb = new Vector3((color >> 16) & 255, (color >> 8) & 255, color & 255) * (1f / 255f);
        var changed = ShiftRgb(rgb, shift);
        float alpha = (color >> 24) * (1f / 255f);
        return unchecked((uint)(TruncateColorByte(alpha) << 24 | TruncateColorByte(changed.X) << 16 |
            TruncateColorByte(changed.Y) << 8 | TruncateColorByte(changed.Z)));
    }

    private static bool LessOrUnordered(float left, float right)
        => left < right || float.IsNaN(left) || float.IsNaN(right);
    private static bool EqualOrUnordered(float left, float right)
        => left == right || float.IsNaN(left) || float.IsNaN(right);
    private static float ClampNative(float value)
        => LessOrUnordered(value, 0) ? 0 : value > 1 ? 1 : value;
    //009A0D67..D97 tests x87 C3, so NaN compares equal to zero for
    //this gate. Infinity still requests recoloring.
    internal static bool ChangesDdsColor(Vector3 shift)
        => !EqualOrUnordered(shift.X, 0) || !EqualOrUnordered(shift.Y, 0) || !EqualOrUnordered(shift.Z, 0);
}
