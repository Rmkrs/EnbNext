using System.Runtime.CompilerServices;

namespace EnbNext.Rendering;

public enum EnbRetailLinePrecision { Double53, Single24 }

// x87 PC=00 rounds the significand after each arithmetic instruction while
// retaining its register exponent. Casting every operation to float would
// incorrectly underflow the tiny incoming stack rays observed in native controls.
internal static class EnbRetailLineArithmetic
{
    [ThreadStatic] private static EnbRetailLinePrecision _precision;
    [ThreadStatic] private static bool _nativeNaNs;
    internal static EnbRetailLinePrecision CurrentPrecision => _precision;
    internal readonly struct Scope : IDisposable
    {
        private readonly EnbRetailLinePrecision _previous;
        private readonly bool _previousNativeNaNs;
        private readonly ushort _previousX87Status;
        internal Scope(EnbRetailLinePrecision precision,bool nativeNaNs=false) { _previous = _precision; _precision = precision;_previousNativeNaNs=_nativeNaNs;_nativeNaNs=nativeNaNs;_previousX87Status=EnbRetailExtendedX87Register.EnterScope(); }
        public void Dispose() { _precision = _previous;_nativeNaNs=_previousNativeNaNs;EnbRetailExtendedX87Register.LeaveScope(_previousX87Status); }
    }

