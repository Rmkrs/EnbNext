using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbSavedAccountsPanel
{
    public bool ImportOpen=>IsOpen&&Page!="accounts";
    public void OpenImport(){Close();ShowSync();SyncOpened?.Invoke();}
    private void RenderImport(ImmutableArray<EnbFrontendDraw>.Builder draws,EnbResolvedRenderCamera camera,
        EnbUiRect bounds,EnbUiPoint pointer,EnbFrontendVisual caret)
    {
        EnbUiRect Screen(EnbUiRect r)=>new(r.X*bounds.Width/640,r.Y*bounds.Height/480,r.Width*bounds.Width/640,r.Height*bounds.Height/480);
        var resources=owner.Resources;
        if(!resources.DialogLabels.TryResolve("DIALOG_INFO_SMALL",out var body)||!resources.DialogLabels.TryResolve("OPTION_INFO_0",out var option))return;
        string fontId=body.Properties["FontType"],optionFontId=option.Properties["FontType"];
        var font=owner.Text.Font(fontId,bounds.Size);var optionFont=owner.Text.Font(optionFontId,bounds.Size);
        if(font is null||optionFont is null)return;
        float fx=font.Scale*640/(float)bounds.Width,fy=font.Scale*480/(float)bounds.Height;
        float lineHeight=font.CellHeight*fy;
        float contentWidth=Page=="sync"?340:440;
        var lines=new List<string>{Page=="sync"?"Import Accounts from Net-7.org":"Review imported accounts",""};
        int userRow=-1,passwordRow=-1,previewStart=-1,previewEnd=-1;
        if(Page=="sync")
        {userRow=lines.Count;lines.Add("");lines.Add("");passwordRow=lines.Count;lines.Add("");lines.Add("");}
        else
        {
            var wrapped=Changes.SelectMany(line=>EnbRetailDialogText.Wrap(line,contentWidth,c=>font.CharacterAdvance(c)*fx)).ToArray();
            if(wrapped.Length==0)wrapped=["No game accounts were found on this website login."];
            int visible=Math.Max(1,(int)(290/lineHeight));offset=Math.Clamp(offset,0,Math.Max(0,wrapped.Length-visible));
            previewStart=lines.Count;lines.AddRange(wrapped.Skip(offset).Take(visible));previewEnd=lines.Count;
            if(wrapped.Length>visible)lines.Add($"Scroll to review ({offset+1}-{Math.Min(offset+visible,wrapped.Length)} / {wrapped.Length})");
            lines.Add("");
        }
        lines.AddRange(EnbRetailDialogText.Wrap(Busy?(Page=="sync"?"Importing accounts...":"Saving accounts..."):Status,contentWidth,c=>font.CharacterAdvance(c)*fx));
        EnbDialogBorder Border(string name){var d=resources.DialogFrame(name).Definition;return new((float)d.BorderX,(float)d.BorderY);}
        string action=Page=="sync"?"Import":"Apply";
        float ox=optionFont.Scale*640/(float)bounds.Width,oy=optionFont.Scale*480/(float)bounds.Height;
        var layout=EnbRetailDialogLayout.Measure(lines.Select(_=>contentWidth).ToArray(),lineHeight,
            new[]{optionFont.MeasureWidth("Cancel")*ox,optionFont.MeasureWidth(action)*ox},optionFont.CellHeight*oy,
            Border("DIALOG_BOX_WINDOW"),Border("DIALOG_BOX_INFO"),Border("DIALOG_BOX_OPTION"));
        var placed=EnbRetailDialogLayout.Place(layout.Window.Width,layout.Window.Height,320,240);
        EnbUiRect Place(EnbDialogRect r)=>new(placed.X+r.X,placed.Y+r.Y,r.Width,r.Height);
        var box=Screen(Place(layout.Window));
        for(int i=0;i<draws.Count;i++)
            if(draws[i].Key.StartsWith("ui/BUTTON")||draws[i].Key.StartsWith("accounts/")||draws[i].Key.StartsWith("server/")||draws[i].Key=="ui/EXIT")
                draws[i]=draws[i] with {Disabled=true,Highlight=false,Pressed=false};
        foreach(var pair in owner.Text.Labels)
        {
            if(!pair.Value.IsVisible)continue;
            if(new EnbUiRect(pair.Value.X,pair.Value.Y,pair.Value.Width,pair.Value.Height).Intersects(box))pair.Value.IsVisible=false;
            else pair.Value.Opacity=.35;
        }
        hitCount=0;
        Frame("panel","DIALOG_BOX_WINDOW",Place(layout.Window));Frame("info","DIALOG_BOX_INFO",Place(layout.Info));
        for(int i=0;i<lines.Count;i++)
        {
            var rect=Place(layout.Lines[i]);
            owner.Text.PlaceCentered("accounts/import-line"+i,lines[i],rect,fontId,resources.Color(body.Properties["Color"]),bounds.Size);
            var label=owner.Text.Get("accounts/import-line"+i);label.Opacity=1;
            if(i>=previewStart&&i<previewEnd)label.X=Screen(rect).X;
        }
        if(Page=="sync")
        {
            Field("site-user","Net-7.org username:",owner.SiteUsername,Place(layout.Lines[userRow]));
            Field("site-password","Net-7.org password:",owner.SitePassword,Place(layout.Lines[passwordRow]));
            if(importFocus&&!Busy){importFocus=false;owner.SiteUsername.Focus();}
        }
        Button("close","Cancel",Place(layout.Options[0]),Place(layout.Labels[0]),true);
        Button(Page=="sync"?"read":"apply",action,Place(layout.Options[1]),Place(layout.Labels[1]),!Busy);
        void Button(string key,string value,EnbUiRect rect,EnbUiRect labelRect,bool enabled)
        {
            Frame(key,"DIALOG_BOX_OPTION",rect,enabled);
            owner.Text.PlaceCentered("accounts/"+key,value,labelRect,optionFontId,resources.Color(option.Properties["Color"]),bounds.Size);
            owner.Text.Get("accounts/"+key).Opacity=enabled?1:.35;
            if(enabled)hits[hitCount++]=(key,Screen(rect));
        }
        void Field(string key,string caption,EnbUiTextInput input,EnbUiRect row)
        {
            var labelRect=row with {Width=126};var rect=new EnbUiRect(row.X+130,row.Y-2,row.Width-130,row.Height+4);
            owner.Text.PlaceCentered("accounts/"+key+"-caption",caption,labelRect,fontId,new(1,1,1),bounds.Size);
            owner.Text.Get("accounts/"+key+"-caption").X=Screen(labelRect).X;
            Frame(key,"DIALOG_BOX_OPTION",rect,!Busy);
            input.IsVisible=true;input.IsEnabled=!Busy;input.Bounds=Screen(rect);
            string value=input.PasswordChar==0?input.Text:new string(input.PasswordChar,input.Text.Length);
            owner.Text.PlaceCentered("accounts/"+key,value,rect,fontId,new(1,1,1),bounds.Size);
            var label=owner.Text.Get("accounts/"+key);label.Opacity=Busy?.35:1;
            var screen=Screen(rect);label.Clip=new(screen.X+3-label.X,screen.Y-label.Y,screen.Width-6,screen.Height);
            if(!Busy)hits[hitCount++]=(key,screen);
            if(!input.IsFocused||Busy)return;
            int index=Math.Clamp(input.CaretIndex,0,value.Length);
            var c=EnbRetailCaretGeometry.Resolve(new((float)rect.Center.X-320,240-(float)rect.Center.Y),
                font.MeasureWidth(value)*fx,font.MeasureWidth(value[..index])*fx,
                font.CharacterAdvance(index<value.Length?value[index]:'n')*fx,lineHeight,true,true,input.Editor.InsertMode);
            caret.Bounds=Screen(new(c.X+320,240-c.Y,c.Z,c.W));caret.IsVisible=true;
        }
        void Frame(string key,string name,EnbUiRect rect,bool enabled=false)
        {
            var asset=resources.DialogFrame(name);var geometry=asset.Geometry((float)rect.Width,(float)rect.Height);
            var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
            var world=Matrix4x4.CreateTranslation(-min.X,-max.Y,0)*Matrix4x4.CreateTranslation((float)rect.X-320,240-(float)rect.Y,200);
            draws.Add(new("accounts/"+key,asset.Resource,asset.Resource.Meshes.Single(),world,geometry,camera,0)
                {Highlight=enabled&&Screen(rect).Contains(pointer),Disabled=Busy&&key is not ("panel" or "info" or "close")});
        }
    }
}
