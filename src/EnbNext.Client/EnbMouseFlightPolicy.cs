namespace EnbNext.Client;

public static class EnbMouseFlightPolicy
{
    // HandleMouseFlightControl @ 006B5AA1..006B5B25: strict interior.
    // X limits are float .25 / double .75; Y limits float .2 / double .8.
    public static bool ShouldThrust(float x, float y, bool enabled, bool speedLocked)
        => enabled && !speedLocked && x > .25f && x < .75 && y > .2f && y < .8;
}
