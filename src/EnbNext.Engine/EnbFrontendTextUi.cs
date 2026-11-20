using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Rect=EnbNext.Presentation.EnbUiRect;
using Size=EnbNext.Presentation.EnbUiSize;
namespace EnbNext.Engine;

internal sealed class EnbFrontendTextUi(EnbRetailAssetBundle assets) : IDisposable
{
    private readonly EnbRetailAssetBundle _assets=assets;
    private readonly EnbFrontendResources _resources=EnbRenderSession.Of(assets).Frontend;
    private readonly Dictionary<string,EnbRetailFontFace> fonts=[];
    public readonly Dictionary<string,Label> Labels=[];
    public event Action<Label>? Created;
    internal sealed class Label : EnbRetailText
    {
        public double X,Y,Opacity=1;
        public int ZIndex;
        public EnbUiRect? Clip;
        public Action? Retiring;
    }
    public Label Get(string id)
    {
        if(Labels.TryGetValue(id,out var label))return label;
        Labels.Add(id,label=new());Created?.Invoke(label);return label;
    }
    public double RenderScaling {get;set;}=1;
    public EnbRetailFontFace? Font(string id,EnbUiSize size)
    {
        if(!EnbRetailFontFactory.TryResolveDefinition(_assets,id,(int)(size.Width*RenderScaling),(int)(size.Height*RenderScaling),out var definition))return null;
        if(!fonts.TryGetValue(definition.Resource!,out var font))
        {if(!EnbRetailFontFactory.TryCreate(_assets,definition,out font))return null;fonts.Add(definition.Resource!,font);}
        return font;
    }
    public void PlaceCentered(string id,string value,EnbUiRect rect,string fontId,EnbRgb color,EnbUiSize size)
    {
        if(Font(fontId,size) is not {} font)return;
        var label=Get(id);label.ZIndex=1000;
        label.SetText(value,font,color);label.SetDisplayScale(1,1);
        var target=new EnbUiRect(rect.X*size.Width/640,rect.Y*size.Height/480,rect.Width*size.Width/640,rect.Height*size.Height/480);
        label.FitTo(target.Size,RenderScaling);
        label.X=target.Center.X-label.Width/2;label.Y=target.Center.Y-label.Height/2;label.IsVisible=true;
    }
    public void Clear()
    {
        foreach(var label in Labels.Values){label.Retiring?.Invoke();label.Retiring=null;label.Dispose();}
        Labels.Clear();_wrappedLabels.Clear();
    }
    public void ClearWrapped()=>_wrappedLabels.Clear();
    public void Dispose(){Clear();fonts.Clear();}
    private sealed record WrappedTextState(int Slot, string Text, Size Size, string[] Lines,
        EnbWrappedLabelRow[] Rows, float Speed)
    {
        public long? Tick;
    }
    private readonly Dictionary<string,WrappedTextState> _wrappedLabels=[];

