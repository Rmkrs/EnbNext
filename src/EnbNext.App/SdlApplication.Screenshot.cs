using EnbNext.Assets;
using EnbNext.Rendering;
using SkiaSharp;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private bool screenshotBusy;
    private async Task TakeFrontendScreenshot()
    {
        if(screenshotBusy||frontendScreenshotDirectory is not {} directory)return;
        screenshotBusy=true;
        try{await CaptureAsync(directory,"ScreenShot",EnbScreenshotFormat.Bmp);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {AppendDiagnostic("Screenshot failed: "+ex.Message);}
        finally{screenshotBusy=false;}
    }
    private async Task TakeScreenshot()
    {
        if(screenshotBusy||worldScreen?.Owner.CanUseGeneralControls!=true||_client?.SelectedFixedSessionId is not {} character||_client.RetailAssets is not {} assets)return;
        screenshotBusy=true;
        try
        {
            _client.ReportScreenshotMessage(character,false);
            await CaptureAsync(Path.GetDirectoryName(assets.Paths.ClientExecutablePath)!,"ScreenShot",EnbScreenshotFormat.Jpeg);
            if(_client?.SelectedFixedSessionId==character)_client.ReportScreenshotMessage(character,true);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {if(_client?.SelectedFixedSessionId==character)worldScreen?.Owner.Chat.ShowError("Screenshot failed: "+ex.Message);}
        finally{screenshotBusy=false;}
    }
    private Task CaptureMovieAsync(EnbRetailAssetBundle assets,string prefix)=>CaptureAsync(Path.GetDirectoryName(assets.Paths.ClientExecutablePath)!,prefix,EnbScreenshotFormat.Tga);
    private TaskCompletionSource<SKBitmap>? pendingGlScreenshot;
    private void CompleteGlScreenshot()
    {
        var pending=pendingGlScreenshot;if(pending is null)return;pendingGlScreenshot=null;
        try{pending.TrySetResult(CaptureGl());}catch(Exception ex){pending.TrySetException(ex);}
    }
    private async Task CaptureAsync(string directory,string prefix,EnbScreenshotFormat format)
    {
        if(OperatingSystem.IsWindows()){nint handle=window.NativeHandle;await Task.Run(()=>{using var image=SdlScreenshot.Capture(handle);SdlScreenshot.SaveOutput(image,directory,prefix,format);});return;}
        if(pendingGlScreenshot is not null)throw new InvalidOperationException("A screenshot is already pending.");
        pendingGlScreenshot=new(TaskCreationOptions.RunContinuationsAsynchronously);
        SKBitmap pixels;
        try{pixels=await pendingGlScreenshot.Task;}catch(OperationCanceledException)when(_frontendClosed){return;}
        await Task.Run(()=>{using(pixels)SdlScreenshot.SaveOutput(pixels,directory,prefix,format);});
    }
    private unsafe SKBitmap CaptureGl()
    {
        int width=window.PixelWidth,height=window.PixelHeight;byte[] raw=new byte[checked(width*height*4)];
        var read=(delegate* unmanaged<int,int,int,int,uint,uint,void*,void>)window.Gl.GetProcAddress("glReadPixels");
        fixed(byte* data=raw)read(0,0,width,height,0x1908,0x1401,data);
        var image=new SKBitmap(width,height,SKColorType.Bgra8888,SKAlphaType.Opaque);
        var output=image.GetPixelSpan();
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int s=(y*width+x)*4,d=(height-y-1)*image.RowBytes+x*4;
            output[d]=raw[s+2];output[d+1]=raw[s+1];output[d+2]=raw[s];output[d+3]=255;
        }
        return image;
    }
}
