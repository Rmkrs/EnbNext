using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>One row of the multibox quick menu: a heading, a checkbox, or a slider over
/// <see cref="Count"/> positions.</summary>
public sealed record EnbMultiboxMenuItem(string Key, string Text, bool? On = null, bool Heading = false, int Value = 0, int Count = 0)
{
    public bool Slider => Count > 0;
}

/// <summary>Next-only quick menu opened from the multibox utility button. It lies above the
/// button and is built from the game's own Options art: the OPT04A checkbox and slider track,
/// and the 0066E460 slider arrows and thumb. It stays open while entries
/// change and closes on an outside click or Escape.</summary>
internal sealed class EnbMultiboxMenu(EnbRetailAssetBundle assets) : IDisposable
{
    internal EnbUiLayoutProfiles? Layout {get;set;}
    public ImmutableArray<EnbMultiboxMenuItem> Items {get;set;}=[];
    /// <summary>Activated(key, value): a checkbox reports -1, a slider its new position.</summary>
    public Action<string,int>? Activated {get;set;}
    public bool Open {get;private set;}
    /// <summary>The button's screen rectangle (before the Utility layout placement).</summary>
    public EnbUiRect Button {get;set;}
    public bool Dragging=>drag is not null;
    private readonly EnbConfirmationResources resources=EnbRenderSession.Of(assets).Confirmations;
    private readonly EnbOptionsPanelResources fonts=EnbRenderSession.Of(assets).OptionsPanels;
    private readonly EnbSliderPanelResources panel=EnbRenderSession.Of(assets).SliderPanels;
    private readonly EnbHorizontalSliderResources slider=EnbRenderSession.Of(assets).HorizontalSlider;
    private readonly List<EnbHorizontalSliderVisual> thumbs=[];
    private readonly List<EnbRetailText> labels=[];
    private readonly List<(int Index,double Width,double Height,EnbAuthoredUiAsset Asset)> frames=[];
    private enum Part {Row,Left,Right,Track}
    private readonly List<(string Key,EnbUiRect Rect,Part Part,EnbUiRect Track,int Count)> hits=[];
    private readonly EnbCurrentRetailTooltipDelay tooltipDelay=new();
    private readonly object frameOwner=new();
    private readonly List<object> owners=[];
    private EnbUiTooltipDraw? tooltip;
    private EnbUiPoint pointer=new(-100000,-100000);
    private EnbUiRect bounds;
    private (string Key,EnbUiRect Track,int Count,double Thumb)? drag;
    private ImmutableList<EnbUiModelDraw>? cached;
    private EnbUiSize cachedSize;
    private double cachedScale;
    public EnbUiRect Bounds=>bounds;
    public EnbUiRect? FindHit(string key)=>hits.Where(h=>h.Key==key&&h.Part is Part.Row or Part.Track).Select(h=>(EnbUiRect?)h.Rect).FirstOrDefault();
    /// <summary>The slider arrow of an entry (forward = right arrow).</summary>
    public EnbUiRect? FindArrow(string key,bool forward)=>hits.Where(h=>h.Key==key&&h.Part==(forward?Part.Right:Part.Left)).Select(h=>(EnbUiRect?)h.Rect).FirstOrDefault();
    public void Invalidate(){cached=null;tooltip=null;}
    public void SetOpen(bool open){if(Open!=open){Open=open;drag=null;tooltipDelay.Reset();Invalidate();}}
    private EnbUiPoint Utility(EnbUiPoint p)=>Layout?.Inverse(EnbUiGroup.Utility,p)??p;
    public bool ButtonContains(EnbUiPoint p)
    {
        if(Button.Width<=0)return false;p=Utility(p);
        double x=(p.X-Button.Center.X)/(Button.Width/2),y=(p.Y-Button.Center.Y)/(Button.Height/2);return x*x+y*y<=1;
    }
    public bool Contains(EnbUiPoint p)=>ButtonContains(p)||Open&&bounds.Contains(p);
    public bool OwnsTooltip=>ButtonContains(pointer)||Open&&bounds.Contains(pointer);
    public bool ButtonHovered=>ButtonContains(pointer);
    /// <summary>Tracks the pointer; returns true when what it hovers changed.</summary>
    public bool Pointer(EnbUiPoint p)
    {
        var before=Hovered();pointer=p;
        if(drag is {} d){Slide(d.Key,d.Track,d.Count,d.Thumb,p);return true;}
        var now=Hovered();if(now==before)return false;
        // 006AB930: a fresh hover restarts the ordinary tooltip delay.
        if(now!="\0button")tooltipDelay.Reset();
        Invalidate();return true;
    }
    private string? Hovered()
    {
        if(ButtonContains(pointer))return "\0button";
        if(!Open||!bounds.Contains(pointer))return null;
        foreach(var h in hits)if(h.Rect.Contains(pointer))return h.Key+"/"+h.Part;
        return "\0frame";
    }
    /// <summary>Left press inside the open menu. Returns true when the press belonged to it.</summary>
    public bool Press(EnbUiPoint p)
    {
        if(!Open||!bounds.Contains(p))return false;
        foreach(var h in hits)
        {
            if(!h.Rect.Contains(p))continue;
            var item=Items.FirstOrDefault(i=>i.Key==h.Key);if(item is null)break;
            switch(h.Part)
            {
                case Part.Row:Activated?.Invoke(h.Key,-1);break;
                case Part.Left:if(item.Value>0)Activated?.Invoke(h.Key,item.Value-1);break;
                case Part.Right:if(item.Value<item.Count-1)Activated?.Invoke(h.Key,item.Value+1);break;
                case Part.Track:
                    double thumb=ThumbLength(h.Track.Width,h.Count);
                    drag=(h.Key,h.Track,h.Count,thumb);Slide(h.Key,h.Track,h.Count,thumb,p);break;
            }
            break;
        }
        Invalidate();return true;
    }
    public void Release(){if(drag is not null){drag=null;Invalidate();}}
    private void Slide(string key,EnbUiRect track,int count,double thumb,EnbUiPoint p)
    {
        double span=Math.Max(1,track.Width-thumb);
        int value=count<2?0:(int)Math.Clamp(Math.Round((p.X-track.X-thumb/2)/span*(count-1)),0,count-1);
        if(Items.FirstOrDefault(i=>i.Key==key) is {} item&&item.Value!=value)Activated?.Invoke(key,value);
    }
    private static double ThumbLength(double track,int count)=>Math.Max(16,track/Math.Max(1,count));
    public EnbUiTooltipDraw? Tooltip(EnbUiSize size,double scale)
    {
        if(!ButtonContains(pointer)||Open||Layout?.Editing==true){tooltipDelay.Reset();return null;}
        var tips=EnbRenderSession.Of(assets).TooltipResources;
        if(!tooltipDelay.IsReady(0,Environment.TickCount64,EnbCurrentRetailTooltipDelay.Milliseconds(assets.ResolvePlayerOptions(null).Options.InterfaceTooltipDelay)))return null;
        if(tooltip is not null)return tooltip;
        if(tips.Frame is not {} frame||tips.Definition is not {} definition||fonts.Font(definition.FontId,size,scale) is not {} font)return null;
        return tooltip=EnbTooltipContent.Create(frame,font,"Multiboxing",size,scale,new(.1f,1,.1f),pointer,i=>tipLabels[i]);
    }
    private readonly EnbRetailText[] tipLabels=Enumerable.Range(0,4).Select(_=>new EnbRetailText()).ToArray();
    /// <summary>Next Modern UI: the menu drawn as a Modern UI panel (its symbols and text), with the same entries and input.</summary>
    private bool modern;
    internal bool Modern {get=>modern;set{if(modern==value)return;modern=value;frames.Clear();Invalidate();}}
    public ImmutableList<EnbUiModelDraw> Draw(EnbUiSize size,double scale)
    {
        if(!Open||Items.IsEmpty){hits.Clear();bounds=default;return [];}
        if(cached is not null&&cachedSize==size&&cachedScale==scale)return cached;
        if(Modern&&EnbModernText.Renderer is {} modernText)return DrawModern(size,scale,modernText);
        if(cachedSize!=size||cachedScale!=scale)frames.Clear();cachedSize=size;cachedScale=scale;
        var font=fonts.Font("FONT_SMALL",size,scale);if(font is null)return [];
        hits.Clear();int labelIndex=0,ownerIndex=0,thumbIndex=0;
        var draws=ImmutableList.CreateBuilder<EnbUiModelDraw>();var texts=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        // Options art is authored at 640x480, like the settings pages it comes from.
        double kx=size.Width/640,ky=size.Height/480;
        var check=panel.Pieces.First(p=>p.Name=="CHECK00");var track=panel.Pieces.First(p=>p.Name.StartsWith("TRACK",StringComparison.Ordinal));
        double text=font.CellHeight*font.Scale,pad=8*kx,gap=6*kx;
        double checkW=check.Bounds.Width*kx,checkH=check.Bounds.Height*ky,arrow=16*kx,trackW=96*kx,sliderH=16*ky;
        double row=Math.Max(text,Math.Max(checkH,sliderH))+5*ky;
        double labelW=Items.Max(i=>font.MeasureWidth(i.Text)*font.Scale);
        double sliderW=arrow*2+trackW,width=pad*2+checkW+gap+labelW+gap+sliderW;
        double height=pad*2+Items.Sum(i=>i.Heading?row+2*ky:row);
        // Above the button, left-aligned with it, after the Utility placement.
        var button=Layout?.Apply(EnbUiGroup.Utility,Button)??Button;
        bounds=new(Math.Clamp(button.X,0,Math.Max(0,size.Width-width)),Math.Clamp(button.Y-height-4,0,Math.Max(0,size.Height-height)),width,height);
        Frame(0,bounds);
        double y=bounds.Y+pad,left=bounds.X+pad,textX=left+checkW+gap,sliderX=bounds.Right-pad-sliderW;
        foreach(var item in Items)
        {
            if(item.Heading)
            {
                var heading=new EnbUiRect(left,y+2*ky,width-pad*2,row);y+=row+2*ky;
                Text(item.Text,heading.X,heading,new(.1f,1,.1f));continue;
            }
            var line=new EnbUiRect(bounds.X+pad/2,y,width-pad,row);y+=row;
            bool hover=line.Contains(pointer)||drag?.Key==item.Key;
            // Every line highlights under the pointer; its text turns yellow while it does.
            if(hover)Frame(2,line);
            if(item.On is bool on)
            {
                var box=new EnbUiRect(left,line.Center.Y-checkH/2,checkW,checkH);
                Art(on?panel.Checked:check.Asset,box,check.Bounds.Translate(-320,-240),false);
                hits.Add((item.Key,line,Part.Row,default,0));
            }
            Text(item.Text,textX,line,hover?Yellow:White);
            if(!item.Slider)continue;
            var l=new EnbUiRect(sliderX,line.Center.Y-sliderH/2,arrow,sliderH);
            var t=new EnbUiRect(l.Right,l.Y,trackW,sliderH);
            var rt=new EnbUiRect(t.Right,l.Y,arrow,sliderH);
            Art(track.Asset,t,track.Bounds.Translate(-320,-240),false);
            Art(slider.Left,l,new(-1,-8,16,16),l.Contains(pointer));
            Art(slider.Right,rt,new(-15,-8,16,16),rt.Contains(pointer));
            if(thumbIndex==thumbs.Count)thumbs.Add(new(slider));var visual=thumbs[thumbIndex++];
            double thumb=ThumbLength(t.Width,item.Count);visual.Resize(thumb/kx);
            double tx=item.Count>1?t.X+(t.Width-thumb)*item.Value/(item.Count-1):t.X;
            Art(visual.Asset,new(tx,t.Y,thumb,sliderH),visual.Bounds,false);
            hits.Add((item.Key,l,Part.Left,t,item.Count));hits.Add((item.Key,rt,Part.Right,t,item.Count));hits.Add((item.Key,t,Part.Track,t,item.Count));
        }
        if(draws.Count>0)draws[^1]=draws[^1] with {Images=texts.ToImmutable()};
        return cached=draws.ToImmutableList();
        void Frame(int index,EnbUiRect rect)
        {
            var art=frames.FirstOrDefault(f=>f.Index==index&&f.Width==rect.Width&&f.Height==rect.Height).Asset;
            if(art is null){var source=resources.Frames[index];art=source.Asset with {Geometry=source.Resize.Geometry((float)rect.Width,(float)rect.Height)};frames.Add((index,rect.Width,rect.Height,art));}
            var min=art.Geometry!.Positions.Aggregate(Vector3.Min);var max=art.Geometry.Positions.Aggregate(Vector3.Max);
            draws.Add(new(art,0,rect,false){DesignBounds=new(min.X,-max.Y,max.X-min.X,max.Y-min.Y),GadgetOwner=index==0?frameOwner:Owner()});
        }
        void Art(EnbAuthoredUiAsset asset,EnbUiRect rect,EnbUiRect design,bool hot)=>draws.Add(new(asset,0,rect,hot){DesignBounds=design,GadgetOwner=Owner()});
        object Owner(){if(ownerIndex==owners.Count)owners.Add(new());return owners[ownerIndex++];}
        void Text(string value,double x,EnbUiRect rect,EnbRgb color)
        {
            if(labelIndex==labels.Count)labels.Add(new());var label=labels[labelIndex++];label.SetText(value,font,color);label.SetDisplayScale(1,1);
            if(label.Raster is {} raster)texts.Add(new(raster,new(x,rect.Center.Y-label.Height/2,label.Width,label.Height)));
        }
    }
    private ImmutableList<EnbUiModelDraw> DrawModern(EnbUiSize size,double scale,IEnbModernTextRenderer font)
    {
        if(cachedSize!=size||cachedScale!=scale)frames.Clear();cachedSize=size;cachedScale=scale;
        hits.Clear();
        var images=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        double u=Math.Max(.75,size.Height/1080),text=14*u,pad=Math.Round(10*u),gap=Math.Round(8*u),row=Math.Round(24*u),check=Math.Round(14*u),arrow=Math.Round(16*u),trackW=Math.Round(120*u),sliderH=Math.Round(12*u);
        float px=(float)(text*scale);
        double labelW=Items.Max(i=>font.Measure(i.Text,px)/scale);
        double sliderW=arrow*2+trackW,width=Math.Ceiling(pad*2+check+gap+labelW+gap*2+sliderW),height=pad*2+Items.Sum(i=>i.Heading?row+Math.Round(2*u):row);
        var button=Layout?.Apply(EnbUiGroup.Utility,Button)??Button;
        bounds=new(Snap(Math.Clamp(button.X,0,Math.Max(0,size.Width-width))),Snap(Math.Clamp(button.Y-height-4*u,0,Math.Max(0,size.Height-height))),width,height);
        double y=bounds.Y+pad,left=bounds.X+pad,textX=left+check+gap,sliderX=bounds.Right-pad-sliderW;
        foreach(var item in Items)
        {
            if(item.Heading)
            {
                y+=Math.Round(2*u);Label(item.Text,left,new(left,y,width-pad*2,row),new(1,.85f,.3f));y+=row;continue;
            }
            var line=new EnbUiRect(bounds.X+pad/2,y,width-pad,row);y+=row;
            bool hover=line.Contains(pointer)||drag?.Key==item.Key;
            if(hover)images.Add(new(EnbModernIcons.Fill(new(.45f,.65f,1),60),line));
            if(item.On is bool on)
            {
                int pixels=(int)Math.Round(check*scale);
                images.Add(new(EnbModernIcons.Check(pixels,on,hover),new(Snap(left),Snap(line.Center.Y-pixels/scale/2),pixels/scale,pixels/scale)));
                hits.Add((item.Key,line,Part.Row,default,0));
            }
            Label(item.Text,textX,line,hover?new(1,1,.45f):new(.9f,.93f,1));
            if(!item.Slider)continue;
            // A slider: arrows either side of a track with its thumb.
            var l=new EnbUiRect(sliderX,line.Center.Y-arrow/2,arrow,arrow);
            var t=new EnbUiRect(l.Right,line.Center.Y-sliderH/2,trackW,sliderH);
            var r=new EnbUiRect(t.Right,l.Y,arrow,arrow);
            int arrowPixels=(int)Math.Round(arrow*scale);
            if(EnbModernSymbols.Get("back",arrowPixels,l.Contains(pointer)) is {} back)images.Add(new(back,new(Snap(l.X),Snap(l.Y),arrowPixels/scale,arrowPixels/scale)));
            if(EnbModernSymbols.Get("submenu",arrowPixels,r.Contains(pointer)) is {} forward)images.Add(new(forward,new(Snap(r.X),Snap(r.Y),arrowPixels/scale,arrowPixels/scale)));
            images.Add(new(EnbModernIcons.Fill(new(.02f,.03f,.08f),220),t));
            Border(t,new(.35f,.45f,.75f));
            double thumb=ThumbLength(t.Width,item.Count);
            double tx=item.Count>1?t.X+(t.Width-thumb)*item.Value/(item.Count-1):t.X;
            images.Add(new(EnbModernIcons.Fill(drag?.Key==item.Key||hover?new(1,1,.45f):new(.55f,.72f,1),230),new(Snap(tx),t.Y,Snap(thumb),t.Height)));
            hits.Add((item.Key,l,Part.Left,t,item.Count));hits.Add((item.Key,r,Part.Right,t,item.Count));hits.Add((item.Key,t,Part.Track,t,item.Count));
        }
        var art=frames.FirstOrDefault(f=>f.Index==0&&f.Width==bounds.Width&&f.Height==bounds.Height).Asset;
        if(art is null){var source=resources.Frames[0];art=source.Asset with {Geometry=source.Resize.Geometry((float)(bounds.Width/u),(float)(bounds.Height/u))};frames.Add((0,bounds.Width,bounds.Height,art));}
        var min=art.Geometry!.Positions.Aggregate(Vector3.Min);var max=art.Geometry.Positions.Aggregate(Vector3.Max);
        return cached=[new(art,0,bounds,false){DesignBounds=new(min.X,-max.Y,max.X-min.X,max.Y-min.Y),GadgetOwner=frameOwner,Images=images.ToImmutable()}];
        double Snap(double v)=>Math.Round(v*scale)/scale;
        void Label(string value,double x,EnbUiRect rect,EnbRgb color)
        {
            if(font.Render(value,px,color) is not {} image)return;
            double p=font.Padding(px)/scale,h=image.Height/scale;
            images.Add(new(image,new(Snap(x-p),Snap(rect.Center.Y-h/2),image.Width/scale,h)));
        }
        void Border(EnbUiRect r,EnbRgb color)
        {
            double t=Math.Max(1,Math.Round(u*scale))/scale;var fill=EnbModernIcons.Fill(color,255);
            images.Add(new(fill,new(r.X,r.Y,r.Width,t)));images.Add(new(fill,new(r.X,r.Bottom-t,r.Width,t)));
            images.Add(new(fill,new(r.X,r.Y,t,r.Height)));images.Add(new(fill,new(r.Right-t,r.Y,t,r.Height)));
        }
    }
    private static readonly EnbRgb White=new(1,1,1),Yellow=new(1,1,.1f);
    public void Dispose(){foreach(var label in labels)label.Dispose();foreach(var label in tipLabels)label.Dispose();}
}
