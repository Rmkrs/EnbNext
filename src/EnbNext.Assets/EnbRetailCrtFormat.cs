using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Assets;

/// <summary>
/// The statically linked VS2003 CRT sprintf of client.exe (00A266DA, _output 00A2EA38) for a format
/// whose only variadic argument is one float promoted to double. Every helper below is a port of the
/// named native routine; the result is the exact byte sequence sprintf writes (Latin-1 chars).
/// </summary>
public static class EnbRetailCrtFormat
{
    /// <summary>
    /// Formats <paramref name="value"/> exactly as sprintf(buffer, format, (double)value) does.
    /// Returns null when the native call has no defined result: a conversion that reads past the
    /// one double argument, dereferences a non-null argument as a pointer, stores through %n, or
    /// writes more than <paramref name="capacity"/> bytes (terminating NUL included) or past
    /// _output's 512-byte conversion buffer.
    /// </summary>
    public static string? Format(string format, float value, int capacity = int.MaxValue)
    {
        var output = new Output(capacity);
        return Run(format, Promote(value), ref output) ? output.Text.ToString() : null;
    }

    // x87 FLD/FSTP promotion. A signaling NaN is quieted; every float maps to a normal double.
    private static ulong Promote(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        ulong sign = (ulong)(bits >> 31) << 63;
        uint exponent = (bits >> 23) & 0xff, fraction = bits & 0x7fffff;
        if (exponent == 0xff)
            return sign | 0x7ff0000000000000UL | (ulong)(fraction == 0 ? 0 : fraction | 0x400000) << 29;
        if (exponent == 0)
        {
            if (fraction == 0) return sign;
            int shift = 0;
            while ((fraction & 0x800000) == 0) { fraction <<= 1; shift++; }
            return sign | (ulong)(1023 - 126 - shift) << 52 | (ulong)(fraction & 0x7fffff) << 29;
        }
        return sign | (ulong)(exponent - 127 + 1023) << 52 | (ulong)fraction << 29;
    }

    private struct Output(int capacity)
    {
        public readonly StringBuilder Text = new();
        public bool Overflow;
        public void Put(char ch)
        {
            if (Text.Length + 1 >= capacity) { Overflow = true; return; }
            Text.Append(ch);
        }
        public void Pad(char ch, int count) { for (; count > 0 && !Overflow; count--) Put(ch); }
    }

    private struct Arguments(ulong bits)
    {
        private int _offset;
        public bool Undefined;
        public uint Int32()
        {
            if (_offset + 4 > 8) { Undefined = true; return 0; }
            uint value = (uint)(bits >> (_offset * 8)); _offset += 4; return value;
        }
        public ulong Int64()
        {
            if (_offset != 0) { Undefined = true; return 0; }
            _offset = 8; return bits;
        }
    }

    // 00B17B98: class nibble (indexed by char - ' ') and next-state nibble (class * 8 + state).
    private static ReadOnlySpan<byte> Table =>
    [
        0x06,0x00,0x00,0x06,0x00,0x01,0x00,0x00,0x10,0x00,0x03,0x06,0x00,0x06,0x02,0x10,
        0x04,0x45,0x45,0x45,0x05,0x05,0x05,0x05,0x05,0x35,0x30,0x00,0x50,0x00,0x00,0x00,
        0x00,0x20,0x28,0x38,0x50,0x58,0x07,0x08,0x00,0x37,0x30,0x30,0x57,0x50,0x07,0x00,
        0x00,0x20,0x20,0x08,0x00,0x00,0x00,0x00,0x08,0x60,0x68,0x60,0x60,0x60,0x60,0x00,
        0x00,0x70,0x70,0x78,0x78,0x78,0x78,0x08,0x07,0x08,0x00,0x00,0x07,0x00,0x08,0x08,
        0x08,0x00,0x00,0x08,0x00,0x08,0x00,0x07,0x08,
    ];

