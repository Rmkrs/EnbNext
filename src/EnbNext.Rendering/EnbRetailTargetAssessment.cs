using System.Globalization;

namespace EnbNext.Rendering;

/// <summary>0060ED60 assessment text and cue selection for constructed object schemas.</summary>
public readonly record struct EnbRetailTargetAssessment(string? Text, string? Sound)
{
    public static EnbRetailTargetAssessment Resolve(string? assessment, int? level, string? sound)
    {
        // The level branch is inside the nonempty assessment branch and replaces
        // the string buffer. It does not append the level to the comparison.
        if (string.IsNullOrEmpty(assessment)) return default;
        return new(level is >= 0 ? "Combat Level " + level.Value.ToString(CultureInfo.InvariantCulture) : assessment,
            string.IsNullOrEmpty(sound) ? null : sound);
    }
}
