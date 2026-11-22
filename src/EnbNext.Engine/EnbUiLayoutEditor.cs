using EnbNext.Presentation;
namespace EnbNext.Engine;

internal sealed partial class EnbUiLayoutEditor(EnbWorldUi ui)
{
    private readonly List<KeyValuePair<EnbUiGroup,EnbUiRect>> bounds=[];
    private EnbUiPoint grab;
    private EnbUiPlacement original;
    internal EnbUiGroup Selected {get;private set;}
    internal bool Dragging {get;private set;}
    internal bool Dropdown {get;private set;}
    internal bool Managing {get;private set;}
    internal bool ConfirmDelete {get;private set;}
    // The fixed tooltip area (Quality of Life option) is an element only while that option is on.
    internal ReadOnlySpan<EnbUiGroup> Elements{get{bool tip=ui.FixedTooltip.Enabled;return Layout.Docked?(ui.ModernUi?(tip?DockedModernTip:DockedModern):tip?DockedTip:DockedSpace):ui.ModernUi?(tip?ModernTip:ModernSpace):(tip?RetailTip:RetailSpace);}}
    private static readonly EnbUiGroup[] DockedSpace=[EnbUiGroup.Chat,EnbUiGroup.Group,EnbUiGroup.Friends,EnbUiGroup.Guild,EnbUiGroup.GroupFinder,EnbUiGroup.Target,EnbUiGroup.Cargo,EnbUiGroup.Equipment,EnbUiGroup.Character,EnbUiGroup.Vault,EnbUiGroup.Utility,EnbUiGroup.Options,EnbUiGroup.Map,EnbUiGroup.Multibox,EnbUiGroup.DamageMeter],RetailSpace=[EnbUiGroup.Cockpit,EnbUiGroup.Buffs,EnbUiGroup.Scanner,EnbUiGroup.Target,EnbUiGroup.Chat,EnbUiGroup.Group,EnbUiGroup.Friends,EnbUiGroup.Guild,EnbUiGroup.GroupFinder,EnbUiGroup.Utility,EnbUiGroup.Cargo,EnbUiGroup.Equipment,EnbUiGroup.Character,EnbUiGroup.Loot,EnbUiGroup.Options,EnbUiGroup.Map,EnbUiGroup.Multibox,EnbUiGroup.DamageMeter];
    // Modern UI swaps the retail target panel and target portrait for its player and target frames.
    // Only the fixed elements: the Modern UI windows (inventory, equipment, skills, missions, factions, commands, friends,
    // guild, group finder, options, maps, My characters, damage meter) are dragged where they stand and keep their place.
    private static readonly EnbUiGroup[] ModernSpace=[EnbUiGroup.ActionBar,EnbUiGroup.TargetButtons,EnbUiGroup.Engine,EnbUiGroup.CombatXp,EnbUiGroup.ExploreXp,EnbUiGroup.TradeXp,EnbUiGroup.PlayerFrame,EnbUiGroup.TargetFrame,EnbUiGroup.ModernGroup,EnbUiGroup.Utility];
    private static readonly EnbUiGroup[] DockedTip=[..DockedSpace,EnbUiGroup.Tooltip],RetailTip=[..RetailSpace,EnbUiGroup.Tooltip],ModernTip=[..ModernSpace,EnbUiGroup.Tooltip];
    // Docked with Modern UI: what its station HUD shows (EnbModernHud.Station: micro menu, experience and group) and its
    // none of the retail station panels and none of the freely dragged windows (the chat windows among them).
    private static readonly EnbUiGroup[] DockedModern=[EnbUiGroup.ModernGroup,EnbUiGroup.CombatXp,EnbUiGroup.ExploreXp,EnbUiGroup.TradeXp,EnbUiGroup.Utility],DockedModernTip=[..DockedModern,EnbUiGroup.Tooltip];
    /// <summary>Whether the current element list holds a group: only those get a layout preview.</summary>
    internal bool Lists(EnbUiGroup group){foreach(var listed in Elements)if(listed==group)return true;return false;}
    internal int ProfileScroll {get;private set;}
    internal string Name {get;private set;}="";
    internal int Naming {get;private set;}
    internal string Message {get;private set;}="";
    internal EnbUiLayoutProfiles Layout=>ui.Layout;
    internal IReadOnlyList<KeyValuePair<EnbUiGroup,EnbUiRect>> Groups=>bounds;
    internal EnbUiRect Entry=>ui.Options.LayoutEntry;
    internal EnbUiPoint Pointer {get;private set;}
    internal bool Pressed {get;private set;}
    internal double Unit {get;set;}=1;
    internal EnbUiRect Toolbar=>ToolRect(0);
    internal EnbUiRect Manager=>new(Math.Max(0,(Layout.Size.Width-700*Unit)/2),Math.Max(0,(Layout.Size.Height-280*Unit)/2),700*Unit,280*Unit);
    internal EnbUiRect Action(int id)
    {
        var p=Toolbar;var m=Manager;double u=Unit,w=(m.Width-84*u)/4;
        return id switch {
            0=>Managing?new(m.X+24*u,m.Y+50*u,m.Width-48*u,34*u):new(p.X+24*u,p.Y+50*u,p.Width-356*u,34*u),
            1=>new(m.X+24*u,m.Y+104*u,w,34*u),2=>new(m.X+36*u+w,m.Y+104*u,w,34*u),3=>new(m.X+48*u+2*w,m.Y+104*u,w,34*u),
            11=>new(m.X+60*u+3*w,m.Y+104*u,w,34*u),12=>new(m.Right-108*u,m.Bottom-58*u,84*u,34*u),
            4=>new(p.Right-196*u,p.Y+50*u,84*u,34*u),5=>new(p.Right-100*u,p.Y+50*u,76*u,34*u),
            10=>new(p.Right-320*u,p.Y+50*u,112*u,34*u),
            8=>new(m.X+24*u,m.Y+150*u,m.Width-144*u,34*u),9=>new(m.Right-108*u,m.Y+150*u,84*u,34*u),
            13=>new(p.X+24*u,p.Y+100*u,124*u,34*u),14=>new(p.X+160*u,p.Y+100*u,124*u,34*u),
            15=>new(GridWindow.X+24*u,GridWindow.Y+54*u,GridWindow.Width-48*u,34*u),
            16=>new(GridWindow.X+24*u,GridWindow.Y+100*u,GridWindow.Width-48*u,34*u),
            17=>new(GridWindow.X+24*u,GridWindow.Y+152*u,34*u,34*u),18=>new(GridWindow.Right-58*u,GridWindow.Y+152*u,34*u,34*u),
            19=>new(PanelsWindow.Right-48*u,PanelsWindow.Y+12*u,24*u,24*u),20=>new(GridWindow.Right-48*u,GridWindow.Y+12*u,24*u,24*u),
            _=>default};
    }
    internal EnbUiRect ProfileRow(int row){var p=Action(0);double h=34*Unit;return new(p.X,Math.Clamp(p.Bottom,0,Math.Max(0,Layout.Size.Height-ProfileRows*h))+row*h,p.Width,h);}
    /// <summary>Three columns when the element list is longer than the retail lists (Modern UI's separately placed parts).</summary>
    internal int ElementColumns=>ui.ModernUi&&!Layout.Docked?3:2;
    internal EnbUiRect ElementRow(int row)
    {
        if(ElementColumns==3){var q=PanelsWindow;double v=Unit,cw=(q.Width-72*v)/3;return new(q.X+24*v+(row%3)*(cw+12*v),q.Y+54*v+(row/3)*44*v,cw,34*v);}
        var p=PanelsWindow;double u=Unit,w=(p.Width-60*u)/2;int gap=Layout.Docked?(row>=6?1:0)+(row>=10?1:0):(row>=4?1:0)+(row>=10?1:0)+(row>=14?1:0);return new(p.X+24*u+(row%2)*(w+12*u),p.Y+54*u+(row/2)*44*u+gap*12*u,w,34*u);}
    /// <summary>Next panel layout options: each panel's choices that change how it lays itself out, kept in the layout
    /// file for every profile (Default too). A panel with options has an Options button on its scale bar that lists them under the bar.</summary>
    internal static readonly (EnbUiGroup Group,string Key,string Label,string Tip)[] PanelOptions=
    [
        (EnbUiGroup.ModernGroup,"ModernGroup/NameBeside","Name beside the bars","Show each member's name in the row with the bars, left of them, instead of on its own line above them."),
        (EnbUiGroup.ActionBar,EnbModernHud.BarOneRowOption,"One row of twelve","All twelve slots side by side.\nWith no choice, screens narrower than 16:9 get two rows."),
        (EnbUiGroup.ActionBar,EnbModernHud.BarTwoRowsOption,"Two rows of six","Slots 1-6 above A1-A6: half as wide, one row higher.\nWith no choice, screens narrower than 16:9 get two rows."),
        (EnbUiGroup.Utility,EnbModernHud.MicroLinesOption+"1","One line","The buttons in one line.\nWith no choice: one line, two on screens narrower than 16:9."),
        (EnbUiGroup.Utility,EnbModernHud.MicroLinesOption+"2","Two lines","The buttons in two lines: half as long."),
        (EnbUiGroup.Utility,EnbModernHud.MicroLinesOption+"3","Three lines","The buttons in three lines."),
        (EnbUiGroup.Utility,EnbModernHud.MicroLinesOption+"4","Four lines","The buttons in four lines."),
        (EnbUiGroup.TargetFrame,EnbModernHud.VerbSizeOption+"125","Buttons 125%","The target's action buttons (harvest, trade, dock...) a quarter bigger.\nWith no choice: normal size."),
        (EnbUiGroup.TargetFrame,EnbModernHud.VerbSizeOption+"150","Buttons 150%","The target's action buttons half again as big.\nWith no choice: normal size."),
        (EnbUiGroup.TargetFrame,EnbModernHud.VerbSizeOption+"175","Buttons 175%","The target's action buttons three quarters bigger.\nWith no choice: normal size."),
        (EnbUiGroup.TargetFrame,EnbModernHud.VerbSizeOption+"200","Buttons 200%","The target's action buttons twice as big.\nWith no choice: normal size."),
        (EnbUiGroup.Utility,EnbModernHud.MicroVerticalOption,"Upright","The lines stand upright: columns instead of rows."),
    ];
    internal static bool HasOptions(EnbUiGroup group){foreach(var o in PanelOptions)if(o.Group==group)return true;return false;}
    /// <summary>The panel whose options are listed under its scale bar, if any.</summary>
    internal EnbUiGroup OptionsOpen {get;private set;}
    private double OptionsWidth(EnbUiGroup group)=>HasOptions(group)?84*Unit:0;
    internal EnbUiRect OptionsAction(EnbUiGroup group){var r=ScaleBar(group);return new(r.Right-80*Unit,r.Y,80*Unit,r.Height);}
    /// <summary>The open panel's option rows, under its scale bar: (key, label, tip, row).</summary>
    internal IEnumerable<(string Key,string Label,string Tip,EnbUiRect Row)> OptionRows()
    {
        if(OptionsOpen==EnbUiGroup.None)yield break;
        var bar=ScaleBar(OptionsOpen);double u=Unit;int i=0,count=0;foreach(var o in PanelOptions)if(o.Group==OptionsOpen)count++;
        // Under the bar, or above it when the panel sits too low (the micro menu in its corner); kept on screen.
        double width=Math.Max(bar.Width,260*u),x=Math.Clamp(bar.X,0,Math.Max(0,Layout.Size.Width-width));
        bool above=bar.Bottom+2*u+count*32*u>Layout.Size.Height;double top=above?bar.Y-2*u-count*32*u:bar.Bottom+2*u;
        foreach(var o in PanelOptions)if(o.Group==OptionsOpen)yield return (o.Key,o.Label,o.Tip,new(x,top+i++*32*u,width,30*u));
    }
    internal EnbUiRect ScaleBar(EnbUiGroup group)
    {
        if(!TryBounds(group,out var r))return default;r=Layout.Apply(group,r);double u=Unit,w=164*u+OptionsWidth(group);
        return new(Math.Clamp(r.Center.X-w/2,0,Math.Max(0,Layout.Size.Width-w)),Math.Clamp(r.Top,0,Math.Max(0,Layout.Size.Height-28*u)),w,28*u);
    }
    internal EnbUiRect ScaleAction(EnbUiGroup group,bool plus){var r=ScaleBar(group);double w=28*Unit;return new(plus?r.Right-OptionsWidth(group)-60*Unit:r.X,r.Y,w,w);}
    internal EnbUiRect ResetAction(EnbUiGroup group){var r=ScaleBar(group);return new(r.Right-OptionsWidth(group)-28*Unit,r.Y,28*Unit,28*Unit);}
    internal EnbUiRect DragHandle(EnbUiGroup group){var r=ScaleBar(group);return new(r.X+28*Unit,r.Y,76*Unit,r.Height);}
    internal int ProfileRows=>Math.Min(10,Layout.Document.Profiles.Count+1-ProfileScroll);
    internal string ProfileName(int index)=>index==0?"Default (locked)":Layout.Document.Profiles[index-1].Name;
    internal void Begin(){Layout.Begin();
        // Modern UI has no retail cockpit, buff box, scanner or target portrait: no previews or outlines for them.
        if(ui.ModernUi)foreach(var retail in new[]{EnbUiGroup.Cockpit,EnbUiGroup.Buffs,EnbUiGroup.Scanner,EnbUiGroup.Target})if(Layout.Visible(retail))Layout.Toggle(retail);foreach(var draw in ui.Inventory.Presentation)if(draw.LayoutGroup!=EnbUiGroup.None&&!Layout.Visible(draw.LayoutGroup))Layout.Toggle(draw.LayoutGroup);if(ui.Map.IsOpen&&!Layout.Visible(EnbUiGroup.Map))Layout.Toggle(EnbUiGroup.Map);
        // What edit mode does not list (in Modern UI the freely dragged windows) has no preview, outline or scale bar.
        foreach(var group in Enum.GetValues<EnbUiGroup>())if(group!=EnbUiGroup.None&&Layout.Visible(group)&&!Elements.Contains(group))Layout.Toggle(group);
        Selected=EnbUiGroup.None;Naming=0;Dropdown=false;Managing=false;ConfirmDelete=false;Message=Layout.Error??"";}
    internal void Cancel(){if(ConfirmDelete)AnswerDelete(false);CancelDrag();Layout.Cancel();Naming=0;Dropdown=false;Managing=false;OptionsOpen=EnbUiGroup.None;}
    internal void CancelDrag(){if(ConfirmDelete)ui.Options.RetireLayoutDeleteInput();Dragging=false;draggingTool=-1;Pressed=false;BypassSnap=false;}
    internal void Refresh()
    {
        bounds.Clear();
        if(ui.DamagePanel is {} meter)Add(EnbUiGroup.DamageMeter,meter.Bounds);
        if(ui.FixedTooltip.Enabled)Add(EnbUiGroup.Tooltip,EnbFixedTooltip.DefaultArea(Layout.Size));
        if(ui.MultiboxPanel is {} multibox)Add(EnbUiGroup.Multibox,multibox.Bounds);
        if(ui.ModernHud is {} modern)
        {
            // Docked, the Modern HUD keeps the micro menu, experience and group; in space it also has the frames and bars.
            if(!Layout.Docked)
            {
                Add(EnbUiGroup.PlayerFrame,modern.PlayerBounds);Add(EnbUiGroup.TargetFrame,modern.TargetBounds);Add(EnbUiGroup.ActionBar,modern.ActionBarBounds);
                Add(EnbUiGroup.Engine,modern.EngineLayoutBounds);Add(EnbUiGroup.TargetButtons,modern.TargetButtonsBounds);
            }
            Add(EnbUiGroup.CombatXp,modern.XpBounds(0));Add(EnbUiGroup.ExploreXp,modern.XpBounds(1));Add(EnbUiGroup.TradeXp,modern.XpBounds(2));
            Add(EnbUiGroup.Utility,modern.MicroBounds);Add(EnbUiGroup.ModernGroup,modern.GroupBounds);
        }
        else if(!Layout.Docked)
        {
            Add(EnbUiGroup.Cockpit,ui.Cockpit.LayoutBounds(false));Add(EnbUiGroup.Buffs,ui.Cockpit.LayoutBounds(true));
            if(ui.Scanner.IsVisible)Add(EnbUiGroup.Scanner,ui.Scanner.LayoutBounds);
        }
        foreach(var h in ui.Chat.Hits.Values)if(h.IsVisible&&h.IsHitTestVisible&&h.Group!=EnbUiGroup.ModernChat)Add(h.Group,h.Bounds);
        foreach(var d in ui.Options.Presentation)if(d.LayoutGroup!=EnbUiGroup.None)Add(d.LayoutGroup,d.Bounds);
        if(ui.Map.IsOpen||ui.Map.LayoutPreview)Add(EnbUiGroup.Map,ui.Map.LayoutBounds);
        foreach(var d in ui.Inventory.Presentation)if(d.LayoutGroup!=EnbUiGroup.None)Add(d.LayoutGroup,d.Bounds);
    }
    private void Add(EnbUiGroup group,EnbUiRect r)
    {if(!Layout.Visible(group)||r.Width<=0||r.Height<=0)return;int i=bounds.FindIndex(p=>p.Key==group);if(i<0)bounds.Add(new(group,r));else bounds[i]=new(group,bounds[i].Value.Union(r));}
    internal void Press(EnbUiPoint point)
    {
        Pointer=point;Pressed=true;if(ConfirmDelete){ui.Options.LayoutDeleteInput(point,1,0,true);return;}
        if(Dropdown)
        {
            for(int row=0;row<ProfileRows;row++)if(ProfileRow(row).Contains(point))
            {int i=row+ProfileScroll;Layout.Select(i==0?Guid.Empty:Layout.Document.Profiles[i-1].Id);ui.Options.LayoutClickSound();Dropdown=false;Naming=0;ConfirmDelete=false;Message="";return;}
            if(Action(0).Contains(point)){Dropdown=false;return;}
            Dropdown=false;
        }
        int tool=Managing?3:ToolAt(point);
        if(tool>=0&&tool<3)BringForward(tool);
        for(int id=0;id<=20;id++)if(ActiveAction(id)&&ActionTool(id)==tool&&Action(id).Contains(point)){Activate(id);return;}
        if(Managing)return;
        if(tool==1)for(int row=0;row<Elements.Length;row++)if(ElementRow(row).Contains(point)){Layout.Toggle(Elements[row]);ui.Options.LayoutClickSound();return;}
        if(tool>=0)
        {
            var rect=ToolRect(tool);
            if(point.Y<rect.Y+44*Unit){grab=point;toolGrab=new(rect.X,rect.Y);Dragging=true;draggingTool=tool;}
            return;
        }
        // An open panel's options: a row toggles its option (a profile's, not Default's); a press elsewhere closes them.
        if(OptionsOpen!=EnbUiGroup.None)
        {
            foreach(var (key,_,_,row) in OptionRows())if(row.Contains(point))
            {
                ui.Options.LayoutClickSound();Layout.SetOption(key,!Layout.Option(key));Message="";
                return;
            }
            if(!OptionsAction(OptionsOpen).Contains(point))OptionsOpen=EnbUiGroup.None;
        }
        foreach(var pair in bounds.AsEnumerable().Reverse())if(ScaleBar(pair.Key).Contains(point))
        {
            Selected=pair.Key;
            if(HasOptions(pair.Key)&&OptionsAction(pair.Key).Contains(point)){ui.Options.LayoutClickSound();OptionsOpen=OptionsOpen==pair.Key?EnbUiGroup.None:pair.Key;}
            else if(ScaleAction(pair.Key,false).Contains(point)){ui.Options.LayoutClickSound();Scale(-.1);}
            else if(ScaleAction(pair.Key,true).Contains(point)){ui.Options.LayoutClickSound();Scale(.1);}
            else if(ResetAction(pair.Key).Contains(point)){if(!Layout.Locked){ui.Options.LayoutClickSound();Layout.Set(Selected,EnbUiPlacement.Identity);Message="";}}
            else BeginDrag(point);
            return;
        }
        foreach(var pair in bounds.AsEnumerable().Reverse())if(Layout.Apply(pair.Key,pair.Value).Contains(point))
        {
            Selected=pair.Key;
            BeginDrag(point);return;
        }
        Selected=EnbUiGroup.None;
    }
    private void BeginDrag(EnbUiPoint point)
    {
        if(Layout.Locked){Message="Default is locked. Use Manage to create or duplicate a profile.";return;}
        original=Layout.Placement(Selected);grab=point;Dragging=true;
    }
    private void Activate(int id)
    {
        Message="";if(id!=11)ConfirmDelete=false;ui.Options.LayoutClickSound();
        switch(id)
        {
            case 0:Dropdown=!Dropdown;ProfileScroll=0;break;
            case 1:case 2:case 3:
                if(id==3&&Layout.Locked){Message="Default cannot be renamed.";break;}
                Naming=id;Name=id==3?Layout.Selected!.Name:UniqueName(id==2?(Layout.Selected?.Name??"Default")+" copy":"New profile");break;
            case 4:if(Naming!=0){AcceptName();if(Naming!=0)break;}if(Layout.Save()){Naming=0;CancelDrag();}else Message=Layout.Error??"Save failed.";break;
            case 5:Cancel();break;
            case 10:Managing=true;Naming=0;ConfirmDelete=false;break;
            case 11:if(!Layout.Locked){Naming=0;ConfirmDelete=true;ui.Options.ShowLayoutDelete(Layout.Selected!.Name);}break;
            case 12:if(Naming!=0){AcceptName();if(Naming!=0)break;}Managing=false;Naming=0;ConfirmDelete=false;Dropdown=false;break;
            case 9:AcceptName();break;
            case 13:PanelsOpen=!PanelsOpen;if(PanelsOpen)BringForward(1);break;
            case 14:GridToolsOpen=!GridToolsOpen;if(GridToolsOpen)BringForward(2);break;
            case 15:GridVisible=!GridVisible;break;
            case 16:SnapToGrid=!SnapToGrid;break;
            case 17:GridSpacing=Math.Max(8,GridSpacing/2);break;
            case 18:GridSpacing=Math.Min(64,GridSpacing*2);break;
            case 19:PanelsOpen=false;break;
            case 20:GridToolsOpen=false;break;
        }
    }
    internal bool ActiveAction(int id)=>!ConfirmDelete&&!(Layout.Locked&&id is 3 or 11)&&(Managing?id is 0 or 1 or 2 or 3 or 11 or 12||(Naming!=0&&id is 8 or 9):id is 0 or 4 or 5 or 10 or 13 or 14||(GridToolsOpen&&id is 15 or 16 or 17 or 18 or 20)||(PanelsOpen&&id==19));
    internal void AnswerDelete(bool yes){if(yes&&!Layout.Locked)Layout.Delete();ConfirmDelete=false;ui.Options.CloseLayoutDelete();Message="";}
    internal void Release(EnbUiPoint point){if(ConfirmDelete)ui.Options.LayoutDeleteInput(point,1,0x1000,false);CancelDrag();}
    internal void Escape(){if(ConfirmDelete){AnswerDelete(false);return;}if(Managing){Managing=false;Naming=0;ConfirmDelete=false;Dropdown=false;}else if(Dropdown)Dropdown=false;else Cancel();}
    private string UniqueName(string prefix)
    {string name=prefix;for(int i=2;Layout.Document.Profiles.Any(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase));i++)name=prefix+" "+i;return name;}
    internal void AcceptName()
    {
        try{if(Naming==3)Layout.Rename(Name);else if(Naming!=0)Layout.Create(Name,Naming==2);Naming=0;Message="";}
        catch(ArgumentException e){Message=e.Message;}
    }
    internal void Type(string text){if(Naming!=0){Name+=new string(text.Where(c=>!char.IsControl(c)).ToArray());if(Name.Length>80)Name=Name[..80];}}
    internal void Backspace(){if(Name.Length>0)Name=Name[..(Name.Length>1&&char.IsLowSurrogate(Name[^1])?^2:^1)];}
    internal void ClearName()=>Name="";
    internal void Move(EnbUiPoint point)
    {
        Pointer=point;if(ConfirmDelete){ui.Options.LayoutDeleteInput(point,0,0,Pressed);return;}if(!Dragging)return;
        if(draggingTool>=0){toolPositions[draggingTool]=new(toolGrab.X+point.X-grab.X,toolGrab.Y+point.Y-grab.Y);return;}
        var moved=original with {X=original.X+(point.X-grab.X)/Layout.Size.Width,Y=original.Y+(point.Y-grab.Y)/Layout.Size.Height};
        if(SnapToGrid&&!BypassSnap&&TryBounds(Selected,out var r))moved=SnapPlacement(moved,r,Layout.Size,GridSpacing/DisplayScale);
        Set(moved);
    }
    internal void Wheel(double delta)
    {
        if(ConfirmDelete)return;
        if(Dropdown){ProfileScroll=Math.Clamp(ProfileScroll-(int)Math.Sign(delta),0,Math.Max(0,Layout.Document.Profiles.Count+1-10));return;}
        if(!Managing&&ToolAt(Pointer)<0)foreach(var pair in bounds)if(Layout.Apply(pair.Key,pair.Value).Contains(Pointer)){Selected=pair.Key;Scale(Math.Sign(delta)*.05);break;}
    }
    private void Scale(double delta)
    {
        if(Selected==EnbUiGroup.None||Layout.Locked||!TryBounds(Selected,out var r))return;
        var p=Layout.Placement(Selected);double scale=Math.Clamp(Math.Round(p.Scale+delta,3),.5,3);
        var center=p.Apply(r.Center,Layout.Size);
        Set(new((center.X-r.Center.X*scale)/Layout.Size.Width,(center.Y-r.Center.Y*scale)/Layout.Size.Height,scale));
    }
    private void Set(EnbUiPlacement p)
    {
        if(!TryBounds(Selected,out var r))return;
        double w=Layout.Size.Width,h=Layout.Size.Height;
        double cap=Math.Min(3,Math.Min(w/r.Width,h/r.Height));
        if(p.Scale>cap)Message=$"Scale is limited to {Math.Max(.5,cap):P0} to keep this group on screen.";
        p=p with {Scale=Math.Min(p.Scale,Math.Max(.5,cap))};
        var placed=p.Apply(r,Layout.Size);
        double x=Math.Clamp(placed.X,0,Math.Max(0,w-placed.Width)),y=Math.Clamp(placed.Y,0,Math.Max(0,h-placed.Height));
        Layout.Set(Selected,p with {X=p.X+(x-placed.X)/w,Y=p.Y+(y-placed.Y)/h});
    }
    private bool TryBounds(EnbUiGroup group,out EnbUiRect rect){foreach(var pair in bounds)if(pair.Key==group){rect=pair.Value;return true;}rect=default;return false;}
}
