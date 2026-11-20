namespace EnbNext.Rendering;

/// <summary>Lighting inherited by normal RenderObj capture between ClientView
/// frames. CharacterView00679930 (including login) and MainView0050D520 finish
/// with the shared scene created by0065A820: ambient.5, no scene lights.
/// 00917C80 truncates that ambient to127. Device creation/reset starts black.
/// This describes those frame boundaries, not arbitrary WW3D scene draws.</summary>
public sealed class EnbCurrentRetailClientViewLightingState
{
    private static readonly EnbW3dLightEnvironment Tail =
        EnbW3dLightEnvironment.CreateCurrentRetail(0x7f7f7f, []);
    private EnbW3dLightEnvironment _current = EnbW3dLightEnvironment.BlackNoLights;

    public EnbW3dLightEnvironment CaptureEnvironment => Volatile.Read(ref _current);
    public void CompleteClientViewFrame() => Volatile.Write(ref _current, Tail);
    public void ResetDevice() => Volatile.Write(ref _current, EnbW3dLightEnvironment.BlackNoLights);
}
