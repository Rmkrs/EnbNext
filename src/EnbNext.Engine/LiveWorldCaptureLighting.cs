using System.Runtime.CompilerServices;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // Frontend and world are separate host surfaces but one retail client.
    // Neither HUD-only redraws nor embedded scanner completion is a native
    // ClientView frame boundary. Keep the state with the shared asset session.
    private static EnbCurrentRetailClientViewLightingState CaptureLighting(EnbRetailAssetBundle assets)
        => EnbRenderSession.Of(assets).ClientViewLighting;

    internal void ContextLost()
    {
        // 00921C50/00924890 reset the D3D device;00921880 does not restore
        // ambient lighting. A lost context is distinct from creating another
        // view on the same device. This callback must not issue GL commands.
        if (_retailAssetsAccessor() is {} assets)
            CaptureLighting(assets).ResetDevice();
        ReleaseGpuContext(null);
    }
    private void CompleteCaptureLightingFrame()
    {
        if (_retailAssetsAccessor() is {} assets)
            CaptureLighting(assets).CompleteClientViewFrame();
    }
}
