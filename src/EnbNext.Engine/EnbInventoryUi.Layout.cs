using EnbNext.Presentation;
namespace EnbNext.Engine;
internal sealed partial class EnbInventoryUi
{
    internal EnbUiPoint LayoutInput(string id,EnbUiPoint p)=>_hits.TryGetValue(id,out var h)?UiLayout?.Inverse(h.LayoutGroup,p)??p:p;
    internal EnbUiPoint LayoutOutput(string id,EnbUiPoint p)=>_hits.TryGetValue(id,out var h)?UiLayout?.Placement(h.LayoutGroup).Apply(p,_size)??p:p;
    internal bool LayoutContains(EnbInventoryHit h,EnbUiPoint p)=>h.Bounds.Contains(UiLayout?.Inverse(h.LayoutGroup,p)??p);

    private void LayoutGroupPreview()
    {
        if(!_art.ContainsKey("group/FORMATION_BUTTO"))return;
        Draw("group/FORMATION_BUTTO",Native("group/FORMATION_BUTTO"));
        for(int index=0;_groupAnchors.TryGetValue("group/GROUP"+index.ToString("00"),out var origin);index++)
        {
            var offset=new EnbUiPoint(origin.X-320,origin.Y-240);
            EnbUiRect Row(string mesh)
            {
                var rect=Native("groupRow/"+mesh).Translate(offset);
                double z=_groupDepths["group/GROUP"+index.ToString("00")]+_groupDepths["groupRow/"+mesh];
                double perspective=320/(320-z);
                return new(320+(rect.X-320)*perspective,240+(rect.Y-240)*perspective,rect.Width*perspective,rect.Height*perspective);
            }
            foreach(string mesh in new[]{"GROUP","PLAYER01","MAIN","HULL_BAR1","HULL_BAR0","SHIELD_BAR1","SHIELD_BAR0","P_INTARGET01","P_OUTTARGET01","P_BOOT01"})
                if(_art.ContainsKey("groupRow/"+mesh))Draw("groupRow/"+mesh,Row(mesh));
            GroupLabel("NAME"+index.ToString("00")+"_TSI03A","Group member "+(index+1),false);
        }
        Flush();
    }

    private EnbInventoryUi? _layoutPanelPreview;
    private bool _layoutPreviewOwner, _layoutLootPreview;
    private static readonly EnbSceneGuildSnapshot LayoutGuild=new(new(0,uint.MaxValue,uint.MaxValue,uint.MaxValue),[],[],[],[],null);
    private static readonly EnbSceneGroupSnapshot LayoutGroup=new(false,"",0,[],null,false,System.Collections.Immutable.ImmutableHashSet<uint>.Empty);
    private void LayoutPanelPreview()
    {
        ulong published=0;foreach(var draw in _draws)published|=1ul<<(int)draw.LayoutGroup;
        if(UiLayout!.Preview(EnbUiGroup.Cargo)&&!_cargo)
        {
            _layoutGroup=EnbUiGroup.Cargo;var page=_page;var count=_pageCount;
            try{Cargo();Flush();}finally{_page=page;_pageCount=count;_layoutGroup=EnbUiGroup.None;}
            published|=1ul<<(int)EnbUiGroup.Cargo;
        }
        if(ModernUi&&UiLayout.Preview(EnbUiGroup.ModernInventory)&&!_modernInventory)
        {_layoutGroup=EnbUiGroup.ModernInventory;try{ModernInventory();Flush();}finally{_layoutGroup=EnbUiGroup.None;}published|=1ul<<(int)EnbUiGroup.ModernInventory;}
        if(ModernUi&&UiLayout.Preview(EnbUiGroup.ModernEquipment)&&!_modernEquipment)
        {_layoutGroup=EnbUiGroup.ModernEquipment;try{ModernEquipment();Flush();}finally{_layoutGroup=EnbUiGroup.None;}published|=1ul<<(int)EnbUiGroup.ModernEquipment;}
        if(ModernUi&&UiLayout.Preview(EnbUiGroup.ModernSkills)&&!_modernSkills)
        {_layoutGroup=EnbUiGroup.ModernSkills;try{ModernSkillsWindow();Flush();}finally{_layoutGroup=EnbUiGroup.None;}published|=1ul<<(int)EnbUiGroup.ModernSkills;}
        if(ModernUi&&UiLayout.Preview(EnbUiGroup.ModernMissions)&&!_modernMissions)
        {_layoutGroup=EnbUiGroup.ModernMissions;try{ModernMissionsWindow();Flush();}finally{_layoutGroup=EnbUiGroup.None;}published|=1ul<<(int)EnbUiGroup.ModernMissions;}
        if(ModernUi&&UiLayout.Preview(EnbUiGroup.ModernFactions)&&!_modernFactions)
        {_layoutGroup=EnbUiGroup.ModernFactions;try{ModernFactionsWindow();Flush();}finally{_layoutGroup=EnbUiGroup.None;}published|=1ul<<(int)EnbUiGroup.ModernFactions;}
        if(ModernUi&&UiLayout.Preview(EnbUiGroup.ModernCommands)&&!_modernCommands)
        {_layoutGroup=EnbUiGroup.ModernCommands;try{ModernCommandsWindow();Flush();}finally{_layoutGroup=EnbUiGroup.None;}published|=1ul<<(int)EnbUiGroup.ModernCommands;}
        if(_scene is {} scene&&_assets is {} assets)
        {
            // One unwired presentation owner keeps all preview panels' animation
            // state together. No transport, persistence or audio callbacks.
            _layoutPanelPreview??=new(session){_layoutPreviewOwner=true};
            var preview=_layoutPanelPreview;preview.ModernUi=ModernUi;preview.Update(scene,assets,_size,_scale,true);
            preview._character=UiLayout.Preview(EnbUiGroup.Character);preview._characterTab=0;
            preview._equip=UiLayout.Preview(EnbUiGroup.Equipment);preview._cargo=preview._vault=UiLayout.Preview(EnbUiGroup.Vault);
            preview._friendsOpen=UiLayout.Preview(EnbUiGroup.Friends);preview._guildOpen=UiLayout.Preview(EnbUiGroup.Guild);
            preview._finderOpen=UiLayout.Preview(EnbUiGroup.GroupFinder);preview._finderStream=preview.Group!.SymbolStream;
            preview._layoutLootPreview=!UiLayout.Docked&&UiLayout.Preview(EnbUiGroup.Loot);
            preview.Publish();
            foreach(var draw in preview.Presentation)
                if(draw.LayoutGroup!=EnbUiGroup.None&&UiLayout.Preview(draw.LayoutGroup)&&(published&(1ul<<(int)draw.LayoutGroup))==0)_draws.Add(draw);
        }
        const string key="targetPortrait/FRAME_DETAIL";
        if(!UiLayout.Preview(EnbUiGroup.Target)||(published&(1ul<<(int)EnbUiGroup.Target))!=0||!_art.ContainsKey(key))return;
        _layoutGroup=EnbUiGroup.Target;var r=Native(key);Draw(key,r);
        Text("layoutPreview","Target preview",new(r.X+8,r.Y+8,Math.Max(1,r.Width-16),24));Flush();_layoutGroup=EnbUiGroup.None;
    }
}