    private static bool Run(string format, ulong argumentBits, ref Output output)
    {
        var args = new Arguments(argumentBits);
        int state = 0, flags = 0, width = 0, precision = -1, prefixLength = 0, hexAdjust = 0, textLength = 0;
        bool caps = false, suppressed = false, wideText = false;
        char prefix0 = '\0', prefix1 = '\0';
        for (int i = 0; i < format.Length && format[i] != '\0';)
        {
            char ch = format[i++];
            int cls = ch < ' ' || ch > 'x' ? 0 : Table[ch - ' '] & 0xf;
            state = Table[cls * 8 + state] >> 4;
            switch (state)
            {
                case 0:
                    // C-locale _pctype marks no lead bytes.
                    output.Put(ch);
                    break;
                case 1:
                    precision = -1; caps = false; suppressed = false; width = 0; prefixLength = 0; flags = 0; wideText = false;
                    break;
                case 2:
                    flags |= ch switch { ' ' => 2, '#' => 0x80, '+' => 1, '-' => 4, '0' => 8, _ => 0 };
                    break;
                case 3:
                    if (ch == '*')
                    {
                        width = unchecked((int)args.Int32());
                        if (width < 0) { flags |= 4; width = unchecked(-width); }
                    }
                    else width = unchecked(ch - '0' + width * 10);
                    break;
                case 4:
                    precision = 0;
                    break;
                case 5:
                    if (ch == '*') { precision = unchecked((int)args.Int32()); if (precision < 0) precision = -1; }
                    else precision = unchecked(ch - '0' + precision * 10);
                    break;
                case 6:
                    if (ch == 'I')
                    {
                        if (i + 1 < format.Length && format[i] == '6' && format[i + 1] == '4') { i += 2; flags |= 0x8000; }
                        else { state = 0; output.Put(ch); }
                    }
                    else flags |= ch switch { 'h' => 0x20, 'l' => 0x10, 'w' => 0x800, _ => 0 };
                    break;
                case 7:
                    string text = string.Empty;
                    switch (ch)
                    {
                        case 'C' or 'c':
                            if (ch == 'C' && (flags & 0x830) == 0) flags |= 0x800;
                            uint character = args.Int32();
                            if ((flags & 0x810) == 0) text = ((char)(byte)character).ToString();
                            // 00A3B6DA's C-locale branch converts only code points below 0x100.
                            else if ((ushort)character < 0x100) text = ((char)(ushort)character).ToString();
                            else suppressed = true;
                            textLength = suppressed ? -1 : 1;
                            break;
                        case 'E' or 'G' or 'e' or 'f' or 'g':
                            if (ch is 'E' or 'G') { caps = true; ch = (char)(ch + 0x20); }
                            flags |= 0x40;
                            if (precision < 0) precision = 6;
                            else if (precision == 0 && ch == 'g') precision = 1;
                            ulong bits = args.Int64();
                            if (args.Undefined) return false;
                            string? converted = FloatingPoint.Convert(bits, ch, precision, caps,
                                (flags & 0x80) != 0 && precision == 0, ch == 'g' && (flags & 0x80) == 0);
                            if (converted is null) return false;
                            if (converted.Length > 0 && converted[0] == '-') { flags |= 0x100; converted = converted[1..]; }
                            text = converted; textLength = text.Length;
                            break;
                        case 'S' or 's':
                            if (ch == 'S' && (flags & 0x830) == 0) flags |= 0x800;
                            int limit = precision == -1 ? int.MaxValue : precision;
                            if (args.Int32() != 0) return false; // non-null pointer: undefined read
                            if ((flags & 0x810) != 0) wideText = true;
                            text = limit < 0 ? "(null)" : "(null)"[..Math.Min(limit, 6)];
                            textLength = text.Length;
                            break;
                        case 'Z':
                            if (args.Int32() != 0) return false;
                            text = "(null)"; textLength = 6;
                            break;
                        case 'n':
                            return false;
                        case 'd' or 'i' or 'o' or 'p' or 'u' or 'X' or 'x':
                            int radix = 10;
                            if (ch is 'd' or 'i') flags |= 0x40;
                            else if (ch == 'o') { radix = 8; if ((flags & 0x80) != 0) flags |= 0x200; }
                            else if (ch is 'p' or 'X' or 'x')
                            {
                                if (ch == 'p') precision = 8;
                                hexAdjust = ch == 'x' ? 0x27 : 7; radix = 16;
                                if ((flags & 0x80) != 0) { prefix0 = '0'; prefix1 = (char)(hexAdjust + 'Q'); prefixLength = 2; }
                            }
                            ulong number;
                            if ((flags & 0x8000) != 0) number = args.Int64();
                            else
                            {
                                uint raw = args.Int32();
                                number = (flags & 0x20) != 0
                                    ? (flags & 0x40) != 0 ? (ulong)(long)(short)raw : raw & 0xffff
                                    : (flags & 0x40) != 0 ? (ulong)(long)(int)raw : raw;
                            }
                            if (args.Undefined) return false;
                            if ((flags & 0x40) != 0 && (long)number < 0) { flags |= 0x100; number = unchecked((ulong)-(long)number); }
                            if ((flags & 0x8000) == 0) number &= 0xffffffff;
                            if (precision < 0) precision = 1; else flags &= ~8;
                            if (number == 0) prefixLength = 0;
                            // Digits are written backwards from the end of the 512-byte buffer.
                            if (precision > 512) return false;
                            var digits = new StringBuilder();
                            while (precision > 0 || number != 0)
                            {
                                precision--;
                                int digit = (int)(number % (ulong)radix) + '0';
                                number /= (ulong)radix;
                                if (digit > '9') digit += hexAdjust;
                                digits.Insert(0, (char)digit);
                            }
                            if ((flags & 0x200) != 0 && (digits.Length == 0 || digits[0] != '0')) digits.Insert(0, '0');
                            if (digits.Length > 512) return false;
                            text = digits.ToString(); textLength = text.Length;
                            break;
                        default:
                            // A type character without a handler ('B') outputs the previous field length;
                            // only a first conversion (length 0) reads nothing.
                            if (textLength != 0) return false;
                            break;
                    }
                    if (args.Undefined) return false;
                    if (!suppressed)
                    {
                        if ((flags & 0x40) != 0 && ((flags & 0x100) != 0 || (flags & 3) != 0))
                        {
                            prefix0 = (flags & 0x100) != 0 ? '-' : (flags & 1) != 0 ? '+' : ' ';
                            prefixLength = 1;
                        }
                        int padding = unchecked(width - prefixLength - text.Length);
                        if ((flags & 0xc) == 0) output.Pad(' ', padding);
                        if (prefixLength > 0) output.Put(prefix0);
                        if (prefixLength > 1) output.Put(prefix1);
                        if ((flags & 8) != 0 && (flags & 4) == 0) output.Pad('0', padding);
                        foreach (char c in text) { if (wideText && c >= 0x100) break; output.Put(c); }
                        if ((flags & 4) != 0) output.Pad(' ', padding);
                    }
                    break;
            }
            if (output.Overflow) return false;
        }
        return !output.Overflow;
    }

