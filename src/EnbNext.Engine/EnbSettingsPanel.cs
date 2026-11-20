using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>Authored OPT04A Settings presentation over its actual draft owner.</summary>
internal sealed partial class EnbSettingsPanel(EnbSliderPanelResources resources, EnbHorizontalSliderResources horizontal,
    EnbOptionsPanelResources fonts, EnbFontEffectPalette palette, EnbSettingsState state) : IDisposable
{
    private readonly EnbHorizontalSliderVisual[] sliders = Enumerable.Range(0,5).Select(_ => new EnbHorizontalSliderVisual(horizontal)).ToArray();
    private readonly string[] keys = resources.Pieces.Select(p => "settings/" + p.Name).ToArray();
    private readonly object[] pieceOwners = resources.Pieces.Select(_ => new object()).ToArray();
    private readonly object[] sliderOwners = Enumerable.Range(0,15).Select(_ => new object()).ToArray();
    private sealed class Label(EnbInterfaceTextDefinition? definition)
    {
        public readonly EnbInterfaceTextDefinition? Definition=definition;
        public EnbRetailText? Text;
        public EnbAttachedTextStyle? Style;
        public EnbUiRect Clip;
        public EnbAttachedFontColor Clock;
        public EnbRgb Ink;
        public int GadgetIndex;
    }
    private readonly object routeOwner=new(),autoPilotOwner=new();
    private readonly object[] convenienceOwners=Enumerable.Range(0,QolCount).Select(_=>new object()).ToArray();
    private Label[] labels = [];
    public EnbUiPoint Offset { get; set; }

    public void Open(EnbInterfaceLayoutCatalog layout)
    {
        // Retail pages keep their authored placement; a grown Next category is centred.
        Dispose(); Offset=state.Page==7 && ExtraHeight>0?new(0,240-Design("settings/FRAME").Center.Y):default;
        labels=layout.Definitions.Select(d=>new Label(d) {Style=resources.LabelStyle(d,Offset),GadgetIndex=Gadget("settings/"+d.Properties.GetValueOrDefault("Attach","").ToUpperInvariant()),Ink=palette.ResolveSelected(d.Properties,false,false,0)}).Concat(Enumerable.Range(0,14).Select(_=>new Label(null))).ToArray();
        OpenEncounterSlider();
        UpdateClips();
    }

    private EnbUiRect AuthoredTrack(int row)
    {
        var t = resources.Tracks[row]; return new(320+t.X,240-t.Y-8,t.Width,t.Height);
    }
    // Next categories place one slider on a dependent row, after its caption: Quality of Life's
    // encounter pause and Navigation's waypoint arrow size.
    internal string? NextSliderKey=>state.Page!=7?null:state.Section switch
        {EnbSettingsState.QualitySection=>"settings/QOL10",EnbSettingsState.NavigationSection=>"settings/QOL16",EnbSettingsState.ModernSection=>"settings/QOL25",_=>null};
    private bool EncounterSlider=>NextSliderKey is not null;
    internal static int ArrowSizeSteps=>(EnbQualityOfLifeOptions.ArrowSizeMaximum-EnbQualityOfLifeOptions.ArrowSizeMinimum)/EnbQualityOfLifeOptions.ArrowSizeStep+1;
    public EnbUiRect Track(int row)
    {
        var track=AuthoredTrack(row);
        if(!EncounterSlider||row!=0)return track;
        double inset=EnbGraphicsSettingsState.NextSliderInset;
        return new(track.X+inset,RowCheck(Slot(NextSliderKey!)).Center.Y-track.Height/2,track.Width-inset,track.Height);
    }
    private EnbSliderInput? encounter;
    private EnbSliderInput Slider(int row)=>EncounterSlider&&row==0&&encounter is not null?encounter:state.Sliders[row];
    private int SliderRows=>EncounterSlider?1:state.Rows;
    internal static int ChatTextSteps=>EnbQualityOfLifeOptions.ChatTextMaximum-EnbQualityOfLifeOptions.ChatTextMinimum+1;
    private int Range(int row)=>EncounterSlider&&row==0?NextSliderKey switch {"settings/QOL16"=>ArrowSizeSteps,"settings/QOL25"=>ChatTextSteps,_=>60}:state.Range(row);
    private bool SliderEnabled(int row)=>!EncounterSlider||NextSliderKey switch {"settings/QOL16"=>state.QualityOfLife.WaypointArrow,"settings/QOL25"=>state.QualityOfLife.ModernUi,_=>state.Multibox.EnableDamageMeter};
    private void OpenEncounterSlider()
    {
        encounter=null;if(!EncounterSlider)return;
        var track=Track(0);double span=resources.ArrowSpan;
        var input=new EnbUiRect(track.X,track.Center.Y-span/2,track.Width,span);
        encounter=new(input,new(input.X-span,input.Y,input.Width+2*span,span),horizontal:true,singlePrecision:true);
        if(NextSliderKey=="settings/QOL16")
        {
            encounter.Configure(ArrowSizeSteps,1);
            encounter.SetValue((state.QualityOfLife.ArrowSizePercent-EnbQualityOfLifeOptions.ArrowSizeMinimum)/EnbQualityOfLifeOptions.ArrowSizeStep);
        }
        else if(NextSliderKey=="settings/QOL25"){encounter.Configure(ChatTextSteps,1);encounter.SetValue(state.QualityOfLife.ChatTextSize-EnbQualityOfLifeOptions.ChatTextMinimum);}
        else{encounter.Configure(60,1);encounter.SetValue(Math.Clamp(state.Multibox.EncounterPauseSeconds,1,60)-1);}
        encounter.Cancel();
    }
    private void SyncEncounter()
    {
        if(!EncounterSlider||encounter is null)return;
        if(NextSliderKey=="settings/QOL16")
        {
            int percent=EnbQualityOfLifeOptions.ArrowSizeMinimum+(int)Math.Round(encounter.Value)*EnbQualityOfLifeOptions.ArrowSizeStep;
            if(percent!=state.QualityOfLife.ArrowSizePercent)state.SetArrowSize(percent);
            return;
        }
        if(NextSliderKey=="settings/QOL25")
        {
            int size=EnbQualityOfLifeOptions.ChatTextMinimum+(int)Math.Round(encounter.Value);
            if(size!=state.QualityOfLife.ChatTextSize)state.SetChatTextSize(size);
            return;
        }
        int seconds=(int)Math.Round(encounter.Value)+1;
        if(seconds!=state.Multibox.EncounterPauseSeconds)state.Multibox=state.Multibox with {EncounterPauseSeconds=seconds};
    }
    // Next rows follow the authored Doppler checkbox: box left, caption right, dependents indented.
    internal readonly record struct NextRow(string Key,int Indent);
    private static readonly NextRow[] SoundRows=[new("settings/QOL3",0)];
    private static readonly NextRow[] SocialRows=[new("settings/QOL4",0),new("settings/QOL7",0),new("settings/QOL8",1)];
    private static readonly NextRow[] NavigationRows=[new("settings/ROUTE",0),new("settings/CHECK00",1),new("settings/QOL45",1),new("settings/QOL2",0),new("settings/QOL14",0),new("settings/QOL15",0),
        new("settings/QOL16",1),new("settings/QOL17",1),new("settings/QOL18",1)];
    private static readonly NextRow[] QualityRows=[new("settings/QOL5",0),new("settings/QOL6",0),new("settings/QOL9",0),new("settings/QOL10",1),new("settings/QOL13",0),new("settings/QOL12",0),
        new("settings/QOL19",0),new("settings/QOL22",1),new("settings/QOL20",1),new("settings/QOL21",1),new("settings/QOL24",0),new("settings/QOL42",0)];
    private static readonly NextRow[] ModernRows=[new("settings/QOL23",0),new("settings/QOL25",1),new("settings/QOL36",1),new("settings/QOL37",1),new("settings/QOL38",1),new("settings/QOL39",1),new("settings/QOL40",1),new("settings/QOL41",1),new("settings/QOL43",1),new("settings/QOL44",1)];
    // Labels: the switch, then one row per label category (EnbObjectLabels order).
    private static readonly NextRow[] LabelRows=[new("settings/QOL26",0),..Enumerable.Range(0,EnbObjectLabels.Count).Select(i=>new NextRow("settings/QOL"+(27+i),1))];
    internal NextRow[] Rows=>state.Page==8?SoundRows:state.Page!=7?[]:state.Section switch
        {EnbSettingsState.SocialSection=>SocialRows,EnbSettingsState.NavigationSection=>NavigationRows,EnbSettingsState.QualitySection=>QualityRows,EnbSettingsState.ModernSection=>ModernRows,EnbSettingsState.LabelsSection=>LabelRows,_=>[]};
    // Rows use the authored slider-row pitch.
    private readonly double RowPitch=resources.Tracks[0].Y-resources.Tracks[1].Y;
    private const double Indent=24;
    private int Slot(string key)
    {
        if(key=="settings/QOL11")key="settings/QOL10";
        var rows=Rows;for(int i=0;i<rows.Length;i++)if(rows[i].Key==key)return i;return -1;
    }
    // Cached authored bounds keep per-frame input allocation-free.
    private readonly EnbUiRect AuthoredCheck=resources.Pieces.First(p=>p.Name=="CHECK00").Bounds,
        authoredOk=resources.Pieces.First(p=>p.Name=="OK").Bounds,authoredCancel=resources.Pieces.First(p=>p.Name=="CANCEL").Bounds,
        authoredFrame=resources.Pieces.First(p=>p.Name=="FRAME").Bounds;
    internal EnbUiRect RowCheck(int slot)
    {
        // Next pages start where the authored Doppler checkbox sits; Sound continues on its first empty slider row.
        var check=AuthoredCheck;double top=state.Page==8?AuthoredTrack(state.Rows).Center.Y-check.Height/2:check.Y;
        return new(check.X+Rows[slot].Indent*Indent,top+slot*RowPitch,check.Width,check.Height);
    }
    internal bool RowEnabled(string key)=>key switch
    {
        "settings/CHECK00"=>state.Page!=7 || state.RoutePlanner,"settings/QOL45"=>state.RoutePlanner,"settings/QOL8"=>state.DiscordPresence.Enabled,
        "settings/QOL10" or "settings/QOL11"=>state.Multibox.EnableDamageMeter,
        "settings/QOL16" or "settings/QOL17" or "settings/QOL18"=>state.QualityOfLife.WaypointArrow,
        "settings/QOL20" or "settings/QOL21" or "settings/QOL22"=>state.QualityOfLife.MissionTracker,
        "settings/QOL25" or "settings/QOL36" or "settings/QOL37" or "settings/QOL38" or "settings/QOL39" or "settings/QOL40" or "settings/QOL41" or "settings/QOL43" or "settings/QOL44"=>state.QualityOfLife.ModernUi,
        _ when LabelCategory(key)>=0=>state.QualityOfLife.AlwaysShowLabels,_=>true
    };
    internal bool RowChecked(string key)=>key=="settings/ROUTE"?state.RoutePlanner:key=="settings/CHECK00"?state.Check:state.ConvenienceCheck(int.Parse(key.AsSpan(12)));
    private static int LabelCategory(string key)=>key.StartsWith("settings/QOL",StringComparison.Ordinal)&&int.TryParse(key.AsSpan(12),out int row)&&row>=27&&row<27+EnbObjectLabels.Count?row-27:-1;
    internal string RowText(string key)=>LabelCategory(key) is int label and >=0?EnbObjectLabels.Names[label]:key switch
    {
        "settings/ROUTE"=>"Enable Route Planner","settings/CHECK00"=>"Enable Auto Pilot","settings/QOL45"=>"Use wormholes in routes","settings/QOL3"=>"Mute audio in background",
        "settings/QOL4"=>"Auto-accept group invites","settings/QOL5"=>"Auto-accept trade","settings/QOL6"=>"Auto form up",
        "settings/QOL7"=>"Discord Rich Presence","settings/QOL8"=>"Share character name","settings/QOL9"=>"Enable damage meter",
        "settings/QOL10"=>"Encounter pause ("+state.Multibox.EncounterPauseSeconds+"s)","settings/QOL12"=>"Enable local multiboxing","settings/QOL2"=>"Shade map outside the sector",
        "settings/QOL14"=>"Enable map markers","settings/QOL15"=>"Enable waypoint arrow",
        "settings/QOL16"=>"Arrow size ("+state.QualityOfLife.ArrowSizePercent+"%)","settings/QOL17"=>"Show distance","settings/QOL18"=>"Always show height difference",
        "settings/QOL19"=>"Enable mission tracker","settings/QOL20"=>"Show in stations","settings/QOL21"=>"Tracker background","settings/QOL22"=>"Track new missions automatically","settings/QOL23"=>"Enable Modern UI","settings/QOL24"=>"Tooltips in a fixed position","settings/QOL26"=>"Always show labels on objects in view","settings/QOL36"=>"Percentages on hull, shield and energy bars","settings/QOL37"=>"Percentages on experience bars","settings/QOL38"=>"Experience values on experience bars","settings/QOL39"=>"Right-click on target to cancel targeting","settings/QOL40"=>"Hide corpses in space (loot them from Local)","settings/QOL41"=>"Keep the Local window open on Escape","settings/QOL42"=>"Mouse look and strafing in stations","settings/QOL43"=>"Connection indicator in the button bar","settings/QOL44"=>"Attacker names on damage you take","settings/QOL25"=>"Chat text size ("+state.QualityOfLife.ChatTextSize+")",_=>"Enable Loot all button"
    };
    private static string SectionTitle(int section)=>section switch
        {EnbSettingsState.SocialSection=>"Social Options",EnbSettingsState.NavigationSection=>"Navigation Options",EnbSettingsState.ModernSection=>"Modern UI",EnbSettingsState.LabelsSection=>"Object Labels",_=>"Quality of Life"};
    internal double ExtraHeight
    {
        get
        {
            var rows=Rows;if(rows.Length==0)return 0;
            return Math.Max(0,Math.Ceiling(RowCheck(rows.Length-1).Bottom+10-authoredOk.Y));
        }
    }
    public EnbUiRect Design(string key)
    {
        if(EncounterSlider && key=="settings/TRACK00")
        {
            var authored=resources.Pieces.First(p=>p.Name=="TRACK00").Bounds;double inset=EnbGraphicsSettingsState.NextSliderInset;
            return new(authored.X+inset,authored.Y+Track(0).Center.Y-AuthoredTrack(0).Center.Y,authored.Width-inset,authored.Height);
        }
        if(key.StartsWith("settings/QOL") || key=="settings/ROUTE" || state.Page==7 && key=="settings/CHECK00")
            return Slot(key) is int slot and >=0?RowCheck(slot):default;
        if (Row(key) is int row && !key.Contains("/TRACK"))
        {
            var track = Track(row);
            if(key.Contains("/left")) return new(track.X-14,track.Y,16,16);
            if(key.Contains("/right")) return new(track.Right,track.Y,16,16);
            int count=Range(row);double length=Math.Max(16,track.Width/count);
            return new(track.X+(track.Width-length)*(float)Slider(row).Value/(count-1),track.Y,length,16);
        }
        var bounds=resources.Pieces.First(p => p.Name == key[9..]).Bounds;
        if(key=="settings/FRAME")return bounds with {Height=bounds.Height+ExtraHeight};
        if(key is "settings/OK" or "settings/CANCEL")return bounds.Translate(0,ExtraHeight);
        return bounds;
    }
    public bool Visible(string key)
    {
        if(state.Page is not (7 or 8 or 9)) return false;
        ReadOnlySpan<char> name=key.AsSpan(9);
        // The encounter pause row is a slider (row 0), not a checkbox.
        if(name.StartsWith("QOL10") || name.StartsWith("QOL11") || name.StartsWith("QOL16") || name.StartsWith("QOL25"))return false;
        if(name.StartsWith("QOL") || name.SequenceEqual("ROUTE"))return Slot(key)>=0;
        if(name.SequenceEqual("RESOLUTION")) return state.Page==9;
        if(name.SequenceEqual("CHECK00")) return state.Page==7?Slot(key)>=0:state.Page==8 || state.Page==9 && state.Fonts.Length>1;
        if(Row(key) is int row) return row<SliderRows && Range(row)>1 && !(state.Page==9 && row==0 && state.Check);
        return true;
    }
    public static int? Row(string key)
    {
        ReadOnlySpan<char> span=key;
        if(!span.StartsWith("settings/"))return null;
        span=span[9..];int length=span.StartsWith("TRACK")||span.StartsWith("right")||span.StartsWith("thumb")?5:span.StartsWith("left")?4:0;
        if(length!=0 && int.TryParse(span[length..],out int row) && row is >=0 and <5)return row;
        return null;
    }
    public static bool IsButton(string key) => key.StartsWith("settings/QOL") || Row(key) is not null || key is "settings/EXIT" or "settings/OK" or "settings/CANCEL" or "settings/CHECK00" or "settings/RESOLUTION" or "settings/ROUTE";

    public ImmutableList<EnbUiModelDraw> Draw(EnbUiSize size, double scaling, string? hovered, string? pressed, long hoverElapsed, string? error)
        => DrawCore(size,scaling,hovered,pressed,hoverElapsed,error,false);

    private ImmutableList<EnbUiModelDraw> DrawCore(EnbUiSize size,double scaling,string? hovered,string? pressed,long hoverElapsed,string? error,bool native)
    {
        if(state.Page is not (7 or 8 or 9)) return [];
        var list=ImmutableList.CreateBuilder<EnbUiModelDraw>();
        EnbUiRect Screen(EnbUiRect r) => new((r.X+Offset.X)*size.Width/640,(r.Y+Offset.Y)*size.Height/480,r.Width*size.Width/640,r.Height*size.Height/480);
        // Stable native resource order, with the frame first, as in the accepted consumer.
        for(int pass=0;pass<2;pass++)
            for(int i=0;i<resources.Pieces.Length;i++)
            {
                var piece=resources.Pieces[i];string key=keys[i];
                if((piece.Name=="FRAME") != (pass==0) || !Visible(key)) continue;
                if(state.Page==7 && piece.Name=="CHECK00") continue;
                Add(piece.Name=="CHECK00" && state.Check ? resources.Checked : piece.Asset,key,Screen(Design(key)),piece.Bounds.Translate(-320,-240),pieceOwners[i]);
                if(piece.Name=="TRACK00" && !SliderEnabled(0))list[^1]=list[^1] with {EmissiveOverride=new Vector3(.33f)};
            }
        var nextRows=Rows;
        if(nextRows.Length>0)
        {
            var check=resources.Pieces.First(p=>p.Name=="CHECK00");
            foreach(var row in nextRows)
            {
                bool enabled=RowEnabled(row.Key);
                if(row.Key==NextSliderKey)continue;
                else
                {
                    int gadget=Gadget(row.Key);
                    Add(RowChecked(row.Key)?resources.Checked:check.Asset,row.Key,Screen(RowCheck(Slot(row.Key))),check.Bounds.Translate(-320,-240),gadget==0?autoPilotOwner:gadget==10?routeOwner:convenienceOwners[gadget-12]);
                    if(!enabled)list[^1]=list[^1] with {EmissiveOverride=new Vector3(.33f)};
                }
            }
        }
        var texts=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var transforms=EnbAuthoredUiAsset.ResolveTransforms(resources.Asset);
        for(int index=0;index<labels.Length-14;index++)
        {
            var d=labels[index].Definition!;string attach=d.Properties.GetValueOrDefault("Attach","");
            string text=d.Properties.GetValueOrDefault("String0","");
            if(state.Page==7 && d.StringId=="text00_opt04a") continue;
            if(state.Page==7 && state.Section!=EnbSettingsState.RetailSection)
            {
                if(attach.Equals("help text",StringComparison.OrdinalIgnoreCase))text=SectionTitle(state.Section);
                else if(attach.ToUpperInvariant() is not ("OK" or "CANCEL"))continue;
            }
            if(state.Page==9)
            {
                if(d.StringId=="title_opt_font") text="Chat Font Settings";
                if(d.StringId=="same_opt_font" && state.Fonts.Length==1) continue;
                if(d.StringId.StartsWith("slider_text_opt_font_") && int.TryParse(d.StringId.AsSpan(21),out int row))
                {
                    var f=state.Fonts[state.FontIndex];
                    text=row switch {0=>state.Check || state.Fonts.Length==1 ? "" : state.FontIndex==0 ? "Player Chat Font" : "Game Message Font",
                        1=>"Select Font ("+Path.GetFileNameWithoutExtension(f.Resource!).Replace("font_","")+")",
                        2=>FormattableString.Invariant($"Scale Font ({f.Scale:0.0})"),3=>$"Letter Spacing ({f.Spacing})",_=>$"Line Spacing ({f.LineSpacing})"};
                }
            }
            var pivot=resources.Asset.Hierarchy.Pivots.FirstOrDefault(p=>p.Name.Equals(attach,StringComparison.OrdinalIgnoreCase));
            if(pivot is null) continue;
            var color=native?labels[index].Ink:palette.Resolve(d.Properties,hovered=="settings/"+attach.ToUpperInvariant(),hoverElapsed);
            Text(index,text,transforms[pivot.Ordinal].Translation+(attach.ToUpperInvariant() is "OK" or "CANCEL"?new Vector3(0,-(float)ExtraHeight,0):Vector3.Zero),color,d.Properties.GetValueOrDefault("FontType","FONT_SMALL"),d.Properties.GetValueOrDefault("Justify",""));
        }
        if(nextRows.Length>0)
        {
            // The authored caption beside CHECK00 (text00) supplies the box-to-caption gap; captions use the
            // white slider-caption ink (text01), as the retail Camera captions do.
            var definition=labels.Select(l=>l.Definition).First(d=>d?.Properties.GetValueOrDefault("Attach","").Equals("text00",StringComparison.OrdinalIgnoreCase)==true)!;
            var caption=labels.Select(l=>l.Definition).FirstOrDefault(d=>d?.Properties.GetValueOrDefault("Attach","").Equals("text01",StringComparison.OrdinalIgnoreCase)==true)??definition;
            var pivot=resources.Asset.Hierarchy.Pivots.First(p=>p.Name.Equals(definition.Properties["Attach"],StringComparison.OrdinalIgnoreCase));
            float gap=transforms[pivot.Ordinal].Translation.X+320-(float)AuthoredCheck.X;
            var color=palette.ResolveSelected(caption.Properties,false,false,0);
            for(int slot=0;slot<nextRows.Length;slot++)
            {
                var key=nextRows[slot].Key;var bounds=RowCheck(slot);
                float left=(float)bounds.X+gap-320;
                Text(labels.Length-14+slot,RowText(key),new(left,(float)(240-bounds.Center.Y),0),color,definition.Properties.GetValueOrDefault("FontType","FONT_SMALL"),"LEFT");
                if(!RowEnabled(key) && texts.Count>0)texts[^1]=texts[^1] with {Opacity=.33f};
            }
        }
        if(error is not null) Text(labels.Length-1,error,new(0,-150,0),new(1,.3f,.3f),"FONT_TINY","");
        if(list.Count>0) list[^1]=list[^1] with {Images=texts.ToImmutable()};
        for(int i=0;i<SliderRows;i++)
        {
            string thumbKey=$"settings/thumb{i}";if(!Visible(thumbKey)) continue;
            var track=Track(i);var visual=sliders[i];visual.Resize(Math.Max(16,track.Width/Range(i)));
            string left=$"settings/left{i}",right=$"settings/right{i}";
            Add(horizontal.Left,left,Screen(Design(left)),new(-1,-8,16,16),sliderOwners[3*i]);
            Add(horizontal.Right,right,Screen(Design(right)),new(-15,-8,16,16),sliderOwners[3*i+1]);
            Add(visual.Asset,thumbKey,Screen(Design(thumbKey)),visual.Bounds,sliderOwners[3*i+2]);
            if(!SliderEnabled(i))for(int n=list.Count-3;n<list.Count;n++)list[n]=list[n] with {EmissiveOverride=new Vector3(.33f)};
        }
        return list.ToImmutable();

        void Add(EnbAuthoredUiAsset asset,string key,EnbUiRect placed,EnbUiRect design,object owner)
        {
            if(!native) {list.Add(new(asset,pressed==key && !key.Contains("thumb")?1u:0u,placed,IsButton(key)&&hovered==key) {GadgetOwner=owner,DesignBounds=design});return;}
            int gadget=Gadget(key);bool slider=Row(key) is not null;
            var part=key.Contains("left")?EnbScrollbarPart.Up:key.Contains("right")?EnbScrollbarPart.Down:key.Contains("thumb")?EnbScrollbarPart.Thumb:EnbScrollbarPart.None;
            bool selected=slider?Row(key) is int row && part is EnbScrollbarPart.Up or EnbScrollbarPart.Down && Slider(row).Pressed==part:gadget>=0 && Pointer.Selected(gadget);
            bool flash=gadget>=0 && Pointer.Flash(gadget) && (!slider || Pointer.FlashSource(gadget)==(int)part);
            list.Add(new(asset,0,placed,flash) {GadgetOwner=owner,DesignBounds=design,EmissiveOverride=selected?new Vector3(.35f):null,FlashHighlight=flash&&!Pointer.FlashInside(gadget)});
        }
        void Text(int index,string text,Vector3 p,EnbRgb color,string fontId,string justify)
        {
            if(fonts.Font(fontId,size,scaling) is not { } font) return;
            var label=labels[index].Text??=new();label.SetText(text,font,color);label.SetDisplayScale(1,1);
            if(labels[index].Definition is not null)label.FitTo(new(labels[index].Clip.Width*size.Width/640,labels[index].Clip.Height*size.Height/480),scaling);
            if(label.Raster is not { } raster) return;
            double offset=justify.EndsWith("RIGHT")?label.Width:justify.EndsWith("LEFT")?0:label.Width/2;
            texts.Add(new(raster,new((p.X+320+Offset.X)*size.Width/640-offset,(240-p.Y+Offset.Y)*size.Height/480-label.Height/2,label.Width,label.Height)));
        }
    }
    public void Dispose() { CancelInput(); foreach(var label in labels) label.Text?.Dispose();labels=[]; }
}
