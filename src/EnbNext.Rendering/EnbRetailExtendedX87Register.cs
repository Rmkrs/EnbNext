using System.Numerics;
using System.Runtime.InteropServices;

namespace EnbNext.Rendering;

/// <summary>The retail x87 register operations, evaluated in managed code on every
/// platform. Values keep the 80-bit register layout; arithmetic runs in double
/// with the control word's precision (single or double) and rounding mode
/// applied. Results can differ from the old FPU in the last bits, which is fine:
/// look and feel, not bit-exactness.</summary>
[StructLayout(LayoutKind.Sequential,Pack=1)]
internal readonly struct EnbRetailExtendedX87Register(ulong significand,ushort exponent)
{
    [ThreadStatic] private static ushort stickyStatus;
    [ThreadStatic] private static int scopeDepth;
    internal static ushort StickyStatus=>stickyStatus;
    internal static ushort EnterScope(){ushort previous=stickyStatus;if(scopeDepth++==0)stickyStatus=0;return previous;}
    internal static void LeaveScope(ushort previous){if(--scopeDepth==0)stickyStatus=previous;}
    private readonly ulong significand=significand;
    private readonly ushort exponent=exponent;
    [StructLayout(LayoutKind.Sequential,Pack=1)]
    private struct RemainderResult { public EnbRetailExtendedX87Register Value; public ushort Status; }
    public static EnbRetailExtendedX87Register PartialRemainder(EnbRetailExtendedX87Register dividend,EnbRetailExtendedX87Register divisor,ushort control,out ushort status)
    {
        var result=Remainder(dividend,divisor);status=result.Status;return result.Value;
    }
    private static EnbRetailExtendedX87Register Evaluate(uint operation,EnbRetailExtendedX87Register left,EnbRetailExtendedX87Register right=default)
    {
        ushort control=EnbRetailLineArithmetic.CurrentPrecision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        return EvaluateControlled(operation,left,right,control);
    }
    internal static EnbRetailExtendedX87Register EvaluateControlled(uint operation,EnbRetailExtendedX87Register left,EnbRetailExtendedX87Register right,ushort control)
    {
        var result=Compute(operation,left,right,control);stickyStatus|=(ushort)(result.Status&0x3f);return result.Value;
    }
    internal uint RawLowWord=>(uint)significand;
    internal ulong RawBits=>significand;
    internal static EnbRetailExtendedX87Register FromDoubleBits(ulong bits,ushort control)
    {
        uint e=(uint)(bits>>52)&2047;
        if(e is >0 and <2047)
            return new(((bits&0xfffffffffffffUL)|(1UL<<52))<<11,
                (ushort)((bits>>48&0x8000)|(e+15360)));
        if((bits&0x7fffffffffffffffUL)==0)return new(0,(ushort)(bits>>48&0x8000));
        return EvaluateControlled(0,new(bits,0),default,control);
    }
    internal static ulong DoubleBits(EnbRetailExtendedX87Register value,ushort control)=>EvaluateControlled(2,value,default,control).significand;
    internal static ushort CompareDoubleMemory(EnbRetailExtendedX87Register value,ulong bits,ushort control)=>(ushort)EvaluateControlled(24,value,new(bits,0),control).significand;
    public static implicit operator EnbRetailExtendedX87Register(double value)=>FromDoubleBits(BitConverter.DoubleToUInt64Bits(value),
        EnbRetailLineArithmetic.CurrentPrecision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f);
    public static explicit operator double(EnbRetailExtendedX87Register value)=>BitConverter.UInt64BitsToDouble(Evaluate(2,value).significand);
    public static explicit operator float(EnbRetailExtendedX87Register value)=>BitConverter.UInt32BitsToSingle((uint)Evaluate(3,value).significand);
    public static EnbRetailExtendedX87Register FromFloatBits(uint value)
    {
        // FLD extends normal/zero IEEE operands exactly, independently of PC;
        // no arithmetic or exception status changes. Preserve signed zero.
        // Subnormal loads and NaNs retain the instruction's exception behavior.
        uint e=value>>23&255;
        if(e is >0 and <255)
            return new(((ulong)(value&0x7fffff)|0x800000UL)<<40,
                (ushort)((value>>16&0x8000)|(e+16256)));
        if((value&0x7fffffff)==0)return new(0,(ushort)(value>>16&0x8000));
        return Evaluate(1,new(value,0));
    }
    internal static EnbRetailExtendedX87Register FromInteger64(long value,ushort control)=>EvaluateControlled(29,new(unchecked((ulong)value),0),default,control);
    public static EnbRetailExtendedX87Register operator +(EnbRetailExtendedX87Register a,EnbRetailExtendedX87Register b)=>Evaluate(4,a,b);
    public static EnbRetailExtendedX87Register operator -(EnbRetailExtendedX87Register a,EnbRetailExtendedX87Register b)=>Evaluate(5,a,b);
    public static EnbRetailExtendedX87Register operator *(EnbRetailExtendedX87Register a,EnbRetailExtendedX87Register b)=>Evaluate(6,a,b);
    public static EnbRetailExtendedX87Register operator /(EnbRetailExtendedX87Register a,EnbRetailExtendedX87Register b)=>Evaluate(7,a,b);
    public static EnbRetailExtendedX87Register Sqrt(EnbRetailExtendedX87Register value)=>Evaluate(8,value);
    public static EnbRetailExtendedX87Register Abs(EnbRetailExtendedX87Register value)=>new(value.significand,(ushort)(value.exponent&0x7fff));
    public static EnbRetailExtendedX87Register operator -(EnbRetailExtendedX87Register value)=>Evaluate(9,value);
    public static EnbRetailExtendedX87Register Sine(EnbRetailExtendedX87Register value)=>Evaluate(10,value);
    public static EnbRetailExtendedX87Register Cosine(EnbRetailExtendedX87Register value)=>Evaluate(11,value);
    public static EnbRetailExtendedX87Register NaturalExponent(EnbRetailExtendedX87Register value)=>Evaluate(36,value);
    public static EnbRetailExtendedX87Register SubtractFloatMemory(EnbRetailExtendedX87Register value,uint bits)=>Evaluate(13,value,new(bits,0));
    public static EnbRetailExtendedX87Register MultiplyFloatMemory(EnbRetailExtendedX87Register value,uint bits)=>Evaluate(14,value,new(bits,0));
    public static ushort CompareFloatMemory(EnbRetailExtendedX87Register value,uint bits)=>(ushort)Evaluate(18,value,new(bits,0)).significand;
    public static ulong TruncateInteger64(EnbRetailExtendedX87Register value)=>Evaluate(19,value).significand;
    public static ushort CompareRegisters(EnbRetailExtendedX87Register left,EnbRetailExtendedX87Register right)=>(ushort)Evaluate(20,left,right).significand;

    // ---- Managed evaluation ------------------------------------------------
    // Status word bits as FNSTSW reports them after FNINIT plus one load (TOP=7).
    private const ushort Top7=0x3800, C0=0x100, C1=0x200, C2=0x400, C3=0x4000;
    private const ushort Invalid=0x01, ZeroDivide=0x04, Overflow=0x08, Underflow=0x10, Inexact=0x20;

    internal static double ToDouble(EnbRetailExtendedX87Register value)
    {
        bool negative=(value.exponent&0x8000)!=0;
        int e=value.exponent&0x7fff;
        double magnitude;
        if(e==0x7fff)magnitude=(value.significand<<1)==0?double.PositiveInfinity:double.NaN;
        else if(value.significand==0)magnitude=0;
        else magnitude=Math.ScaleB((double)value.significand,e-16383-63);
        return negative?-magnitude:magnitude;
    }

    internal static EnbRetailExtendedX87Register FromDouble(double value)
    {
        ulong bits=BitConverter.DoubleToUInt64Bits(value);
        ushort sign=(ushort)(bits>>48&0x8000);
        uint e=(uint)(bits>>52)&2047;
        ulong mantissa=bits&0xfffffffffffffUL;
        if(e is >0 and <2047)return new((mantissa|(1UL<<52))<<11,(ushort)(sign|(e+15360)));
        if(e==2047)return mantissa==0?new(0x8000000000000000UL,(ushort)(sign|0x7fff))
            :new(0xc000000000000000UL|(mantissa<<11),(ushort)(sign|0x7fff));
        if(mantissa==0)return new(0,sign);
        int shift=BitOperations.LeadingZeroCount(mantissa);
        return new(mantissa<<shift,(ushort)(sign|(15372-shift)));
    }

    private static double Precision(double value,ushort control,ref ushort status)
    {
        if(((control>>8)&3)!=0)return value;
        double rounded=EnbRetailLineArithmetic.Register.RoundPc24(value);
        if(rounded!=value&&!double.IsNaN(value))status|=Inexact;
        if(double.IsInfinity(rounded)&&!double.IsInfinity(value))status|=Overflow;
        return rounded;
    }

    private static double RoundIntegral(double value,ushort control)=>((control>>10)&3) switch
    {
        0=>Math.Round(value,MidpointRounding.ToEven),
        1=>Math.Floor(value),
        2=>Math.Ceiling(value),
        _=>Math.Truncate(value),
    };

    private static ushort Compare(double left,double right,ref ushort status)
    {
        if(double.IsNaN(left)||double.IsNaN(right)){status|=Invalid;return C3|C2|C0;}
        return left<right?C0:left==right?C3:(ushort)0;
    }

    private static ushort Classify(EnbRetailExtendedX87Register value)
    {
        ushort sign=(value.exponent&0x8000)!=0?C1:(ushort)0;
        int e=value.exponent&0x7fff;
        ushort kind;
        if(e==0x7fff)kind=(value.significand<<1)==0?(ushort)(C2|C0):C0;
        else if(e==0)kind=value.significand==0?C3:(ushort)(C3|C2);
        else kind=(value.significand>>63)!=0?C2:(ushort)0;
        return (ushort)(Top7|sign|kind);
    }

    private static RemainderResult Result(double value,int status)
    {
        return new(){Value=FromDouble(value),Status=(ushort)(Top7|status)};
    }

    private static RemainderResult Bits(ulong bits,int status)=>new(){Value=new(bits,0),Status=(ushort)(Top7|status)};

    private static double Arithmetic(double result,double left,double right,ushort control,ref ushort status,bool division=false)
    {
        if(double.IsNaN(result)&&!double.IsNaN(left)&&!double.IsNaN(right))status|=Invalid;
        else if(division&&right==0&&!double.IsNaN(left)&&left!=0&&!double.IsInfinity(left))status|=ZeroDivide;
        else if(double.IsInfinity(result)&&!double.IsInfinity(left)&&!double.IsInfinity(right))status|=Overflow;
        else if(result!=0&&Math.Abs(result)<2.2250738585072014E-308)status|=Underflow|Inexact;
        return Precision(result,control,ref status);
    }

    private static RemainderResult Compute(uint operation,EnbRetailExtendedX87Register left,EnbRetailExtendedX87Register right,ushort control)
    {
        ushort status=0;
        double a=ToDouble(left);
        double FloatMemory()=>BitConverter.UInt32BitsToSingle((uint)right.significand);
        double DoubleMemory()=>BitConverter.UInt64BitsToDouble(right.significand);
        switch(operation)
        {
            case 0: return Result(BitConverter.UInt64BitsToDouble(left.significand),0);
            case 1: return Result(BitConverter.UInt32BitsToSingle((uint)left.significand),0);
            case 2: return Bits(BitConverter.DoubleToUInt64Bits(a),0);
            case 3:
            {
                float single=(float)a;
                if(single!=a&&!double.IsNaN(a))status|=Inexact;
                if(float.IsInfinity(single)&&!double.IsInfinity(a))status|=Overflow;
                return Bits(BitConverter.SingleToUInt32Bits(single),status);
            }
            case 4: { double b=ToDouble(right); return Result(Arithmetic(a+b,a,b,control,ref status),status); }
            case 5: { double b=ToDouble(right); return Result(Arithmetic(a-b,a,b,control,ref status),status); }
            case 6: { double b=ToDouble(right); return Result(Arithmetic(a*b,a,b,control,ref status),status); }
            case 7: { double b=ToDouble(right); return Result(Arithmetic(a/b,a,b,control,ref status,true),status); }
            case 8:
                if(a<0)status|=Invalid;
                return Result(Precision(Math.Sqrt(a),control,ref status),status);
            case 9: return new(){Value=new(left.significand,(ushort)(left.exponent^0x8000)),Status=Top7};
            case 10:
                if(double.IsInfinity(a))status|=Invalid;
                return Result(Math.Sin(a),status|Inexact);
            case 11:
                if(double.IsInfinity(a))status|=Invalid;
                return Result(Math.Cos(a),status|Inexact);
            case 12: { double b=FloatMemory(); return Result(Arithmetic(a+b,a,b,control,ref status),status); }
            case 13: { double b=FloatMemory(); return Result(Arithmetic(a-b,a,b,control,ref status),status); }
            case 14: { double b=FloatMemory(); return Result(Arithmetic(a*b,a,b,control,ref status),status); }
            case 15: { double b=FloatMemory(); return Result(Arithmetic(a/b,a,b,control,ref status,true),status); }
            case 16: { double b=FloatMemory(); return Result(Arithmetic(b-a,b,a,control,ref status),status); }
            case 17: { double b=FloatMemory(); return Result(Arithmetic(b/a,b,a,control,ref status,true),status); }
            case 18: { ushort flags=Compare(a,FloatMemory(),ref status); return Bits(flags,status); }
            case 19: return Bits(Integer(Math.Truncate(a),a,64,ref status),status);
            case 20: { ushort flags=Compare(a,ToDouble(right),ref status); return Bits(flags,status); }
            case 21: return Bits(Classify(left),0);
            case 22:
            {
                double scale=Math.Truncate(ToDouble(right));
                double result=double.IsNaN(scale)?double.NaN:scale>int.MaxValue?a*double.PositiveInfinity
                    :scale<int.MinValue?a*0:Math.ScaleB(a,(int)scale);
                return Result(result,status);
            }
            case 23: return new(){Value=new(left.significand,(ushort)(left.exponent&0x7fff)),Status=Top7};
            case 24: { ushort flags=Compare(a,DoubleMemory(),ref status); return Bits(flags,status); }
            case 25: { double b=DoubleMemory(); return Result(Arithmetic(b/a,b,a,control,ref status,true),status); }
            case 26: return Bits(Integer(RoundIntegral(a,control),a,32,ref status),status);
            case 27: return Result(Math.PI,0);
            case 28: return Result(RoundIntegral(a,control),RoundIntegral(a,control)!=a&&!double.IsNaN(a)?Inexact:(ushort)0);
            case 29: return Result(unchecked((long)left.significand),0);
            case 30: { double b=DoubleMemory(); return Result(Arithmetic(b-a,b,a,control,ref status),status); }
            case 31: { double b=unchecked((int)(uint)right.significand); return Result(Arithmetic(a*b,a,b,control,ref status),status); }
            case 32: { double b=unchecked((int)(uint)right.significand); return Result(Arithmetic(a/b,a,b,control,ref status,true),status); }
            case 33: return Result(Math.Atan2(ToDouble(right),a),Inexact);
            case 34:
            {
                double b=ToDouble(right);
                if(a<0)status|=Invalid; else if(a==0&&b!=0)status|=ZeroDivide;
                return Result(b*Math.Log2(a),status|Inexact);
            }
            case 35: return Result(Math.Pow(2,a)-1,Inexact);
            case 36: return Result(Math.Exp(a),Inexact);
            default: throw new ArgumentOutOfRangeException(nameof(operation),operation,"Unknown x87 operation.");
        }
    }

    private static ulong Integer(double rounded,double original,int width,ref ushort status)
    {
        double limit=width==64?9223372036854775808.0:2147483648.0;
        if(double.IsNaN(rounded)||rounded>=limit||rounded<-limit)
        {
            status|=Invalid;
            return width==64?0x8000000000000000UL:0x80000000UL;
        }
        if(rounded!=original)status|=Inexact;
        return width==64?unchecked((ulong)(long)rounded):unchecked((uint)(int)rounded);
    }

    /// <summary>FPREM run to completion: the truncated remainder, with the low
    /// three quotient bits in C0/C3/C1 and C2 clear.</summary>
    private static RemainderResult Remainder(EnbRetailExtendedX87Register dividend,EnbRetailExtendedX87Register divisor)
    {
        double x=ToDouble(dividend),y=ToDouble(divisor);
        if(double.IsNaN(x)||double.IsNaN(y)||double.IsInfinity(x)||y==0)return Result(double.NaN,Invalid);
        if(double.IsInfinity(y))return Result(x,0);
        double remainder=x%y;
        double quotient=Math.Truncate((x-remainder)/y);
        ulong q=double.IsFinite(quotient)?(ulong)(Math.Abs(quotient)%8):0;
        ushort flags=(ushort)(((q&4)!=0?C0:0)|((q&2)!=0?C3:0)|((q&1)!=0?C1:0));
        return Result(remainder,flags);
    }
}
