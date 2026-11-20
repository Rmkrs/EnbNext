using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbDamageMeterPanel(EnbRetailAssetBundle assets):IDisposable
{
    internal EnbUiLayoutProfiles? Layout {get;set;}
    private EnbCombatMeter model=new();
    private uint sector;
    private long now;
    private bool incoming,session,target,menu,cachedEditing;
    private EnbUiPoint menuAnchor;
    private EnbUiRect menuBounds;
    private EnbUiPoint pointer;
    private readonly List<(string Key,EnbUiRect Rect)> hits=[];
    private readonly List<(int Index,double Width,double Height,EnbAuthoredUiAsset Asset)> frames=[];
    private readonly List<EnbRetailText> labels=[];
    private readonly EnbConfirmationResources resources=EnbRenderSession.Of(assets).Confirmations;
    private readonly EnbOptionsPanelResources fonts=EnbRenderSession.Of(assets).OptionsPanels;
    private ImmutableList<EnbUiModelDraw>? cached;
    private EnbUiSize cachedSize;
    private double cachedScale;
    public EnbUiRect Bounds {get;private set;}
    public EnbUiRect? FindHit(string key)=>hits.Where(h=>h.Key==key).Select(h=>(EnbUiRect?)h.Rect).FirstOrDefault();
    public bool OwnsTooltip=>Contains(pointer);
    public bool Contains(EnbUiPoint p)=>Bounds.Contains(p)||(menu&&menuBounds.Contains(p));
    public bool RightClick(EnbUiPoint p){if(modern)return ModernRightClick(p);bool handled=Contains(p);menu=Bounds.Contains(p);menuAnchor=p;cached=null;return handled;}
    public void Pointer(EnbUiPoint p){if(modern)ModernPointer(p);if(pointer!=p){pointer=p;cached=null;}}
    public void Update(EnbCombatMeter owner,uint currentSector,long time)
    {model=owner;sector=currentSector;now=time;cached=null;}
    public bool Click(EnbUiPoint p)
    {
        if(modern)return ModernClick(p); // Next Modern UI: no buttons (the title drags); the choices are on the right-click menu.
        bool handled=Contains(p)||menu;
        string? key=hits.FirstOrDefault(h=>h.Rect.Contains(p)).Key;
        if(menu){if(key=="reset")model.Reset(now);menu=false;cached=null;return true;}
        switch(key){case "direction":incoming=!incoming;break;case "period":session=!session;break;case "scope":target=!target;break;case "reset":model.Reset(now);break;}
        cached=null;return handled;
    }
    private EnbCombatTotals Values(Guid id,EnbCombatMeter.Member member)=>member.Read(incoming,session,target?sector:null,target?member.TargetLife:null,target?member.TargetEpoch:null);
    internal static string Number(double value,double columnMaximum)
    {
        double unit=columnMaximum>=1000000?1000000:columnMaximum>=1000?1000:1;
        return (value/unit).ToString(unit==1?"0":"0.0",CultureInfo.InvariantCulture)+(unit==1000000?"M":unit==1000?"K":"");
    }
    public ImmutableList<EnbUiModelDraw> Draw(EnbUiSize size,double scale)
    {
        bool editing=Layout?.Editing==true;
        if(cached is not null&&cachedSize==size&&cachedScale==scale&&cachedEditing==editing)return cached;
        cachedEditing=editing;
        if(cachedSize!=size||cachedScale!=scale)frames.Clear();cachedSize=size;cachedScale=scale;
        if(modern)return DrawModern(size,scale,editing);
        var font=fonts.Font("FONT_SMALL",size,scale);if(font is null)return [];
        hits.Clear();int labelIndex=0;var draws=ImmutableList.CreateBuilder<EnbUiModelDraw>();
        double line=font.CellHeight*font.Scale+4,pad=6;
        double Measure(string s)=>font.MeasureWidth(s)*font.Scale;
        double width=Math.Max(Measure("Encounter   Taken   Target")+pad*4,Measure("Character       Damage     DPS")+pad*2);
        foreach(var member in model.Members.Select(p=>p.Value))width=Math.Max(width,Measure(member.Name)+Measure("  999.99M  999.99M")+pad*2);
        int count=editing?6:model.Members.Count;
        bool showTotal=count>1;
        double height=(count+2+(showTotal?1:0))*line+pad*2;
        Bounds=new(EnbGutterUi.EdgeClearance*size.Width/640,Math.Min(size.Height-height-12,size.Height*.26+8*line+pad*4),width,height);
        Frame(0,Bounds,false);
        double x=Bounds.X+pad,y=Bounds.Y+pad,content=width-pad*2;
        Button("period",session?"Session":"Encounter",new(x,y,content*.52,line));
        Button("direction",incoming?"Taken":"Dealt",new(x+content*.52,y,content*.23,line),true);
        Button("scope",target?"Target":"All",new(x+content*.77,y,content*.23,line),true);
        double total=0,duration=model.Duration(session,now)/1000d;
        foreach(var pair in model.Members)total+=Values(pair.Key,pair.Value).Total;
        int row=0;
        foreach(var pair in model.Members)
        {
            var values=Values(pair.Key,pair.Value);
            var rect=new EnbUiRect(x,y+line*(row+1),content,line);
            if(rect.Contains(pointer)&&!menu)Frame(2,rect,true);
            Row(pair.Value.Name,values.Total,rect);hits.Add((pair.Key.ToString(),rect));row++;
        }
        while(row<count){Row("Group member "+(row+1),0,new(x,y+line*(row+1),content,line));row++;}
        if(showTotal)
        {
        var totalRect=new EnbUiRect(x,y+line*(row+1),content,line);
        if(totalRect.Contains(pointer)&&!menu)Frame(2,totalRect,true);
        Row("Total",total,totalRect);hits.Add(("total",totalRect));row++;
        }
        Text(TimeSpan.FromMilliseconds(model.Duration(session,now)).ToString(@"hh\:mm\:ss"),new(x,y+line*(row+1),content,line),new(.7f,.7f,.7f));
        if(!menu&&!editing)Detail();
        if(menu&&!editing)
        {
            double mw=Measure("Reset")+pad*4;
            menuBounds=new(Math.Clamp(menuAnchor.X,0,Math.Max(0,size.Width-mw)),Math.Clamp(menuAnchor.Y,0,Math.Max(0,size.Height-line-pad*2)),mw,line+pad*2);
            Frame(0,menuBounds,false);var action=new EnbUiRect(menuBounds.X+pad,menuBounds.Y+pad,mw-pad*2,line);
            Frame(2,action,action.Contains(pointer));Text("Reset",action,new(1,1,0));hits.Insert(0,("reset",action));
        }
        return cached=draws.Select(d=>d with {LayoutGroup=EnbUiGroup.DamageMeter}).ToImmutableList();
        // Hovered row: the damage types in a pane attached to the meter's right edge, each in the colour retail's
        // floating damage numbers use for that type (fc_cpit.ini VictimDamage* styles).
        void Detail()
        {
            int index=hits.FindIndex(h=>h.Rect.Contains(pointer)&&(h.Key=="total"||Guid.TryParse(h.Key,out _)));
            if(index<0)return;
            var row=hits[index].Rect;var values=new EnbCombatTotals();string name="Total";
            if(hits[index].Key=="total"){foreach(var pair in model.Members){var part=Values(pair.Key,pair.Value);for(int i=0;i<7;i++)values.Types[i]+=part.Types[i];}}
            else{Guid id=Guid.Parse(hits[index].Key);if(model.Find(id) is not {} member)return;name=member.Name;values=Values(id,member);}
            // EMP drains energy and is not part of the damage total (EnbCombatTotals.Total): it follows the total line.
            var shown=Enumerable.Range(0,7).Where(i=>i!=4&&values.Types[i]>0).ToArray();bool emp=values.Types[4]>0;
            string title=name+" - "+(incoming?"Taken":"Dealt");
            double paneWidth=Math.Max(Measure(title),Measure("Explosive")+Measure("  999.99M  100%"))+pad*3;
            double paneHeight=(shown.Length+2+(emp?1:0))*line+pad*2;
            double px=Bounds.Right+2<=size.Width-paneWidth?Bounds.Right+2:Bounds.X-paneWidth-2;
            double py=Math.Clamp(row.Y-pad-line,0,Math.Max(0,size.Height-paneHeight));
            var pane=new EnbUiRect(px,py,paneWidth,paneHeight);
            Frame(0,pane,false);
            double cx=pane.X+pad,cw=paneWidth-pad*2,cy=pane.Y+pad;
            Text(title,new(cx,cy,cw,line),new(1,1,0));
            double sum=values.Total;int n=1;
            foreach(int i in shown)
            {
                var color=TypeColor(i);var rect=new EnbUiRect(cx,cy+line*n++,cw,line);
                Text(TypeNames[i],new(rect.X,rect.Y,rect.Width*.5,line),color);
                Text(Number(values.Types[i],sum),new(rect.X+rect.Width*.5,rect.Y,rect.Width*.28,line),color,true);
                Text((sum>0?values.Types[i]/sum*100:0).ToString("0",CultureInfo.InvariantCulture)+"%",new(rect.X+rect.Width*.78,rect.Y,rect.Width*.22,line),color,true);
            }
            var totalRect=new EnbUiRect(cx,cy+line*n,cw,line);
            Text("Total",new(totalRect.X,totalRect.Y,totalRect.Width*.5,line));
            Text(Number(sum,sum),new(totalRect.X+totalRect.Width*.5,totalRect.Y,totalRect.Width*.28,line),right:true);
            if(emp)
            {
                var color=TypeColor(4);var rect=new EnbUiRect(cx,cy+line*(n+1),cw,line);
                Text(TypeNames[4],new(rect.X,rect.Y,rect.Width*.5,line),color);
                Text(Number(values.Types[4],Math.Max(sum,values.Types[4])),new(rect.X+rect.Width*.5,rect.Y,rect.Width*.28,line),color,true);
            }
        }
        void Row(string name,double value,EnbUiRect rect)
        {
            string damage=Number(value,total),dps=Number(duration>0?value/duration:0,duration>0?total/duration:0)+"/s";
            Text(name,new(rect.X,rect.Y,rect.Width*.52,line));
            Text(damage,new(rect.X+rect.Width*.52,rect.Y,rect.Width*.23,line),right:true);
            Text(dps,new(rect.X+rect.Width*.77,rect.Y,rect.Width*.23,line),right:true);
        }
        void Button(string key,string text,EnbUiRect rect,bool right=false){Text(text,rect,rect.Contains(pointer)?new(0,1,0):new(1,1,0),right);hits.Add((key,rect));}
        void Text(string text,EnbUiRect rect,EnbRgb? color=null,bool right=false)
        {
            if(labelIndex==labels.Count)labels.Add(new());var label=labels[labelIndex++];label.SetText(text,font,color??new(1,1,1));label.SetDisplayScale(1,1);label.FitTo(rect.Size,scale);
            if(label.Raster is {} raster)draws[^1]=draws[^1] with {Images=draws[^1].Images.Add(new(raster,new(right?rect.Right-label.Width:rect.X,rect.Center.Y-label.Height/2,label.Width,label.Height)))};
        }
        void Frame(int index,EnbUiRect rect,bool hover)
        {
            var art=frames.FirstOrDefault(f=>f.Index==index&&f.Width==rect.Width&&f.Height==rect.Height).Asset;
            if(art is null){var source=resources.Frames[index];art=source.Asset with {Geometry=source.Resize.Geometry((float)rect.Width,(float)rect.Height)};frames.Add((index,rect.Width,rect.Height,art));}
            var min=art.Geometry!.Positions.Aggregate(Vector3.Min);var max=art.Geometry.Positions.Aggregate(Vector3.Max);
            draws.Add(new(art,0,rect,hover){DesignBounds=new(min.X,-max.Y,max.X-min.X,max.Y-min.Y)});
        }
    }
    private static readonly string[] TypeNames=["Impact","Explosive","Plasma","Energy","EMP","Chemical","Other"];
    private EnbRgb[]? typeColors;
    /// <summary>Damage type colours as retail's victim damage text (EnbDamageText): Plasma, EMP and Chemical have
    /// their own styles, every other type the normal style.</summary>
    private EnbRgb TypeColor(int type)
    {
        if(typeColors is null)
        {
            typeColors=[..Enumerable.Repeat(new EnbRgb(1,1,1),7)];
            var palette=new Dictionary<string,EnbRgb>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach(var section in EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini")).EnumerateSections().Select(s=>s.Value))
                    if(section.GetValueOrDefault("ColorID") is {} id&&section.GetValueOrDefault("RGB")?.Split(',') is {Length:3} rgb&&
                        byte.TryParse(rgb[0],out var r)&&byte.TryParse(rgb[1],out var g)&&byte.TryParse(rgb[2],out var b))palette[id]=new(r/255f,g/255f,b/255f);
            }
            catch(IOException){}
            for(int i=0;i<7;i++)
            {
                string style=i switch{2=>"VictimDamagePlasma",4=>"VictimDamageEMP",5=>"VictimDamageChemical",_=>"VictimDamageNormal"};
                if(assets.CockpitLayout?.TryResolve(style,out var definition)==true&&palette.TryGetValue(definition.Properties.GetValueOrDefault("Color",""),out var color))typeColors[i]=color;
            }
        }
        return typeColors[type];
    }
    /// <summary>The hover detail is the pane drawn beside the meter; the panel keeps the pointer so world tooltips stay away.</summary>
    public EnbUiTooltipDraw? Tooltip(EnbUiSize size,double scale)=>null;
    public void Dispose(){foreach(var label in labels)label.Dispose();}
}
