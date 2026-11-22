using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>OPT04A Graphics presentation, retaining actual gadget and text owners.</summary>
internal sealed partial class EnbGraphicsPanel : IDisposable
{
    internal sealed class Gadget(string key, EnbUiRect bounds)
    {
        public readonly string Key=key;
        public EnbUiRect Bounds=bounds;
        public EnbHorizontalSliderVisual? Visual;
    }
    private struct Label
    {
        public EnbInterfaceTextDefinition? Definition;
        public Vector3 Position;
        public EnbRetailText? Text;
        public EnbUiRect Clip;
        public EnbAttachedFontColor Clock;
        public EnbRgb Ink;
        public int InputIndex;
    }
    private readonly EnbSliderPanelResources resources;
    private readonly EnbHorizontalSliderResources horizontal;
    private readonly EnbOptionsPanelResources fonts;
    private readonly EnbFontEffectPalette palette;
    private readonly EnbGraphicsSettingsState state;
    private Label[] labels=new Label[35];
    private int choiceCapacity=12;
    private Gadget ChoiceGadget(int i)=>Gadgets[i<12?choiceStart+i:scopeStart+11+i-12];
    private static int ChoiceInput(int i)=>i<12?i:i+21;
    // Advanced Graphics: the graphics card shares the footer row when there is a choice of card.
    private bool AdapterVisible=>state.Advanced&&state.Adapters.Length>2;
    public Gadget[] Gadgets { get; private set; }
    private readonly EnbUiRect leftBounds,rightBounds,authoredOk,authoredCancel,authoredFrame,authoredTrack;
    private readonly (Vector3 Position,EnbUiRect Clip)[] authoredLabels=new (Vector3,EnbUiRect)[12];
    private const double Indent=24;
    private readonly int sliderStart,choiceStart,scopeStart;
    private string? fittedSource,fittedFont,fittedText;
    private double fittedWidth,fittedScale;
    private bool disposed;