    /// <summary>__cfltcvt 00A2E7BC with _cftoe/_cftof/_cftog, _fltout2 and $I10_OUTPUT.</summary>
    private static class FloatingPoint
    {
        private const int BufferSize = 512;

        private ref struct Buffer
        {
            public Span<char> Chars;
            public int Highest;
            public char this[int index]
            {
                get => Chars[index];
                set { Chars[index] = value; if (index + 1 > Highest) Highest = index + 1; }
            }
            public int Length(int start) { int end = start; while (Chars[end] != '\0') end++; return end - start; }
            // 00A2E80D
            public void Shift(int start, int count)
            {
                if (count == 0) return;
                int length = Length(start);
                for (int i = length; i >= 0; i--) this[start + count + i] = Chars[start + i];
            }
        }

        private struct Decimal
        {
            public char Sign;
            public int Point;
            public string Mantissa;
        }

        public static string? Convert(ulong bits, char kind, int precision, bool caps, bool forcePoint, bool cropZeros)
        {
            // Every write is at most a few dozen bytes beyond the precision, so a larger
            // precision always overruns _output's 512-byte buffer.
            if (precision > 4 * BufferSize) return null;
            var buffer = new Buffer { Chars = new char[precision + 128] };
            Decimal value = Output(bits);
            switch (kind)
            {
                case 'e':
                    ToString(ref buffer, (value.Sign == '-' ? 1 : 0) + (precision > 0 ? 1 : 0), precision + 1, ref value);
                    Exponential(ref buffer, precision, caps, ref value, false);
                    break;
                case 'f':
                    ToString(ref buffer, value.Sign == '-' ? 1 : 0, value.Point + precision, ref value);
                    Fixed(ref buffer, precision, ref value, false);
                    break;
                default:
                    int before = value.Point - 1, start = value.Sign == '-' ? 1 : 0;
                    ToString(ref buffer, start, precision, ref value);
                    int magnitude = value.Point - 1;
                    if (magnitude < -4 || magnitude >= precision) Exponential(ref buffer, precision, caps, ref value, true);
                    else
                    {
                        if (before < magnitude) buffer[start + buffer.Length(start) - 1] = '\0';
                        Fixed(ref buffer, precision, ref value, true);
                    }
                    break;
            }
            if (forcePoint) ForceDecimalPoint(ref buffer);
            if (cropZeros) CropZeros(ref buffer);
            if (buffer.Highest > BufferSize) return null;
            return new string(buffer.Chars[..buffer.Length(0)]);
        }

