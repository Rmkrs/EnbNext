namespace EnbNext.Rendering;

public enum EnbSpaceScenePass { Backdrop, Stars, Orbitals, World }

/// <summary>50E090: shared sky110, optional stars114, orbital11C, then world10C.
/// SwapBackdrop is client10FA; MainView130 gates only stars (constructor default true).</summary>
public static class EnbRetailSpaceComposition
{
    public static IReadOnlyList<EnbSpaceScenePass> Passes(bool swapBackdrop, bool starsEnabled) =>
        (swapBackdrop, starsEnabled) switch
        {
            (false, true) => [EnbSpaceScenePass.Backdrop, EnbSpaceScenePass.Stars, EnbSpaceScenePass.Orbitals, EnbSpaceScenePass.World],
            (false, false) => [EnbSpaceScenePass.Backdrop, EnbSpaceScenePass.Orbitals, EnbSpaceScenePass.World],
            (true, true) => [EnbSpaceScenePass.Stars, EnbSpaceScenePass.Orbitals, EnbSpaceScenePass.Backdrop, EnbSpaceScenePass.World],
            _ => [EnbSpaceScenePass.Orbitals, EnbSpaceScenePass.Backdrop, EnbSpaceScenePass.World],
        };

    // 0050DB20: fixed orbital -> sky -> world order, no star draw.
    public static IReadOnlyList<EnbSpaceScenePass> GasPasses { get; } =
        [EnbSpaceScenePass.Orbitals, EnbSpaceScenePass.Backdrop, EnbSpaceScenePass.World];

    public static EnbRenderClipRange BackdropClip => new(20, 100000); // AECF38/34
    public static EnbRenderClipRange StarsClip => new(50, 150); // AECF40/3C
}
