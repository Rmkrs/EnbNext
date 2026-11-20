using EnbNext.Presentation;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>6A6550 skill mode0/1 indicators; presentation only, never activation admission.</summary>
public readonly record struct EnbRetailSkillFeedback(EnbRetailWeaponFeedback Range,
    bool Invalid, bool WrongTarget, float? Powerup)
{
    public static EnbRetailSkillFeedback Evaluate(EnbSceneSkillFeedbackSnapshot state, int ability,
        float? distance, uint now, float angle, uint elapsed)
    {
        bool ready = unchecked(state.LastActivation + state.RecycleTime) <= (long)now;
        int mode = (state.Validity & 0x3f8) == 0 && (state.Targets & 0x34) != 0 ? 2 : 0;
        var range = EnbRetailWeaponFeedback.EvaluateSkill(distance, state.Range, mode, angle, elapsed)
            with { Cooling = !ready };
        return new(range, state.Validity != 0 || !ready, (state.Validity & 0x170) != 0,
            state.PowerupAbility == ability && ability != -1 ? Progress(state.PowerupStart, state.PowerupEnd, now) : null);
    }

    // 6A4C20 saturates signed64 Time values to signed32, then compares their
    // bit patterns with unsigned SClient+10BC. The denominator is signed32.
    public static float Progress(long startTime, long endTime, uint now, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        uint start = unchecked((uint)(int)Math.Clamp(startTime, int.MinValue, int.MaxValue));
        uint end = unchecked((uint)(int)Math.Clamp(endTime, int.MinValue, int.MaxValue));
        if (now >= end || now <= start) return 0;
        using var scope = new EnbRetailLineArithmetic.Scope(precision, true);
        return (float)(R.FromInteger64(unchecked(now - start), precision == EnbRetailLinePrecision.Single24 ? (ushort)0x7f : (ushort)0x27f) / R.FromInteger64(unchecked((int)(end - start)), precision == EnbRetailLinePrecision.Single24 ? (ushort)0x7f : (ushort)0x27f));
    }
}