        // 00A3D6A4 _fptostr
        private static void ToString(ref Buffer buffer, int start, int digits, ref Decimal value)
        {
            string mantissa = value.Mantissa;
            int m = 0, p = start + 1;
            buffer[start] = '0';
            int remaining = digits;
            if (digits > 0)
            {
                for (; remaining > 0; remaining--)
                {
                    char c = m < mantissa.Length ? mantissa[m] : '\0';
                    if (c == '\0') c = '0'; else m++;
                    buffer[p++] = c;
                }
            }
            buffer[p] = '\0';
            char next = m < mantissa.Length ? mantissa[m] : '\0';
            if (remaining >= 0 && (sbyte)next >= '5')
            {
                p--;
                while (buffer[p] == '9') buffer[p--] = '0';
                buffer[p] = (char)(buffer[p] + 1);
            }
            if (buffer[start] == '1') value.Point++;
            else
            {
                int length = buffer.Length(start + 1);
                for (int i = 0; i <= length; i++) buffer[start + i] = buffer[start + 1 + i];
            }
        }

        // 00A2E56B _cftoe2
        private static void Exponential(ref Buffer buffer, int precision, bool caps, ref Decimal value, bool general)
        {
            if (general) buffer.Shift(value.Sign == '-' ? 1 : 0, precision > 0 ? 1 : 0);
            int p = 0;
            if (value.Sign == '-') { buffer[0] = '-'; p = 1; }
            if (precision > 0) { buffer[p] = buffer[p + 1]; p++; buffer[p] = '.'; }
            int q = p + precision + (general ? 0 : 1);
            foreach (char c in "e+000\0") buffer[q++] = c;
            q -= 6;
            if (caps) buffer[q] = 'E';
            q++;
            if (value.Mantissa[0] != '0')
            {
                int exponent = value.Point - 1;
                if (exponent < 0) { exponent = -exponent; buffer[q] = '-'; }
                q++;
                if (exponent >= 100) { buffer[q] = (char)(byte)(buffer[q] + exponent / 100); exponent %= 100; }
                q++;
                if (exponent >= 10) { buffer[q] = (char)(byte)(buffer[q] + exponent / 10); exponent %= 10; }
                buffer[q + 1] = (char)(byte)(buffer[q + 1] + exponent);
            }
        }

        // 00A2E682 _cftof2
        private static void Fixed(ref Buffer buffer, int precision, ref Decimal value, bool general)
        {
            int sign = value.Sign == '-' ? 1 : 0;
            if (general && value.Point - 1 == precision)
            {
                buffer[sign + value.Point - 1] = '0';
                buffer[sign + value.Point] = '\0';
            }
            int p = 0;
            if (sign != 0) { buffer[0] = '-'; p = 1; }
            if (value.Point < 1) { buffer.Shift(p, 1); buffer[p] = '0'; p++; }
            else p += value.Point;
            if (precision > 0)
            {
                buffer.Shift(p, 1);
                buffer[p] = '.';
                p++;
                if (value.Point < 0)
                {
                    int zeros = general || precision >= -value.Point ? -value.Point : precision;
                    buffer.Shift(p, zeros);
                    for (int i = 0; i < zeros; i++) buffer[p + i] = '0';
                }
            }
        }

