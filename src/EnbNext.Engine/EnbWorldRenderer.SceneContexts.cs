using EnbNext.Presentation;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private readonly EnbSceneFrame _scannerScene=new();
    private readonly EnbSceneFrame _scannerLightingScene=new();
    private IEnbSceneFrame PrepareScannerScene(IEnbSceneFrame source)
    {
        _scannerScene.AdoptMetadata(source);
        _scannerScene.Effects=[];
        return _scannerScene;
    }
}
