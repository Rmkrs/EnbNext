using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering.OpenGL;
using SkiaSharp;

namespace EnbNext.Host.Sdl;

internal sealed unsafe partial class SdlUiGpuSurface
{
    private readonly StatusText?[] diagnosticText=new StatusText?[42];
    internal void RenderDiagnostics(EnbGlApi gl,int width,int height,double scale,EnbDiagnosticUi owner)
    {
        if(!owner.PerformanceVisible&&owner.IssueCount==0&&!owner.DeveloperOpen)return;
        var canvas=Begin(gl,0,width,height);canvas.Save();canvas.Scale((float)scale);
        if(owner.PerformanceVisible)
        {
            var text=diagnosticText[1]??=new();text.Use(owner.PerformanceText,(float)owner.PerformanceBounds.Width-24);owner.MeasurePerformanceBody(text.Height);
            Panel(owner.PerformanceBounds);Text(0,"Performance",owner.ActionBounds(0));Button(1,owner.Details?"Less":"Details");
            var p=owner.PerformanceBounds;Text(1,owner.PerformanceText,new(p.X+12,p.Y+40,p.Width-24,text.Height));
            var intervals=owner.Performance?.Intervals??[];double ceiling=50;
            foreach(double interval in intervals)ceiling=Math.Max(ceiling,interval);
            double step=(p.Width-24)/Math.Max(1,intervals.Length);float graphY=(float)(p.Y+48+text.Height);
            paint.Style=SKPaintStyle.Fill;
            for(int n=0;n<intervals.Length;n++)
            {
                float bar=(float)(Math.Clamp(intervals[n]/ceiling,0,1)*58);
                paint.Color=intervals[n]>33.34?SKColors.OrangeRed:intervals[n]>16.67?SKColors.Gold:SKColors.Turquoise;
                canvas.DrawRect((float)(p.X+12+n*step),graphY+58-bar,(float)Math.Max(1,step-1),bar,paint);
            }
            paint.Color=SKColors.White;paint.StrokeWidth=1;float budget=graphY+(float)(58*(1-16.6667/ceiling));
            canvas.DrawLine((float)p.X+12,budget,(float)p.Right-12,budget,paint);
        }
        if(owner.IssueCount>0)
        {
            Button(2,$"Issues ({owner.IssueCount})");
            if(owner.IssuesOpen)
            {
                var p=owner.IssueBounds;Panel(p);canvas.Save();canvas.ClipRect(Bounds(new(p.X+12,p.Y+10,p.Width-24,p.Height-80),scale));
                Text(3,owner.IssueText,new(p.X+12,p.Y+10-owner.IssueScroll,p.Width-24,100000));canvas.Restore();
                Text(4,owner.Status,new(p.X+12,p.Bottom-68,p.Width-24,28));Button(3,"Copy diagnostics");Button(4,"Close");
            }
        }
        if(owner.DeveloperOpen)
        {
            var p=owner.DeveloperBounds;Panel(p);Text(5,"EnbNext developer diagnostics — NOT a benchmark",new(p.X+12,p.Y+12,p.Width-105,48));Button(5,"Close");
            canvas.Save();canvas.ClipRect(SKRect.Create((float)p.X,(float)(p.Y+70),(float)p.Width,(float)(p.Height-82)));
            string[] names=["Dazzle visibility and drawing","Particle drawing (simulation continues)","Generated effect drawing (simulation continues)","Scanner rendering","HUD drawing (updates and input continue)","Backdrop / stars","Dynamic shadow capture","Gutter radar","Audio output (mixing and completion continue)"];
            for(int n=0;n<9;n++)Button(10+n,(EnbDiagnosticUi.Switch(n)?"[x] ":"[ ] ")+names[n]);
            Button(20,"−");Button(21,"+");Button(22,"−");Button(23,"+");
            var caps=owner.Caps;Text(6,$"Focused cap: {caps.Foreground}",new(p.X+52,p.Y+334-owner.Scroll,150,24));
            Text(7,$"Background: {caps.Background}",new(p.X+52,p.Y+364-owner.Scroll,150,24));
            Button(24,"Save diagnostic receipt");Button(25,"Record 10 seconds + save");
            Text(8,owner.Status+"\n"+owner.DeveloperText,new(p.X+14,p.Y+435-owner.Scroll,p.Width-28,1500));canvas.Restore();
        }
        canvas.Restore();canvas.Flush();gpu!.Flush();
        for(uint unit=0;unit<textureUnits;unit++)bindSampler(unit,0);
        gl.Disable(0x0c11);gl.DepthMask(1);
        void Panel(EnbUiRect rect){paint.Style=SKPaintStyle.Fill;paint.Color=new(12,20,28,245);canvas.DrawRect(Bounds(rect,scale),paint);}
        void Button(int id,string label)
        {
            var r=owner.ActionBounds(id);paint.Style=SKPaintStyle.Fill;paint.Color=new(40,60,78);canvas.DrawRect(Bounds(r,scale),paint);
            Text(id+16,label,new(r.X+5,r.Y+4,r.Width-10,r.Height-4));
        }
        void Text(int slot,string value,EnbUiRect rect)
        {
            if(rect.Width<=0||rect.Height<=0)return;
            var text=diagnosticText[slot]??=new();text.Use(value,(float)rect.Width);paint.Style=SKPaintStyle.Fill;paint.Color=SKColors.White;
            canvas.Save();canvas.ClipRect(Bounds(rect,scale));text.Draw(canvas,(float)rect.X,(float)rect.Y,paint);canvas.Restore();
        }
    }
}
