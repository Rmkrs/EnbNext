using System.Runtime.InteropServices;
using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    internal Func<EnbBuffDraw?>? LayoutBuffPreview {get;set;}
    internal EnbUiLayoutProfiles? UiLayout {get;set;}
    private EnbUiRect LayoutRect(EnbUiGroup group,EnbUiRect r)=>UiLayout?.Apply(group,r)??r;
    private EnbUiRect? LayoutRect(EnbUiGroup group,EnbUiRect? r)=>r is {} value?LayoutRect(group,value):null;
    private Matrix4x4 LayoutClip(EnbUiGroup group)=>(UiLayout?.Placement(group)??EnbUiPlacement.Identity).ClipTransform;

    // Temporary rollback: ENB_RETAIN_HUD_PROCEDURES=0 restores repeated lookup.
    // EGL procedure resolution takes ANGLE's global lock; it belongs to context
    // initialization, not repeated HUD drawing. These are delegates, not GL state.
    internal bool RetainHudProcedures { get; set; } = Environment.GetEnvironmentVariable("ENB_RETAIN_HUD_PROCEDURES") != "0";
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void MapScissor(int x,int y,int width,int height);
    private void ScissorMap(EnbGlApi gl,EnbUiRect bounds,int width,int height)
    {
        var r=(UiLayout?.Placement(EnbUiGroup.Map)??EnbUiPlacement.Identity).Apply(bounds,new(640,480));
        HudScissor(gl)((int)Math.Ceiling(r.Left*width/640),height-(int)Math.Floor(r.Bottom*height/480),Math.Max(0,(int)Math.Floor(r.Right*width/640)-(int)Math.Ceiling(r.Left*width/640)),Math.Max(0,(int)Math.Floor(r.Bottom*height/480)-(int)Math.Ceiling(r.Top*height/480)));
    }
    private MapScissor? _hudScissor;
    private LoadingColorMask? _hudColorMask;

    private MapScissor HudScissor(EnbGlApi gl) => RetainHudProcedures
        ? _hudScissor ??= Marshal.GetDelegateForFunctionPointer<MapScissor>(gl.GetProcAddress("glScissor"))
        : Marshal.GetDelegateForFunctionPointer<MapScissor>(gl.GetProcAddress("glScissor"));
    private LoadingColorMask HudColorMask(EnbGlApi gl) => RetainHudProcedures
        ? _hudColorMask ??= Marshal.GetDelegateForFunctionPointer<LoadingColorMask>(gl.GetProcAddress("glColorMask"))
        : Marshal.GetDelegateForFunctionPointer<LoadingColorMask>(gl.GetProcAddress("glColorMask"));

    private void ReleaseHudProcedures()
    {
        _hudScissor=null;
        _hudColorMask=null;
    }
}
