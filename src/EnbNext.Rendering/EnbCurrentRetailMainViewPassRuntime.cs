namespace EnbNext.Rendering;

/// <summary>
/// Current-retail MainView per-mode pass selection.
/// ConfigureActiveCameraForClientState 0050CAE0 renders only bound scene objects
/// while SClient+0x1198 is set (00737CB0 sets it on handoff, 00745160 clears it
/// for Start); otherwise client mode 0, 1 and 5 select 0050E090, 0050E6E0 and
/// 0050DB20, each of which renders the sky scene +0x110. Any other mode asserts
/// and renders nothing.
/// </summary>
public static class EnbCurrentRetailMainViewPassRuntime
{
    public static bool RendersSkyScene(bool initializing, int? mode)
        => !initializing && mode is 0 or 1 or 5;

    /// <summary>0050E6E0 calls 00510D40 only when SwapBackdrop (+0x10FA) is clear
    /// and the camera is not below the terrain sea level (+0x13C +0xE8/+0x160).</summary>
    public static bool RendersPlanetFogBackground(bool initializing, int? mode, bool swapBackdrop, bool belowSeaLevel)
        => !initializing && mode == 1 && !swapBackdrop && !belowSeaLevel;

    /// <summary>0050F1B0 rebuilds the four authored directional lights and runs
    /// AdjustDayNight (0075F690) only in mode 0; other modes keep light +0x144 only.</summary>
    public static bool UsesModeZeroLightRig(int? mode) => mode == 0;
}
