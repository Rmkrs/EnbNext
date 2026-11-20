using EnbNext.Assets;
using EnbNext.Presentation;
namespace EnbNext.Engine;
internal interface IEnbWorldHost
{
    EnbUiSize Size {get;}
    double Scale {get;}
    bool IssuePanelOpen {get;}
    bool IssueContains(EnbUiPoint point);
    bool WorldDropAllowed(EnbUiPoint point);
    IEnbOptionsBrowser CreateOptionsBrowser(Uri uri,string profile,string query);
    void EnsureCursor(EnbRetailAssetBundle? assets);
    void RequestAnimationFrame(Action frame);
    void PostRefresh(Action refresh);
    Task WaitForPresentationAsync();
    void PresentationChanged(EnbWorldController owner);
    void UpdatePerformance(bool visible,LivePerformanceSnapshot? snapshot);
    void ReleaseUi();
}
