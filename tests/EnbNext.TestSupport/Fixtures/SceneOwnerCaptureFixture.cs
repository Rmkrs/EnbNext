using EnbNext.Presentation;
using EnbNext.Net7.World;
using EnbNext.Assets;
namespace EnbNext.Rendering;
// Test-only detached input/output adapter. Runs the production owner path;
// keeps existing immutable expected-value assertions, never linked into the app.
public static class SceneOwnerCaptureFixture
{
    private static EnbSceneFrame Import(EnbSceneSnapshot source){var frame=new EnbSceneFrame();frame.Import(source);return frame;}
    private static EnbSceneSnapshot Capture(EnbSceneFrame frame)=>EnbSceneCapture.CaptureDiagnostics(frame) with {RenderPoses=frame.RenderPoses};
    public static EnbSceneSnapshot ApplyFrame(this EnbShipVisualBankRuntime runtime,EnbSceneSnapshot scene,double elapsed)
        =>Capture(runtime.ApplyFrame(Import(scene),elapsed));
    public static EnbSceneSnapshot ApplyFrame(this EnbShipWarpCacheRuntime runtime,EnbSceneSnapshot scene)
        =>Capture(runtime.ApplyFrame(Import(scene)));
    public static EnbSceneSnapshot ApplyFrame(this EnbCockpitWarpCallbacks runtime,EnbSceneSnapshot scene,EnbRetailCameraState camera,bool active=true)
        =>Capture(runtime.ApplyFrame(Import(scene),camera,active));
    public static EnbSceneSnapshot ApplyFrame(this EnbSpaceSpeakerRuntime runtime,EnbSceneSnapshot scene,uint elapsed,IReadOnlyList<EnbRgb> colors)
        =>Capture(runtime.ApplyFrame(Import(scene),elapsed,colors));
    public static EnbSceneSnapshot Apply(this EnbShipEmoteRuntime runtime,EnbSceneSnapshot scene,uint now,bool localPlaybackAllowed=true)
        =>Capture(runtime.Apply(Import(scene),now,localPlaybackAllowed));
    public static EnbSceneSnapshot? SampleLiveFrame(this EnbPresentationSnapshotPublisher publisher,Net7WorldState world,uint time,int? mode)
    {
        var frame=publisher.AcquireLiveFrame(world,time,mode);
        if(frame is null)return null;
        try{return Capture(frame);}finally{frame.Release();}
    }
}
