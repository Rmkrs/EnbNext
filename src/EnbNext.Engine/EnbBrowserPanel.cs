using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed class EnbBrowserPanel(EnbBrowserPanelResources resources,EnbOptionsPanelResources fonts):IDisposable
{
    public EnbBrowserPanelResources Resources=>resources;
    private readonly object[] gadgetOwners=resources.Pieces.Select(_=>new object()).ToArray();
    private readonly EnbRetailText titleLabel=new(),errorLabel=new();
    private bool disposed;
    public EnbUiGadgetPointer Pointer {get;}=new(1,hasBackground:false);
    private readonly EnbUiGadgetPointer background=new(0);
    private EnbUiGadgetContext? context;
    /// <summary>Next Modern UI: the page in a Modern UI window (frame, title, close button) instead of the retail browser frame.</summary>
    internal EnbConfirmationResources? ModernFrames {get;set;}
    private EnbUiRect modernWindow,modernClose; // fractions of the screen
    private readonly List<(double Width,double Height,EnbAuthoredUiAsset Asset)> modernArt=[];
    public uint HitMask(double x,double y)
    {
        if(ModernFrames is not null)return (modernClose.Contains(new(x,y))?1u:0u)|(modernWindow.Contains(new(x,y))?2u:0u);
        uint hits=0;
        foreach(var piece in resources.Pieces)
        {
            if(piece.Key=="manual/EXIT_BUTTON"&&piece.Query.Contains(x,y))hits|=1;
            if((piece.Query with {ParentSphere=resources.BackgroundSphere}).Contains(x,y))hits|=2;
        }
        return hits;
    }
    public void Attach(EnbUiGadgetContext target)
    {
        if(context==target)return;
        Detach();context=target;
        target.Add(Pointer,EnbUiGadgetGroup.First);target.Add(background,EnbUiGadgetGroup.First);
    }
    public void Detach(){context?.Remove(Pointer);context?.Remove(background);context=null;}
    public void CancelInput(){Pointer.Retire();background.Retire();}
    public void PrepareInput(EnbUiInputEvent input)
    {uint hits=HitMask(input.X,input.Y);Pointer.Prepare(hits&1);background.Prepare((hits>>1)&1);}
    public string? CompleteInput(EnbUiPointerFrame frame)=>Pointer.Command(frame)==0?"manual/EXIT_BUTTON":null;
    public EnbUiRect Design(string key)=>resources.Design(key);
    public void Draw(ImmutableList<EnbUiModelDraw>.Builder list,EnbUiSize size,double scaling,string title,string? hovered,string? pressed,string? error)
        =>DrawCore(list,size,scaling,title,hovered,pressed,error,false);
    public void DrawFrame(ImmutableList<EnbUiModelDraw>.Builder list,EnbUiSize size,double scaling,string title,string? error)
        =>DrawCore(list,size,scaling,title,null,null,error,true);
    private void DrawCore(ImmutableList<EnbUiModelDraw>.Builder list,EnbUiSize size,double scaling,string title,string? hovered,string? pressed,string? error,bool native)
    {
        if(disposed)return;
        if(ModernFrames is {} frames){DrawModern(list,size,scaling,title,error,native?Pointer.Flash(0):hovered=="manual/EXIT_BUTTON",frames);return;}
        foreach(int index in resources.DrawOrder)
        {
            var piece=resources.Pieces[index];bool button=piece.Key=="manual/EXIT_BUTTON",selected=button&&(native?Pointer.Selected(0):piece.Key==pressed);
            var r=piece.Bounds;
            list.Add(new(piece.Asset,!native&&selected?1u:0u,new(r.X*size.Width/640,r.Y*size.Height/480,r.Width*size.Width/640,r.Height*size.Height/480),button&&(native?Pointer.Flash(0):hovered==piece.Key&&!selected))
                {GadgetOwner=gadgetOwners[index],DesignBounds=r.Translate(-320,-240),EmissiveOverride=native&&selected?new System.Numerics.Vector3(.35f):null,
                    FlashHighlight=native&&button&&Pointer.Flash(0)&&!Pointer.FlashInside(0)});
        }
        if(list.Count>0)list[list.Count-1]=list[list.Count-1] with {Images=Labels(size,scaling,title,error)};
    }
    /// <summary>
    /// Next Modern UI: the page's place, sized for the pages shown (the support and manual sites are laid out for about
    /// 850 pixels) instead of the retail full-screen browser area; centred, a little above the middle.
    /// </summary>
    internal EnbUiRect ModernPage(EnbUiSize size)
    {
        double u=Math.Max(.75,size.Height/1080),scale=Math.Max(1,u);
        double width=Math.Min(Math.Round(900*scale),size.Width-Math.Round(40*u)),height=Math.Min(Math.Round(640*scale),size.Height-Math.Round(160*u));
        double x=Math.Round((size.Width-width)/2),y=Math.Round(Math.Max(Math.Round(50*u),(size.Height-height)*.42));
        return new(x,y,width,height);
    }
    /// <summary>The Modern UI window round the page: a dialog frame, the title and the close button.</summary>
    private void DrawModern(ImmutableList<EnbUiModelDraw>.Builder list,EnbUiSize size,double scaling,string title,string? error,bool closeHot,EnbConfirmationResources frames)
    {
        var page=ModernPage(size);
        double u=Math.Max(.75,size.Height/1080),pad=Math.Round(8*u),head=Math.Round(30*u);
        double x=Math.Max(0,page.X-pad),y=Math.Max(0,page.Y-head-pad);
        var window=new EnbUiRect(x,y,Math.Min(size.Width-x,page.Right+pad-x),Math.Min(size.Height-y,page.Bottom+pad-y));
        var art=modernArt.FirstOrDefault(a=>a.Width==window.Width&&a.Height==window.Height).Asset;
        if(art is null)
        {
            var source=frames.Frames[0];
            art=source.Asset with {Geometry=source.Resize.Geometry((float)(window.Width/u),(float)(window.Height/u))};
            if(modernArt.Count>=4)modernArt.RemoveAt(0);
            modernArt.Add((window.Width,window.Height,art));
        }
        var min=art.Geometry!.Positions.Aggregate(System.Numerics.Vector3.Min);var max=art.Geometry.Positions.Aggregate(System.Numerics.Vector3.Max);
        var images=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        double closeSize=Math.Round(head-8*u);int closePixels=(int)Math.Round(closeSize*scaling);
        var close=new EnbUiRect(Math.Round((window.Right-pad-closeSize)*scaling)/scaling,Math.Round((window.Y+(head+pad-closeSize)/2)*scaling)/scaling,closePixels/scaling,closePixels/scaling);
        images.Add(new(EnbModernIcons.CloseButton(closePixels,closeHot),close));
        if(EnbModernText.Renderer is {} m)
        {
            float px=(float)(16*u*scaling);
            if(m.Render(title,px,new(1,.85f,.3f)) is {} titleImage)
            {
                double p=m.Padding(px)/scaling,h=titleImage.Height/scaling;
                images.Add(new(titleImage,new(Math.Round((window.X+pad*1.5-p)*scaling)/scaling,Math.Round((window.Y+(head+pad)/2-h/2)*scaling)/scaling,titleImage.Width/scaling,h)));
            }
            if(error is not null&&m.Render(error,(float)(12*u*scaling),new(1,.45f,.45f)) is {} errorImage)
                images.Add(new(errorImage,new((size.Width-errorImage.Width/scaling)/2,size.Height*.47,errorImage.Width/scaling,errorImage.Height/scaling)));
        }
        list.Add(new(art,0,window,false){GadgetOwner=gadgetOwners[0],DesignBounds=new(min.X,-max.Y,max.X-min.X,max.Y-min.Y),Images=images.ToImmutable()});
        modernWindow=new(window.X/size.Width,window.Y/size.Height,window.Width/size.Width,window.Height/size.Height);
        modernClose=new(close.X/size.Width,close.Y/size.Height,close.Width/size.Width,close.Height/size.Height);
    }
    private ImmutableArray<EnbUiTextDraw> Labels(EnbUiSize size,double scaling,string title,string? error)
    {
        var result=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        if(resources.Layout.TryResolve("TITLE_JWEB01",out var definition)&&fonts.Font(definition.Properties.GetValueOrDefault("FontType",""),size,scaling) is {} font)
        {
            var point=resources.TitlePosition(definition);
            var color=definition.Properties.GetValueOrDefault("Color") switch {"FA_GREEN"=>new EnbRgb(0,1,0),"FA_YELLOW"=>new EnbRgb(1,1,0),"FA_GRAY"=>new EnbRgb(.5f,.5f,.5f),_=>new EnbRgb(1,1,1)};
            titleLabel.SetText(title,font,color);titleLabel.SetDisplayScale(1,1);
            if(titleLabel.Raster is {} raster)
            {
                string justify=definition.Properties.GetValueOrDefault("Justify","");
                double offset=justify.EndsWith("LEFT",StringComparison.OrdinalIgnoreCase)?0:justify.EndsWith("RIGHT",StringComparison.OrdinalIgnoreCase)?titleLabel.Width:titleLabel.Width/2;
                result.Add(new(raster,new((point.X+320)*size.Width/640-offset,(240-point.Y)*size.Height/480-titleLabel.Height/2,titleLabel.Width,titleLabel.Height)));
            }
        }
        if(error is not null&&fonts.Font("FONT_TINY",size,scaling) is {} errorFont)
        {
            errorLabel.SetText(error,errorFont,new(1,.3f,.3f));errorLabel.SetDisplayScale(1,1);
            if(errorLabel.Raster is {} raster)result.Add(new(raster,new((size.Width-errorLabel.Width)/2,size.Height*.47,errorLabel.Width,errorLabel.Height)));
        }
        return result.ToImmutable();
    }
    public void Dispose(){Detach();CancelInput();disposed=true;titleLabel.Clear();errorLabel.Clear();}
}
