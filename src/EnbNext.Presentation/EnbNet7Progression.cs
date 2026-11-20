namespace EnbNext.Presentation;

/// <summary>
/// Net-7 experience per level step. The server sends only the fraction of the current step
/// (LocalPlayerAuxData.RPGInfo.*Exp) and the level; the step sizes are the current Net-7 server's, as validated in
/// Net7ClientManager (ClientProgressionRules). The historical Net-7 source (PlayerClass.cpp LevelXP) has an older,
/// smaller table that the current server no longer uses.
/// </summary>
public static class EnbNet7Progression
{
    public const int MaximumLevel = 50;
    private const int PostMaximum = 422_500;
    private static readonly int[] ToNextLevel =
    [
        10_000, 12_500, 15_000, 17_500, 20_000, 22_500, 27_500, 32_500, 37_500, 42_500,
        47_500, 52_500, 57_500, 62_500, 67_500, 72_500, 77_500, 82_500, 87_500, 92_500,
        97_500, 102_500, 102_500, 102_500, 102_500, 102_500, 102_500, 102_500, 102_500, 102_500,
        102_500, 112_500, 112_500, 122_500, 132_500, 142_500, 152_500, 162_500, 182_500, 202_500,
        222_500, 242_500, 262_500, 282_500, 302_500, 322_500, 342_500, 362_500, 382_500, 402_500,
        PostMaximum,
    ];

    /// <summary>Experience needed for the step from this level to the next; null for an unknown level.</summary>
    public static int? StepSize(int level) => level < 0 ? null : level <= MaximumLevel ? ToNextLevel[level] : PostMaximum;

    /// <summary>Earned within the step and the step size, from the server's fraction.</summary>
    public static (int Earned, int Required)? Step(int level, float fraction)
    {
        if (StepSize(level) is not int required || !float.IsFinite(fraction)) return null;
        int earned = Math.Clamp((int)Math.Round(Math.Clamp(fraction, 0, 1) * required, MidpointRounding.AwayFromZero), 0, required);
        return (earned, required);
    }
}
