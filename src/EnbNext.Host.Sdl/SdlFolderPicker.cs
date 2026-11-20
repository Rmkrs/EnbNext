using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace EnbNext.Host.Sdl;

internal static unsafe class SdlFolderPicker
{
    // SDL owns the asynchronous native dialog. Only the managed completion is retained.
    internal static Task<string?> Show(SdlGameWindow window)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(completion);
        try { SDL_ShowOpenFolderDialog(&Complete, GCHandle.ToIntPtr(handle), window.Window, (byte*)null, false); }
        catch { handle.Free(); throw; }
        return completion.Task;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Complete(nint userdata, byte** files, int filter)
    {
        var handle = GCHandle.FromIntPtr(userdata);
        var completion = (TaskCompletionSource<string?>)handle.Target!;
        try
        {
            if (files == null) completion.TrySetException(new IOException("Unable to open the folder picker: " + SDL_GetError()));
            else completion.TrySetResult(*files == null ? null : Marshal.PtrToStringUTF8((nint)(*files)));
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally { handle.Free(); }
    }
}
