using System.Runtime.InteropServices;
namespace EnbNext.App;
internal static class CefNative
{
    private const string Library="EnbNext.Cef";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Event(nint tag,int kind,nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint Resolve([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [StructLayout(LayoutKind.Sequential)] internal struct Frame {internal uint Texture;internal int Width,Height;internal ulong Serial;}
    [StructLayout(LayoutKind.Sequential)] internal struct Rect {internal int X,Y,Width,Height;}
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int enb_cef_initialize([MarshalAs(UnmanagedType.LPUTF8Str)]string helper,[MarshalAs(UnmanagedType.LPUTF8Str)]string resources,[MarshalAs(UnmanagedType.LPUTF8Str)]string cache);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_pump();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_shutdown();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern nint enb_cef_create([MarshalAs(UnmanagedType.LPUTF8Str)]string url,[MarshalAs(UnmanagedType.LPUTF8Str)]string profile,Event callback,nint tag,Resolve resolve,nint parent,int silent);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_navigate(nint page,[MarshalAs(UnmanagedType.LPUTF8Str)]string url);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_script(nint page,[MarshalAs(UnmanagedType.LPUTF8Str)]string code);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_place(nint page,int width,int height,float scale,int visible);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_focus(nint page,int focus);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_mouse(nint page,int kind,int x,int y,int button,int value,int modifiers,int clicks=1);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_key(nint page,int kind,int key,int native,int modifiers,int character);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int enb_cef_frame(nint page,int popup,out Frame frame,out Rect rect);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_ime(nint page,[MarshalAs(UnmanagedType.LPUTF8Str)]string text,int start,int length,int commit);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_release_gpu(nint page,int lost);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_close(nint page);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int enb_cef_closed(nint page);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern void enb_cef_release(nint page);
}