    internal readonly struct Register(double value)
    {
        private readonly double _value = value;
        public static implicit operator Register(double value) => new(value);
        public static explicit operator double(Register value) => value._value;
        public static explicit operator float(Register value) => (float)value._value;
        public static explicit operator int(Register value) => (int)value._value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Register Rounded(double value)
            => _precision == EnbRetailLinePrecision.Double53 ? new(value) : new(RoundPc24(value));

        /// <summary>x87 precision control 24: round the significand to 24 bits
        /// while keeping the register's wide exponent.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static double RoundPc24(double value)
        {
            if (!double.IsFinite(value) || value == 0) return value;
            ulong bits = BitConverter.DoubleToUInt64Bits(value);
            // Within the normal binary32 range, CVTSD2SS performs exactly
            // the same round-to-nearest-even 24-bit significand operation.
            // Outside it retain the extended register exponent below: a float
            // cast would lose native underflow/overflow behavior there.
            ulong magnitude=bits&0x7fffffffffffffffUL;
            if(magnitude>=0x3810000000000000UL && magnitude<=0x47efffffe0000000UL)
                return (float)value;
            // A subnormal binary64 input has no implicit leading bit. Its
            // native PC24 significand starts at its actual highest set bit.
            int shift=magnitude<0x0010000000000000UL
                ?Math.Max(0,System.Numerics.BitOperations.Log2(magnitude)+1-24):29;
            if(shift==0)return value;
            ulong discardedMask = (1UL << shift) - 1;
            ulong discarded = bits & discardedMask;
            ulong kept = bits & ~discardedMask;
            ulong halfway = 1UL << (shift-1);
            if (discarded > halfway || (discarded == halfway && (kept & (1UL << shift)) != 0)) kept += 1UL << shift;
            return BitConverter.UInt64BitsToDouble(kept);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Register Binary(Register a,Register b,double result)
        {
            if(!_nativeNaNs||!double.IsNaN(result))return Rounded(result);
            ulong selected=0xfff8000000000000;
            bool an=double.IsNaN(a._value),bn=double.IsNaN(b._value);
            if(an||bn)
            {
                ulong av=BitConverter.DoubleToUInt64Bits(a._value),bv=BitConverter.DoubleToUInt64Bits(b._value);
                ulong am=av&0x7fffffffffffffff,bm=bv&0x7fffffffffffffff;
                selected=!an?bv:!bn?av:am>bm?av:am<bm?bv:Math.Min(av,bv);
                selected|=0x8000000000000;
            }
            return new(BitConverter.UInt64BitsToDouble(selected));
        }
        public static Register operator +(Register a, Register b) => Binary(a,b,a._value + b._value);
        internal static Register MultiplyFloatMemory(Register a,uint bits)
        {
            if(_nativeNaNs&&double.IsNaN(a._value)&&(bits&0x7f800000)==0x7f800000&&(bits&0x7fffff)!=0&&(bits&0x400000)==0)return a;
            return a*(Register)BitConverter.UInt32BitsToSingle(bits);
        }
        public static Register operator -(Register a, Register b) => Binary(a,b,a._value - b._value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Register operator *(Register a, Register b)
        {
            // Float loads and rounded PC24 registers have at most 24 bits.
            // Their product fits exactly in binary64 (48 bits), including
            // register exponents outside the float range. Avoid decomposing
            // both significands for every particle/collision multiplication.
            const ulong lowBits=(1UL<<29)-1;
            ulong ab=BitConverter.DoubleToUInt64Bits(a._value),bb=BitConverter.DoubleToUInt64Bits(b._value);
            int exponentSum=(int)((ab>>52)&2047)+(int)((bb>>52)&2047);
            if(((ab|bb)&lowBits)==0 && exponentSum is >=1024 and <=3066)
                return Binary(a,b,a._value*b._value);
            return MultiplyWide(a,b);
        }

        private static Register MultiplyWide(Register a, Register b)
        {
            if (_precision != EnbRetailLinePrecision.Single24 || !double.IsFinite(a._value) || !double.IsFinite(b._value) || a._value == 0 || b._value == 0)
                return Binary(a,b,a._value * b._value);
            // FMUL rounds the exact product directly to PC24. An intermediate
            // binary64 multiplication can turn a near tie into an exact tie.
            static (ulong Significand, int Exponent) Split(double value)
            {
                ulong bits=BitConverter.DoubleToUInt64Bits(value)&0x7fffffffffffffff;
                int exponent=(int)(bits>>52);
                return (exponent==0?bits&0xfffffffffffff:(bits&0xfffffffffffff)|(1UL<<52),exponent==0?-1074:exponent-1075);
            }
            var left=Split(a._value);var right=Split(b._value);
            int leftBits=System.Numerics.BitOperations.Log2(left.Significand)+1-System.Numerics.BitOperations.TrailingZeroCount(left.Significand);
            int rightBits=System.Numerics.BitOperations.Log2(right.Significand)+1-System.Numerics.BitOperations.TrailingZeroCount(right.Significand);
            double binaryProduct=a._value*b._value;
            if(leftBits+rightBits<=53 && double.IsNormal(binaryProduct))return Rounded(binaryProduct);
            // Two binary64 significands have at most 106 product bits.
            // Keep the exact rounding path in registers instead of allocating
            // arbitrary-precision limbs for every such render operation.
            UInt128 product=(UInt128)left.Significand*right.Significand;
            ulong high=(ulong)(product>>64);
            int shift=(high==0?System.Numerics.BitOperations.Log2((ulong)product)+1:
                65+System.Numerics.BitOperations.Log2(high))-24;
            if(shift>0)
            {
                var retained=product>>shift;
                var remainder=product-(retained<<shift);
                var halfway=(UInt128)1<<(shift-1);
                if(remainder>halfway||(remainder==halfway&&(retained&1)!=0))retained++;
                product=retained;
            }
            else shift=0;
            double rounded=Math.ScaleB((double)product,left.Exponent+right.Exponent+shift);
            if((BitConverter.DoubleToUInt64Bits(a._value)^BitConverter.DoubleToUInt64Bits(b._value))>>63!=0)rounded=-rounded;
            return new(rounded);
        }
        public static Register operator /(Register a, Register b) => Binary(a,b,a._value / b._value);
        internal static Register Sqrt(Register value) => Rounded(Math.Sqrt(value._value));
        public static Register operator -(Register a) => new(-a._value);
        public static bool operator >(Register a, Register b) => a._value > b._value;
        public static bool operator <(Register a, Register b) => a._value < b._value;
        public static bool operator >=(Register a, Register b) => a._value >= b._value;
        public static bool operator <=(Register a, Register b) => a._value <= b._value;
        public static bool operator ==(Register a, Register b) => a._value == b._value;
        public static bool operator !=(Register a, Register b) => a._value != b._value;
        public override bool Equals(object? other) => other is Register register && _value.Equals(register._value);
        public override int GetHashCode() => _value.GetHashCode();
    }
}
