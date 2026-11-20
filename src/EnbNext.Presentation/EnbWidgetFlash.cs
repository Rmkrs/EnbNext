namespace EnbNext.Presentation;

/// <summary>006609A0 forced-highlight material pulse from the unsigned timer-origin clock.</summary>
public static class EnbWidgetFlash
{
    public static float Emissive(uint milliseconds)
    {
        float phase=(float)(milliseconds%1000)*.001f;
        phase=phase-.5f;
        return phase<0 ? phase*-2f : phase+phase;
    }
}
