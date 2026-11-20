using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

// Immutable publication inputs, not a replay of retained particle/history state.
// Serialized by the diagnostic timer, never by the render callback.
internal sealed record SpacePerformanceScene(EnbSceneSnapshot Scene,
    EnbResolvedRenderCamera Camera,uint ClientTime,string RendererStatus)
{
    public bool FirstPerson { get; init; }
    public EnbRenderDetailRuntimeSettings? DetailSettings { get; init; }
    public uint? FixedSessionId { get; init; }
    public DateTimeOffset CapturedUtc { get; init; }=DateTimeOffset.UtcNow;
    public Guid BuildModule { get; init; }=typeof(SpacePerformanceScene).Module.ModuleVersionId;
}

// UI-thread state. Only Capture crosses to the diagnostic timer.
internal sealed class SpacePerformanceState(IEnbSceneFrame scene,
    EnbResolvedRenderCamera camera,uint clientTime,string rendererStatus)
{
    public IEnbSceneFrame Scene {get;set;}=scene;
    public EnbResolvedRenderCamera Camera {get;set;}=camera;
    public uint ClientTime {get;set;}=clientTime;
    public string RendererStatus {get;set;}=rendererStatus;
    public bool FirstPerson {get;set;}
    public EnbRenderDetailRuntimeSettings? DetailSettings {get;set;}
    public uint? FixedSessionId {get;set;}
    public DateTimeOffset CapturedUtc {get;set;}=DateTimeOffset.UtcNow;
    public Guid BuildModule {get;set;}=typeof(SpacePerformanceScene).Module.ModuleVersionId;
    public SpacePerformanceScene Capture()=>new(EnbSceneCapture.CaptureDiagnostics(Scene),Camera,ClientTime,RendererStatus)
    {FirstPerson=FirstPerson,DetailSettings=DetailSettings,FixedSessionId=FixedSessionId,CapturedUtc=CapturedUtc,BuildModule=BuildModule};
}
