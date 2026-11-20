using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Retail 006E2CC0/006E3710/006E37F0/006E3B60 transient shake contributions.</summary>
public sealed class EnbShakeRuntime(EnbRetailMovieRandom? random = null)
{
    private readonly EnbRetailMovieRandom _random = random ?? EnbRetailMovieRandom.Shake;
    private readonly List<Impulse> _impulses = [];
    public int ActiveCount => _impulses.Count;
    public void Clear() => _impulses.Clear();

    // 00582BC6..00582BCF: private warp state 2 requests type 2, amplitude 1.
    // 006E33BE/006E3D30: one-second triangular envelope, random XY only.
    public void AddWarpStart() => _impulses.Add(new(default, 0, .001f, 1, false, true));

    public static (int X, int Y) InterfacePixelOffset(Vector3 shake, int width, int height)
        // A positive camera translation moves projected art in the opposite
        // direction. These are OpenGL bottom-left pixel coordinates.
        => (-(int)MathF.Round(shake.X * width * .0015625f), -(int)MathF.Round(shake.Y * height * .0020833334f));

    public void AddForce(Vector3 force)
    {
        // Presentation-only finite guard; this does not change AuxData acceptance.
        if (!Finite(force)) return;
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        // 6E2DD9 adds Z,Y,X in that order; cancellation can change admission.
        if ((double)((R)force.Z + (R)force.Y + (R)force.X) == 0) return;
        // The binary checks positive X/Y, not absolute magnitude or Z.
        if (force.X > 12 || force.Y > 12)
        {
            // 4481E0 spills X²+Y²+Z² before447C40, retains the reciprocal
            // in x87, then spills each normalized component before scaling.
            R square = (R)force.X * (R)force.X + (R)force.Y * (R)force.Y + (R)force.Z * (R)force.Z;
            if ((double)square != 0)
            {
                R inverse = (double)EnbRetailLineCoalescing.ReciprocalSquareRoot((float)square);
                force = new((float)(inverse * (R)force.X), (float)(inverse * (R)force.Y), (float)(inverse * (R)force.Z));
            }
            force = new((float)((R)force.X * (R)12f), (float)((R)force.Y * (R)12f), (float)((R)force.Z * (R)12f));
        }
        _impulses.Add(new(force, 0, .0015f, 0, false));
    }

    public void AddDamage(float damage, float? maximumShield, float? shieldPercent)
    {
        if (!float.IsFinite(damage) || damage <= 0) return;
        float shield = (maximumShield ?? damage * 4) * (shieldPercent ?? 1);
        float low = shield * .15f, high = shield * .8f;
        if (!float.IsFinite(shield) || high <= low) return;
        float fraction = (Math.Clamp(damage, low, high) - low) / (high - low);
        if (fraction <= 0) return;
        _impulses.Add(new(default, 1, 1 / ((fraction + 1) * .5f * 1000), fraction * .12f, true));
    }

    /// <param name="randomPercent">Inclusive 0..100 integer sample; injectable for deterministic fixtures.</param>
    public Vector3 Advance(double elapsedMilliseconds, int width, int height, Func<int>? randomPercent = null,
        Func<int>? randomWarpSample = null)
    {
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0 || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException();
        var sum = Vector3.Zero;
        for (int i = 0; i < _impulses.Count;)
        {
            var impulse = _impulses[i];
            if(!AdvanceImpulse(ref impulse,ref sum,elapsedMilliseconds,randomPercent??(()=>_random.Next(0,100)),
                randomWarpSample??(()=>_random.Next(0,25))))
            {
                _impulses.RemoveAt(i);continue;
            }
            _impulses[i++]=impulse;
        }
        if (!Finite(sum)) return Vector3.Zero;
        sum = new(Wrap(sum.X), Wrap(sum.Y), Wrap(sum.Z));
        return SnapToPixelGrid(sum,width,height);
    }

    internal static bool AdvanceImpulse(ref Impulse impulse,ref Vector3 sum,double elapsedMilliseconds,
        Func<int> randomPercent,Func<int> randomWarpSample)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        //The three callbacks use signed FILD on the DWORD tick subtraction,
        //then spill elapsed to float before498E00 retains step through addition.
        float elapsed=(float)unchecked((int)(uint)elapsedMilliseconds);
        float target=impulse.Damage?0:1;
        bool rising=impulse.Phase<target;
        R step=(R)elapsed*(R)impulse.Rate;
        R next=rising?(R)impulse.Phase+step:(R)impulse.Phase-step;
        float phase=(float)next;
        int comparison=R.CompareFloatMemory(next,BitConverter.SingleToUInt32Bits(target));
        if(rising?(comparison&0x100)==0:(comparison&0x4100)!=0)phase=target;
        impulse=impulse with {Phase=phase};
        if(impulse.Damage?phase<=0:phase>=1)return false;
        R envelope=impulse.Damage?(R)phase:
            phase>.33f?(R)1d-((R)phase-(R).33f)*(R)1.4925374f:(R)phase*(R)3.030303f;
        if(impulse.Damage || impulse.Warp)
        {
            bool damage=impulse.Damage;
            float amplitude=(float)(envelope*(R)impulse.Amplitude);
            int Sample()
            {
                int value=damage?randomPercent():randomWarpSample();
                int maximum=damage?100:25;
                if(value<0 || value>maximum)throw new ArgumentOutOfRangeException(damage?nameof(randomPercent):nameof(randomWarpSample));
                return value;
            }
            sum.X=(float)((R)Sample()*(R)amplitude+(R)sum.X);
            sum.Y=(float)((R)Sample()*(R)amplitude+(R)sum.Y);
            if(impulse.Damage)sum.Z=(float)((R)Sample()*(R)amplitude+(R)sum.Z);
        }
        else
        {
            R amount=envelope*(R)10f;
            sum.X=(float)(amount*(R)impulse.Force.X+(R)sum.X);
            sum.Y=(float)(amount*(R)impulse.Force.Y+(R)sum.Y);
            sum.Z=(float)(amount*(R)impulse.Force.Z+(R)sum.Z);
        }
        return true;
    }

    internal static Vector3 SnapToPixelGrid(Vector3 sum,int width,int height)
    {
        //491550 spills scaled XY before A26D75. That CRT routine selects
        //round-down explicitly; it is not rounding in the caller's FPU mode.
        return new(EnbRetailScreenPixelGrid.Snap(new(sum.X,sum.Y),width,height),sum.Z);
    }

    private static float Wrap(float value)
    {
        if (value > 12) { float remainder = value % 12; return remainder == 0 ? 12 : remainder; }
        if (value < -12) { float remainder = value % 12; return remainder == 0 ? -12 : remainder; }
        return value;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    internal readonly record struct Impulse(Vector3 Force, float Phase, float Rate, float Amplitude, bool Damage, bool Warp = false);
}
