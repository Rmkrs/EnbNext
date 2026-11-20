using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbFrontendController
{
    private void RenderImportButton(ImmutableArray<EnbFrontendDraw>.Builder draws)
    {
        if(_screen!="login"||!_hits.TryGetValue("BUTTON_REGISTRA",out var original))return;
        int index=-1;for(int i=0;i<draws.Count;i++)if(draws[i].Key=="ui/BUTTON_REGISTRA"){index=i;break;}
        if(index<0)return;
        var source=draws[index];double gap=3*Bounds.Width/640;
        double leftWidth=(original.Width-gap)*.56,rightWidth=original.Width-gap-leftWidth;
        var left=original with {Width=leftWidth};var right=new EnbUiRect(left.Right+gap,original.Y,rightWidth,original.Height);
        EnbFrontendDraw Button(string id,EnbUiRect rect)
        {
            float x=(float)(original.X*640/Bounds.Width-320);
            var transform=source.Transform*Matrix4x4.CreateTranslation(-x,0,0)*Matrix4x4.CreateScale((float)(rect.Width/original.Width),1,1)*
                Matrix4x4.CreateTranslation((float)(rect.X*640/Bounds.Width-320),0,0);
            bool disabled=source.Disabled||Owner.Accounts.Busy||Owner.Accounts.ImportOpen;
            return source with {Key="ui/"+id,Transform=transform,Highlight=!disabled&&rect.Contains(_pointer),Disabled=disabled,Pressed=_pressedName==id};
        }
        draws[index]=Button("BUTTON_REGISTRA",left);draws.Add(Button("BUTTON_IMPORT",right));
        _hits["BUTTON_REGISTRA"]=left;_hits["BUTTON_IMPORT"]=right;
        var label=Owner.Text.Get("Register_fe01a");
        var import=Owner.Text.Get("Import_fe01a");
        import.SetText("Import Accounts",label.Font??Owner.Text.Font("FONT_SMALL",Bounds.Size),source.Disabled||Owner.Accounts.Busy||Owner.Accounts.ImportOpen?new(.25f,.25f,.25f):right.Contains(_pointer)?new(0,1,0):new(1,1,1));
        import.SetDisplayScale(1,1);
        // Both halves share one size, shrunk until the wider label fits its half at narrow resolutions.
        double pad=8*Bounds.Width/640,fit=Math.Min(1,Math.Min((left.Width-pad)/label.Width,(right.Width-pad)/import.Width));
        double middle=label.Y+label.Height/2;
        label.Shrink(fit);import.Shrink(fit);
        label.X=left.Center.X-label.Width/2;label.Y=middle-label.Height/2;
        import.X=right.Center.X-import.Width/2;import.Y=label.Y;import.Opacity=label.Opacity;import.ZIndex=label.ZIndex;
    }
}
