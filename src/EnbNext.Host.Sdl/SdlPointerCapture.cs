namespace EnbNext.Host.Sdl;

using SDL;
using static SDL.SDL3;

internal enum SdlPointerOwner {None,Flight,Station,Inventory,Chat,Cockpit,SkillMenu,Map,Options,Diagnostics,Layout,GroupPanel,MultiboxMenu,DamagePanel}
internal sealed unsafe class SdlPointerCapture(SDL_Window* window,Action<SdlPointerOwner> cancel) : IDisposable
{
    private bool native;
    internal SdlPointerOwner Owner {get;private set;}
    internal void Acquire(SdlPointerOwner next)
    {
        if(Owner==next)return;
        var old=Owner;Owner=SdlPointerOwner.None;
        if(old!=SdlPointerOwner.None)cancel(old);
        Owner=next;Sync();
    }
    internal void Release(SdlPointerOwner owner)
    {if(Owner!=owner)return;Owner=SdlPointerOwner.None;Sync();}
    private void Sync()
    {
        bool next=Owner!=SdlPointerOwner.None&&((ulong)SDL_GetWindowFlags(window)&SDL_WINDOW_INPUT_FOCUS)!=0;
        if(next==native)return;NativePins.CaptureMouse(next,"world UI capture");native=next;
    }
    internal void Cancel()=>Acquire(SdlPointerOwner.None);
    public void Dispose()=>Cancel();
}