        // 00A2E40C _forcdecpt
        private static void ForceDecimalPoint(ref Buffer buffer)
        {
            int p = 0;
            if (buffer[0] is not ('e' or 'E'))
                do p++; while (buffer[p] is >= '0' and <= '9');
            char hold = buffer[p];
            buffer[p] = '.';
            char written;
            do
            {
                p++;
                char next = buffer[p];
                buffer[p] = hold;
                written = hold;
                hold = next;
            } while (written != '\0');
        }

        // 00A2E466 _cropzeros
        private static void CropZeros(ref Buffer buffer)
        {
            int p = 0;
            while (buffer[p] != '\0' && buffer[p] != '.') p++;
            if (buffer[p++] == '\0') return;
            while (buffer[p] != '\0' && buffer[p] != 'e' && buffer[p] != 'E') p++;
            int source = p;
            do p--; while (buffer[p] == '0');
            if (buffer[p] == '.') p--;
            char c;
            do { c = buffer[source++]; buffer[++p] = c; } while (c != '\0');
        }

        // 00A3D71B _fltout2 = 00A3D777 __dtold + 00A3F4C7 $I10_OUTPUT(ld, 17, 0)
        private static Decimal Output(ulong bits)
        {
            // __dtold
            ushort top = (ushort)(bits >> 48);
            uint biased = (uint)(top & 0x7ff0) >> 4, low = (uint)bits, high = (uint)(bits >> 32);
            uint manHigh = 0x80000000, manLow;
            int exponent;
            if (biased == 0)
            {
                if ((high & 0xfffff) == 0 && low == 0) return new Decimal { Sign = ' ', Point = 0, Mantissa = "0" };
                exponent = 0x3c01; manHigh = 0;
            }
            else exponent = biased == 0x7ff ? 0x7fff : (int)biased + 0x3c00;
            manHigh = low >> 21 | (high & 0xfffff) << 11 | manHigh;
            manLow = low << 11;
            while ((manHigh & 0x80000000) == 0) { manHigh = manLow >> 31 | manHigh << 1; manLow <<= 1; exponent--; }
            ushort expn = (ushort)(exponent & 0x7fff);
            char sign = (top & 0x8000) != 0 ? '-' : ' ';
            // $I10_OUTPUT
            if (expn == 0x7fff)
            {
                string special;
                if ((manHigh == 0x80000000 && manLow == 0) || (manHigh & 0x40000000) != 0)
                    special = sign == '-' && manHigh == 0xc0000000 ? manLow == 0 ? "1#IND" : "1#QNAN"
                        : manHigh == 0x80000000 && manLow == 0 ? "1#INF" : "1#QNAN";
                else special = "1#SNAN";
                return new Decimal { Sign = sign, Point = 1, Mantissa = special };
            }
            int point = unchecked((short)(((((uint)expn >> 8) + (manHigh >> 24) * 2) * 0x4d - 0x134312f4 + (uint)expn * 0x4d10) >> 16));
            Span<byte> number = stackalloc byte[14];
            number.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(number[2..], manLow);
            BinaryPrimitives.WriteUInt32LittleEndian(number[6..], manHigh);
            BinaryPrimitives.WriteUInt16LittleEndian(number[10..], expn);
            MultiplyTenPower(number, -point);
            if (BinaryPrimitives.ReadUInt16LittleEndian(number[10..]) > 0x3ffe)
            {
                point++;
                Multiply(number, [0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xfb, 0x3f]);
            }
            int shift = BinaryPrimitives.ReadUInt16LittleEndian(number[10..]) - 0x3ffe;
            number[10] = number[11] = 0;
            for (int i = 0; i < 8; i++) ShiftLeft(number);
            if (shift < 0) for (uint i = (uint)-shift & 0xff; i != 0; i--) ShiftRight(number);
            Span<char> digits = stackalloc char[18];
            Span<byte> copy = stackalloc byte[12];
            for (int i = 0; i < digits.Length; i++)
            {
                number[..12].CopyTo(copy);
                ShiftLeft(number); ShiftLeft(number); Add(number, copy); ShiftLeft(number);
                digits[i] = (char)(number[11] + '0');
                number[11] = 0;
            }
            int last = 16;
            if (digits[17] > '4')
            {
                while (last >= 0 && digits[last] == '9') digits[last--] = '0';
                if (last < 0) { last = 0; point++; }
                digits[last]++;
            }
            else
            {
                while (last >= 0 && digits[last] == '0') last--;
                if (last < 0) return new Decimal { Sign = ' ', Point = 0, Mantissa = "0" };
            }
            return new Decimal { Sign = sign, Point = point, Mantissa = new string(digits[..(last + 1)]) };
        }

