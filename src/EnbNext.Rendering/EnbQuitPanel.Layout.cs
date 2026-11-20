using System.Collections.Immutable;
using EnbNext.Presentation;
namespace EnbNext.Rendering;
public sealed partial class EnbQuitPanel
{
    private double EditStep=>Math.Abs(Design("quit/BTTN02").Y-Design("quit/BTTN01").Y);
    public EnbUiRect EditBounds=>Design("quit/BTTN02").Translate(new(0,EditStep));
    private bool EditHit(double x,double y)=>gadgets.First(g=>g.Key=="quit/BTTN02").Query.Contains(x,y-EditStep/480);
    public EnbUiRect ScreenEdit(EnbUiSize size){var r=EditBounds;return new(r.X*size.Width/640,r.Y*size.Height/480,r.Width*size.Width/640,r.Height*size.Height/480);}
    public string? CommandKey(int index)=>layoutEditing&&index==5?"quit/EDIT":layoutEditing&&index==6?"quit/FRAME":Key(index);
    private double LayoutY(string key,double y)=>layoutEditing&&key=="quit/BTTN03"?y-EditStep/480:y;
    private EnbUiRect LayoutBounds(string key,EnbUiRect r)=>!layoutEditing?r:key=="quit/FRAME"?r with {Height=r.Height+EditStep}:key=="quit/BTTN03"?r.Translate(new(0,EditStep)):r;
    public EnbUiModelDraw Control(string id,string mesh,EnbUiRect rect,string text,EnbUiSize size,double scale,bool hover=false,bool pressed=false,string fontId="FONT_SMALL",bool heading=false,bool enabled=true,bool ellipsis=false)
    {
        var piece=gadgets.First(g=>g.Key==mesh);
        var images=ImmutableArray<EnbUiTextDraw>.Empty;
        var definition=labelInput[heading?0:1]!.Definition;if(heading)fontId=definition.Properties.GetValueOrDefault("FontType",fontId);
        if(text.Length>0&&ResolveFont(fontId,size,scale) is {} font)
        {
            if(ellipsis&&font.MeasureWidth(text)*font.Scale>Math.Max(1,rect.Width-16))
            {while(text.Length>0&&font.MeasureWidth(text+"...")*font.Scale>Math.Max(1,rect.Width-16))text=text[..^1];text+="...";}
            if(!labels.TryGetValue(id,out var label))labels[id]=label=new();
            label.SetText(text,font,enabled?palette.ResolveSelected(definition.Properties,hover,pressed,0):new EnbNext.Assets.EnbRgb(.45f,.45f,.45f));
            label.SetDisplayScale(1,1);
            var ink=label.InkBounds;double fit=Math.Min(1,Math.Min(Math.Max(1,rect.Width-16)/label.Width,Math.Max(1,rect.Height-6)/Math.Max(1,ink.Height*font.Scale)));
            label.SetDisplayScale(fit,fit);
            if(label.Raster is {} raster)images=[new(raster,new(rect.Center.X-label.Width/2,rect.Center.Y-(ink.Top+ink.Height/2)*font.Scale*fit,label.Width,label.Height)){Clip=rect}];
        }
        return new(piece.Asset,0,rect,enabled&&hover&&!pressed){DesignBounds=piece.Design.Translate(-320,-240),GadgetOwner=labels.GetValueOrDefault(id),EmissiveOverride=!enabled?new System.Numerics.Vector3(.25f):pressed?new System.Numerics.Vector3(.35f):null,Images=images};
    }
}
