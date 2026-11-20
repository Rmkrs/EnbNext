namespace EnbNext.Rendering;

/// <summary>6AD4B0 welcome seeding and 5ADA50 login cue selection.</summary>
public sealed class EnbRetailLoginNarration(bool tutorialCompleted)
{
    private bool _welcomePlayed=tutorialCompleted;
    private bool _returningZeroAndOnePlayed;

    public string? TakeIntro(long epochSeconds,bool registration=false)
    {
        if(!_welcomePlayed)
        {
            _welcomePlayed=true; // Native marks both Welcome and WelcomeReg.
            return registration?"WelcomeReg":"Welcome";
        }
        int variant=(int)(epochSeconds%3);
        if(variant!=2 && _returningZeroAndOnePlayed)return null;
        // 5ADC23/5ADC33 mark ReturningUser0 and ReturningUser1 only.
        // ReturningUser2 is deliberately not marked by this controller.
        _returningZeroAndOnePlayed=true;
        return $"ReturningUser{variant}";
    }
}