    // 55F140 -> 6D0A70 -> 6CF930 constructs wrapped owners, not ordinary labels.
    public void WrappedLocation(string text,EnbFrontendModel model,EnbW3dHierarchyPoseOwner pose,long now,
        EnbInterfaceLayoutCatalog layout,EnbUiRect bounds,int slot,EnbUiPoint pointer,bool pointerInside)
    {
        EnbUiRect Screen(EnbUiRect r)=>new(r.X*bounds.Width/640,r.Y*bounds.Height/480,r.Width*bounds.Width/640,r.Height*bounds.Height/480);
        const string id="WRAP_fe12_Location_Title";
        if(!layout.TryResolve(id,out var definition))return;
        string attach=definition.Properties["Attach"];
        int pivot=model.Pivot(attach);
        if(!pose.Visibility[pivot])return;
        if(Font(definition.Properties["FontType"],bounds.Size) is not {} font)return;
        var transform=pose.WorldTransforms[pivot];
        var mesh=model.Resource.Meshes.FirstOrDefault(m=>m.Name.Equals(attach,StringComparison.OrdinalIgnoreCase));
        if(mesh is null)return;
        var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
        for(int i=0;i<8;i++)
        {
            var corner=Vector3.Transform(new Vector3((i&1)==0?mesh.BoundingBoxMin.X:mesh.BoundingBoxMax.X,
                (i&2)==0?mesh.BoundingBoxMin.Y:mesh.BoundingBoxMax.Y,(i&4)==0?mesh.BoundingBoxMin.Z:mesh.BoundingBoxMax.Z),transform);
            min=Vector3.Min(min,corner);max=Vector3.Max(max,corner);
        }
        float Number(string key,float fallback=0)=>float.Parse(definition.Properties.GetValueOrDefault(key,fallback.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture);
        float[] Pair(string key)=>definition.Properties.GetValueOrDefault(key,"0,0").Split(',').Select(v=>float.Parse(v,CultureInfo.InvariantCulture)).ToArray();
        var box=Pair("BoxOffset");var offset=Pair("Offset");
        int left=Math.Clamp((int)(min.X+box[0]),-320,320),right=Math.Clamp((int)(max.X-box[0]),-320,320);
        int top=Math.Clamp((int)(max.Y-box[1]),-240,240),bottom=Math.Clamp((int)(min.Y+box[1]),-240,240);
        var clip=Screen(new(left+320,240-top,Math.Max(0,right-left),Math.Max(0,top-bottom)));
        float xUnit=640f/(float)bounds.Width,yUnit=480f/(float)bounds.Height;
        float height=font.CellHeight*font.Scale*yUnit,step=height*Number("Spacing");
        float width=Number("Width");
        if(width<=0)width=(int)max.X-(int)min.X-2*box[0];
        width-=font.MeasureWidth("...")*font.Scale*xUnit;
        if(!_wrappedLabels.TryGetValue(id,out var state)||state.Slot!=slot||state.Text!=text||state.Size!=bounds.Size)
        {
            // 6CF930 substitutes a space for empty text; default wrapper flags
            // are established by 4FA8F0 -> 4F9BF0, shared with dialog wrapping.
            var lines=EnbRetailDialogText.Wrap(text.Length==0?" ":text,width,c=>font.CharacterAdvance(c)*font.Scale*xUnit);
            float speed=0;
            var effects=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"fontart.ini"));
            foreach(var effect in effects.EnumerateSections().Select(s=>s.Value))
                if(effect.GetValueOrDefault("EffectID")==definition.Properties.GetValueOrDefault("MouseOver") &&
                    effect.GetValueOrDefault("EffectType")=="SCROLL"&&effect.GetValueOrDefault("Direction")=="UP")
                    speed=float.Parse(effect["Speed"],CultureInfo.InvariantCulture);
            state=new(slot,text,bounds.Size,lines,lines.Select((_,i)=>new EnbWrappedLabelRow(
                (int)((Number("Wait")+i*Number("TimeSpacing"))*1000))).ToArray(),speed);
            _wrappedLabels[id]=state;
        }
        int delta=state.Tick is {} tick?(int)Math.Max(0,now-tick):0;state.Tick=now;
        // 6D5D40/6CD180 use the attachment clip, independently of button hits.
        bool hovered=pointerInside&&pointer.X>clip.Left&&pointer.X<clip.Right&&pointer.Y>clip.Top&&pointer.Y<clip.Bottom;
        float limit=Math.Max(0,Math.Abs(step)*state.Lines.Length-(int)(max.Y-min.Y-2*box[1]));
        var origin=transform.Translation;
        var color=_resources.Color(definition.Properties["Color"]);
        for(int i=0;i<state.Lines.Length;i++)
        {
            var sample=state.Rows[i].Advance(delta,hovered,limit,state.Speed);
            if(!sample.Visible)continue;
            float y=origin.Y+offset[1]*480+i*step+sample.Offset;
            bool ellipsis=state.Rows[i].UpdateEllipsis(y,bottom,(int)height,i+1==state.Lines.Length);
            string key=id+"/"+i;
            var label=Get(key);
            label.SetText(state.Lines[i]+(ellipsis?"...":""),font,color);label.SetDisplayScale(1,1);
            var anchor=Screen(new(origin.X+offset[0]*640+320,240-y,0,0));
            label.X=anchor.X;label.Y=anchor.Y-label.Height/2;
            label.Clip=new(clip.X-label.X,clip.Y-label.Y,clip.Width,clip.Height);
            label.IsVisible=true;
        }
    }
}