        // 00BD3010 (+10^n) and 00BD3170 (-10^n): two base-eight groups reach every promoted binary32.
        private static ReadOnlySpan<byte> Positive => Convert(
            "000000000000000000a00240000000000000000000c80540000000000000000000fa08400000000000000000409c0c40" +
            "000000000000000050c30f40000000000000000024f412400000000000000080969816400000000000000020bcbe1940" +
            "000000000004bfc91b8e3440000000a1edccce1bc2d34e4020f09eb5702ba8adc59d6940d05dfd25e51a8e4f19eb8340" +
            "7196d795430e058d29af9e40f9bfa044ed81128f8182b940");
        private static ReadOnlySpan<byte> Negative => Convert(
            "cdcccdccccccccccccccfb3f713d0ad7a3703d0ad7a3f83f5a643bdf4f8d976e1283f53fc3d32c6519e25817b7d1f13f" +
            "d00f2384471b47acc5a7ee3f40a6b6696caf05bd3786eb3f333dbc427ae5d594bfd6e73fc2fdfdce61841177ccabe43f" +
            "2f4c5be14dc4be9495e6c93f92c4533b7544cd14be9aaf3fde67ba943945ad1eb1cf943f2423c6e2bcba3b31618b7a3f" +
            "615559c17eb1537c12bb5f3fd7ee2f8d06be928515fb443f");
        private static byte[] Convert(string hex) => System.Convert.FromHexString(hex);

        // 00A3F97A __multtenpow12(ld, power, 1)
        private static void MultiplyTenPower(Span<byte> number, int power)
        {
            if (power == 0) return;
            ReadOnlySpan<byte> table = Positive;
            if (power < 0) { power = -power; table = Negative; }
            Span<byte> factor = stackalloc byte[12];
            for (int group = 0; power != 0; group++)
            {
                int last = power & 7;
                power >>= 3;
                if (last == 0) continue;
                table.Slice((group * 7 + last - 1) * 12, 12).CopyTo(factor);
                if (BinaryPrimitives.ReadUInt16LittleEndian(factor) > 0x7fff)
                    BinaryPrimitives.WriteUInt32LittleEndian(factor[2..], BinaryPrimitives.ReadUInt32LittleEndian(factor[2..]) - 1);
                Multiply(number, factor);
            }
        }

