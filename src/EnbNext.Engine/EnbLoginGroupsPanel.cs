using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Client.Models;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed class EnbLoginGroupsPanel(EnbFrontendUi owner)
{
    public SavedLoginGroup[] Groups { get; set; } = [];
    public HashSet<Guid> Eligible { get; set; } = [];
    public SavedLoginGroup? Selected { get; private set; }
    public bool GroupMode { get; private set; }
    public bool Editing { get; private set; }
    public bool Busy { get; set; }
    public string Status { get; set; } = "Select 2 to 6 characters, one per account.";
    public Action? RefreshRequested;
    public Action<SavedLoginGroup>? SaveRequested;
    public Action<SavedLoginGroup>? DeleteRequested;
    public void Deleted(Guid id){if(Selected?.Id==id)Selected=null;expanded=true;}
    private Task<bool>? clearConfirmation;
    private bool expanded;
    private int listOffset,listSelection,cardPage;
    private SavedLoginGroup[] ServerGroups()=>Groups.Where(g=>string.Equals(g.Server,owner.SelectedServer,StringComparison.OrdinalIgnoreCase)).OrderBy(g=>g.Name,StringComparer.OrdinalIgnoreCase).ToArray();
    private Guid draftId;
    private readonly List<SavedGroupMember> members = [];
    private readonly List<(string Key, EnbUiRect Rect)> hits = [];
    private readonly List<(string Name,double Width,double Height,EnbW3dDeformedGeometry Geometry)> frames=[];
    public EnbUiRect? FindHit(string key) => hits.Where(h=>h.Key==key).Select(h=>(EnbUiRect?)h.Rect).FirstOrDefault();
    public (string Text,EnbUiRect Bounds,int Id)? TooltipAt(EnbUiPoint point)
    {
        for(int i=0;i<hits.Count;i++)
            if(hits[i].Rect.Contains(point))
            {
                if(hits[i].Key.StartsWith("edit/"))return ("Edit this group.",hits[i].Rect,1000+i);
                if(hits[i].Key.StartsWith("delete/"))return ("Delete this group.",hits[i].Rect,1000+i);
            }
        return null;
    }
    public void RestoreSelection(bool groupMode,SavedLoginGroup? group){GroupMode=groupMode;Selected=group;expanded=false;}
    public void Select(SavedLoginGroup group) { Selected=group; GroupMode=true; expanded=false; }
    public void Saved(SavedLoginGroup group) { Select(group); Editing=false; owner.GroupName.IsVisible=false; }
    public void Edit(bool fresh=false)
    {
        owner.Accounts.Close(); expanded=false; Editing=true; Busy=false;
        draftId=!fresh && Selected is {} selected?selected.Id:Guid.NewGuid();
        owner.GroupName.Text=!fresh?Selected?.Name??"":"";members.Clear();
        if(!fresh && Selected is {} group)members.AddRange(group.Members);
        Status="Select 2 to 6 characters, one per account.";
        RefreshRequested?.Invoke();
    }
    public bool Key(EnbUiInputEvent key)
    {
        if(key.Code==0x1b && (Editing||expanded)){Editing=false;expanded=false;owner.GroupName.IsVisible=false;return true;}
        if(expanded)
        {
            var list=ServerGroups();
            if(key.Code is 0x26 or 0x28){listSelection=Math.Clamp(listSelection+(key.Code==0x26?-1:1),0,Math.Max(0,list.Length-1));listOffset=Math.Clamp(listSelection<listOffset?listSelection:listSelection>=listOffset+9?listSelection-8:listOffset,0,Math.Max(0,list.Length-9));return true;}
            if(key.Code==0x0d){if(list.Length>0)Select(list[listSelection]);return true;}
        }
        return Editing && key.Code==0x0d;
    }
    public bool Click(EnbUiPoint point)
    {
        string? key=hits.FirstOrDefault(h=>h.Rect.Contains(point)).Key;
        if(Busy)return Editing || key is not null;
        if(key=="account"){GroupMode=false;expanded=false;owner.Accounts.Open();return true;}
        if(key=="group"){owner.Accounts.Close();GroupMode=true;expanded=true;RefreshRequested?.Invoke();return true;}
        if(key=="open"){expanded=!expanded;return true;}
        if(key=="list-prev"){listOffset=Math.Max(0,listOffset-9);return true;}
        if(key=="list-next"){listOffset+=9;return true;}
        if(key=="cards-prev"){cardPage=Math.Max(0,cardPage-1);return true;}
        if(key=="cards-next"){cardPage++;return true;}
        if(key=="manage"){Edit(true);return true;}
        if(key?.StartsWith("edit/")==true){Select(Groups.Single(g=>g.Id==Guid.Parse(key[5..])));Edit();return true;}
        if(key?.StartsWith("delete/")==true){DeleteRequested?.Invoke(Groups.Single(g=>g.Id==Guid.Parse(key[7..])));return true;}
        if(key=="new")
        {
            if(members.Count==0 && owner.GroupName.Text.Length==0)Edit(true);
            else clearConfirmation=owner.Dialogs.ConfirmAsync("Clear the group name and selected characters? Saved groups will not be removed.");
            return true;
        }
        if(key=="cancel"){Editing=false;owner.GroupName.IsVisible=false;return true;}
        if(key=="name"){owner.GroupName.Focus();return true;}
        if(key=="save")
        {
            var group=new SavedLoginGroup(draftId,owner.GroupName.Text.Trim(),owner.SelectedServer,members.ToArray());
            Status=LoginGroupRules.Validate(group)??"Saving group...";
            if(LoginGroupRules.Validate(group) is null)SaveRequested?.Invoke(group);
            return true;
        }
        if(key?.StartsWith("select/")==true){Select(Groups.Single(g=>g.Id==Guid.Parse(key[7..])));return true;}
        if(key?.StartsWith("char/")==true)
        {
            var parts=key.Split('/');var account=owner.Accounts.Accounts.Single(a=>a.Id==Guid.Parse(parts[1]));
            var character=account.Characters[int.Parse(parts[2])];
            int index=members.FindIndex(m=>m.AccountId==account.Id);
            if(index>=0 && members[index].Character==character.Name)members.RemoveAt(index);
            else if(index>=0)members[index]=members[index] with {Character=character.Name};
            else if(members.Count<6)members.Add(new(Guid.NewGuid(),account.Id,character.Name));
            return true;
        }
        if(key?.StartsWith("up/")==true || key?.StartsWith("down/")==true)
        {
            int i=int.Parse(key[(key.IndexOf('/')+1)..]);int j=i+(key.StartsWith("up/")?-1:1);
            if(j>=0&&j<members.Count)(members[i],members[j])=(members[j],members[i]);return true;
        }
        if(expanded && key is null){expanded=false;return true;}
        return Editing || key is not null;
    }
    public void Render(ImmutableArray<EnbFrontendDraw>.Builder draws,EnbResolvedRenderCamera camera,
        EnbUiRect bounds,EnbUiPoint pointer,EnbUiRect? nameBounds,bool available,bool disabled,EnbFrontendVisual caret)
    {
        hits.Clear();
        if(clearConfirmation is {IsCompletedSuccessfully:true} confirmation)
        {
            clearConfirmation=null;
            if(confirmation.Result)Edit(true);
        }
        owner.GroupName.IsVisible=available && nameBounds is not null && Editing;
        if(!available||nameBounds is not {} name)return;
        if(Selected is {} selected && !string.Equals(selected.Server,owner.SelectedServer,StringComparison.OrdinalIgnoreCase))Selected=null;
        EnbUiRect Screen(EnbUiRect r)=>new(r.X*bounds.Width/640,r.Y*bounds.Height/480,r.Width*bounds.Width/640,r.Height*bounds.Height/480);
        var field=new EnbUiRect(name.X*640/bounds.Width,name.Y*480/bounds.Height,name.Width*640/bounds.Width,name.Height*480/bounds.Height);
        var sourceField=draws.FirstOrDefault(d=>d.Key=="ui/BUTTON_NAME");
        bool dropdown=GroupMode?expanded:owner.Accounts.IsOpen&&owner.Accounts.Page=="accounts";
        if(dropdown)
        {
            for(int i=draws.Count-1;i>=0;i--)if(draws[i].Key=="ui/BUTTON_NAME")draws.RemoveAt(i);
            owner.Text.Get("Name_fe01a").IsVisible=false;
            double width=field.Width-EnbSavedAccountsPanel.ArrowWidth;
            Button("account","Account",new(field.X,field.Y,width/2-1,field.Height),!GroupMode);
            Button("group","Group",new(field.X+width/2+1,field.Y,width/2-1,field.Height),GroupMode);
        }
        if(GroupMode)
        {
            owner.Username.IsVisible=owner.Password.IsVisible=false;caret.IsVisible=false;
            for(int i=draws.Count-1;i>=0;i--)if(draws[i].Key is "ui/BUTTON_NAME" or "ui/BUTTON_PASSWORD" || draws[i].Key.StartsWith("accounts/")&&draws[i].Key!="accounts/open")draws.RemoveAt(i);
            foreach(var pair in owner.Text.Labels)if(pair.Key is "Name_fe01a" or "Password_fe01a" || pair.Key.StartsWith("accounts/") || pair.Key is "N_fe01a" or "P_fe01a")pair.Value.IsVisible=false;
            if(!expanded)GroupRow(Selected,field);
            if(!disabled&&!Busy)hits.Add(("open",Screen(new(field.Right-EnbSavedAccountsPanel.ArrowWidth,field.Y,EnbSavedAccountsPanel.ArrowWidth,field.Height))));
            for(int i=0;i<draws.Count;i++)if(draws[i].Key=="accounts/open")draws[i]=draws[i] with {Disabled=disabled||Busy,Highlight=expanded};
            if(!expanded && Selected is {} selectedGroup)
                for(int i=0;i<selectedGroup.Members.Length;i++)
                {
                    var member=selectedGroup.Members[i];var account=owner.Accounts.Accounts.FirstOrDefault(a=>a.Id==member.AccountId);
                    Label("member-summary/"+i,(account?.Login??"Missing account")+": "+member.Character,new(field.X,field.Y+32+i*12,field.Width,12));
                    owner.Text.Get("groups/member-summary/"+i).X=Screen(field).X;
                }
            if(expanded)
            {
                int row=1;var list=ServerGroups();listOffset=Math.Clamp(listOffset,0,Math.Max(0,list.Length-9));
                foreach(var group in list.Skip(listOffset).Take(9))GroupRow(group,field with {Y=field.Y+(row++)*field.Height});
                Button("manage","Create group",new(field.X,field.Y+row*field.Height,field.Width-EnbSavedAccountsPanel.ArrowWidth,field.Height));
                if(list.Length>9){Button("list-prev","Previous",new(field.X,field.Y+(row+1)*field.Height,field.Width/2,14),false,listOffset>0);Button("list-next","Next",new(field.X+field.Width/2,field.Y+(row+1)*field.Height,field.Width/2,14),false,listOffset+9<list.Length);}
            }
        }
        void GroupRow(SavedLoginGroup? group,EnbUiRect row)
        {
            double width=row.Width-EnbSavedAccountsPanel.ArrowWidth;
            string key=expanded&&group is not null?"select/"+group.Id:"open";
            Button(key,group?.Name??"Choose group",row);
            if(group is not null)
            {
                var editRect=new EnbUiRect(row.X+width-26,row.Center.Y-5,10,10);
                var edit=owner.Resources.Model("tsi14a");
                foreach(string part in new[]{"OPTIONS_BUT","ICON06"})
                {
                    var meshPart=edit.Resource.Meshes.Single(m=>m.Name==part);var lo=meshPart.BoundingBoxMin;var hi=meshPart.BoundingBoxMax;
                    var transform=Matrix4x4.CreateTranslation(-lo.X,-hi.Y,0)*Matrix4x4.CreateScale((float)editRect.Width/(hi.X-lo.X),(float)editRect.Height/(hi.Y-lo.Y),1)*Matrix4x4.CreateTranslation((float)editRect.X-320,240-(float)editRect.Y,220);
                    draws.Add(new("groups/edit/"+group.Id+"/"+part,edit.Resource,meshPart,transform,null,camera,0){Highlight=!disabled&&!Busy&&Screen(editRect).Contains(pointer),Disabled=disabled||Busy});
                }
                if(!disabled&&!Busy)hits.Add(("edit/"+group.Id,Screen(editRect)));
                var rect=new EnbUiRect(row.X+width-12,row.Center.Y-5,10,10);
                var model=owner.Resources.Model("trade00a");var mesh=model.Resource.Meshes.Single(m=>m.Name=="EXIT");var min=mesh.BoundingBoxMin;var max=mesh.BoundingBoxMax;
                var world=Matrix4x4.CreateTranslation(-min.X,-max.Y,0)*Matrix4x4.CreateScale((float)rect.Width/(max.X-min.X),(float)rect.Height/(max.Y-min.Y),1)*Matrix4x4.CreateTranslation((float)rect.X-320,240-(float)rect.Y,220);
                draws.Add(new("groups/delete/"+group.Id,model.Resource,mesh,world,null,camera,0){Highlight=!disabled&&!Busy&&Screen(rect).Contains(pointer),Disabled=disabled||Busy});
                if(!disabled&&!Busy)hits.Add(("delete/"+group.Id,Screen(rect)));
            }
        }
        if(!Editing)return;
        foreach(var pair in owner.Text.Labels)pair.Value.IsVisible=false;
        for(int i=0;i<draws.Count;i++)if(draws[i].Key.StartsWith("ui/")||draws[i].Key.StartsWith("groups/")||draws[i].Key.StartsWith("server/")||draws[i].Key.StartsWith("accounts/"))draws[i]=draws[i] with {Disabled=true,Highlight=false};
        hits.Clear();owner.Username.IsEnabled=owner.Password.IsEnabled=false;caret.IsVisible=false;
        Frame("panel","DIALOG_BOX_WINDOW",new(70,35,500,405));
        Label("title","Compose group",new(80,40,480,16));
        Label("name-caption","Group name:",new(80,59,76,16));
        var nameRect=new EnbUiRect(160,59,240,16);Button("name",owner.GroupName.Text,nameRect);
        owner.GroupName.IsVisible=true;owner.GroupName.IsEnabled=!Busy&&!disabled;owner.GroupName.Bounds=Screen(nameRect);
        if(owner.GroupName.IsFocused && owner.GroupName.IsEnabled && owner.Text.Font("FONT_SMALL",bounds.Size) is {} nameFont)
        {
            string value=owner.GroupName.Text;int index=Math.Clamp(owner.GroupName.CaretIndex,0,value.Length);
            float fx=nameFont.Scale*640/(float)bounds.Width,fy=nameFont.Scale*480/(float)bounds.Height;
            var c=EnbRetailCaretGeometry.Resolve(new((float)nameRect.Center.X-320,240-(float)nameRect.Center.Y),nameFont.MeasureWidth(value)*fx,nameFont.MeasureWidth(value[..index])*fx,nameFont.CharacterAdvance(index<value.Length?value[index]:'n')*fx,nameFont.CellHeight*fy,true,true,owner.GroupName.Editor.InsertMode);
            caret.Bounds=Screen(new(c.X+320,240-c.Y,c.Z,c.W));caret.IsVisible=true;
        }
        Button("new","Clear form",new(430,59,130,16));
        var accounts=owner.Accounts.Accounts.Where(a=>string.Equals(a.Server,owner.SelectedServer,StringComparison.OrdinalIgnoreCase)).ToArray();
        cardPage=Math.Clamp(cardPage,0,Math.Max(0,(accounts.Length-1)/9));
        for(int i=0;i<Math.Min(9,accounts.Length-cardPage*9);i++)
        {
            var a=accounts[cardPage*9+i];double x=80+i%3*106,y=82+i/3*101;
            Frame("card/"+a.Id,"DIALOG_BOX_INFO",new(x,y,102,97));
            Label("account/"+a.Id,a.Login+(Eligible.Contains(a.Id)?"":" (save password)"),new(x+3,y+3,96,12));
            bool eligible=Eligible.Contains(a.Id);bool chosen=members.Any(m=>m.AccountId==a.Id);
            for(int c=0;c<a.Characters.Length;c++)
            {
                var character=a.Characters[c];bool active=members.Any(m=>m.AccountId==a.Id&&m.Character==character.Name);
                Button("char/"+a.Id+"/"+c,character.Name,new(x+4,y+18+c*14,94,13),active,eligible&&(chosen||members.Count<6));
            }

        }
        Label("order-title","Launch order",new(405,84,150,15));
        for(int i=0;i<members.Count;i++)
        {
            Label("member/"+i,$"{i+1}. {members[i].Character}",new(405,105+i*29,100,20));
            Button("up/"+i,"Up",new(507,105+i*29,23,20),false,i>0);
            Button("down/"+i,"Down",new(532,105+i*29,28,20),false,i<members.Count-1);
        }
        if(accounts.Length>9){Button("cards-prev","Previous accounts",new(405,320,150,18),false,cardPage>0);Button("cards-next","Next accounts",new(405,342,150,18),false,(cardPage+1)*9<accounts.Length);}
        Label("status",Status,new(80,389,480,18));
        Button("cancel","Cancel",new(80,411,230,19));
        Button("save","Save",new(320,411,240,19),false,members.Count>=2&&owner.GroupName.Text.Trim().Length>0&&members.All(m=>Eligible.Contains(m.AccountId)));
        void Label(string key,string value,EnbUiRect rect)
        {owner.Text.PlaceCentered("groups/"+key,value,rect,"FONT_SMALL",new(1,1,1),bounds.Size);}
        void Button(string key,string value,EnbUiRect rect,bool selected=false,bool enabled=true)
        {
            enabled&=!disabled&&!Busy;bool hover=enabled&&Screen(rect).Contains(pointer);
            if(!Editing && GroupMode && sourceField is not null && (key=="open"||key.StartsWith("select/")))
            {
                draws.Add(sourceField with {Key="groups/"+key,Transform=sourceField.Transform*Matrix4x4.CreateTranslation(0,-(float)(rect.Y-field.Y),0),Highlight=selected||hover,Pressed=false,Disabled=!enabled});
                Label(key,value,rect with {Width=rect.Width-EnbSavedAccountsPanel.ArrowWidth-30});
                var text=owner.Text.Get("groups/"+key);var clip=Screen(rect with {Width=rect.Width-EnbSavedAccountsPanel.ArrowWidth-30});
                text.Clip=new(clip.X-text.X,clip.Y-text.Y,clip.Width,clip.Height);
            }
            else {Frame(key,"DIALOG_BOX_OPTION",rect,selected||hover,!enabled);Label(key,value,rect);}
            var label=owner.Text.Get("groups/"+key);label.Opacity=enabled?1:.35;
            if(hover)label.SetText(value,label.Font,new(0,1,0));
            if(enabled)hits.Add((key,Screen((key.StartsWith("select/")||key=="open")?rect with {Width=rect.Width-EnbSavedAccountsPanel.ArrowWidth-30}:rect)));
        }
        void Frame(string key,string frame,EnbUiRect rect,bool highlight=false,bool inactive=false)
        {
            var asset=owner.Resources.DialogFrame(frame);
            var geometry=frames.FirstOrDefault(f=>f.Name==frame&&f.Width==rect.Width&&f.Height==rect.Height).Geometry;
            if(geometry is null){geometry=asset.Geometry((float)rect.Width,(float)rect.Height);frames.Add((frame,rect.Width,rect.Height,geometry));}
            var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
            var world=Matrix4x4.CreateTranslation(-min.X,-max.Y,0)*Matrix4x4.CreateTranslation((float)rect.X-320,240-(float)rect.Y,210);
            draws.Add(new("groups/"+key,asset.Resource,asset.Resource.Meshes.Single(),world,geometry,camera,0){Highlight=highlight,Disabled=inactive});
        }
    }
}