    public EnbGraphicsPanel(EnbSliderPanelResources resources,EnbHorizontalSliderResources horizontal,
        EnbOptionsPanelResources fonts,EnbFontEffectPalette palette,EnbGraphicsSettingsState state,EnbInterfaceLayoutCatalog layout)
    {
        this.resources=resources;this.horizontal=horizontal;this.fonts=fonts;this.palette=palette;this.state=state;
        sliderStart=resources.Pieces.Length;choiceStart=sliderStart+15;scopeStart=choiceStart+12;
        Gadgets=new Gadget[scopeStart+11];
        for(int i=0;i<sliderStart;i++)Gadgets[i]=new("graphics/"+resources.Pieces[i].Name,resources.Pieces[i].Bounds);
        leftBounds=EnbAuthoredUiAsset.MeshBounds(horizontal.Left,horizontal.Left.Resource.Meshes.Single().Name).Translate(-320,-240);
        rightBounds=EnbAuthoredUiAsset.MeshBounds(horizontal.Right,horizontal.Right.Resource.Meshes.Single().Name).Translate(-320,-240);
        for(int i=0;i<5;i++)
        {
            Gadgets[sliderStart+3*i]=new("graphics/left"+i,default);
            Gadgets[sliderStart+3*i+1]=new("graphics/right"+i,default);
            Gadgets[sliderStart+3*i+2]=new("graphics/thumb"+i,default){Visual=new(horizontal)};
        }
        var resolution=Design("graphics/RESOLUTION");
        for(int i=0;i<12;i++)Gadgets[choiceStart+i]=new($"graphics/choice{i:00}",new(resolution.X,resolution.Y-(i+1)*(resolution.Height+1),resolution.Width,resolution.Height));
        Gadgets[scopeStart]=new("graphics/scopeSystem",new(334,136,55,15));
        Gadgets[scopeStart+1]=new("graphics/scopeCharacter",new(402,136,65,15));
        authoredOk=Design("graphics/OK");authoredCancel=Design("graphics/CANCEL");authoredFrame=Design("graphics/FRAME");
        Gadgets[scopeStart+2]=new("graphics/UISCALE",default);
        Gadgets[scopeStart+3]=new("graphics/TEXTUREOVERRIDES",default);
        for(int i=0;i<5;i++)Gadgets[scopeStart+4+i]=new(i==0?"graphics/ENHANCED":i==1?"graphics/BLOOM":i==2?"graphics/HDR":i==3?"graphics/SUPERSAMPLING":"graphics/ANISOTROPIC",default);
        Gadgets[scopeStart+9]=new("graphics/BGCAP",default);
        Gadgets[scopeStart+10]=new("graphics/ADAPTER",default);
        authoredTrack=Design("graphics/TRACK04");
        var transforms=EnbAuthoredUiAsset.ResolveTransforms(resources.Asset);
        for(int i=0;i<12;i++)if(layout.TryResolve(LabelId(i),out var definition))
        {
            var pivot=resources.Asset.Hierarchy.Pivots.First(p=>p.Name.Equals(definition.Properties["Attach"],StringComparison.OrdinalIgnoreCase));
            labels[i].Definition=definition;labels[i].Position=transforms[pivot.Ordinal].Translation;
            labels[i].InputIndex=InputIndex("graphics/"+definition.Properties["Attach"].ToUpperInvariant());
            labels[i].Ink=palette.ResolveSelected(definition.Properties,false,false,0);
            var b=resources.LabelStyle(definition,default).CenteredClipBounds;
            int left=Math.Clamp((int)b.X,-320,320),right=Math.Clamp((int)b.Z,-320,320);
            int bottom=Math.Clamp((int)b.Y,-240,240),top=Math.Clamp((int)b.W,-240,240);
            labels[i].Clip=new(320+left,240-top,right-left,top-bottom);
        }
        // The recommended caption is pivot-attached and therefore has a
        // full-screen retail clip. Its fixed text column ends before Low and
        // the scope selector; neither neighbour moves when text is enlarged.
        double captionLeft=labels[8].Position.X+320;
        double captionRight=Math.Min(labels[9].Position.X+320,Gadgets[scopeStart].Bounds.Left);
        labels[8].Clip=labels[8].Clip with {X=captionLeft,Width=captionRight-captionLeft};
        for(int i=0;i<12;i++)authoredLabels[i]=(labels[i].Position,labels[i].Clip);
        Relayout();
    }
    // Graphics keeps the authored OPT04A layout. Advanced Graphics replaces the retail rows
    // with the Next options and adds one footer row above Ok/Cancel for UI scale and textures.
    public void Relayout()
    {
        bool advanced=state.Advanced;var resolution=Gadgets.First(g=>g.Key=="graphics/RESOLUTION").Bounds;
        double shift=advanced?resolution.Height+6:0;
        Gadgets.First(g=>g.Key=="graphics/OK").Bounds=authoredOk.Translate(0,shift);
        Gadgets.First(g=>g.Key=="graphics/CANCEL").Bounds=authoredCancel.Translate(0,shift);
        Gadgets.First(g=>g.Key=="graphics/FRAME").Bounds=authoredFrame with {Height=authoredFrame.Height+shift};
        double available=authoredCancel.Right-authoredOk.X,gap=8;bool adapters=AdapterVisible;
        double third=(available-2*gap)/3,scaleWidth=adapters?third:(available-gap)*.45,textureWidth=adapters?third:available-scaleWidth-gap;
        Gadgets[scopeStart+2].Bounds=new(authoredOk.X,authoredOk.Y,scaleWidth,resolution.Height);
        Gadgets[scopeStart+3].Bounds=new(authoredOk.X+scaleWidth+gap,authoredOk.Y,textureWidth,resolution.Height);
        Gadgets[scopeStart+10].Bounds=adapters?new(authoredOk.X+scaleWidth+textureWidth+2*gap,authoredOk.Y,available-scaleWidth-textureWidth-2*gap,resolution.Height):default;
        var check=Design("graphics/CHECK00");
        for(int i=0;i<5;i++)Gadgets[scopeStart+4+i].Bounds=i==0?check:
            new(check.X+Indent,240-Track(i-1).Y-check.Height/2,check.Width,check.Height);
        // The background frame cap is its own top-level row on the last slider row.
        Gadgets[scopeStart+9].Bounds=new(check.X,240-Track(4).Y-check.Height/2,check.Width,check.Height);
        double inset=advanced?EnbGraphicsSettingsState.NextSliderInset:0;
        Gadgets.First(g=>g.Key=="graphics/TRACK04").Bounds=authoredTrack with {X=authoredTrack.X+inset,Width=authoredTrack.Width-inset};
        for(int i=0;i<12;i++)
        {
            var (position,clip)=authoredLabels[i];
            if(i is 6 or 7){position.Y-=(float)shift;clip=clip.Translate(0,shift);}
            labels[i].Position=position;labels[i].Clip=clip;
        }
        UpdateGeometry();
    }
    private EnbSliderInput Slider(int row)=>state.Advanced&&row==4?state.Background:state.Sliders[row];
    private int Range(int row)=>state.Advanced&&row==4?EnbGraphicsSettingsState.BackgroundCaps.Length:100;
    // Advanced Graphics has only the background cap slider; retail rows follow Use recommended values.
    private bool RowEnabled(int row)=>state.Advanced?row==4&&state.BackgroundCapEnabled:SlidersEnabled;
    private bool RetailVisible(string name)=>!state.Advanced||name is not ("CHECK00" or "RESOLUTION" or "TRACK00" or "TRACK01" or "TRACK02" or "TRACK03");
    public EnbRetailText? FindLabel(string id)
    { if(id=="graphics/scopeSystem")return labels[12].Text;if(id=="graphics/scopeCharacter")return labels[13].Text;foreach(var label in labels)if(label.Definition?.StringId==id)return label.Text;return null; }
    private static string LabelId(int index)=>index switch
    {
        0=>"title_opt04a",1=>"text01_opt04a",2=>"text02_opt04a",3=>"text03_opt04a",4=>"text04_opt04a",5=>"text05_opt04a",
        6=>"ok_opt04a",7=>"cancel_opt04a",8=>"text00_opt04a",9=>"low_opt04a",10=>"high_opt04a",_=>"resolution_opt04a"
    };
    public EnbUiRect Track(int row)
    {
        var t=resources.Tracks[row];
        return state.Advanced&&row==4?t with {X=t.X+EnbGraphicsSettingsState.NextSliderInset,Width=t.Width-EnbGraphicsSettingsState.NextSliderInset}:t;
    }
    public double ThumbLength(int row)=>Math.Max(16,Track(row).Width/Range(row));
    public double ThumbX(int row,float value)=>Track(row).X+Math.Max(0,Track(row).Width-ThumbLength(row))*value/(Range(row)-1);
    public EnbUiRect Design(string key)
    {
        foreach(var gadget in Gadgets)if(gadget is not null && gadget.Key==key)return gadget.Bounds;
        throw new KeyNotFoundException(key);
    }
    private void LayoutChoices(int count)
    {
        if(count>choiceCapacity)
        {
            CancelInput();pointer=null;
            var gadgets=Gadgets;Array.Resize(ref gadgets,scopeStart+11+count-12);Gadgets=gadgets;
            Array.Resize(ref labels,35+count-12);
            for(int i=choiceCapacity;i<count;i++)Gadgets[scopeStart+11+i-12]=new($"graphics/choice{i:00}",default);
            choiceCapacity=count;
        }
        var resolution=Design("graphics/RESOLUTION");
        if(state.UiScaleDropdown)resolution=Design("graphics/UISCALE");
        if(state.TextureDropdown)resolution=Design("graphics/TEXTUREOVERRIDES");
        if(state.AdapterDropdown)resolution=Design("graphics/ADAPTER");
        double pitch=Math.Min(resolution.Height+1,resolution.Y/Math.Max(1,count));
        for(int i=0;i<count;i++)ChoiceGadget(i).Bounds=new(resolution.X,resolution.Y-(i+1)*pitch,resolution.Width,pitch-1);
    }
    private void UpdateGeometry()
    {
        for(int row=0;row<5;row++)
        {
            var t=Track(row);var visual=Gadgets[sliderStart+3*row+2].Visual!;visual.Resize(ThumbLength(row));
            Gadgets[sliderStart+3*row].Bounds=leftBounds.Translate(320+t.X-resources.ArrowSpan,240-t.Y);
            Gadgets[sliderStart+3*row+1].Bounds=rightBounds.Translate(320+t.Right+resources.ArrowSpan,240-t.Y);
            Gadgets[sliderStart+3*row+2].Bounds=visual.Bounds.Translate(320+ThumbX(row,(float)Slider(row).Value),240-t.Y);
        }
    }
    public ImmutableList<EnbUiModelDraw> Draw(EnbUiSize size,double scaling,string? hovered,string? pressed,long elapsed,string? error,
        IReadOnlyList<EnbDisplayChoice> choices,int choice,bool dropdown,EnbDisplayScope scope,string character)
        =>DrawCore(size,scaling,hovered,pressed,elapsed,error,choices,choice,dropdown,scope,character,false);
    private ImmutableList<EnbUiModelDraw> DrawCore(EnbUiSize size,double scaling,string? hovered,string? pressed,long elapsed,string? error,
        IReadOnlyList<EnbDisplayChoice> choices,int choice,bool dropdown,EnbDisplayScope scope,string character,bool native)
    {
        if(disposed || !state.PreviewActive)return [];
        int count=state.AdapterDropdown?state.Adapters.Length:state.TextureDropdown?state.TexturePacks.Length:state.UiScaleDropdown?state.UiScales.Length:choices.Count;
        dropdown|=state.UiScaleDropdown||state.TextureDropdown||state.AdapterDropdown;LayoutChoices(count);UpdateGeometry();var result=ImmutableList.CreateBuilder<EnbUiModelDraw>();
        EnbUiRect Screen(EnbUiRect r)=>new(r.X*size.Width/640,r.Y*size.Height/480,r.Width*size.Width/640,r.Height*size.Height/480);
        for(int pass=0;pass<2;pass++)for(int i=0;i<sliderStart;i++)
        {
            var piece=resources.Pieces[i];if((piece.Name=="FRAME")!=(pass==0)||!RetailVisible(piece.Name))continue;
            var gadget=Gadgets[i];bool button=piece.Name is "EXIT" or "OK" or "CANCEL" or "CHECK00" or "RESOLUTION" || piece.Name.StartsWith("TRACK");
            bool selected=button && pressed==gadget.Key;
            Add(piece.Name=="CHECK00"&&state.Recommended?resources.Checked:piece.Asset,gadget,piece.Bounds.Translate(-320,-240),selected?1u:0u,button&&hovered==gadget.Key&&!selected);
        }
        var images=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        for(int i=0;i<12;i++)
        {
            ref var label=ref labels[i];if(label.Definition is not { } definition || fonts.Font(definition.Properties.GetValueOrDefault("FontType",""),size,scaling) is not { } font)continue;
            string text=definition.Properties.GetValueOrDefault("String0","");
            if(state.Advanced)
            {
                if(i is >=1 and <=4 or >=8)continue;
                if(i==0)text="Advanced Graphics";
                if(i==5)continue;
            }
            if(i==11 && choices.Count>0)text=choices[choice].ButtonLabel+(dropdown&&!state.UiScaleDropdown&&!state.TextureDropdown&&!state.AdapterDropdown?"  ^":"  v");
            string attach=definition.Properties["Attach"].ToUpperInvariant();
            var color=native?label.Ink:palette.Resolve(definition.Properties,hovered=="graphics/"+attach,elapsed);
            var rendered=Text(i,text,font,color,scaling);if(rendered.Raster is not { } raster)continue;
            rendered.FitTo(Screen(label.Clip).Size,scaling);
            string justify=definition.Properties.GetValueOrDefault("Justify","");
            double offset=justify.EndsWith("LEFT",StringComparison.OrdinalIgnoreCase)?0:justify.EndsWith("RIGHT",StringComparison.OrdinalIgnoreCase)?rendered.Width:rendered.Width/2;
            var p=label.Position;images.Add(new(raster,new((p.X+320)*size.Width/640-offset,(240-p.Y)*size.Height/480-rendered.Height/2,rendered.Width,rendered.Height)){Opacity=native&&i is >=1 and <=5&&!RowEnabled(i-1)?BitConverter.Int32BitsToSingle(0x3ea8f5c3):1});
        }
        if(!state.Advanced && fonts.Font("FONT_SMALL",size,scaling) is { } scopeFont)for(int i=0;i<2;i++)
        {
            var screen=Screen(Gadgets[scopeStart+i].Bounds);string caption=i==0?"System":scaling==1?Fit(character,scopeFont,screen.Width-2,scaling):character;
            bool selected=i==0?scope==EnbDisplayScope.System:scope==EnbDisplayScope.Character;
            var text=Text(12+i,caption,scopeFont,selected?new(1,1,0):new(.7f,.7f,.7f),scaling);
            text.FitTo(new(screen.Width-2,screen.Height),scaling);
            if(text.Raster is { } raster)images.Add(new(raster,new(screen.X+(screen.Width-text.Width)/2,screen.Y+(screen.Height-text.Height)/2,text.Width,text.Height)));
        }
        if(error is not null && fonts.Font("FONT_TINY",size,scaling) is { } errorFont)
        {
            var text=Text(26,error,errorFont,new(1,.3f,.3f),scaling);
            if(text.Raster is { } raster)images.Add(new(raster,new((size.Width-text.Width)/2,size.Height*.47,text.Width,text.Height)));
        }
        if(result.Count>0)result[^1]=result[^1] with {Images=images.ToImmutable()};
        if(state.Advanced)AddAdvanced();
        DrawSliders();
        DrawChoices();
        return result.ToImmutable();
        void AddAdvanced()
        {
        var scaleGadget=Gadgets[scopeStart+2];
        var scaleAsset=resources.Pieces.First(p=>p.Name=="RESOLUTION");
        Add(scaleAsset.Asset,scaleGadget,scaleAsset.Bounds.Translate(-320,-240),0,hovered==scaleGadget.Key);
        if(fonts.Font("FONT_SMALL",size,scaling) is {} scaleFont)
        {
            var bounds=Screen(scaleGadget.Bounds);
            var label=Text(27,"UI Scale: "+state.ScaleLabel(state.UiScale),scaleFont,new(1,1,1),scaling);
            label.FitTo(new(bounds.Width-4,bounds.Height),scaling);
            double fit=Math.Min(1,(bounds.Width-4)/Math.Max(1,label.Width));
            if(label.Raster is {} raster)result[^1]=result[^1] with {Images=[new(raster,new(bounds.Center.X-label.Width*fit/2,bounds.Center.Y-label.Height*fit/2,label.Width*fit,label.Height*fit))]};
        }
        var overrideGadget=Gadgets[scopeStart+3];
        var checkPiece=scaleAsset;
        Add(checkPiece.Asset,overrideGadget,checkPiece.Bounds.Translate(-320,-240),0,hovered==overrideGadget.Key);
        if(fonts.Font("FONT_SMALL",size,scaling) is {} overrideFont)
        {
            var bounds=Screen(overrideGadget.Bounds);var label=Text(28,"Textures: "+EnbGraphicsSettingsState.TextureLabel(state.TextureOverrides)+"  v",overrideFont,new(1,1,1),scaling);
            label.FitTo(new(bounds.Width-4,bounds.Height),scaling);
            double fit=Math.Min(1,(bounds.Width-4)/Math.Max(1,label.Width));
            if(label.Raster is {} raster)result[^1]=result[^1] with {Images=[new(raster,new(bounds.Center.X-label.Width*fit/2,bounds.Center.Y-label.Height*fit/2,label.Width*fit,label.Height*fit))]};
        }
        if(AdapterVisible)
        {
            var adapterGadget=Gadgets[scopeStart+10];
            Add(scaleAsset.Asset,adapterGadget,scaleAsset.Bounds.Translate(-320,-240),0,hovered==adapterGadget.Key);
            if(fonts.Font("FONT_SMALL",size,scaling) is {} adapterFont)
            {
                var bounds=Screen(adapterGadget.Bounds);
                var label=Text(34,"Card: "+state.AdapterLabel+"  v",adapterFont,new(1,1,1),scaling);
                label.FitTo(new(bounds.Width-4,bounds.Height),scaling);
                double fit=Math.Min(1,(bounds.Width-4)/Math.Max(1,label.Width));
                if(label.Raster is {} raster)result[^1]=result[^1] with {Images=[new(raster,new(bounds.Center.X-label.Width*fit/2,bounds.Center.Y-label.Height*fit/2,label.Width*fit,label.Height*fit))]};
            }
        }
        var checkAsset=resources.Pieces.First(p=>p.Name=="CHECK00");
        var capGadget=Gadgets[scopeStart+9];
        Add(state.BackgroundCapEnabled?resources.Checked:checkAsset.Asset,capGadget,checkAsset.Bounds.Translate(-320,-240),0,hovered==capGadget.Key);
        if(fonts.Font("FONT_SMALL",size,scaling) is {} capFont)
        {
            var bounds=Screen(capGadget.Bounds);
            var text=Text(5,"Background FPS cap ("+state.BackgroundCap.ToString(System.Globalization.CultureInfo.InvariantCulture)+")",capFont,new(1,1,1),scaling);
            double anchor=(labels[8].Position.X+320)*size.Width/640;
            double available=Screen(Gadgets[sliderStart+12].Bounds).Left-anchor-6;
            text.FitTo(new(available,bounds.Height),scaling);
            double fit=Math.Min(1,available/Math.Max(1,text.Width));
            if(text.Raster is {} raster)result[^1]=result[^1] with {Images=[new(raster,new(anchor,bounds.Center.Y-text.Height*fit/2,text.Width*fit,text.Height*fit))]};
        }
        for(int i=0;i<5;i++)
        {
            var gadget=Gadgets[scopeStart+4+i];bool enabled=i==0||state.Enhanced.Enabled;
            bool ticked=i==0?state.Enhanced.Enabled:i==1?state.Enhanced.Bloom:i==2?state.Enhanced.Hdr:i==3?state.Enhanced.Supersampling:state.Enhanced.AnisotropicFiltering;
            Add(ticked?resources.Checked:checkAsset.Asset,gadget,checkAsset.Bounds.Translate(-320,-240),0,enabled&&hovered==gadget.Key);
            if(!enabled)result[^1]=result[^1] with {EmissiveOverride=new Vector3(.33f)};
            if(fonts.Font("FONT_SMALL",size,scaling) is {} font)
            {
                var bounds=Screen(gadget.Bounds);var text=Text(29+i,i==0?"Enhanced Graphics":i==1?"Bloom":i==2?"HDR & Tone Mapping":i==3?"Supersampling (200%)":"Anisotropic Filtering (16x)",font,enabled?new(1,1,1):new(.4f,.4f,.4f),scaling);
                // Same box-to-caption gap as the authored Use recommended values row.
                double anchor=(labels[8].Position.X+320+(i==0?0:Indent))*size.Width/640;
                double available=Screen(Design("graphics/FRAME")).Right-anchor-8;
                text.FitTo(new(available,bounds.Height),scaling);
                double fit=Math.Min(1,available/Math.Max(1,text.Width));
                if(text.Raster is {} raster)result[^1]=result[^1] with {Images=[new(raster,new(anchor,bounds.Center.Y-text.Height*fit/2,text.Width*fit,text.Height*fit))]};
            }
        }
        }
        void DrawSliders()
        {
        for(int row=0;row<5;row++)for(int part=0;part<3;part++)
        {
            if(state.Advanced?row!=4:native&&!SlidersEnabled)continue;
            var gadget=Gadgets[sliderStart+3*row+part];var visual=gadget.Visual;
            var asset=part==0?horizontal.Left:part==1?horizontal.Right:visual!.Asset;
            var bounds=part==0?new EnbUiRect(-1,-8,16,16):part==1?new EnbUiRect(-15,-8,16,16):visual!.Bounds;
            Add(asset,gadget,bounds,0,hovered==gadget.Key);
            // An unticked background cap keeps its value visible, dimmed like other dependent rows.
            if(state.Advanced&&!state.BackgroundCapEnabled)result[^1]=result[^1] with {EmissiveOverride=new Vector3(.33f)};
        }
        }
        void DrawChoices()
        {
        if(dropdown && count>0 && fonts.Font("FONT_SMALL",size,scaling) is { } choiceFont)
        {
            var resolution=resources.Pieces.First(p=>p.Name=="RESOLUTION");
            for(int i=0;i<count;i++)
            {
                var gadget=ChoiceGadget(i);var screen=Screen(gadget.Bounds);
                bool current=state.AdapterDropdown?i==state.AdapterIndex:state.TextureDropdown?state.TexturePacks[i]==state.TextureOverrides:state.UiScaleDropdown?state.UiScales[i]==state.UiScale:i==choice;
                var text=Text(i<12?14+i:35+i-12,state.AdapterDropdown?state.Adapters[i].Label:state.TextureDropdown?EnbGraphicsSettingsState.TextureLabel(state.TexturePacks[i]):state.UiScaleDropdown?state.ScaleLabel(state.UiScales[i]):choices[i].ButtonLabel,choiceFont,current?new(1,1,0):new(1,1,1),scaling);
                text.FitTo(new(screen.Width-4,screen.Height),scaling);
                double fit=Math.Min(1,Math.Min((screen.Width-4)/Math.Max(1,text.Width),screen.Height/Math.Max(1,text.Height)));
                ImmutableArray<EnbUiTextDraw> rows=text.Raster is { } raster?[new(raster,new(screen.Center.X-text.Width*fit/2,screen.Center.Y-text.Height*fit/2,text.Width*fit,text.Height*fit))]:[];
                result.Add(new(resolution.Asset,current?1u:0u,screen,native?Pointer.Flash(ChoiceInput(i)):hovered==gadget.Key){GadgetOwner=gadget,DesignBounds=resolution.Bounds.Translate(-320,-240),Images=rows,FlashHighlight=native&&Pointer.Flash(ChoiceInput(i))&&!Pointer.FlashInside(ChoiceInput(i))});
            }
        }
        }
        void Add(EnbAuthoredUiAsset asset,Gadget gadget,EnbUiRect design,uint animation,bool hover)
        {
            if(!native){result.Add(new(asset,animation,Screen(gadget.Bounds),hover){GadgetOwner=gadget,DesignBounds=design});return;}
            string key=gadget.Key;int index=InputIndex(key);int? row=Row(key);
            var part=key.Contains("left")?EnbScrollbarPart.Up:key.Contains("right")?EnbScrollbarPart.Down:key.Contains("thumb")?EnbScrollbarPart.Thumb:EnbScrollbarPart.None;
            bool selected=row is int r?part is EnbScrollbarPart.Up or EnbScrollbarPart.Down&&Slider(r).Pressed==part:index>=0&&Pointer.Selected(index);
            bool flash=index>=0&&Pointer.Flash(index)&&(row is null||Pointer.FlashSource(index)==(int)part);
            result.Add(new(asset,0,Screen(gadget.Bounds),flash){GadgetOwner=gadget,DesignBounds=design,EmissiveOverride=selected?new Vector3(.35f):null,FlashHighlight=flash&&!Pointer.FlashInside(index)});
        }
    }
    private EnbRetailText Text(int index,string value,EnbRetailFontFace font,EnbRgb color,double scaling)
    {
        var text=labels[index].Text??=new();text.SetText(value,font,color);text.SetDisplayScale(1,1);return text;
    }
    private string Fit(string name,EnbRetailFontFace font,double width,double scaling)
    {
        if(fittedSource==name&&fittedFont==font.ResourceName&&fittedWidth==width&&fittedScale==scaling&&fittedText is {} cached)return cached;
        using var measure=new EnbRetailText();
        bool Fits(string value){measure.SetText(value,font,new(1,1,1));measure.SetDisplayScale(1,1);return measure.Width<=width;}
        string fitted=name;
        if(!Fits(name))
        {
            int low=0,high=name.Length;while(low<high){int middle=(low+high+1)/2;if(Fits(name[..middle].TrimEnd()+"..."))low=middle;else high=middle-1;}
            fitted=name[..low].TrimEnd()+"...";
        }
        fittedSource=name;fittedFont=font.ResourceName;fittedWidth=width;fittedScale=scaling;return fittedText=fitted;
    }
    public void Dispose(){CancelInput();disposed=true;foreach(var label in labels)label.Text?.Dispose();}
}