        // 00A3F75A __ld12mul
        private static void Multiply(Span<byte> x, ReadOnlySpan<byte> y)
        {
            ushort ex = BinaryPrimitives.ReadUInt16LittleEndian(x[10..]), ey = BinaryPrimitives.ReadUInt16LittleEndian(y[10..]);
            ushort sign = (ushort)((ex ^ ey) & 0x8000);
            ex &= 0x7fff; ey &= 0x7fff;
            ushort sum = (ushort)(ex + ey);
            if (ex >= 0x7fff || ey >= 0x7fff || sum > 0xbffd)
            {
                x[..12].Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(x[8..], (sign != 0 ? 0x80000000u : 0) + 0x7fff8000);
                return;
            }
            if (sum <= 0x3fbf) { x[..12].Clear(); return; }
            if (ex == 0)
            {
                sum++;
                if ((BinaryPrimitives.ReadUInt32LittleEndian(x[8..]) & 0x7fffffff) == 0 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(x[4..]) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(x) == 0)
                { BinaryPrimitives.WriteUInt16LittleEndian(x[10..], 0); return; }
            }
            if (ey == 0)
            {
                sum++;
                if ((BinaryPrimitives.ReadUInt32LittleEndian(y[8..]) & 0x7fffffff) == 0 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(y[4..]) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(y) == 0)
                { x[..12].Clear(); return; }
            }
            Span<byte> t = stackalloc byte[14];
            t.Clear();
            for (int i = 0; i < 5; i++)
            {
                int poffs = i * 2, qoffs = 8, roffs = i * 2;
                for (int j = 5 - i; j > 0; j--, poffs += 2, qoffs -= 2)
                {
                    uint product = (uint)BinaryPrimitives.ReadUInt16LittleEndian(x[poffs..]) * BinaryPrimitives.ReadUInt16LittleEndian(y[qoffs..]);
                    uint r = BinaryPrimitives.ReadUInt32LittleEndian(t[roffs..]), total = unchecked(r + product);
                    BinaryPrimitives.WriteUInt32LittleEndian(t[roffs..], total);
                    if (total < r || total < product)
                        BinaryPrimitives.WriteUInt16LittleEndian(t[(roffs + 4)..], (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(t[(roffs + 4)..]) + 1));
                }
            }
            sum = unchecked((ushort)(sum + 0xc002));
            while ((short)sum > 0 && (t[11] & 0x80) == 0) { ShiftLeft(t); sum--; }
            if ((short)sum <= 0)
            {
                sum--;
                int sticky = 0;
                while ((short)sum < 0) { if ((t[0] & 1) != 0) sticky++; ShiftRight(t); sum++; }
                if (sticky != 0) t[0] |= 1;
            }
            if (BinaryPrimitives.ReadUInt16LittleEndian(t) > 0x8000 || (BinaryPrimitives.ReadUInt32LittleEndian(t) & 0x1ffff) == 0x18000)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(t[2..]) == uint.MaxValue)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(t[2..], 0);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(t[6..]) == uint.MaxValue)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(t[6..], 0);
                        ushort top = BinaryPrimitives.ReadUInt16LittleEndian(t[10..]);
                        if (top == ushort.MaxValue) { BinaryPrimitives.WriteUInt16LittleEndian(t[10..], 0x8000); sum++; }
                        else BinaryPrimitives.WriteUInt16LittleEndian(t[10..], (ushort)(top + 1));
                    }
                    else BinaryPrimitives.WriteUInt32LittleEndian(t[6..], BinaryPrimitives.ReadUInt32LittleEndian(t[6..]) + 1);
                }
                else BinaryPrimitives.WriteUInt32LittleEndian(t[2..], BinaryPrimitives.ReadUInt32LittleEndian(t[2..]) + 1);
            }
            if (sum > 0x7ffe)
            {
                x[..12].Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(x[8..], (sign != 0 ? 0x80000000u : 0) + 0x7fff8000);
                return;
            }
            t.Slice(2, 10).CopyTo(x);
            BinaryPrimitives.WriteUInt16LittleEndian(x[10..], (ushort)(sum | sign));
        }

        // 00A3F3A5 / 00A3F3D3 / 00A3F347
        private static void ShiftLeft(Span<byte> v)
        {
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(v), b = BinaryPrimitives.ReadUInt32LittleEndian(v[4..]), c = BinaryPrimitives.ReadUInt32LittleEndian(v[8..]);
            BinaryPrimitives.WriteUInt32LittleEndian(v, a << 1);
            BinaryPrimitives.WriteUInt32LittleEndian(v[4..], b << 1 | a >> 31);
            BinaryPrimitives.WriteUInt32LittleEndian(v[8..], c << 1 | b >> 31);
        }
        private static void ShiftRight(Span<byte> v)
        {
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(v), b = BinaryPrimitives.ReadUInt32LittleEndian(v[4..]), c = BinaryPrimitives.ReadUInt32LittleEndian(v[8..]);
            BinaryPrimitives.WriteUInt32LittleEndian(v, a >> 1 | b << 31);
            BinaryPrimitives.WriteUInt32LittleEndian(v[4..], b >> 1 | c << 31);
            BinaryPrimitives.WriteUInt32LittleEndian(v[8..], c >> 1);
        }
        private static void Add(Span<byte> x, ReadOnlySpan<byte> y)
        {
            ulong low = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(x) + BinaryPrimitives.ReadUInt32LittleEndian(y);
            ulong middle = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(x[4..]) + BinaryPrimitives.ReadUInt32LittleEndian(y[4..]) + (low >> 32);
            uint highValue = unchecked(BinaryPrimitives.ReadUInt32LittleEndian(x[8..]) + BinaryPrimitives.ReadUInt32LittleEndian(y[8..]) + (uint)(middle >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(x, (uint)low);
            BinaryPrimitives.WriteUInt32LittleEndian(x[4..], (uint)middle);
            BinaryPrimitives.WriteUInt32LittleEndian(x[8..], highValue);
        }
    }
}
