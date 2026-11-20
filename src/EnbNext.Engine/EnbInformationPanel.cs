using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>One mutable Information view over asset-session prepared data.</summary>
internal sealed partial class EnbInformationPanel(EnbInformationUi owner,EnbInformationResources.Panel resource, EnbOptionsPanelResources fonts,
    EnbInterfaceLayoutCatalog defaults, EnbFontEffectPalette palette, EnbVerticalScrollbarResources? scrollbar) : IDisposable
{
    internal LiveAvatarSpeechPlayback? PortraitPlayback;
    internal EnbAvatarPortraitAsset? PortraitPose;
    internal void RetirePortrait(){PortraitPlayback=null;PortraitPose=null;}
    public EnbInformationResources.Panel Resource { get; } = resource;
    private readonly object[] gadgets = Enumerable.Range(0,resource.Pieces.Length+3).Select(_=>new object()).ToArray();
    private readonly EnbAttachedFontColor[] colors = new EnbAttachedFontColor[resource.Layout.Count];
    private readonly EnbAttachedLabelAlpha?[] alpha = new EnbAttachedLabelAlpha?[resource.Layout.Count];
    private readonly EnbRetailText?[] labels = new EnbRetailText?[resource.Layout.Count+1];
    private readonly EnbVerticalScrollbarVisual? scrollVisual=scrollbar is null?null:new(scrollbar);
    private EnbUiRect scrollUp,scrollDown,scrollTrack,scrollThumb;
    public int VisibleLines {get;private set;}
    public int MaxScroll {get;private set;}
    public EnbUiRect Design(string key)=>key.EndsWith("/SCROLL_UP")?scrollUp:key.EndsWith("/SCROLL_DOWN")?scrollDown:
        key.EndsWith("/SCROLL_TRACK")?scrollTrack:key.EndsWith("/SCROLL_THUMB")?scrollThumb:Resource.Design(key);
    public EnbUiRect Screen(string key,EnbUiSize size,EnbUiPoint offset)
    {
        var r=Design(key).Translate(offset.X,offset.Y);
        return new(r.X*size.Width/640,r.Y*size.Height/480,r.Width*size.Width/640,r.Height*size.Height/480);
    }
    public static bool IsKey(string key)=>key.StartsWith("log/") || key.StartsWith("help/") || key.StartsWith("petition/") || key.StartsWith("player/") || key.StartsWith("complaint/");
    public static bool IsButton(string key)=>key is "log/EXIT2" or "log/PREVIOUS" or "log/NEXT" or "log/DATE_STAMP" or "log/ENTRY_FIELD00" or "log/MESSAGE"
        or "help/EXIT2" or "help/KNOWLEDGE" or "help/BUG" or "help/BILLING" or "help/GAMEPLAY" or "help/STUCK" or "help/PLAYER"
        or "complaint/EXIT2" or "petition/EXIT_BUTTON" or "petition/BACK" or "petition/SUBMIT" or "petition/ENTRY_FIELD00" or "petition/MESSAGE"
        or "player/ENTRY_FIELD01" or "player/EXIT_IMPORT" || key.StartsWith("complaint/BUTTON_",StringComparison.Ordinal)
        || (key.StartsWith("log/") || key.StartsWith("petition/")) && key.Contains("/SCROLL_");
    public static bool Enabled(string key,int page,uint category,int logPage)
    {
        if(key=="log/PREVIOUS")return logPage>1;
        if(key=="log/NEXT")return logPage<999;
        bool petition=page is 11 or 12 && category!=0;
        if(petition && key.StartsWith("help/") && key!="help/EXIT2")return key==(category==1?"help/GAMEPLAY":"help/STUCK");
        if(petition && key.StartsWith("complaint/BUTTON_"))return key=="complaint/BUTTON_"+(category-2).ToString("00");
        return true;
    }
    public void Draw(ImmutableList<EnbUiModelDraw>.Builder list,EnbUiSize size,double scaling,EnbUiPoint offset,
        int page,uint category,int logPage,string? pressed,string? hovered,float blink,string? error,
        EnbRetailTextEditor body,EnbRetailTextEditor entry,EnbRetailTextEditor player,ref int scroll,ref bool followCaret,bool native=false,long now=0)
    {
        string prefix=Resource.Prefix;
        for(int i=0;i<Resource.Pieces.Length;i++)
        {
            var piece=Resource.Pieces[i];string key=piece.Key;bool button=IsButton(key),enabled=Enabled(key,page,category,logPage);
            int gadget=Array.IndexOf(Resource.PointerOrder,key);
            bool selected=native && gadget>=0 && Pointer.Selected(gadget),flash=native && gadget>=0 && Pointer.Flash(gadget);
            list.Add(new(piece.Asset,!enabled || !native && pressed==key && button?1u:0u,Screen(key,size,offset),native?flash:button && enabled && hovered==key)
                {GadgetOwner=gadgets[i],DesignBounds=piece.Bounds.Translate(-320,-240),
                    EmissiveOverride=selected?new System.Numerics.Vector3(.35f):null,FlashHighlight=flash&&!Pointer.FlashInside(gadget)});
        }
        var texts=ImmutableArray.CreateBuilder<EnbUiTextDraw>();int labelIndex=0;
        foreach(var definition in Resource.Layout.Definitions)
        {
            int index=labelIndex++;
            string attach=definition.Properties.GetValueOrDefault("Attach","");
            var pivot=Resource.Asset.Hierarchy.Pivots.FirstOrDefault(p=>p.Name.Equals(attach,StringComparison.OrdinalIgnoreCase));
            if(pivot is null)continue;
            string text=definition.Properties.GetValueOrDefault("String0","");if(text.Length==0)continue;
            var p=Resource.Transforms[pivot.Ordinal].Translation;
            var localOffset=offset;
            var authored=definition.Properties.GetValueOrDefault("Offset","0,0").Split(',');
            if(authored.Length>=2 && float.TryParse(authored[0],NumberStyles.Float,CultureInfo.InvariantCulture,out float dx) &&
                float.TryParse(authored[1],NumberStyles.Float,CultureInfo.InvariantCulture,out float dy))
                localOffset=new(localOffset.X+dx*640,localOffset.Y-dy*480);
            float opacity=definition.Properties.GetValueOrDefault("MouseOver")=="FA_BLINK" && string.Equals(hovered,prefix+"/"+attach,StringComparison.OrdinalIgnoreCase) &&
                Enabled(prefix+"/"+attach.ToUpperInvariant(),page,category,logPage)?blink:1;
            var color=palette.Color(definition.Properties.GetValueOrDefault("Color","FA_WHITE"));
            if(native)
            {
                var effect=Resource.LabelStyles[index];var b=effect.Style.CenteredClipBounds;
                float x=(float)offset.X,y=-(float)offset.Y;
                int left=Math.Clamp((int)(b.X+x),-320,320),right=Math.Clamp((int)(b.Z+x),-320,320);
                int bottom=Math.Clamp((int)(b.Y+y),-240,240),top=Math.Clamp((int)(b.W+y),-240,240);
                var clip=new EnbUiRect(320+left,240-top,right-left,top-bottom);
                bool inside=EnbAttachedFontColor.Contains(clip,Math.Max(1,(int)Math.Round(size.Width*scaling)),Math.Max(1,(int)Math.Round(size.Height*scaling)),(float)inputPoint.X,(float)inputPoint.Y);
                int labelGadget=Array.FindIndex(Resource.PointerOrder,k=>k.Equals(prefix+"/"+attach,StringComparison.OrdinalIgnoreCase));
                if(prefix is "log" or "petition" or "player")color=colors[index].Update(palette,definition.Properties,inside,labelGadget>=0 && Pointer.Selected(labelGadget),now);
                opacity=(alpha[index]??=new()).Update(now,inside,effect.Initial,effect.Target,effect.Fade,effect.Style.BlinkSpeed,effect.Style.RevertSpeed);
            }
            Text(texts,index,text,new((p.X+320+localOffset.X)*size.Width/640,(240-p.Y+localOffset.Y)*size.Height/480,0,0),
                definition.Properties.GetValueOrDefault("FontType","FONT_SMALL"),color,
                definition.Properties.GetValueOrDefault("Justify","CENTER,CENTER"),size,scaling,
                opacity);
        }
        if(prefix is "log" or "petition")
        {
            DrawEditor(texts,prefix+"/ENTRY_FIELD00",entry,false,"FONT_SMALL",size,scaling,offset,page,ref scroll,ref followCaret);
            string id=prefix=="log"?"UI_LOG_FONT":"UI_PETITION_STUCK_EDIT_FONT";
            string font=defaults.TryResolve(id,out var style)?style.Properties.GetValueOrDefault("FontType","FONT_SMALL"):"FONT_SMALL";
            DrawEditor(texts,prefix+"/MESSAGE",body,true,font,size,scaling,offset,page,ref scroll,ref followCaret);
        }
        if(prefix=="player")DrawEditor(texts,"player/ENTRY_FIELD01",player,false,"FONT_SMALL",size,scaling,offset,page,ref scroll,ref followCaret);
        if(error is not null)Text(texts,labels.Length-1,error,new(size.Width/2,size.Height*.85,0,0),"FONT_TINY",new(1,.3f,.3f),"CENTER,CENTER",size,scaling,1,true);
        if(list.Count>0)list[^1]=list[^1] with {Images=texts.ToImmutable()};
        if(scrollVisual is not null && body.HasLayout)
        {
            if(native && (prefix=="log"?owner.LogInput:owner.PetitionInput?.Editor) is {} input)DrawNativeScroll(list,body,scroll,size,offset,input);
            else DrawScroll(list,body,scroll,size,offset,pressed,hovered);
        }
    }
    private void DrawNativeScroll(ImmutableList<EnbUiModelDraw>.Builder list,EnbRetailTextEditor editor,int scroll,
        EnbUiSize size,EnbUiPoint offset,EnbInformationEditorInput input)
    {
        VisibleLines=Math.Max(1,(int)(editor.Bounds.Height/editor.Step));
        MaxScroll=Math.Max(0,editor.TextRowCount-VisibleLines);
        input.UpdateLayout(editor.TextRowCount,VisibleLines,scroll);
        var slider=input.Slider;var pointer=input.SliderPointer;var visual=scrollVisual!;
        scrollUp=Resource.ScrollUp;scrollDown=Resource.ScrollDown;scrollTrack=Resource.ScrollTrack;
        visual.Resize(slider.ThumbLength);
        scrollThumb=new(scrollTrack.X,slider.ThumbStart,scrollTrack.Width,slider.ThumbLength);
        Add(visual.Resources.Up.Asset,visual.Resources.Up.Bounds,"UP",EnbScrollbarPart.Up);
        Add(visual.Resources.Down.Asset,visual.Resources.Down.Bounds,"DOWN",EnbScrollbarPart.Down);
        Add(visual.Thumb,visual.ThumbBounds,"THUMB",EnbScrollbarPart.Thumb);
        void Add(EnbAuthoredUiAsset asset,EnbUiRect bounds,string name,EnbScrollbarPart part)
        {
            bool flash=pointer.FlashSource(0)==(int)part,selected=slider.Pressed==part;
            list.Add(new(asset,0,Screen(Resource.Prefix+"/SCROLL_"+name,size,offset),flash)
                {GadgetOwner=pointer,DesignBounds=bounds,EmissiveOverride=selected?new System.Numerics.Vector3(.35f):null,
                    FlashHighlight=flash&&!pointer.FlashInside(0)});
        }
    }
    private void DrawScroll(ImmutableList<EnbUiModelDraw>.Builder list,EnbRetailTextEditor editor,int scroll,
        EnbUiSize size,EnbUiPoint offset,string? pressed,string? hovered)
    {
        var visual=scrollVisual!;
        VisibleLines=Math.Max(1,(int)(editor.Bounds.Height/editor.Step));
        MaxScroll=Math.Max(0,editor.TextRowCount-VisibleLines);
        var top=Resource.Pivot("SLIDER_00_MIN");var bottom=Resource.Pivot("SLIDER_00_MAX");
        foreach(string part in new[]{"UP","DOWN"})
        {
            var arrow=part=="UP"?visual.Resources.Up:visual.Resources.Down;var source=arrow.Bounds;
            var r=new EnbUiRect(top.X+320-source.Width/2,part=="UP"?240-top.Y:240-bottom.Y-source.Height,source.Width,source.Height);
            if(part=="UP")scrollUp=r;else scrollDown=r;
            string key=Resource.Prefix+"/SCROLL_"+part;
            list.Add(new(arrow.Asset,pressed==key?1u:0u,Screen(key,size,offset),hovered==key)
                {GadgetOwner=gadgets[part=="UP"?^3:^2],DesignBounds=source});
        }
        scrollTrack=new(scrollUp.X,scrollUp.Bottom,scrollUp.Width,Math.Max(0,scrollDown.Y-scrollUp.Bottom));
        double height=Math.Min(scrollTrack.Height,Math.Max(16,scrollTrack.Height*VisibleLines/Math.Max(1d,editor.TextRowCount)));
        visual.Resize(height);
        scrollThumb=new(scrollTrack.X,scrollTrack.Y+(MaxScroll==0?0:(scrollTrack.Height-height)*scroll/MaxScroll),scrollTrack.Width,height);
        string thumbKey=Resource.Prefix+"/SCROLL_THUMB";
        list.Add(new(visual.Thumb,pressed==thumbKey?1u:0u,Screen(thumbKey,size,offset),hovered==thumbKey)
            {GadgetOwner=gadgets[^1],DesignBounds=visual.ThumbBounds});
    }

    private void Text(ImmutableArray<EnbUiTextDraw>.Builder texts,int index,string text,EnbUiRect bounds,string fontId,
        EnbRgb color,string justify,EnbUiSize size,double scaling,float opacity,bool physicalFooter=false)
    {
        if(fonts.Font(fontId,size,scaling) is not { } font)return;
        var label=labels[index]??=new();label.SetText(text,font,color);label.SetDisplayScale(1,1);
        if(label.Raster is not { } raster)return;
        double x=justify.EndsWith("LEFT")?0:justify.EndsWith("RIGHT")?label.Width:label.Width/2;
        double y=justify.StartsWith("TOP")?0:label.Height/2;
        var positioned=new EnbUiRect(bounds.X-x,bounds.Y-y,label.Width,label.Height);
        // Match the GPU left/bottom pixel rounding before logical DPI conversion.
        // Recombining logical Y + Height otherwise moves 927.5 below the tie at 150%.
        if(physicalFooter)positioned=new(Math.Round(bounds.X*scaling-raster.Width/2d)/scaling,
            (Math.Round(bounds.Y*scaling+raster.Height/2d)-raster.Height)/scaling,label.Width,label.Height);
        texts.Add(new(raster,positioned){Opacity=opacity});
    }
    internal EnbRetailText? Label(int index)=>labels[index];
    public void Dispose(){RetirePortrait();CancelInput();foreach(var label in labels)label?.Dispose();Array.Clear(labels);}
    private void DrawEditor(ImmutableArray<EnbUiTextDraw>.Builder images, string key, EnbRetailTextEditor editor, bool multiline, string fontId, EnbUiSize size, double scaling, EnbUiPoint offset, int page, ref int scroll, ref bool followCaret)
    {
        if (fonts.Font(fontId,size,scaling) is not { } font) return;
        EnbUiRect r = Screen(key,size,offset);
        if (multiline)
        {
            // 00671CB0 creates TOP,LEFT text on the MESSAGE pivot. The mesh
            // bounds supply capacity, not the text origin (006D64C0).
            var asset = Resource.Asset;
            var pivot = asset.Hierarchy.Pivots.First(p=>p.Name=="MESSAGE");
            var p = Resource.Transforms[pivot.Ordinal].Translation;
            r = new((p.X+320+offset.X)*size.Width/640,(240-p.Y+offset.Y)*size.Height/480,r.Width,r.Height);
        }
        // 005AB030/005D4890: page/subject 95%; 005D6BC0: player 90%.
        double inset = key.StartsWith("player/") ? .05 : .025;
        if (!multiline)
        {
            var source = Resource.Asset;
            var pivot = source.Hierarchy.Pivots.First(p=>p.Name.Equals(key[(key.IndexOf('/')+1)..],StringComparison.OrdinalIgnoreCase));
            var p = Resource.Transforms[pivot.Ordinal].Translation;
            r = new((p.X+320+offset.X)*size.Width/640,(240-p.Y+offset.Y)*size.Height/480-r.Height/2,r.Width*(1-2*inset),r.Height);
        }
        EnbRgb textColor = new(1,1,1);
        if (multiline && defaults.TryResolve(page==10?"UI_LOG_FONT":"UI_PETITION_STUCK_EDIT_FONT",out var style))
            textColor = palette.Color(style.Properties[editor.IsFocused?"AltColor":"Color"]);
        else if (!multiline)
        {
            string id = key.StartsWith("log/") ? "PAGE_EDIT_log00a" : key.StartsWith("player/") ? "PLAYER_NAME_hsi03a" : "SUBJECT_EDIT_hsi02a";
            var layout = Resource.Layout;
            if (layout.TryResolve(id,out var definition))
            {
                // 006AAD10 -> 006CE910 sets the base/alternate selector;
                // 006CCE60 consumes the authored color after the state update.
                string primary = definition.Properties["Color"];
                textColor = palette.Color(editor.IsFocused ? definition.Properties.GetValueOrDefault("AltColor",primary) : primary);
            }
        }
        string placeholder = key.EndsWith("/MESSAGE") ? (page == 10 ? "<blank>" : "Click here and enter description of problem, then press Submit to send...")
            : key.StartsWith("player/") ? "Enter name or click Import Name" : "Enter Subject Here";
        editor.Draw(images,r,font,size,scaling,textColor,placeholder,scroll,followCaret);
        if(multiline) { scroll=editor.Scroll; followCaret=false; }
    }

}
