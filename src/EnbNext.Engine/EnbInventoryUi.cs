using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>Inventory, equipment, character information and shared item examination. Canvas supplies input only.</summary>
internal sealed partial class EnbInventoryUi(Func<uint?> session) : IDisposable
{
    internal EnbUiLayoutProfiles? UiLayout {get;set;}
    private sealed record Art(EnbAuthoredUiAsset Asset, Rect Design);
    private readonly Dictionary<string, Art> _art = [];
    private readonly Dictionary<string, EnbInventoryHit> _hits = [];
    private int _hitCount;
    private readonly Dictionary<string, string> _tips = [];
    private readonly Dictionary<string, EnbRetailText> _labels = [];
    private readonly Dictionary<string, EnbRetailFontFace> _fonts = [];
    private readonly Dictionary<string,(string Text,EnbRetailFontFace Font,double Width,string[] Lines)> _wrappedText=[];
    private readonly Dictionary<string, EnbUiRaster?> _icons = [];
    private readonly Dictionary<string, EnbAuthoredTooltipFrame> _frames = [];
    private readonly EnbCurrentRetailTooltipDelay _dwell = new();
    private readonly Dictionary<string, EnbInventoryItemRequest> _items = [];
    private EnbRetailAssetBundle? _assets;
    private IEnbSceneFrame? _scene;
    private EnbSceneInventorySnapshot? Data => _scene?.Hud.Inventory;
    private Size _size;
    private double _scale = 1;
    private uint? _owner;
    private ulong _ship;
    private bool _interactive, _cargo, _equip, _character, _filterOpen, _busy, _reverse, _tooltipVisible;
    private int _page, _pageCount=1, _filter, _sort, _characterTab, _characterPage;
    private IReadOnlyDictionary<string,string>? _inventorySlotTips;
    private KeyModifiers _itemPressModifiers;
    private bool _itemPressRight;
    private string? _hover, _pressed, _skillDetail;
    private bool _captured;
    private Point _point;
    private Point _pressOrigin;
    private bool _draggingAbility;
    public Func<uint,ulong,int,Point,bool>? SkillDropped {get;set;}
    private Point _detailOrigin, _lastDrag;
    private Vector3 _previewView = new(0,0,1.1f*MathF.Sqrt(3));
    private Quaternion _previewRotation = Quaternion.Identity;
    private EnbInventoryItemRequest? _examined, _pressedItem;
    private string? _message;
    private bool _expanded;
    private int _skillPage;
    private bool _skillPanelActive;
    private int _skillVisibleUnits=5;
    // 0054B140 advances one authored row per UI update, not per elapsed second.
    private void AdvanceSkillPanel()
    {
        if(!_interactive||!_character||_characterTab!=0||!_skillPanelActive)return;
        int target=_expanded?10:5;
        if(_skillVisibleUnits!=target)_skillVisibleUnits+=Math.Sign(target-_skillVisibleUnits);
    }
    private int _selectedSkill=-1, _selectedMissionRow=-1;
    private string? _selectedFaction;
    private int _factionPage, _selectedFactionRow=-1;
    // 005C5330/005C5DB0 retain a visible row, not a mission identity.
    private EnbCharacterMissionSnapshot? SelectedMission => _selectedMissionRow is >=0 and <3
        ? Data?.Missions.ElementAtOrDefault(_characterPage+_selectedMissionRow) : null;
    private EnbCharacterMissionSnapshot? _missionDetailSnapshot;
    private int _missionDetailPage;
    private long _hoverSince;

    private bool _animatingText;
    private readonly Dictionary<string,(string Text,long Start)> _reveals=[];
    private double _wipeSpeed;
    private double _scrollSpeed;
    private IReadOnlyDictionary<string,string>? _forfeitDialog;
    private EnbInterfaceLayoutCatalog? _attributeLayout;
    private Guid _attributeLabelSession;
    public event Action<string,float,float>? CharacterTextSoundRequested;
    private ImmutableList<EnbUiModelDraw>.Builder _draws = ImmutableList.CreateBuilder<EnbUiModelDraw>();
    private ImmutableArray<EnbUiTextDraw>.Builder _images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
    public bool IsOpen => StationPlayerMenuOpen || _finderOpen || _friendsOpen || _guildOpen || GuildConfirmationOpen || _recovery is not null || _playerTrade is not null || Data?.HarvestableId is not null || Data?.HulkId is not null || _terminal is not null || _conversationOwner is not null || _cargo || _equip || _character || _examined is not null || _modernInventory || _modernVault || _modernEquipment || _modernSkills || _modernMissions || _modernFactions || _modernCommands || _modernLocal || _modernStationMap || _modernMusic || _modernWiki || _modernItems || _modernLog || ModernOptionsShown;
    public bool InventoryOpen => _cargo;
    // 69CCD0: station service interfaces, NPC conversation (including its
    // pending owner), and player trade; ordinary cargo/chat are not this gate.
    public bool StationLabelInteractionOpen => _terminal is not null || _conversationOwner is not null || _playerTrade is not null;
    public bool StationLabelServiceOpen => _terminal is not null;
    internal string UtilityInputState => $"ready={_interactive} cargo={_cargo} equip={_equip} character={_character} tab={_characterTab} owner={_ship}";
    public Func<int, bool>? BridgePanelRequested { get; set; }
    public bool CharacterOpen => _character;
    // Terminals and NPC conversations take the whole screen.
    public bool CoversScreen => _interactive && (_terminalEntering is not null || _terminal is not null || _conversationEntering is not null || _conversationOwner is not null);
    public bool BlocksStationMovement => StationPlayerMenuOpen || _finderOpen || _friendsOpen || _guildOpen || GuildConfirmationOpen || _terminalEntering is not null || _terminal is not null || _conversationEntering is not null || _conversationOwner is not null || _examined is not null || _confirmation is not null || _destroyItem is not null || _reloadItem is not null || _draggingItem;
    public string? Error { get; private set; }
    public ImmutableList<EnbUiModelDraw> Presentation { get; private set; } = [];
    public Func<EnbCharacterActionRequest,Task<bool>>? TrainRequested {get;set;}
    public Func<EnbCharacterActionRequest,Task<bool>>? ForfeitRequested {get;set;}
    public Func<EnbCharacterActionRequest,Task<bool>>? ClearMissionRequested {get;set;}
    /// <summary>Next mission tracker option: shows Track/Untrack on the selected mission.</summary>
    public bool MissionTrackingEnabled {get;set;}
    public Func<int,bool>? IsMissionTracked {get;set;}
    public Action<int>? MissionTrackToggled {get;set;}
    private EnbCharacterActionRequest? _confirmation;
    private bool _confirmForfeit;
    private Point _confirmationOrigin;
    public Func<EnbInventoryItemRequest, bool, Task<bool>>? AutoFireRequested { get; set; }
    /// <summary>The Auto Fire checkbox's own selection (retail checkbox +58) for the examined item, until the item's published
    /// state agrees or another item is examined.</summary>
    private (EnbInventoryItemRequest Item, bool Value)? _autoFireChoice;
    private bool ShownAutoFire(EnbSceneItemSnapshot item)
    {
        if (_autoFireChoice is { } choice && (_examined != choice.Item || item.AutoFire == choice.Value)) _autoFireChoice = null;
        return _autoFireChoice?.Value ?? item.AutoFire;
    }
    public Func<uint, ulong, int, bool, Task<bool>>? SortRequested { get; set; }
    public event Action<ImmutableList<EnbUiModelDraw>>? Changed;
    public event Action<EnbUiTooltipDraw?>? TooltipChanged;
    public event Action? Opened;

    public void Update(IEnbSceneFrame scene, EnbRetailAssetBundle? assets, Size size, double scale, bool interactive)
    {
        bool activateStationGroup=scene.ActiveStarbaseId is not null &&
            (!_interactive || _scene?.ActiveStarbaseId!=scene.ActiveStarbaseId);
        // The sound stream survives sector handoffs and changes at character selection.
        if (_attributeLabelSession != scene.ClientSoundStreamId)
        {
            _attributeLabelSession=scene.ClientSoundStreamId;
            ResetCharacterOrdinaryText();
            _vendorButtonAlpha.Clear();
            ResetTradeFonts();
            ResetGroupFonts();
            _cargoWipes.Clear();_cargoScroll.Clear();
            ResetEquipmentStatArrows();
        }
        ulong ship = scene.Objects.FirstOrDefault(o => o.IsLocalShip)?.RenderIdentity ?? 0;
        if (_owner != session() || _ship != ship || !ReferenceEquals(assets, _assets))
        {
            if (_owner != session() || !ReferenceEquals(assets, _assets)) ResetCharacterOrdinaryText();
            ResetGroupVisualOwners();
            CloseAll(); _page = _filter = _characterPage = _factionPage = _skillPage = 0; _selectedFactionRow=-1;_socialSliders.Remove("factions");_socialSliders.Remove("skills"); _owner = session(); _ship = ship;
            var options=assets?.ResolvePlayerOptions(_owner).Options;
            _vendorSort=options?.TradeSortByName??false;
            RestoreInventoryFilters(options);
            RestoreInventorySort(options);
            if (!ReferenceEquals(assets, _assets))
            {
                DisposeAssets(); _assets = assets;
                if (assets is not null)
                    try { Load(assets); Error = null; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
                    { Error = ex.Message; }
            }
        }
        // 0054A640 reruns the visible detail handler on incoming SkillPoints,
        // even when the numeric value/title did not change.
        if(_character && _characterTab==0 && _selectedSkill>=0 &&
            Data?.SkillPointsRevision!=scene.Hud.Inventory?.SkillPointsRevision)ClearSkillDetailText();
        SwitchGroupVisualOwner(scene.ActiveStarbaseId is not null);
        SwitchTradeFontOwner(scene.ActiveStarbaseId is not null);
        _scene = scene; _size = size; _scale = scale;
        _interactive = interactive && !scene.IsTransitioning && _art.Count > 0 && Error is null;
        RestoreModernLocal(scene);
        TickModernMusic();
        TickModernItems();
        // 6B4860 calls +60 only for active owners; +64 is the group RET stub.
        // Advance once per UI frame, never on hover-triggered redraws.
        uint groupFrameTime=GroupFrameTime();
        _groupFrameClock.Advance(groupFrameTime,_interactive);
        AdvanceGroupFontClocks(groupFrameTime,_interactive);
        if(StationGroup)_spaceGroupVisual.Clock.Advance(groupFrameTime,false);
        // 73B150 always routes symbols to MainView+1D8, before 5944D0
        // reconciles Aux members. Preserve that retained-recipient ordering.
        if(scene.Hud.Group is {} symbolGroup)
            UpdateGroupSymbols(symbolGroup,StationGroup?_spaceGroupVisual.Clock.Time:_groupFrameClock.Time,
                StationGroup?_spaceGroupVisual.Members:_groupMembers);

        if(scene.Hud.Group is {} group)
        {
            ReceiveGroupAttention(group);
            if(_groupMembers.Update(group.AuxDataState,activateStationGroup,group.Members,StationGroup,
                id=> { if(scene.Objects.Any(o=>o.GameId==id))RadarLabelsInvalidated?.Invoke(id,scene.Hud.GroupMemberIds.Contains(id)); }) is {} groupCue)
                InteractionSoundRequested?.Invoke(groupCue);
            if(group.AuxDataState>=2 || activateStationGroup && group.AuxDataState!=0)
                RefreshGroupFormation(group);
        }

        UpdateTerminal(); UpdatePlayerTrade(); RefreshStationPlayerMenu();
        if (!_interactive) CloseAll();
        else UpdateConversation();
        UpdateRecovery();
        UpdateCallerPortrait(scene,assets,Environment.TickCount64*.001);
        if (_examined is { } request && Resolve(request) is null) _examined = null;
        if(_destroyItem is {} destroy && (Data is not {} inventory || !EnbInventoryManagement.IsCurrent(destroy,inventory,scene.ActiveStarbaseId is not null)))_destroyItem=null;
        if(_reloadItem is {} reload && (IsTerminalMove(reload)?!IsCurrentTerminalMove(reload):(Data is not {} reloadInventory || !EnbInventoryManagement.IsCurrent(reload,reloadInventory,scene.ActiveStarbaseId is not null))))_reloadItem=null;
        ContinueWeaponAfterUnload();
        AdvanceSkillPanel();Publish();
        PublishGroupAttention();
    }
    private void Load(EnbRetailAssetBundle a)
    {
        LoadInventoryFilterMask(a);
        _attributeLayout=EnbInterfaceLayoutCatalog.Load(Path.Combine(a.Paths.ClientIniDirectory,"fa_att.ini"));
        foreach(var (key,file) in new[]{("cannotUse","item_noskill.w3d"),("dragItem","invitem.w3d")})
        {
            var asset=EnbAuthoredUiAsset.Load(a,file)??throw new InvalidDataException(file);
            var mesh=asset.Resource.Meshes.Single();
            // These loose slot render objects use their local origin, rather than a panel pivot.
            var geometry=new EnbW3dDeformedGeometry(mesh.Positions.ToImmutableArray(),mesh.Normals.ToImmutableArray());
            asset=asset with {Geometry=geometry};
            var positions=geometry.Positions;
            var min=positions.Aggregate(Vector3.Min);var max=positions.Aggregate(Vector3.Max);
            _art[key]=new(asset,new(min.X,-max.Y,max.X-min.X,max.Y-min.Y));
            if(key=="dragItem")_art["dragLayer"]=new(asset with {Resource=asset.Resource.Copy().SetMeshes([])},_art[key].Design);
        }
        var skillLabels=EnbInterfaceLayoutCatalog.Load(Path.Combine(a.Paths.ClientIniDirectory,"fa_tsi12c.ini"));
        var inventoryLabels=EnbInterfaceLayoutCatalog.Load(Path.Combine(a.Paths.ClientIniDirectory,"fa_pdainv.ini"));
        _vaultButtonLabel=inventoryLabels.TryResolve("Vault_inv",out var vaultButton)?vaultButton.Properties.GetValueOrDefault("String0",""):"";
        LoadLootArt(a);
        LoadPlayerTrade(a);
        LoadGroup(a);
        LoadGuild(a);
        LoadGroupFinder(a);
        LoadTargetPortrait(a);
        _cargoFonts.Clear();_cargoWipes.Clear();_cargoScroll.Clear();
        foreach (var (prefix, file) in new[] { ("vault", "vault00.w3d"), ("cargo", "tsi04a.w3d"), ("equip", "tsi07a.w3d"),
            ("character", "tsi15a.w3d"), ("options", "opt01a.w3d"), ("checks", "opt03a.w3d"), ("left", "slider_lt.w3d"), ("right", "slider_rt.w3d"), ("skills", "tsi12d.w3d"), ("skillFrame","tsi12b.w3d"), ("bio","tsi12a.w3d"), ("skillInfo","tsi12c.w3d"), ("missions","tsi22a.w3d"), ("missionInfo","tsi23a.w3d"), ("factions","tsi30a.w3d"), ("factionInfo","tsi30b.w3d"), ("up","slider_up.w3d"), ("down","slider_dn.w3d") })
        {
            var asset = EnbAuthoredUiAsset.Load(a, file) ?? throw new InvalidDataException(file + " unavailable");
            if(prefix=="cargo")LoadCargoFonts(a,asset,"fa_pdainv.ini","");
            if(prefix=="vault")LoadCargoFonts(a,asset,"fa_vault00a.ini","vault/");
            if(prefix=="equip")LoadCargoFonts(a,asset,"fa_tsi07a.ini","equip/");
            if(prefix=="character")LoadCharacterShellFonts(a,asset);
            if(prefix is "missions" or "missionInfo" or "factions" or "factionInfo" or "skillInfo" or "skillFrame")LoadCharacterText(a,asset,prefix);
            if(prefix is "missions" or "factions" or "skillFrame")
            {
                var transforms=EnbCockpitUi.ResolveTransforms(asset);
                foreach(var pivot in asset.Hierarchy.Pivots.Where(p=>p.Name.StartsWith("SLIDER_",StringComparison.Ordinal)||p.Name.StartsWith("FILL_BONE",StringComparison.Ordinal)))
                {var point=transforms[pivot.Ordinal].Translation;_guildAnchors[prefix+"/"+pivot.Name]=new(point.X+320,240-point.Y);}
            }
            if (prefix=="bio")
            {
                var parentAsset=_art["skillFrame/NEW_FRAME"].Asset;
                var parentPivot=parentAsset.Hierarchy.Pivots.First(p=>p.Name.Equals("CHAR_ATTRIB",StringComparison.OrdinalIgnoreCase));
                var parentTransform=EnbCockpitUi.ResolveTransforms(parentAsset)[parentPivot.Ordinal];
                LoadCharacterText(a,asset,prefix,parentTransform);
                if(_attributeLayout.TryResolve("CHARACTER_NAME_ATT",out var nameLabel))
                    _biographyNameStyle=EnbAttachedTextStyle.Load(a,asset,nameLabel,parentTransform);
            }
            if(prefix=="skillInfo"&&skillLabels.TryResolve("TITLE_ATT",out var titleLabel))
                _skillTitleStyle=EnbAttachedTextStyle.Load(a,asset,titleLabel,Matrix4x4.Identity);
            foreach (var m in asset.Resource.Meshes)
            {
                // 00662DC0 retains each DOT00 member; they share HIGHLIGHT00's
                // pivot but have individually authored positions (including dot 10).
                if(prefix=="skills"&&m.Name.StartsWith("DOT00_",StringComparison.Ordinal))
                {
                    var member=asset.Resource.HierarchicalLods.Single(h=>h.Name.Equals(asset.Hierarchy.Name,StringComparison.OrdinalIgnoreCase)).Levels[0].Members.Single(v=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,v.Name));
                    var transform=EnbCockpitUi.ResolveTransforms(asset)[(int)member.ReferenceIndex];
                    var positions=m.Positions.Select(v=>Vector3.Transform(v,transform)).ToImmutableArray();
                    var normals=m.Normals.Select(v=>Vector3.TransformNormal(v,transform)).ToImmutableArray();
                    var min=positions.Aggregate(Vector3.Min);var max=positions.Aggregate(Vector3.Max);
                    _art["notch/off/"+m.Name]=new(asset with {Resource=asset.Resource.Copy().SetMeshes(new[]{m}),Geometry=new(positions,normals)},new(min.X,-max.Y,max.X-min.X,max.Y-min.Y));
                }
                if(!asset.Hierarchy.Pivots.Any(p=>p.Name==m.Name))continue;
                _art[prefix + "/" + m.Name] = new(asset with { Resource = asset.Resource.Copy().SetMeshes(new[] { m }) },
                    EnbCockpitUi.MeshBounds(asset, m.Name).Translate(new(-320, -240)));
            }
        }
        LoadEquipmentStatArrows();
        for(int bone=0;bone<10;bone++)
        {
            var anchor=_guildAnchors[$"skillFrame/FILL_BONE{bone:00}"];
            LoadCharacterText(a,_art["skills/DESC00"].Asset,"skills",Matrix4x4.CreateTranslation((float)anchor.X-320,240-(float)anchor.Y,0),bone.ToString(CultureInfo.InvariantCulture));
        }
        foreach(var (key,file) in new[]{("thumb/h","slider_hor.w3d"),("thumb/v","slider_vert.w3d")})
        {
            var asset=EnbAuthoredUiAsset.Load(a,file)??throw new InvalidDataException(file);
            var transforms=EnbCockpitUi.ResolveTransforms(asset);
            var pose=new EnbW3dHierarchyPoseOwner(transforms,Enumerable.Repeat(true,transforms.Length).ToImmutableArray(),null,null);
            if(!EnbW3dSkinDeformer.TryDeform(asset.Resource.Meshes.Single(),pose,out var geometry,out var error))throw new InvalidDataException(error);
            var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
            _art[key]=new(asset with {Geometry=geometry},new(min.X,-max.Y,max.X-min.X,max.Y-min.Y));
        }
        EnbW3dRenderTextureBinding Filled(EnbW3dRenderTextureBinding t)=>t with {LogicalName="if_dot01.tga",Location=null,ResolveLogicalNameAtRuntime=true};
        foreach(var (key,notch) in _art.Where(p=>p.Key.StartsWith("notch/off/",StringComparison.Ordinal)).ToArray())
        _art[key.Replace("notch/off/","notch/on/",StringComparison.Ordinal)]=notch with {Asset=notch.Asset with {Resource=notch.Asset.Resource.Copy().SetMeshes(notch.Asset.Resource.Meshes.Select(m=>(m).Copy().SetMaterialPasses(m.MaterialPasses.Select(p=>(p).Copy().SetTextureStages(p.TextureStages.Select(s=>(s).Copy().SetTextures(s.Textures.Select(Filled).ToArray())).ToArray()).SetTriangleBindings(p.TriangleBindings.Select(t=>t with {Textures=t.Textures.Select(b=>b is null?null:Filled(b)).ToArray()}).ToArray())).ToArray())).ToArray())}};
        var palette=EnbRetailIniDocument.Load(Path.Combine(a.Paths.ClientIniDirectory,"fontart.ini"));

        _wipeSpeed=palette.EnumerateSections().Select(s=>s.Value).Where(s=>s.GetValueOrDefault("EffectID")=="FA_WIPE_ON_RIGHT")
            .Select(s=>double.Parse(s["Speed"],CultureInfo.InvariantCulture)).Single();
        _scrollSpeed=palette.EnumerateSections().Select(s=>s.Value).Where(s=>s.GetValueOrDefault("EffectID")=="FA_SCROLL_UP")
            .Select(s=>double.Parse(s["Speed"],CultureInfo.InvariantCulture)).Single();
        if(EnbRetailIniDocument.Load(Path.Combine(a.Paths.ClientIniDirectory,"dialogbox.ini")).TryGetSection("UI_MISSN",out var dialog))_forfeitDialog=dialog;
        if(EnbRetailIniDocument.Load(Path.Combine(a.Paths.ClientIniDirectory,"dialogbox.ini")).TryGetSection("UI_TRANSACTION_DUMP_ITEM",out var destroyDialog))_destroyDialog=destroyDialog;
        if(EnbRetailIniDocument.Load(Path.Combine(a.Paths.ClientIniDirectory,"tooltip.ini")).TryGetSection("UI_PDA_EQUIP",out var equipmentTips))_equipmentTips=equipmentTips;
        var font = EnbInterfaceLayoutCatalog.Load(Path.Combine(a.Paths.ClientIniDirectory, "fc_default.ini"));
        var strings = EnbRetailIniDocument.Load(Path.Combine(a.Paths.ClientIniDirectory, "tooltip.ini"));
        strings.TryGetSection("GD_SLOT_DATA",out _inventorySlotTips);
        strings.TryGetSection("UI_VAULT",out _vaultControlTips);
        var windows = EnbRetailIniDocument.Load(Path.Combine(a.Paths.ClientIniDirectory, "dynawin.ini"));
        _stationPlayerText=EnbInterfaceLayoutCatalog.Load(Path.Combine(a.Paths.ClientIniDirectory,"fc_symbolchat.ini"));
        _stationPlayerOption=EnbRetailScannerTooltipDefinition.Parse(font,strings,windows,"SYMBOL_CHAT_OPTION");
        foreach (string name in new[] { "ITEM_EXAMINATION_MAIN", "ITEM_EXAMINATION_ATTRIBUTES", "ITEM_EXAMINATION_SPECIALS", "ITEM_EXAMINATION_DESCRIPTION", "ITEM_EXAMINATION_OPTIONS", "TOOLTIPS", "DIALOG_BOX_WINDOW", "DIALOG_BOX_INFO", "DIALOG_BOX_OPTION", "SYMBOL_CHAT_WINDOW", "SYMBOL_CHAT_SECTION", "SYMBOL_CHAT_OPTION" })
        {
            var d = EnbRetailScannerTooltipDefinition.Parse(font, strings, windows, name);
            if (d is not null && EnbAuthoredTooltipFrame.Load(a, d) is { } frame) _frames[name] = frame;
        }
        var check = _art["checks/CHECK00"];
        EnbW3dRenderTextureBinding On(EnbW3dRenderTextureBinding t) => t with { LogicalName = "if_check_on.tga", Location = null, ResolveLogicalNameAtRuntime = true };
        _art["check/on"] = check with { Asset = check.Asset with { Resource = check.Asset.Resource.Copy().SetMeshes(check.Asset.Resource.Meshes.Select(m => (m).Copy().SetMaterialPasses(m.MaterialPasses.Select(p => (p).Copy().SetTextureStages(p.TextureStages.Select(s => (s).Copy().SetTextures(s.Textures.Select(On).ToArray())).ToArray()).SetTriangleBindings(p.TriangleBindings.Select(t => t with { Textures = t.Textures.Select(b => b is null ? null : On(b)).ToArray() }).ToArray())).ToArray())).ToArray()) } };
    }
    public bool ToggleInventory() { if (!_interactive || BridgePanelRequested?.Invoke(_cargo ? 0 : 1) == false) return false; _cargo = !_cargo; if (!_cargo) { _equip = _vault = _vaultFilterOpen = _filterOpen = false; ClearTerminal(); } else {_character=false;Opened?.Invoke();} Publish(); return true; }
    public bool ToggleInventoryPanel(string action)
    {
        if (!_interactive || action is not ("Cargo" or "Equipment" or "Vault")) return false;
        if (action == "Vault" && _scene?.ActiveStarbaseId is null) return false;
        if (action == "Cargo") return ToggleInventory();
        bool visible = action == "Equipment" ? _cargo && _equip : _cargo && _vault;
        if (visible) return ToggleInventory();
        if (BridgePanelRequested?.Invoke(1) == false) return false;
        ClearTerminal(); _character = false; _cargo = true;
        _equip = action == "Equipment"; _vault = action == "Vault";
        _filterOpen = _vaultFilterOpen = false;
        CancelPointer(); ClearHover(); Opened?.Invoke(); Publish();
        return true;
    }
    public bool ToggleCharacter() { if (!_interactive || BridgePanelRequested?.Invoke(_character ? 0 : 2) == false) return false; _reveals.Clear(); _characterTextRows.Clear(); _character = !_character; _skillDetail = null;_selectedMissionRow=-1;_selectedFaction=null; if (_character) {if(_characterTab==1){_characterPage=0;_socialSliders.Remove("missions");}_biographyNameScroll.Clear();ClearTerminal();_cargo=_equip=_vault=_vaultFilterOpen=_filterOpen=false;Opened?.Invoke();} Publish(); return true; }
    public bool ToggleCharacterTab(int tab)
    {
        if (!_interactive || tab is < 0 or > 1) return false;
        if (_character && _characterTab == tab) return ToggleCharacter();
        else
        {
            bool opening = !_character;
            _characterTab = tab; _characterPage = 0; _characterTextRows.Clear(); _socialSliders.Remove("missions");
            _reveals.Clear(); _skillDetail = null; _selectedMissionRow = -1; _selectedFaction = null;
            if (opening) return ToggleCharacter();
            else Publish();
        }
        return true;
    }
    public void Examine(byte inventory, int slot)
    {
        if (!_interactive || Data?.Find(inventory, slot) is not { } item || _owner is not uint owner) return;
        // 00630070: examining the slot that is already examined closes the examination (INV-D05).
        if (_examined is { } open && open.Inventory == inventory && open.Slot == slot) { _examined = null; Publish(); return; }
        _detailOrigin = new(_point.X * 640 / Math.Max(1,_size.Width), _point.Y * 480 / Math.Max(1,_size.Height));
        _examined = new(owner, _ship, inventory, slot, item.TemplateId); _message = null; Opened?.Invoke(); Publish();
    }
    public void ExamineAt(byte inventory, int slot, Point point) { _point=point; Examine(inventory,slot); }
    public bool Close() => CloseCore(true);
    // PDA utility windows do not register Escape (005CF9B0/005D10C0/
    // 00656FF0/005734D0). Leave it to the client's fixed Quit route.
    // Item examination and separately registered dialogs still own their key.
    public bool HandleEscape() => CloseCore(false);
    internal async Task<bool> ConfirmationKey(ushort key)
    {
        bool destroy=_destroyItem is not null;
        bool vendor=_tradeConfirmation is not null;
        if(!destroy && !vendor && !(_confirmation is not null && _confirmForfeit))return false;
        var dialog=destroy?_destroyDialog:vendor?_tradeDialog:_forfeitDialog;
        for(int index=0;index<2;index++)
            if(dialog?.ContainsKey("Option"+index)==true &&
               ParseRetailKey(dialog.GetValueOrDefault("Key"+index,""))==key)
            {
                if(!_busy)await Activate(destroy?(index==0?"destroyNo":"destroyYes"):
                    vendor?(index==0?"tradeYes":"tradeNo"):(index==0?"confirmYes":"confirmNo"));
                return true;
            }
        // Shared retail dialog00588480 adds no implicit Escape shortcut.
        return key!=EnbInventoryKeys.Escape;
    }
    private bool CloseCore(bool closeUtilityPanel)
    {
        if(remoteShortcutMenu is not null){remoteShortcutMenu=null;Publish();return true;}
        if (_friendsOpen) { if (!closeUtilityPanel) return false; CloseFriends(); Publish(); return true; }
        if (_guildOpen || GuildConfirmationOpen)
        {
            // TSI35A setup 008199F0 registers an empty hotkey table.
            if (!closeUtilityPanel) return false;
            CloseGuild(); Publish(); return true;
        }
        // Group formation and symbol owners register no Escape (00594040/0064B050).
        if(!closeUtilityPanel && (_groupFormationOpen || _groupSymbolOrigin is not null))return false;
        // (The Next Modern UI vendor answers No to its question on Escape, below.)
        if(!closeUtilityPanel && (_destroyItem is not null || _tradeConfirmation is not null && !(ModernUi && _trading) || _confirmation is not null && _confirmForfeit))return false;
        if(!closeUtilityPanel && _customConfirm && _terminal?.Type is 5 or 6)return false;
        // Retail filter dropdown005789E0 owns no Escape; preserve it even
        // when the underlying facility has its own Escape binding.
        if(!closeUtilityPanel && (_filterOpen || _vaultFilterOpen) && _examined is null)return false;
        if(_recovery is not null)
        {
            // Recovery TLI01A_AV registers no Escape (005524F0/00552980).
            if(!closeUtilityPanel)return false;
            _=ActivateRecovery("recoveryClose");Publish();return true;
        }
        if(_creditEntry){_creditEntry=false;Publish();return true;}
        if(_playerTrade?.PartnerId is not null && _examined is null && _reloadItem is null)
        {
            // TRADE00A owns no Escape binding; only its explicit Cancel/close
            // action sends cancellation (005E1FA0 / 005E26F0 / 005E2CE0).
            // Next Modern UI window: Escape is its Cancel (same retail request).
            if(!closeUtilityPanel && !ModernUi)return false;
            if(ModernUi){_=ActivatePlayerTrade("playerTradeCancel");Publish();return true;}
            _=SendPlayerTrade(EnbPlayerTradeAction.Cancel);return true;
        }
        if((Data?.HulkId is not null || Data?.HarvestableId is not null) && _examined is null)
        {
            // PSI09A setup/open owns no Escape; its X_SPECIALS gadget closes.
            // Next Modern UI window: Escape is its close button (same retail close).
            if(!closeUtilityPanel && !ModernUi)return false;
            _=CloseLoot();return true;
        }
        if(_terminal is not null && _examined is null)
        {
            // Retail00619470/0054CBC0 consume Escape before fresh customization data.
            if(_terminal.Type is 5 or 6 && !CustomizationDataReady)return true;
            if(ManufacturingExitBlocked)return true;
            // 005A25B0 returns AL=0 while a jobs request is pending.
            // Escape reaches the fixed Quit route; explicit close stays blocked.
            if(JobsRequestPending || AnalysisExitBlocked)return closeUtilityPanel;
            ClearTerminal();_cargo=false;Publish();return true;
        }
        if(_reloadItem is not null){_reloadItem=null;CancelPointer();Publish();return true;}
        if(_destroyItem is not null){_destroyItem=null;CancelPointer();Publish();return true;}
        // CE02A owns focus while trading (00651390 -> 0062E7C0), but
        // registers only Alt-S/Alt-B. Its parent's talk Escape is inactive.
        // Next Modern UI vendor: Escape answers No to a confirmation, else closes the vendor (as its close button).
        if(ModernUi && _trading && _conversationOwner is not null && _examined is null)
        {
            if(_tradeConfirmation is not null)_tradeConfirmation=null;else _=ActivateConversation("vendorClose",null);
            CancelPointer();ClearHover();Publish();return true;
        }
        if(_conversationOwner is not null && _examined is null && (closeUtilityPanel || !_trading)){_=CloseConversation();return true;}
        if(_confirmation is not null)_confirmation=null;
        else if (_examined is not null) _examined = null;
        else if (_filterOpen) _filterOpen = false;
        else if (_modernFilterOpen) _modernFilterOpen = false;
        // Next Modern UI inventory: Escape closes it, like other games' bags.
        else if (_modernVaultFilterOpen) _modernVaultFilterOpen = false;
        else if (_modernVault) CloseModernVault();
        else if (_modernInventory) CloseModernInventory();
        else if (_modernEquipment) _modernEquipment = false;
        else if (_modernMissions && _modernMissionConfirm) _modernMissionConfirm = false;
        else if (_modernMissions && _modernMissionSelected >= 0) _modernMissionSelected = -1;
        else if (_modernMissions) _modernMissions = false;
        else if (_modernCommands && _modernCommandSelected >= 0) _modernCommandSelected = -1;
        else if (_modernCommands) _modernCommands = false;
        else if (_modernStationMap) _modernStationMap = false;
        else if (_modernItems && _itemsFilterOpen) _itemsFilterOpen = false;
        else if (_modernItems) CloseModernItems();
        else if (_modernWiki) CloseModernWiki();
        else if (_modernMusic) _modernMusic = false;
        else if (_modernLocal && _localFilterOpen) _localFilterOpen = false;
        // Next option "Keep the Local window open on Escape": then Escape passes it by (clear target, Quit).
        else if (_modernLocal && !ModernLocalKeptOnEscape) { CloseModernLocal(); WantModernLocal(false); }
        else if (_modernLog) CloseModernLog();
        else if (_modernFactions && _modernFactionSelected is not null) _modernFactionSelected = null;
        else if (_modernFactions) _modernFactions = false;
        else if (_modernSkills && _modernSkillSelected >= 0) _modernSkillSelected = -1;
        else if (_modernSkills) _modernSkills = false;
        else if (_skillDetail is not null)
        {
            // TSI12C setup00548610/open005BB550 register no Escape.
            if(!closeUtilityPanel)return false;
            _skillDetail = null;
        }
        else if (_selectedFaction is not null)
        {
            if(!closeUtilityPanel)return false;
            _selectedFaction=null;
        }
        else if (_selectedMissionRow>=0)
        {
            if(!closeUtilityPanel)return false;
            _selectedMissionRow=-1;
        }
        else if (closeUtilityPanel && _character) { if (BridgePanelRequested?.Invoke(0) == false) return true; _character = false; }
        else if (closeUtilityPanel && _equip) _equip = false;
        else if (closeUtilityPanel && _cargo) { if (BridgePanelRequested?.Invoke(0) == false) return true; _cargo = false; }
        else return false;
        CancelPointer(); ClearHover(); Publish(); return true;
    }
    private void CloseAll() { remoteShortcutMenu=null; _characterTextRows.Clear(); _stationPlayerMenu=null; _lootOpenedAt=null; CloseGroupFinder(); CloseFriends(); CloseGuild(); _recovery=null; _groupFormationOpen=_groupLeaveConfirm=false;_groupSymbolOrigin=null;_answeredGroupInvite=null;_playerTrade=null;_creditEntry=false; ClearTerminal(); _reloadItem=null; _destroyItem=null; ClearConversation(); _reveals.Clear(); _cargo = _equip = _character = _filterOpen = _vault = _vaultFilterOpen = false; CloseModernVault(); CloseModernInventory(); _modernEquipment = _modernSkills = _modernMissions = _modernFactions = false; CloseModernLocal(); _modernStationMap = false; _modernMusic = false; CloseModernWiki(); CloseModernItems(); _examined = null; _skillDetail = null; _confirmation=null;_selectedSkill=_selectedMissionRow=-1;_selectedFaction=null;_expanded=false;_previewView=new(0,0,1.1f*MathF.Sqrt(3));_previewRotation=Quaternion.Identity; _message = null; CancelPointer(); ClearHover(); }
    public void Suspend() { _interactive = false; CloseAll(); Publish(); }
    internal void CloseForPopup() { CloseAll(); Publish(); }
    internal void ClosePdaForModal()
    {
        // 00695F40/00695A00 close map, character, vault, cargo and equipment.
        // Their children retire with them; unrelated transactions retain their
        // own Escape handlers and pending-request gates.
        bool inventoryOpen = _cargo || _equip || _vault;
        _cargo = _equip = _vault = _character = _filterOpen = _vaultFilterOpen = false;
        if (inventoryOpen) _examined = null;
        CancelPointer(); ClearHover();
        // Vendor update 00651510 closes when 00695CE0 finds no PDA owner.
        // 0064DA40 then returns to talk, or exits a vendor-only conversation.
        if (_trading)
        {
            _trading = false; _tradeConfirmation = null; _tradeSelection = null;
            if (_vendorClosesConversation && _conversationOwner is {} owner)
            {
                var request = new EnbStationReplyRequest(owner, Conversation?.Revision ?? 0, 0, true);
                ClearConversation(true);
                if (ReplyRequested is {} reply) _ = Send(() => reply(request));
            }
        }
        Publish();
    }
    internal void CloseUtilityPanels()
    {
        // 00695780/00695A00 select among five PDA owners (006956C0).
        // Loot is a separate 005852D0 owner; switching PDA panels must not
        // replay its opening animation or reset its retained scroll position.
        _cargo=_equip=_character=_filterOpen=_vault=_vaultFilterOpen=false;
        // 00695A00/005D20E0: the PDA owners close with their examinations (INV-D34); Modern windows keep theirs.
        if(!ModernUi)_examined=null;
        ClearTerminal();
        CancelPointer(); ClearHover(); Publish();
    }
    public void CancelPointer() { _equipmentDragArmed=false; ManufacturingSlotInput(1); _finderRepeat=0; _draggingItem=false;_dragItem=null;_draggingAbility=false; RestoreCursorRequested?.Invoke(); bool captured=_captured;_captured=false; _pressed = null; _pressedItem = null; if(captured)CaptureRequested?.Invoke(null); }
    public bool BlocksFlight(Point p) => IsVisible && _hits.Values.Any(hit => hit.IsVisible && LayoutContains(hit,p));
    private Rect Screen(Rect r) => new(r.X * _size.Width / 640, r.Y * _size.Height / 480, r.Width * _size.Width / 640, r.Height * _size.Height / 480);
    private static Rect InsetLeft(Rect r,double x)=>new(r.X+x,r.Y,r.Width-x,r.Height);
    private Rect Native(string id) => _art[id].Design.Translate(new(320,240));
    private EnbSceneItemSnapshot? Resolve(EnbInventoryItemRequest r) => r.Character == session() && r.ShipOwner == _ship && (r.Inventory != 0x12 || r.HarvestableId==Data?.HarvestableId && r.HarvestableOwner==Data?.HarvestableOwner) && (r.Inventory is not (5 or 6) || r.HulkId==Data?.HulkId && r.HulkOwner==Data?.HulkOwner) &&
        (r.Inventory is 12 or 13 or 14 ? Terminal?.Items.FirstOrDefault(i=>i.Inventory==r.Inventory && i.Slot==r.Slot) : Data?.Find(r.Inventory, r.Slot,r.AmmoLayer)) is { } item && item.TemplateId == r.TemplateId ? item : null;

    private EnbUiGroup _layoutGroup;
    private void Publish()
    {
        if(_playerTrade is {Successful:true} && Environment.TickCount64-_tradeCompletedAt>=2000)
        {CloseTradeCargo();_playerTrade=null;}
        if(UiLayout?.Editing!=true&&_layoutPanelPreview is {} retiredPreview){_layoutPanelPreview=null;retiredPreview.Dispose();}
        _draws = ImmutableList.CreateBuilder<EnbUiModelDraw>(); _images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        _animatingText=false;
        if(!_interactive||!_character)_characterFontTab=-1;
        if(!_interactive||!_character||_characterTab!=0)_skillPanelActive=false;
        _characterOrdinaryDrawn.Clear();
        _terminalOrdinaryDrawn.Clear();
        _tradeFontsDrawn.Clear();
        _cargoFontsDrawn.Clear();
        _vendorLabelsDrawn.Clear();_dialogueDrawn.Clear();
        if(TargetPortrait is null)_targetPortraitScroll.Clear();
        if(!_interactive||!_character||_characterTab!=0||_skillVisibleUnits==10)_biographyNameScroll.Pause(Environment.TickCount64);
        if(!_interactive||!_character||_characterTab!=0||_skillDetail is null)_skillTitleScroll.Pause(Environment.TickCount64);
        _hitCount=0; _tips.Clear(); _items.Clear(); foreach (var hit in _hits.Values) hit.IsVisible = false;
        _dropSlots.Clear();
        if(!_interactive)_destroyItem=null;
        if(_modernInventory&&!ModernUi)CloseModernInventory();
        if(!ModernUi)_modernEquipment=_modernSkills=_modernMissions=_modernFactions=_modernCommands=false;
        if((!ModernUi||!_interactive)&&_modernLog)CloseModernLog(true,false);
        IsVisible = _interactive && (IsOpen || ModernQuitShown || _layoutLootPreview || HasGroupUi || ConfirmedDialogOpen || UiLayout?.Editing==true || TargetPortrait is not null && !ModernUi);
        if(_interactive && _size.Width>0 && _size.Height>0)AdvanceGroupFonts();
        // 00585440 removes the mesh immediately; a later show queues frame zero.
        if(!_interactive || Data?.HulkId is null && Data?.HarvestableId is null)_lootOpenedAt=null;
        HideWikiPage();
        if (IsVisible && _size.Width > 0 && _size.Height > 0)
        {
            // Modern UI shows the target's face in its own target frame.
            if(!ModernUi){_layoutGroup=EnbUiGroup.Target;TargetPortraitPanel();Flush();}
            _layoutGroup=EnbUiGroup.Group;GroupPanel();Flush();
            _layoutGroup=EnbUiGroup.None;
            if (_terminal is not null) TerminalBackground();
            if (_cargo) { _layoutGroup=EnbUiGroup.Cargo;Cargo();Flush();_layoutGroup=EnbUiGroup.None; }
            // The Local window is a Next panel: under the maps and every game window (owner: the maps open over it).
            if (_modernLocal)
            {
                Flush(); int first = _draws.Count;
                ModernLocalWindow(); Flush();
                for (int i = first; i < _draws.Count; i++) _draws[i] = _draws[i] with { NextPanel = true };
            }
            // The station map (Next) likewise, over Local.
            if (_modernStationMap && LocalInStation)
            {
                Flush(); int first = _draws.Count;
                ModernStationMapWindow(); Flush();
                for (int i = first; i < _draws.Count; i++) _draws[i] = _draws[i] with { NextPanel = true };
            }
            // The music player (Next) likewise.
            if (_modernMusic)
            {
                Flush(); int first = _draws.Count;
                ModernMusicWindow(); Flush();
                for (int i = first; i < _draws.Count; i++) _draws[i] = _draws[i] with { NextPanel = true };
            }
            // The item catalogue (Next) likewise.
            if (_modernItems)
            {
                Flush(); int first = _draws.Count;
                ModernItemsWindow(); Flush();
                for (int i = first; i < _draws.Count; i++) _draws[i] = _draws[i] with { NextPanel = true };
            }
            // The Modern UI window used last is drawn (and hit) on top: lowest raise first.
            for (int pass = 0, last = int.MinValue; pass < 10; pass++)
            {
                int next = int.MaxValue;
                if (_minvZ > last && _minvZ < next) next = _minvZ;
                if (_mequipZ > last && _mequipZ < next) next = _mequipZ;
                if (_mskillZ > last && _mskillZ < next) next = _mskillZ;
                if (_mmissionZ > last && _mmissionZ < next) next = _mmissionZ;
                if (_mfactionZ > last && _mfactionZ < next) next = _mfactionZ;
                if (_mcommandZ > last && _mcommandZ < next) next = _mcommandZ;
                if (_mlocalZ > last && _mlocalZ < next) next = _mlocalZ;
                if (_mlogZ > last && _mlogZ < next) next = _mlogZ;
                if (_moptZ > last && _moptZ < next) next = _moptZ;
                if (_mvaultZ > last && _mvaultZ < next) next = _mvaultZ;
                last = next;
                if (next == _mequipZ && _modernEquipment) { _layoutGroup=EnbUiGroup.ModernEquipment;ModernEquipment();Flush();_layoutGroup=EnbUiGroup.None; }
                if (next == _mvaultZ && _modernVault) { ModernVaultWindow();Flush(); }
                if (next == _minvZ && _modernInventory) { _layoutGroup=EnbUiGroup.ModernInventory;ModernInventory();Flush();_layoutGroup=EnbUiGroup.None; }
                if (next == _mskillZ && _modernSkills) { _layoutGroup=EnbUiGroup.ModernSkills;ModernSkillsWindow();Flush();_layoutGroup=EnbUiGroup.None; }
                if (next == _mmissionZ && _modernMissions) { _layoutGroup=EnbUiGroup.ModernMissions;ModernMissionsWindow();Flush();_layoutGroup=EnbUiGroup.None; }
                if (next == _mfactionZ && _modernFactions) { _layoutGroup=EnbUiGroup.ModernFactions;ModernFactionsWindow();Flush();_layoutGroup=EnbUiGroup.None; }
                if (next == _mcommandZ && _modernCommands) { _layoutGroup=EnbUiGroup.ModernCommands;ModernCommandsWindow();Flush();_layoutGroup=EnbUiGroup.None; }
                if (next == _mlogZ && _modernLog) { ModernCaptainLogWindow();Flush(); }
                if (next == _moptZ && ModernOptionsShown) { ModernOptionsWindow();Flush(); }
            }
            if (Data?.HulkId is not null || Data?.HarvestableId is not null || _layoutLootPreview) { _remoteHarvestShown=null; if(ModernUi&&!_layoutLootPreview){ModernLootPanel();Flush();}else{_layoutGroup=EnbUiGroup.Loot;LootPanel();Flush();_layoutGroup=EnbUiGroup.None;} }
            else if (ModernRemoteHarvestPanel()) Flush();
            if (_cargo && _vault && _scene?.ActiveStarbaseId is not null) {_layoutGroup=EnbUiGroup.Vault;Vault();Flush();_layoutGroup=EnbUiGroup.None;}
            if (_equip) { _layoutGroup=EnbUiGroup.Equipment;Equip();Flush();_layoutGroup=EnbUiGroup.None; }
            if (_character) { _layoutGroup=EnbUiGroup.Character;Character();Flush();_layoutGroup=EnbUiGroup.None; }
            if (_conversationOwner is not null) ConversationPanel();
            if (_recovery is not null) RecoveryPanel();
            if (_terminal is not null) TerminalPanel();
            if (_playerTrade is not null) { if (ModernUi) ModernPlayerTradePanel(); else PlayerTradePanel(); }
            if (_examined is { } r && Resolve(r) is { } item) { if (ModernUi) ModernDetails(item); else Details(item); }
            if(_confirmation is not null)Confirmation();
            if(_destroyItem is not null)DestroyConfirmation();
            if(_reloadItem is not null)ReloadQuantity();
            if(_creditEntry){if(ModernUi)ModernTradeCreditPanel();else TradeCreditPanel();}
            _layoutGroup=EnbUiGroup.GroupFinder;GroupFinderPanel();Flush();_layoutGroup=EnbUiGroup.None;
            GroupSymbolMenu();
            RemoteShortcutMenu();
            GroupDialog();
            ConfirmedActionDialog();
            _layoutGroup=EnbUiGroup.Friends;FriendsPanel();Flush();_layoutGroup=EnbUiGroup.None;
            _layoutGroup=EnbUiGroup.Guild;GuildPanel();GuildDialog();Flush();_layoutGroup=EnbUiGroup.None;
            StationPlayerMenu();
            // The wiki window (Next) over every other window: its page is the in-game browser, which is drawn over
            // the game's windows, so its frame is too.
            if (_modernWiki && ModernUi && !ModernQuitShown) { Flush(); ModernWikiWindow(); Flush(); }
            if (ModernQuitShown) ModernQuitWindow();
            DraggedItem();
            Flush();
            if(UiLayout?.Editing==true)LayoutPanelPreview();
        }
        PauseTradeFonts();
        foreach(var (id,wipe) in _cargoWipes)
            if(!_cargoFontsDrawn.Contains(id))wipe.Pause(Environment.TickCount64);
        foreach(var (name,alpha) in _vendorButtonAlpha)
            if(!_vendorLabelsDrawn.Contains(name))alpha.Pause(Environment.TickCount64);
        foreach(var (name,alpha) in _terminalOrdinaryAlpha)
            if(!_terminalOrdinaryDrawn.Contains(name))alpha.Pause(Environment.TickCount64);
        foreach(var (name,wipe) in _terminalOrdinaryWipes)
            if(!_terminalOrdinaryDrawn.Contains(name))wipe.Pause(Environment.TickCount64);
        foreach(var (name,motion) in _vendorNameMotion)
            if(!_vendorLabelsDrawn.Contains(name))motion.Pause();
        foreach(var (name,toggle) in _dialogueToggle)
            if(!_dialogueDrawn.Contains(name))toggle.Pause();
        foreach(var (name,wipe) in _dialogueWipe)
            if(!_dialogueDrawn.Contains(name))wipe.Pause(Environment.TickCount64);
        foreach(var (name,wipe) in _characterOrdinaryWipes)
            if(!_characterOrdinaryDrawn.Contains(name))wipe.Pause(Environment.TickCount64);
        Presentation = _draws.ToImmutable(); TransportChanged?.Invoke(); Changed?.Invoke(Presentation); ShowTooltip();
        AnimationRequired?.Invoke(_animatingText);
    }
    private void Flush()
    {
        if (_draws.Count > 0 && _images.Count > 0) _draws[^1] = _draws[^1] with { Images = _draws[^1].Images.AddRange(_images) };
        _images.Clear();
    }
    private void Draw(string art, Rect r, string? button = null, bool enabled = true, bool selected = false)
    {
        if (!_art.TryGetValue(art, out var a)) return;
        if(selected && (art.StartsWith("vendor/HIGHLIGHT") || art.StartsWith("missions/HIGHLIGHT") || art.StartsWith("skills/HIGHLIGHT")))
        {
            string key="selected/"+art;
            if(!_art.TryGetValue(key,out var selectedArt))
            {
                // Retail trade 00651390, mission 005C4610 and skill 00664890 install I_blank_SEL
                // as SelectedBttnTexture while retaining the authored row UVs.
                EnbW3dRenderTextureBinding Replace(EnbW3dRenderTextureBinding texture)=>texture with
                {LogicalName="I_blank_SEL.tga",Location=null,ResolveLogicalNameAtRuntime=true};
                var resource=a.Asset.Resource.Copy().SetMeshes(a.Asset.Resource.Meshes.Select(mesh=>(mesh).Copy().SetMaterialPasses(mesh.MaterialPasses.Select(pass=>(pass).Copy().SetTextureStages(pass.TextureStages.Select((stage,i)=>i==0?(stage).Copy().SetTextures(stage.Textures.Select(Replace).ToArray()):stage).ToArray()).SetTriangleBindings(pass.TriangleBindings.Select(binding=>binding with
                     {Textures=binding.Textures.Select((texture,i)=>i==0 && texture is not null?Replace(texture):texture).ToArray()}).ToArray())).ToArray())).ToArray());
                _art[key]=selectedArt=new(a.Asset with {Resource=resource},a.Design);
            }
            a=selectedArt;
        }
        if (button is not null) Hit(button, r);
        _draws.Add(new(a.Asset, !enabled || button is not null && _pressed == button ? 1u : 0u, (Screen(r)), enabled && button is not null && (selected || _hover == button || button.StartsWith("vendorSelect/") && _hover?.StartsWith(button+"/")==true))
            { LayoutGroup=_layoutGroup, GadgetOwner = button is null ? null : _hits[button], DesignBounds = (a.Design) });
    }
    private void Frame(string name, Rect r, string hit,bool button=false)
    {
        string key = $"dynamic/{name}/{r.Width}/{r.Height}";
        if (!_art.ContainsKey(key) && _frames.TryGetValue(name, out var frame))
        {
            var g = frame.Geometry((float)r.Width, (float)r.Height);
            var min = g.Positions.Aggregate(Vector3.Min); var max = g.Positions.Aggregate(Vector3.Max);
            var source = EnbAuthoredUiAsset.Load(_assets!, frame.Definition.MeshFile + ".w3d")
                ?? throw new InvalidDataException("Dynamic window hierarchy unavailable.");
            _art[key] = new(new(frame.Resource, source.Hierarchy) { Geometry = g }, new(min.X, -max.Y, max.X - min.X, max.Y - min.Y));
        }
        Draw(key, r,button?hit:null); Hit(hit, r);
    }
    private void Button(string id, string text, Rect r, string? tip = null)
    { Draw("options/BTTN00", r, id); Text(id, text, r, id is "filter" or "sort" && _hover!=id ? new(1,1,1):new(1,1,0)); if (tip is not null) _tips[id] = tip; }
    private void Arrow(string id, Rect r, bool previous)
    {
        Draw(previous?"left/CLICK_LEFT":"right/CLICK_RIGHT",r,id);
    }
    private void Cargo()
    {
        var data = Data;
        if(_scene?.ActiveStarbaseId is not null)
        {Draw("options/BTTN00",Native("cargo/BUTTON01"),"vault");CargoLabel("Vault_inv","vault",alternate:_pressed=="vault");}
        Draw("options/BTTN00",Native("cargo/BUTTON02"),"equip");CargoLabel("Equip_inv","equip");
        Draw("cargo/EXIT", Native("cargo/EXIT"), "cargoClose");
        Draw("cargo/CREDITS", Native("cargo/CREDITS"));CargoLabel("Credits","credits","Credits: " + (data?.Credits?.ToString("N0", CultureInfo.InvariantCulture) ?? "0"));
        CargoLabel("Cargo_inv","cargoTitle");
        Button("filter", "Filter", new(11,170,62,14), "Alter the Inventory Filter");
        Draw("options/BTTN00",Native("cargo/BUTTON03"),"sort");CargoLabel("Sort_inv","sort");
        _tips["sort"]="Left-click to activate cargo sort toggle.\nRight-click to reverse the cargo sort order.";
        var items = (data?.Cargo ?? []).OrderBy(i => i.Slot).ToArray();
        var keys=data?.CargoSlotKeys??[];
        int count=keys.Length;
        int pages = Math.Max(1, (count + 3) / 4 - 2); _page = Math.Clamp(_page, 0, pages - 1);
        _pageCount=pages;
        for (int local = 0; local < 12; local++)
        {
            int index = _page * 4 + local;
            int key=index<count?keys[index]:-1;
            var item = items.FirstOrDefault(i => i.Slot == key);
            var r = Native("cargo/FRAME"+local.ToString("00"));
            Slot("cargo" + index, r, item,filter:_cargoFilterValue,enabled:key>=0);
            if(key>=0)
            {
                _dropSlots["cargo"+index]=(1,key);
                if(item is null)EmptyInventorySlotTooltip("cargo"+index);
            }
        }
        Arrow("prev",new(10,340,14,14),true); Arrow("next",new(110,340,14,14),false);
        Draw("cargo/CREDITS",new(24,345,86,4));
        Draw("thumb/h",new(24 + (pages == 1 ? 0 : _page * 51d / (pages - 1)),340,35,14));
        Hit("pageTrack",new(24,340,86,14));
        if(_message is not null && _terminal is null) Text("cargoMessage",_message,new(10,248,118,22),new(1,.4f,.4f),"FONT_TINY");
        Flush();
        if (_filterOpen)
        {
            Frame("ITEM_EXAMINATION_MAIN",new(10,186,117,159),"filterFrame");
            for (int i = 0; i < EnbItemInformation.Filters.Length; i++)
            { string id="filter/"+i; var r=new Rect(18,190+i*12.4,107,12); Hit(id,r); Text(id,EnbItemInformation.Filters[i],r,_hover==id||_filter==i ? new(1,1,0):new(1,1,1),"FONT_SMALL",false); }
            Flush();
        }
    }
    private void Slot(string id, Rect r, EnbSceneItemSnapshot? item,int? weaponLevel=null,bool terminalSlot=false,string frameArt="cargo/FRAME00",string filter="All",bool enabled=true,bool dim=false,bool compact=false,bool weaponAmmoCount=true)
    {
        if(terminalSlot){if(enabled)Hit(id,r);}else Draw(frameArt,r,enabled?id:null);
        if (item is null) return;
        if (_owner is uint owner) _items[id]=new(owner,_ship,item.Inventory,item.Slot,item.TemplateId){TradeRevision=_playerTrade?.Revision,AmmoLayer=item.IsAmmoLayer,HarvestableId=item.Inventory==0x12?Data?.HarvestableId:null,HarvestableOwner=item.Inventory==0x12?Data?.HarvestableOwner??0:0,HulkId=item.Inventory is 5 or 6?Data?.HulkId:null,HulkOwner=item.Inventory is 5 or 6?Data?.HulkOwner??0:0};
        string color=SlotColor(item);
        bool dragSource=_draggingItem && _dragItem is {} drag && drag.Inventory==item.Inventory && drag.Slot==item.Slot && drag.TemplateId==item.TemplateId && drag.IsAmmoLayer==item.IsAmmoLayer;
        bool manufacturingDisplay=item.Inventory is 12 or 13;
        // 670B20 -> 66C2D0/66C360 changes the icon mesh on press/release.
        // Hover leaves RGB alone; 6A8A40 keeps the category frame a sibling.
        float brightness=manufacturingDisplay?ManufacturingSlotBrightness(item):
            dragSource || _pressed==id && _pressedItem is not null?.35f:dim?.18f:1;
        if(item.ItemType!=uint.MaxValue && (!terminalSlot || item.Inventory is 16 or 17) && Texture("icon_"+color+".tga") is {} frame) _images.Add(new(frame,(Screen(r))){Brightness=dim?.3f:1});
        if (Icon(item.IconBaseAsset) is { } icon) _images.Add(new(icon,(Screen(new(r.X+1,r.Y+2,r.Width-2,r.Height-4)))) {Brightness=brightness});
        if(!manufacturingDisplay)ItemRestriction(item,new(r.X+1,r.Y+2,r.Width-2,r.Height-4));
        // A compact (Next Modern UI) slot keeps its overlay text in proportion; retail slots keep the authored rows.
        double overlay=compact?r.Height*.26:9;EnbRgb overlayColor=dim?new(0,.35f,0):new(0,1,0);
        // Modern UI slots draw these in the Modern UI font, centred and pixel-aligned like the rest of that window.
        void Overlay(string key,string value,Rect area){if(compact){var screen=Screen(area);ModernText(key,value,screen,overlayColor,r.Height*_size.Height/480*.22,1);}else Text(key,value,area,overlayColor,"FONT_TINY");}
        Overlay(id+"level",Roman(weaponLevel??item.Level),new(r.X,r.Y,r.Width,compact?r.Height*.2:overlay));
        int count=item.Count;
        if (item.Inventory==2 && item.IsWeapon) count=weaponAmmoCount?Data?.Ammo.FirstOrDefault(a=>a.Slot==item.Slot)?.Count ?? 0:0;
        if (!manufacturingDisplay && (count>1 || item.Inventory==2 && item.IsWeapon && count>0)) Overlay(id+"count",count.ToString(CultureInfo.InvariantCulture),new(r.X,r.Bottom-overlay-(compact?r.Height*.04:1),r.Width,overlay));
        _tips[id]=manufacturingDisplay?SlotTip(EnbRetailItemTooltip.ManufacturingDescription(item)):ItemTip(item);
        InventoryFilterMask(item,r,filter);
        if(_trading && item.Inventory==1 && _tradeSelection is {Inventory:1} selection && selection.Slot==item.Slot)
        {
            const string borderKey="trade-selection-red";
            if(!_icons.TryGetValue(borderKey,out var red) || red is null)_icons[borderKey]=red=new(1,1,ImmutableArray.Create<byte>(0,0,255,255));
            double thickness=1/_scale;
            foreach(var edge in new[]{new Rect(r.X,r.Y,r.Width,thickness),new Rect(r.X,r.Bottom-thickness,r.Width,thickness),new Rect(r.X,r.Y,thickness,r.Height),new Rect(r.Right-thickness,r.Y,thickness,r.Height)})
                _images.Add(new(red,(Screen(edge))));
        }
    }
    private string SlotTip(string description)=>(_inventorySlotTips?.GetValueOrDefault("NORMAL_SLOT","")??"").Replace("\\n","\n").Replace("%s",description);
    // 0068CCC0: an equipment slot's tooltip reads the primary binding, so a loaded weapon names the weapon, not its ammunition.
    private string ItemTip(EnbSceneItemSnapshot item)=>SlotTip(EnbRetailItemTooltip.Description(item.IsAmmoLayer&&Data?.Find(item.Inventory,item.Slot) is {} weapon?weapon:item));
    private static string SlotColor(EnbSceneItemSnapshot item)=>item.ItemType switch { 2 or 6 or 7 or 11=>"grey",10 or 14 or 15 or 16=>"red",18=>"blue",
        _=>EnbItemInformation.Matches(item,10)?"yellow":EnbItemInformation.Matches(item,11)?"green":"brown" };
    internal static string Roman(int n) => n is >=1 and <=20 ? RomanLevels[n-1] : ""; // 0068FA30 table 00B90138: I..XX (INV-D88)
    private static readonly string[] RomanLevels = ["I","II","III","IV","V","VI","VII","VIII","IX","X","XI","XII","XIII","XIV","XV","XVI","XVII","XVIII","XIX","XX"];
    private void Equip()
    {
        Flush();
        var r=Native("equip/NEW_FRAME"); Draw("equip/NEW_FRAME",r); Hit("equipFrame",r);
        Draw("options/EXIT",Native("equip/EXIT"),"equipClose");
        CargoLabel("equip/Ship_Name","shipName",_scene?.Objects.FirstOrDefault(o=>o.IsLocalShip)?.NameDecal?.Name??Data?.ShipStats.GetValueOrDefault("Name","")??"");
        // SHIP's BLACK texture is blended artwork, not an opaque backing.
        // Clear the panel rectangle before drawing the authored bevel.
        Draw("equip/FRAME_DETAIL",Native("equip/FRAME_DETAIL"));
        var shipView=Native("equip/SHIP");
        var shipFrame=Native("equip/FRAME_DETAIL");
        // SHIP extends beneath the bottom equipment slots; keep its projection,
        // but composite only inside the blue panel and its matching top/bottom inset.
        var shipClip=shipView.Intersect(new Rect(shipView.X,shipView.Y,shipView.Width,
            shipFrame.Bottom-(shipView.Top-shipFrame.Top)-shipView.Top));
        _draws[^1] = _draws[^1] with { OpaqueBlackBacking = true, EquipmentPreview = _scene, EquipmentBounds = (Screen(shipView)), EquipmentClipBounds = (Screen(shipClip)), EquipmentView = _previewView, EquipmentRotation = _previewRotation };
        Hit("shipPreview",Native("equip/SHIP"));
        foreach(string title in new[]{"Weapons","Devices","Reactor","Shields","Engines"})
            CargoLabel("equip/Title_"+title,"equipmentTitle"+title);
        EquipmentSlots();
        CargoLabel("equip/TITLE00_TSI07A","shipstats");
        foreach(var (key,title,valueId) in new[]{("HullPoints","HullTitle","HullStrength"),("Defense","DefenseTitle","Defense"),("MissileDefense","MissileTitle","MissileDefense"),("Speed","SpeedTitle","Speed"),("WarpSpeed","WarpTitle","WarpSpeed"),("TurnRate","TurnTitle","TurnRate"),("ScanRange","ScanRangeTitle","ScanRange"),("Visibility","VisibilityTitle","Visibility")})
        {
            string value=Data?.ShipStats.GetValueOrDefault(key,"")??"";
            if(key=="HullPoints") value+="/"+(Data?.ShipStats.GetValueOrDefault("MaxHullPoints","")??"");
            if(key is "ScanRange" or "Visibility" && double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out double n)) value=(n/1000).ToString("0.0",CultureInfo.InvariantCulture)+"k";
            CargoLabel("equip/"+title,"statlabel"+key);
            CargoLabel("equip/"+valueId,"statval"+key,value,color:StatColor(key));
        }
        EquipmentStatArrows();
        CargoLabel("equip/TITLE01_TSI07A","deflects");
        foreach(string key in new[]{"Impact","Explosive","Plasma","Energy","EMP","Chemical","Psionic"})
        { CargoLabel("equip/Resist"+key+"Title","resist"+key);
          CargoLabel("equip/Resist"+key,"resistVal"+key,Data?.ShipStats.GetValueOrDefault("Resist"+key,"")??"",color:StatColor("Resist"+key)); }
        Flush();
    }
    private EnbRgb StatColor(string key)
    {
        // Current client 006A34E0; polarity table initialized at 006A2DA0.
        var neutral=new EnbRgb(.33f,.33f,.99f);var bad=new EnbRgb(.66f,0,0);
        bool Number(string name,out double value)=>double.TryParse(Data?.ShipStats.GetValueOrDefault(name,""),NumberStyles.Float,CultureInfo.InvariantCulture,out value);
        if(!Number(key,out var current))return neutral;
        if(key=="HullPoints")return Number("MaxHullPoints",out var max)&&current<(int)max/2?bad:neutral;
        if(!Number("Base."+key,out var baseline)||current==baseline)return neutral;
        return (current>baseline)==(key!="Visibility")?new(0,1,0):bad;
    }
    private void Details(EnbSceneItemSnapshot item)
    {
        Flush(); var attrs=EnbItemInformation.Attributes(item); var specials=EnbItemInformation.Specials(item,Data?.RequirementState);
        int ah=Math.Max(62,18+attrs.Length*10); double width=270, height=Math.Max(170,ah+95);
        _detailOrigin=new(Math.Clamp(_detailOrigin.X,0,640-width),Math.Clamp(_detailOrigin.Y,0,480-height));
        double top=_detailOrigin.Y, left=_detailOrigin.X;
        Frame("ITEM_EXAMINATION_MAIN",new(left,top,width,height),"detailFrame");
        Hit("detailDrag",new(left,top,width-16,38));
        Draw("options/EXIT",new(left+width-16,top,16,16),"detailClose");
        if(Icon(item.IconBaseAsset) is { } icon) _images.Add(new(icon,(Screen(new(left+3,top+3,37,32)))));
        Text("itemName",item.Name,new(left+40,top+3,width-58,18),new(1,1,1),"FONT_MED");
        Text("itemLevel",$"Level {item.Level} {EnbItemInformation.TypeName(item.Subcategory)}",new(left+40,top+24,width-58,13),new(0,1,0));
        double ax=left+3, ay=top+40, aw=158, sx=ax+161, sw=width-167;
        Frame("ITEM_EXAMINATION_ATTRIBUTES",new(ax,ay,aw,ah),"attributeFrame");
        Text("attributes","Attributes",new(ax,ay+2,aw,12),new(0,1,0));
        int row=0; foreach(var (name,value) in attrs)
        { Text("an"+row,name,new(ax+4,ay+17+row*10,85,10),null,"FONT_TINY",false);
          Text("av"+row,value,new(ax+90,ay+17+row*10,aw-94,10),null,"FONT_TINY",false);
          string help=EnbItemInformation.AttributeHelpAt(item,row);
          if(help.Length>0){string id="attribute/"+row;Hit(id,new(ax+4,ay+17+row*10,aw-8,10));_tips[id]=help;}
          row++; }
        Frame("ITEM_EXAMINATION_DESCRIPTION",new(ax,ay+ah+2,aw,height-ah-44),"descriptionFrame");
        Wrap("description",item.Description,new(ax+4,ay+ah+6,aw-8,height-ah-52),null,_hover=="descriptionFrame");
        Frame("ITEM_EXAMINATION_SPECIALS",new(sx,ay,sw,height-44-(item.HasAutoFire?34:0)),"specialFrame");
        Text("specials","Specials",new(sx,ay+2,sw,12),new(0,1,0));
        double specialY=ay+17, specialBottom=top+height-8-(item.HasAutoFire?34:0);
        for(int i=0;i<specials.Length&&specialY<specialBottom;i++)
        {
            var special=specials[i];string id="special/"+i;
            var bounds=new Rect(sx+4,specialY,sw-8,specialBottom-specialY);
            double used=Wrap(id,special.Text,bounds,special.Unmet?new(1,0,0):null); // FA_RED for an unmet row (INV-D04)
            if(used>0&&special.Tooltip.Length>0)
            {Hit(id,new(bounds.X,bounds.Y,bounds.Width,used));_tips[id]=special.Tooltip;}
            specialY+=used;
        }
        if(item.HasAutoFire)
        {
            Frame("ITEM_EXAMINATION_OPTIONS",new(sx,top+height-36,sw,32),"autoFrame");
            Text("itemOptions","Options",new(sx,top+height-35,sw,12),new(0,1,0));
            Draw(ShownAutoFire(item)?"check/on":"checks/CHECK00",new(sx+5,top+height-21,16,14),"auto",!_busy&&item.State is not null);
            Text("autoLabel","Auto Fire",new(sx+30,top+height-21,sw-34,14),null,"FONT_TINY",false);
            _tips["auto"]="When selected, weapon will fire automatically.";
        }
        if(_message is not null) Text("message",_message,new(left,top+height+2,width,12),new(1,.4f,.4f),"FONT_TINY");
        Flush();
    }
    private void Character()
    {
        Flush();
        for(int i=0;i<3;i++){var r=Native("character/BUTTON0"+i);Draw("character/BUTTON0"+i,r,"tab/"+i);}
        CharacterTabLabels();
        Draw("character/EXIT",Native("character/EXIT"),"characterClose");
        if(_characterTab==0)CharacterSkills();
        else if(_characterTab==1)CharacterMissions();
        else CharacterFactions();
        Flush();
    }
    private void CharacterSkills()
    {
        // 0054A200 resets expansion and selection on activation, retaining +E8.
        if(!_skillPanelActive){_skillPanelActive=true;_expanded=false;_skillVisibleUnits=5;_selectedSkill=-1;_skillDetail=null;}
        bool changing=_skillVisibleUnits!=(_expanded?10:5);
        _animatingText|=changing;
        _characterTextDrawn.Clear();
        Draw("skillFrame/NEW_FRAME",Native("skillFrame/NEW_FRAME"));Hit("characterFrame",Native("skillFrame/NEW_FRAME"));
        var stats=Data?.CharacterStats;
        int Stat(string key)=>int.TryParse(stats?.GetValueOrDefault(key,""),out var n)?n:0;
        if(_skillVisibleUnits<10)
        {
            // TSI12B CHAR_ATTRIB attaches the local TSI12A hierarchy at (-228.5,58).
            foreach(string mesh in new[]{"FRAME02","COMBAT_BAR","EXPLORE_BAR","TRADE_BAR","BARS _FRONT","BAR00"})
            {var key="bio/"+mesh;Draw(key,Native(key).Translate(new(-228.5,-58)));
             if(mesh=="FRAME02"&&_scene?.Objects.FirstOrDefault(o=>o.IsLocalShip) is {Avatar: {} avatar} portraitObject)
                 _draws[^1]=_draws[^1] with {Portrait=avatar,PortraitSharedActor=true,PortraitStationLocal=_scene.ActiveStarbaseId is not null,PortraitPose=_scene.ActiveStarbaseId is null?_callerPortrait.PoseFor(portraitObject.RenderIdentity):null,PortraitEnvironment=_scene.Environment,PortraitBounds=(Screen(Native("bio/PICTURE").Translate(new(-228.5,-58)))),PortraitAspect=Native("bio/PICTURE").Width/Native("bio/PICTURE").Height};
             float? progress=mesh switch {"COMBAT_BAR"=>_scene?.Hud.Experience.Combat,"EXPLORE_BAR"=>_scene?.Hud.Experience.Explore,"TRADE_BAR"=>_scene?.Hud.Experience.Trade,_=>null};
             if(progress is {} f){_draws[^1]=_draws[^1] with {Stage0UvOffset=new(0,.46f*f)};
                 Hit("xp/"+mesh,Native(key).Translate(new(-228.5,-58)));_tips["xp/"+mesh]=FormattableString.Invariant($"Progress: {f*100:0.00}%");}}
            AttachedText("characterName",stats?.GetValueOrDefault("Name","")??"",_biographyNameStyle,_biographyNameScroll,new(1,1,1));
            string cls=EnbNext.Client.Models.CharacterClass.RaceName((uint)Stat("Race"))+" "+EnbNext.Client.Models.CharacterClass.ClassName((uint)Stat("Race"),(uint)Stat("Profession"));
            // 005477C0/00547C20/00547FD0 replace authored labels with mode=0.
            CharacterOrdinaryText("CHARACTER_LEVEL_ATT","overall","Level: "+(Stat("CombatLevel")+Stat("ExploreLevel")+Stat("TradeLevel")));
            CharacterOrdinaryText("CHARACTER_CLASS_NAME_ATT","class",cls);
            CharacterOrdinaryText("CHARACTER_RANK_ATT","rank",stats?.GetValueOrDefault("Rank","")??"");
            CharacterOrdinaryText("CHARACTER_CREDITS_ATT","creditsBio","Credits: "+Data?.Credits?.ToString("N0",CultureInfo.InvariantCulture));
            CharacterOrdinaryText("EXP_DEBT_ATT","xpDebt",Stat("XPDebt")>0?"XP Debt: "+Stat("XPDebt").ToString(CultureInfo.InvariantCulture):"");
            foreach(var (key,label) in new[]{("CombatLevel","Combat"),("ExploreLevel","Exploration"),("TradeLevel","Trade")})
            {CharacterOrdinaryText(label,"xp"+key);CharacterOrdinaryText(label+"_val","xpval"+key,Stat(key).ToString());}
            // 0054A900 removes/reappends rows after the retained biography child.
            // 006D5920 gives biography text sort 16, equal to row backgrounds;
            // 0098DD50/0097D0A0 retain insertion order within that queue level.
            Flush();
        }
        if(_skillVisibleUnits is 5 or 10)Draw(_skillVisibleUnits==10?"skillFrame/MINUS":"skillFrame/PLUS_00",Native(_skillVisibleUnits==10?"skillFrame/MINUS":"skillFrame/PLUS_00"),"expand");
        _tips["expand"]=_expanded?"View character and skills.":"Expand skill list.";
        int firstBone=10-_skillVisibleUnits;
        var skills=Data?.Skills.Where(s=>s.VisibleInSkillPanel).ToImmutableArray()??[];
        double Height(EnbCharacterSkillSnapshot skill)=>_assets?.ResolveShortcutSkills()?.Abilities.Any(a=>a.Activated&&a.FamilyName.Equals(skill.Name,StringComparison.OrdinalIgnoreCase)&&a.MinimumLevel<=(_assets?.ResolveShortcutSkills()?.FindFamily(skill.Name)?.ClassMaximum((uint)Stat("Race"),(uint)Stat("Profession"))??0))==true?50:25;
        int visibleUnits=_skillVisibleUnits;
        int total=EnbSkillListRange.Total(skills.Select(s=>Height(s)>25).ToArray(),visibleUnits);
        if(_skillVisibleUnits is 5 or 10)SocialSlider("skills",total,visibleUnits,true);
        int bone=firstBone;
        var unavailableRows=new List<(string Art,Rect Bounds)>();
        foreach(var skill in skills.Skip(_skillPage))
        {
            var abilities=_assets?.ResolveShortcutSkills()?.Abilities.Where(a=>a.Activated&&a.FamilyName.Equals(skill.Name,StringComparison.OrdinalIgnoreCase)&&a.MinimumLevel<=(_assets?.ResolveShortcutSkills()?.FindFamily(skill.Name)?.ClassMaximum((uint)Stat("Race"),(uint)Stat("Profession"))??0)).OrderBy(a=>a.MinimumLevel).ThenBy(a=>a.Id).ToArray()??[];
            // Keep the next skill's name/rank band when its ability band does not fit.
            if(bone>=10)break;
            bool showAbilities=abilities.Length>0&&bone<9;
            string id="skill/"+skill.Id;
            var anchor=_guildAnchors[$"skillFrame/FILL_BONE{bone:00}"];
            string highlight=showAbilities?"skills/HIGHLIGHT01":"skills/HIGHLIGHT00";
            var r=_art[highlight].Design.Translate(new(anchor.X,anchor.Y));
            Draw(highlight,r,id,selected:skill.Id==_selectedSkill);
            CharacterOrdinaryText("SKILL_NAME_ATT"+bone.ToString(CultureInfo.InvariantCulture),id+"name",skill.Name);
            if(skill.Level<1)
            {
                string darken=showAbilities?"skills/DARKEN01":"skills/DARKEN00";
                unavailableRows.Add((darken,_art[darken].Design.Translate(new(anchor.X,anchor.Y))));
            }
            if(skill.ShowUpgradeIndicator)Draw("skills/UP00",_art["skills/UP00"].Design.Translate(new(anchor.X,anchor.Y)));
            for(int rank=0;rank<Math.Min(11,skill.Maximum);rank++)
            {
                string art=(rank<skill.Level?"notch/on/":"notch/off/")+"DOT00_"+rank.ToString("00",CultureInfo.InvariantCulture);
                var dot=_art[art].Design.Translate(new(anchor.X,anchor.Y));string notch=$"notch/{skill.Id}/{rank+1}";
                Draw(art,dot,notch);
                Hit(notch,dot);_tips[notch]=_assets?.ResolveShortcutSkills()?.FindFamily(skill.Name)?.LevelDescriptions.GetValueOrDefault(rank+1,"")??"";
            }
            for(int n=0;showAbilities&&n<Math.Min(7,abilities.Length);n++)
            {
                string box="skills/SKILL_BOX"+n.ToString("00",CultureInfo.InvariantCulture);
                Draw(box,_art[box].Design.Translate(new(anchor.X,anchor.Y)));
                var icon=_art["skills/ICON_BOX"+n.ToString("00",CultureInfo.InvariantCulture)].Design.Translate(new(anchor.X,anchor.Y));
                ImageTexture(skill.Icon,icon,_hover=="ability/"+abilities[n].Id?Vector3.One:abilities[n].IconTint(skill.Level));
                string abilityId="ability/"+abilities[n].Id;Hit(abilityId,icon);
                _tips[abilityId]=(_assets?.ResolveShortcutSkills()?.Describe(abilities[n],skill.Level)??"")+"\nDrag this icon to your shortcut bar to activate or configure.";
            }
            // 00664C60 wraps descriptions at 40 using 004FA800's unit-byte
            // width, before the tooltip's independent font-width wrapper.
            string description=_assets?.ResolveShortcutSkills()?.FindFamily(skill.Name)?.Description??skill.Description;
            _tips[id]=$"{(skill.Level>0?"Level "+skill.Level:"Unavailable")} {(abilities.Length>0?"Activated":"Passive")} Skill\n\n"
                +string.Concat(EnbRetailDialogText.Wrap(description,40,_=>1).Select(line=>line+"\n"))+"\nClick for description";
            bone+=abilities.Length>0?2:1;
        }
        CharacterOrdinaryText("SKILL_POINTS_ATT","points");
        CharacterOrdinaryText("SKILL_VAL_ATT","pointsval",Stat("SkillPoints").ToString());
        Flush();
        // 00663BE0 shows the authored darken mesh for level < 1. Attachment
        // 006D5920 derives the font sort from the row; 00662DC0 puts darken
        // one level after it in the descending 0097D0A0 queue.
        foreach(var row in unavailableRows)Draw(row.Art,row.Bounds);
        if(_skillDetail is not null && skills.FirstOrDefault(s=>s.Id==_selectedSkill) is {} selected)
        {
            Draw("skillInfo/FRAME_DETAIL",Native("skillInfo/FRAME_DETAIL"));Draw("skillInfo/BAR01",Native("skillInfo/BAR01"));Hit("skillDetailFrame",Native("skillInfo/FRAME_DETAIL"));
            Draw("skillInfo/EXIT",Native("skillInfo/EXIT"),"skillDetail");
            var family=_assets?.ResolveShortcutSkills()?.FindFamily(selected.Name);
            var titleColor=family?.Category switch {"Combat"=>new EnbRgb(.5f,1,.5f),"Trade"=>new(.75f,.25f,1),"Explore"=>new(.2f,.5f,1),_=>new(0,1,1)};
            AttachedText("skillTitle",selected.Name+(family?.Category is "Combat" or "Trade" or "Explore"?"":" Level "+selected.Level),_skillTitleStyle,_skillTitleScroll,titleColor);
            CharacterOrdinaryText("DESC_ATT","descTitle");CharacterWrappedText("WRAP_SKILL_DESC_ATT","skillDescription",family?.Description??selected.Description,selected);
            CharacterOrdinaryText(selected.Level==0?"NOT_ACQUIRED_ATT":"LVL_ATT","currentTitle");CharacterWrappedText("WRAP_LEVEL_DETAIL_ATT","skillCurrent",selected.Level==0?family?.AcquisitionDescription((uint)Stat("Race"),(uint)Stat("Profession"))??"":family?.LevelDescriptions.GetValueOrDefault(selected.Level,"")??"",selected);
            CharacterOrdinaryText("NEXT_LVL_ATT","nextTitle");CharacterWrappedText("WRAP_NEXT_LEVEL_DETAIL_ATT","skillNext",((selected.Availability?.Raw1??0) & 0x40)!=0?"You have already attained the maximum level for this skill.":family?.LevelDescriptions.GetValueOrDefault(selected.Level+1,"")??"",selected);
            CharacterOrdinaryText("POINTS_AVAILABLE_ATT","available","Skill points available: "+Stat("SkillPoints"));
            if(selected.Availability is {} availability)
            {
                CharacterOrdinaryText("UPGRADE_COSTS_ATT","upgradeCost",selected.HideUpgradeCost?"":$"Upgrade costs {availability.UpgradeCost} skill points");
                if(selected.CanTrain) {Draw("skillInfo/UPGRADE",Native("skillInfo/UPGRADE"),"train",!_busy);CharacterOrdinaryText("UPGRADE_BUTTON_ATT","upgrade");}
                else if(selected.UpgradeReason.Length>0)
                    CharacterOrdinaryText("UPGRADE_REQUIRES_ATT","required",selected.UpgradeReason);
            }
        }
        foreach(string key in _characterTextRows.Keys.Where(k=>!_characterTextDrawn.Contains(k)).ToArray())_characterTextRows.Remove(key);
    }
    private void CharacterMissions()
    {
        _characterTextDrawn.Clear();
        Draw("missions/NEW_FRAME",Native("missions/NEW_FRAME"));Hit("characterFrame",Native("missions/NEW_FRAME"));
        var missions=Data?.Missions??[];_characterPage=Math.Clamp(_characterPage,0,Math.Max(0,missions.Length-3));
        // 005C4610 constructs the same GDSlider as the social lists; 005C4F1F
        // truncates its value to a row and rebuilds the three visible missions.
        SocialSlider("missions",missions.Length,3,true);
        var detail=SelectedMission;
        if(detail is null)_selectedMissionRow=-1;
        // Rebuilding the selected row also closes its pending confirmation.
        // Keep action requests captured to the mission that opened the dialog.
        if(detail!=_missionDetailSnapshot || _characterPage!=_missionDetailPage)
        {
            if(_confirmForfeit)_confirmation=null;
            _reveals.Clear();
            _missionDetailSnapshot=detail;_missionDetailPage=_characterPage;
        }
        for(int row=0;row<3;row++)
        {
            string suffix=row.ToString("00"),id="mission/"+(_characterPage+row);
            Draw("missions/HIGHLIGHT"+suffix,Native("missions/HIGHLIGHT"+suffix),id,selected:row==_selectedMissionRow);
            var m=missions.ElementAtOrDefault(_characterPage+row);
            CharacterOrdinaryText("Time"+suffix+"_TSI22A",id+"time",m?.TimerText(_scene?.ClientTime??0)??"");
            CharacterOrdinaryText((row+1)+"_TSI22A",id+"num",m is null?"":(_characterPage+row+1).ToString(CultureInfo.InvariantCulture));
            if(m is null)continue;
            CharacterWrappedText("WRAP_Title"+suffix+"_TSI22A",id+"title",m.Name,m);
            CharacterWrappedText("WRAP_DESC"+suffix+"_TSI22A",id+"step",m.CurrentStep,m);
        }
        Flush();
        if(SelectedMission is {} selected)
        {
            Draw("missionInfo/NEW_FRAME01",Native("missionInfo/NEW_FRAME01"));Hit("missionDetail",Native("missionInfo/NEW_FRAME01"));
            CharacterWrappedText("WRAP_Title_Detail_TSI22A","missionTitle",selected.Name,selected);
            CharacterWrappedText("WRAP_DESC_DETAIL_TSI22A","WRAP_DESC_DETAIL_TSI22A",selected.Description,selected);
            // Next mission tracker: a Track button beside Forfeit/Clear, in the same authored art.
            if(MissionTrackingEnabled&&!selected.CanClear)
            {
                // Bottom-left of the detail pane, clear of the title row that Forfeit/Clear shares.
                var forfeit=Native("missionInfo/FORFEIT");var pane=Native("missionInfo/NEW_FRAME01");
                var track=new Rect(pane.X+10,pane.Bottom-forfeit.Height-10,forfeit.Width,forfeit.Height);
                bool tracked=IsMissionTracked?.Invoke(selected.Id)==true;
                Draw("missionInfo/FORFEIT",track,"track");
                Text("trackLabel",tracked?"Untrack":"Track",track,_hover=="track"?new(1,1,0):new(.4f,1,.4f),"FONT_TINY");
            }
            if(selected.CanForfeit||selected.CanClear)
            {
                Draw("missionInfo/FORFEIT",Native("missionInfo/FORFEIT"),"forfeit",!_busy);
                Text("forfeitLabel",selected.CanClear?"Clear":"Forfeit",Native("missionInfo/FORFEIT"),_hover=="forfeit"?new(1,1,0):new(1,0,0),"FONT_TINY");
            }
        }
        foreach(string key in _characterTextRows.Keys.Where(k=>!_characterTextDrawn.Contains(k)).ToArray())_characterTextRows.Remove(key);
    }
    private void CharacterFactions()
    {
        _characterTextDrawn.Clear();
        Draw("factions/NEW_FRAME",Native("factions/NEW_FRAME"));Hit("characterFrame",Native("factions/NEW_FRAME"));
        string Name(string key)=>!string.IsNullOrWhiteSpace(key)&&_assets?.Factions?.TryResolve(key,out var f)==true?f.Name:"";
        string affiliation=Data?.CharacterStats.GetValueOrDefault("Affiliation","")??"";
        CharacterOrdinaryText("AFFILIATED_TITLE_TSI30A","affiliation");
        CharacterOrdinaryText("AFFILIATED_TSI30A","affiliatedName",affiliation);
        CharacterOrdinaryText("HATED_TSI30A","hated");CharacterOrdinaryText("LOVED_TSI30A","loved");
        var factions=Data?.Factions??[];
        // 0058DC20 resets an invalid retained page to zero after list shrink.
        if(_factionPage!=0 && _factionPage+6>factions.Length)_factionPage=0;
        SocialSlider("factions",factions.Length,6,true);
        for(int row=0;row<6;row++)
        {
            if(factions.ElementAtOrDefault(_factionPage+row) is not {} f)break;
            string suffix=row.ToString("00"),id="faction/"+(_factionPage+row);
            Draw("factions/HIGHLIGHT"+suffix,Native("factions/HIGHLIGHT"+suffix),id);
            for(int n=1;n<=2;n++){string key="factions/BAR"+(row*2+n).ToString("00");Draw(key,Native(key));}
            var gauges=f.GaugeFractions;
            foreach(string side in new[]{"HATE","LOVE"})
            {
                string key="factions/"+side+"_BAR"+suffix;
                Draw(key,Native(key));
                _draws[^1]=_draws[^1] with {Stage0UvOffset=new(0,.46f*(side=="LOVE"?gauges.Love:gauges.Hate))};
            }
            CharacterOrdinaryText("NAME"+suffix+"_TSI30A",id+"name",Name(f.Name));_tips[id]="Reaction : "+f.Reaction.ToString("0.0",CultureInfo.InvariantCulture);
        }
        Flush();
        if(_selectedFaction is {} keyName)
        {
            Draw("factionInfo/NEW_FRAME01",Native("factionInfo/NEW_FRAME01"));Hit("factionDetail",Native("factionInfo/NEW_FRAME01"));
            Draw("factionInfo/EXIT01",Native("factionInfo/EXIT01"),"factionClose");
            var details=_assets?.Factions?.TryResolve(keyName,out var resolved)==true?resolved:null;
            CharacterWrappedText("WRAP_NAME_TSI30B","WRAP_NAME_TSI30B",details?.Name??"",keyName);
            CharacterWrappedText("WRAP_DESC_TSI30B","WRAP_DESC_TSI30B",details?.Description??"",keyName);
        }
        foreach(string key in _characterTextRows.Keys.Where(k=>!_characterTextDrawn.Contains(k)).ToArray())_characterTextRows.Remove(key);
    }
    private void Confirmation()
    {
        if(_confirmForfeit&&_forfeitDialog is {} dialog)
        {
            string[] headers=[dialog.GetValueOrDefault("Header0",""),dialog.GetValueOrDefault("Header1","")];
            Flush();StandardConfirmationDialog("missionConfirm",string.Join('\n',headers),dialog,["confirmYes","confirmNo"],
                qualifyActions:false,origin:_confirmationOrigin,authoredLines:headers);Flush();return;
        }
        Flush();Frame("ITEM_EXAMINATION_MAIN",new(236,128,153,72),"confirmFrame");
        Wrap("confirmText","Are you sure you want to forfeit this mission?",new(240,132,145,40),null,false,"FONT_MED");
        Button("confirmYes","Yes",new(240,176,71,20));Button("confirmNo","No",new(313,176,71,20));Flush();
    }
    private void PreviewCursor(string resource,Vector3? tint=null,int? squarePixels=null)
    {
        if(Texture(resource) is not {} source)return;
        var raster=source;Point hotspot=default;
        if(tint is {} color)
        {
            if(!_art.TryGetValue("dragItem",out var art)||art.Design.Width<=0)throw new InvalidDataException("Shortcut icon geometry unavailable.");
            var size=art.Design;
            int w=squarePixels??Math.Max(1,(int)Math.Round(size.Width*_size.Width/640*_scale));
            int h=squarePixels??Math.Max(1,(int)Math.Round(size.Height*_size.Height/480*_scale));
            string key=$"cursor/{resource}/{tint}/{_size}/{_scale}/{squarePixels}";
            if(!_icons.TryGetValue(key,out var prepared)||prepared is null)
            {
                var pixels=new byte[w*h*4];var input=source.BgraPixels.AsSpan();
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                {
                    int at=(Math.Min(source.Height-1,y*source.Height/h)*source.Width+Math.Min(source.Width-1,x*source.Width/w))*4;
                    int target=(y*w+x)*4;
                    pixels[target]=(byte)Math.Clamp(input[at]*color.Z,0,255);pixels[target+1]=(byte)Math.Clamp(input[at+1]*color.Y,0,255);
                    pixels[target+2]=(byte)Math.Clamp(input[at+2]*color.X,0,255);pixels[target+3]=input[at+3];
                }
                _icons[key]=prepared=new(w,h,ImmutableArray.Create(pixels));
            }
            raster=prepared;
            hotspot=squarePixels is not null?new(w/2,h/2):new(Math.Clamp((int)Math.Round(-size.X/size.Width*w),0,w-1),Math.Clamp((int)Math.Round(-size.Y/size.Height*h),0,h-1));
        }
        PreviewCursorRequested?.Invoke(raster,hotspot);
    }
    private void ImageTexture(string resource,Rect r,Vector3? tint=null)
    {
        if(Texture(resource) is not {} image)return;
        if(tint is {} c&&c!=Vector3.One)
        {
            string key=resource+c;
            if(!_icons.TryGetValue(key,out var colored))
            {
                var bytes=image.BgraPixels.ToArray();
                for(int n=0;n<bytes.Length;n+=4){bytes[n]=(byte)Math.Clamp(bytes[n]*c.Z,0,255);bytes[n+1]=(byte)Math.Clamp(bytes[n+1]*c.Y,0,255);bytes[n+2]=(byte)Math.Clamp(bytes[n+2]*c.X,0,255);}
                _icons[key]=colored=new(image.Width,image.Height,ImmutableArray.Create(bytes));
            }
            image=colored!;
        }
        _images.Add(new(image,(Screen(r))));
    }

    private void Hit(string id,Rect r)
    {
        if(!_hits.TryGetValue(id,out var hit))_hits[id]=hit=new();
        if(!hit.IsVisible)hit.Order=++_hitCount;
        hit.ZIndex=_hitCount;
        hit.IsVisible=true;hit.LayoutGroup=_layoutGroup;hit.Design=r;hit.Bounds=Screen(r);
    }
    internal async Task Activate(string id,bool right=false,EnbInventoryItemRequest? item=null)
    {
        if(ActivateModernScroll(id)){ClearHover();Publish();return;}
        if(id.StartsWith("stationPlayer/",StringComparison.Ordinal))
        {if(_interactive)await ActivateStationPlayer(id);return;}
        if(id.StartsWith("socialSlider/",StringComparison.Ordinal))return;
        if(_reloadItem is {} reload)
        {
            _reloadItem=null;
            if(!right)await CompleteQuantity(reload);
            Publish();return;
        }
        if(_destroyItem is {} destroy)
        {
            if(id=="destroyNo")_destroyItem=null;
            else if(id=="destroyYes"){_destroyItem=null;await MoveItem(destroy);}
            Publish();return;
        }
        if(_interactive&&await ActivateConfirmedDialog(id)){ClearHover();Publish();return;}
        if(!_interactive||_confirmation is not null && id is not ("confirmYes" or "confirmNo")) return;
        if(_creditEntry){await ActivatePlayerTrade(id);Publish();return;}
        if(ActivateRemoteShortcut(id,right)){ClearHover();Publish();return;}
        if(await ActivateGroupFinder(id,right)){ClearHover();Publish();return;}
        if(await ActivateFriends(id,right)){ClearHover();Publish();return;}
        if(await ActivateGuild(id,right)){ClearHover();Publish();return;}
        if(await ActivateGroup(id,right)){ClearHover();Publish();return;}
        if(id=="targetPortraitTalk")
        {
            // 56BEF0 resolves the selected player, independently of the
            // portrait's temporary caller (563910).
            if(!right && _scene?.ActiveStarbaseId is null && _scene?.Objects.FirstOrDefault(o=>o.IsCurrentTarget && o.ObjectType==1) is {} peer)
                TargetPortraitChatRequested?.Invoke(EnbRetailObjectDisplayName.Recipient(peer));
            ClearHover();Publish();return;
        }
        if(id=="targetPortraitToggle")
        {
            if(!right && TargetPortrait is not null)
            {_targetPortraitCollapsed=!_targetPortraitCollapsed;_callerPortrait.Expanded=!_targetPortraitCollapsed;InteractionSoundRequested?.Invoke("TextClick01.wav");InteractionSoundRequested?.Invoke("vidscreen_1.wav");}
            ClearHover();Publish();return;
        }
        // Examination goes by slot (primary binding): a loaded weapon examines the weapon, not its ammunition.
        // The Modern UI ammo column is the one place the ammunition itself is examined.
        if(right && item is {AmmoLayer:true} ammoRequest && !id.StartsWith("mequip/ammo",StringComparison.Ordinal) && Data?.Find(ammoRequest.Inventory,ammoRequest.Slot) is {} loadedWeapon)
            item=ammoRequest with {AmmoLayer=false,TemplateId=loadedWeapon.TemplateId};
        if(right && item is { } request && Resolve(request) is not null)
        {
            // 00630070: the same slot again closes the examination (INV-D05).
            if(_examined is {} open && open.Inventory==request.Inventory && open.Slot==request.Slot){_examined=null;Publish();return;}
            _detailOrigin=new(_point.X*640/_size.Width,_point.Y*480/_size.Height);_examined=request;_message=null;Opened?.Invoke();Publish();return;
        }
        if(ModernTalkMenu(id,right)){ClearHover();Publish();return;}
        // Next Modern UI vendor: an item clicked in the Modern inventory is chosen to sell (as a cargo click in retail).
        if(!right && ModernUi && _trading && item is {Inventory:1} sale && id.StartsWith("minv/",StringComparison.Ordinal) && _tradeConfirmation is null)
        {_tradeSelection=sale;InteractionSoundRequested?.Invoke("fontclick.wav");ClearHover();Publish();return;}
        if(ActivateModernVendor(id)){ClearHover();Publish();return;}
        if(await ActivateModernInventory(id,right)||ActivateModernEquipment(id,right)||await ActivateModernSkills(id,right)||await ActivateModernMissions(id,right)||ActivateModernFactions(id,right)||ActivateModernCommands(id,right)||await ActivateModernLocal(id,right)||await ActivateModernVault(id,right)||ActivateModernStationMap(id,right)||ActivateModernMusic(id,right)||ActivateModernWiki(id,right)||ActivateModernItems(id,right)||ActivateModernLog(id,right)||ActivateModernOptions(id,right)||ActivateModernQuit(id)){ClearHover();Publish();return;}
        if(right && id is not ("sort" or "vaultSort")) return;
        if(await ActivateRecovery(id)){ClearHover();Publish();return;}
        if(await ActivatePlayerTrade(id)){ClearHover();Publish();return;}
        if(ActivateRemoteHarvest(id)){ClearHover();Publish();return;}
        if(await ActivateLoot(id,item)){ClearHover();Publish();return;}
        if(await ActivateTerminal(id)){ClearHover();Publish();return;}
        if(await ActivateConversation(id,item)){ClearHover();Publish();return;}
        switch(id)
        {
            case "train":
                if(!_busy&&_owner is uint trainOwner&&Data?.Skills.FirstOrDefault(s=>s.Id==_selectedSkill) is {CanTrain:true} skill && TrainRequested is {} train)
                {
                    // Retail00548610 binds Upgrade directly to action2;
                    //00548C50 plays this cue and queues training without another dialog.
                    InteractionSoundRequested?.Invoke("skill_train_1.wav");
                    var trainingRequest=new EnbCharacterActionRequest(trainOwner,_ship,skill.Id,skill.Level);
                    await Send(()=>train(trainingRequest));
                }
                break;
            case "track":
                if(SelectedMission is {} trackedMission&&MissionTrackingEnabled)
                {InteractionSoundRequested?.Invoke("TextClick03.wav");MissionTrackToggled?.Invoke(trackedMission.Id);}
                break;
            case "forfeit":
                if(!_busy&&_owner is uint missionOwner&&SelectedMission is {} mission)
                {
                    if(mission.CanClear&&ClearMissionRequested is {} clear)
                    {
                        InteractionSoundRequested?.Invoke("TextClick03.wav");
                        var clearRequest=new EnbCharacterActionRequest(missionOwner,_ship,mission.Slot,mission.Id);
                        await Send(async()=>
                        {
                            bool sent=await clear(clearRequest);
                            if(sent&&clearRequest.Character==session()&&clearRequest.ShipOwner==_ship)_selectedMissionRow=-1;
                            return sent;
                        });
                    }
                    else if(mission.CanForfeit)
                    {
                        InteractionSoundRequested?.Invoke("TextClick03.wav");
                        _confirmation=new(missionOwner,_ship,mission.Slot,mission.Id);_confirmForfeit=true;
                        _confirmationOrigin=new(_point.X*640/_size.Width,_point.Y*480/_size.Height);
                        InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
                    }
                }
                break;
            case "confirmNo":_confirmation=null;break;
            case "confirmYes":
                if(_confirmation is {} confirmed&&confirmed.Character==_owner&&confirmed.ShipOwner==_ship)
                {
                    var action=_confirmForfeit?ForfeitRequested:TrainRequested;
                    bool forfeit=_confirmForfeit;
                    _confirmation=null;
                    if(action is not null)await Send(async()=>
                    {
                        bool sent=await action(confirmed);
                        // Retail 005C66D0 queues 0086, then requests this cue.
                        if(sent && forfeit && confirmed.Character==session() && confirmed.ShipOwner==_ship)
                        {
                            InteractionSoundRequested?.Invoke("Mission_Forfeited");
                            _selectedMissionRow=-1;
                        }
                        return sent;
                    });
                }
                break;
            case "cargoClose":if (BridgePanelRequested?.Invoke(0) == false) return; ClearTerminal();_cargo=_equip=_filterOpen=_vault=false;break;
            case "vault":_vault=!_vault;break;
            case "vaultClose":_vault=false;break;
            case "vaultFilter":_vaultFilterOpen=!_vaultFilterOpen;break;
            case "vaultPrev":_vaultPage=Math.Max(0,_vaultPage-1);break;
            case "vaultNext":_vaultPage++;break;
            case "vaultSort":
                if(!_busy && _vault && _scene?.ActiveStarbaseId is not null && _owner is uint vaultOwner && VaultSortRequested is {} vaultSort)
                {
                    if(right)_vaultReverse=!_vaultReverse;else _vaultSort=(_vaultSort+1)%3;
                    InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
                    int mode=_vaultSort;bool reverse=_vaultReverse;
                    ReportInventorySort(vaultOwner,true,mode,reverse);
                    await Send(()=>vaultSort(vaultOwner,_ship,mode,reverse));
                    PersistInventorySort(vaultOwner,true,mode,reverse,right);
                }
                break;
            case "equipClose":_equip=false;break;
            case "characterClose":if (BridgePanelRequested?.Invoke(0) == false) return; _character=false;break;
            case "detailClose":_examined=null;break;
            case "skillDetail":InteractionSoundRequested?.Invoke("Roll_Select02b.wav");_skillDetail=null;break;
            case "expand":_expanded=!_expanded;if(!_expanded)_biographyNameScroll.Clear();break;
            case "factionClose":_selectedFaction=null;break;
            case "equip":_equip=!_equip;_filterOpen=false;break;
            case "filter":_filterOpen=!_filterOpen;break;
            case "prev":_page=Math.Max(0,_page-1);break;
            case "next":_page++;break;
            case "pageTrack":
                SetPage(_point);break;
            case "sort":
                await SortCargo(right);
                break;
            case "auto":
                if(!_busy&&_examined is { } selected&&Resolve(selected) is { HasAutoFire:true,State:not null } selectedItem&&AutoFireRequested is { } auto)
                {
                    // 0051DEB0/0052B160: the checkbox toggles its own selection, shows it and requests it (INV-D50).
                    bool wanted=!ShownAutoFire(selectedItem);_autoFireChoice=(selected,wanted);
                    await Send(()=>auto(selected,wanted));
                }
                break;
            default:
                if(id.StartsWith("vaultFilter/"))SelectInventoryFilter(true,int.Parse(id.AsSpan(12)));
                if(id.StartsWith("filter/"))SelectInventoryFilter(false,int.Parse(id.AsSpan(7)));
                if(id.StartsWith("tab/")){_characterTab=int.Parse(id.AsSpan(4));_characterPage=0;_characterTextRows.Clear();_socialSliders.Remove("missions");_skillDetail=null;_selectedMissionRow=-1;_selectedFaction=null;_reveals.Clear();}
                if(id.StartsWith("mission/")||id.StartsWith("faction/"))_reveals.Clear();
                if(id.StartsWith("mission/")){int clicked=int.Parse(id.AsSpan(8))-_characterPage;if(_selectedMissionRow!=clicked)InteractionSoundRequested?.Invoke("TextClick03.wav");_selectedMissionRow=_selectedMissionRow==clicked?-1:clicked;}
                if(id.StartsWith("faction/"))
                {
                    int index=int.Parse(id.AsSpan(8)),row=index-_factionPage;
                    _characterTextRows.Clear();
                    if(row is >=0 and <6 && Data?.Factions.ElementAtOrDefault(index) is {} faction)
                    {
                        // 0058DB30 selects the visible slot, while 0058EFC0
                        // captures detail text. Scrolling does not retarget it.
                        _selectedFactionRow=_selectedFactionRow==row?-1:row;
                        _selectedFaction=_selectedFactionRow<0?null:faction.Name;
                    }
                }
                if(id.StartsWith("notch/")){ClearSkillDetailText();_selectedSkill=int.Parse(id.Split('/')[1]);_skillDetail=Data?.Skills.FirstOrDefault(s=>s.Id==_selectedSkill)?.Description;}
                if(id.StartsWith("ability/") && _character && _assets?.ResolveShortcutSkills()?.Abilities.FirstOrDefault(a=>a.Id==int.Parse(id.AsSpan(8))) is {} ability)
                {ClearSkillDetailText();var family=Data?.Skills.FirstOrDefault(s=>s.Name.Equals(ability.FamilyName,StringComparison.OrdinalIgnoreCase));_selectedSkill=family?.Id??-1;_skillDetail=family?.Description;}
                if(id.StartsWith("skill/")){ClearSkillDetailText();InteractionSoundRequested?.Invoke("fontclick.wav");_selectedSkill=int.Parse(id.AsSpan(6));}
                if(id.StartsWith("skill/"))_skillDetail=Data?.Skills.FirstOrDefault(s=>s.Id==int.Parse(id.AsSpan(6)))?.Description;
                break;
        }
        ClearHover();Publish();
    }
    private void SetPage(Point p)
    { _point=p;_page=(int)Math.Round(Math.Clamp((p.X*640/_size.Width-41.5)/51,0,1)*(_pageCount-1)); }
    private async Task Send(Func<Task<bool>> action, Func<bool>? current = null)
    {
        var owner=_owner; var ship=_ship;
        _busy=true;_message=null;Publish();
        try { if(!await action()&&owner==session()&&ship==_ship&&current?.Invoke()!=false)_message="Action unavailable at this time."; }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or OperationCanceledException){if(owner==session()&&ship==_ship&&current?.Invoke()!=false)_message="Action could not be completed.";}
        finally{_busy=false;}
    }
    private void Text(string id,string text,Rect r,EnbRgb? color=null,string fontId="FONT_SMALL",bool center=true,Rect? clip=null,double? revealPixels=null)
    {
        if(Font(fontId) is not { } f)return;
        if(!_labels.TryGetValue(id,out var label))_labels[id]=label=new();
        label.SetText(text,f,color??new(1,1,1));label.SetDisplayScale(1,1);
        if(label.Raster is not { } raster)return;
        var rect=Screen(r);
        if(clip is null)label.FitTo(rect.Size,_scale);
        double fit=Math.Min(1,rect.Width/Math.Max(1,label.Width));
        double w=label.Width*fit,h=label.Height*fit;
        var bounds=new Rect(rect.X+(center?(rect.Width-w)/2:0),rect.Y+(rect.Height-h)/2,w,h);
        if(revealPixels is {} pixels)
        {
            var region=clip??r;
            clip=region.Intersect(new Rect(bounds.X*640/_size.Width,region.Y,Math.Max(0,pixels/_scale)*640/_size.Width,region.Height));
        }
        if(clip is {} clipping)
        {
            var visible=bounds.Intersect(Screen(clipping));
            if(visible.Width<=0||visible.Height<=0)return;
            _images.Add(new(raster,(bounds)){Clip=(visible)});
            return;
        }
        _images.Add(new(raster,(bounds)));
    }

    private double Wrap(string id,string text,Rect r,EnbRgb? color=null,bool scroll=false,string fontId="FONT_TINY",double? revealElapsed=null,double wait=0,double timeSpacing=0,bool center=false,double step=9)
    {
        // Line breaks are measured with the actual installed retail font, rather than character-count estimates.
        if(Font(fontId) is not { } f)return 0;
        double available=Screen(r).Width/f.Scale;
        step*=_scale;
        if(!_wrappedText.TryGetValue(id,out var cached)||cached.Text!=text||!ReferenceEquals(cached.Font,f)||cached.Width!=available)
        {
        var wrapped=new List<string>();
        foreach(string paragraph in text.Replace("\\n","\n").Split('\n'))
        {
            string line="";
            foreach(string word in paragraph.Split(' '))
            {
                string next=line.Length==0?word:line+" "+word;
                if(line.Length>0&&f.MeasureWidth(next)>available){wrapped.Add(line);line=word;}else line=next;
            }
            wrapped.Add(line);
        }
        _wrappedText[id]=cached=(text,f,available,wrapped.ToArray());
        }
        var lines=cached.Lines;
        double overflow=Math.Max(0,lines.Length*step-r.Height);
        double offset=scroll?Math.Min(overflow,Math.Max(0,Environment.TickCount64-_hoverSince)*_scrollSpeed*.02):0;
        _animatingText|=scroll&&overflow>0&&offset<overflow;
        int first=(int)(offset/step);
        for(int n=first;n<lines.Length&&n*step-offset<r.Height;n++)
        {
            double? pixels=null;
            if(revealElapsed is {} elapsed)
            {
                double age=elapsed-wait-n*timeSpacing;
                pixels=age<0?0:Math.Max(1,Math.Truncate(age*_wipeSpeed*.01));
                _animatingText|=pixels<Screen(r).Width*_scale;
            }
            Text(id+n,lines[n],new(r.X,r.Y+n*step-offset,r.Width,step),color,fontId,center,r,pixels);
        }
        return Math.Min(lines.Length*step,r.Height);
    }
    private EnbRetailFontFace? Font(string id)
    {
        if(_assets is null||!EnbRetailFontFactory.TryResolveDefinition(_assets,id,(int)(_size.Width*_scale),(int)(_size.Height*_scale),out var d)||d.Resource is null)return null;
        if(_fonts.TryGetValue(d.Resource,out var f))return f;
        return EnbRetailFontFactory.TryCreate(_assets,d,out f)?_fonts[d.Resource]=f:null;
    }
    private EnbUiRaster? Icon(int id)
    {
        string? resource=_assets?.PresentationCatalog?.ResolveBaseAsset(id)?.Properties?.GetValueOrDefault("Icon");
        return Texture(resource);
    }
    private EnbUiRaster? Texture(string? resource)
    {
        if(string.IsNullOrWhiteSpace(resource))return null;
        resource=resource.Trim().Trim('"');if(_icons.TryGetValue(resource,out var raster))return raster;
        if(_assets!.Textures.Resolve(resource).Image is not { } image)return _icons[resource]=null;
        var pixels=ImmutableArray.CreateBuilder<byte>(image.Width*image.Height*4);
        foreach(var p in image.Pixels.Span){pixels.Add(p.B);pixels.Add(p.G);pixels.Add(p.R);pixels.Add(p.A);}
        return _icons[resource]=new(image.Width,image.Height,pixels.MoveToImmutable()){CompressedSource=image};
    }
    private void Hover(string id,Point point){if(_hover!=id){_dwell.Reset();_hoverSince=Environment.TickCount64;}_hover=id;_point=point;Publish();}
    private void ClearHover(){_hover=null;_dwell.Reset();if(_tooltipVisible)TooltipChanged?.Invoke(null);_tooltipVisible=false;}
    public void SetImmediateTooltips(bool value) { _dwell.Immediate = value; ShowTooltip(); }
    private void ShowTooltip()
    {
        if(_hover is not { } id||!_tips.TryGetValue(id,out var text)||!_frames.TryGetValue("TOOLTIPS",out var frame)||Font(frame.Definition.FontId) is not { } font){if(_tooltipVisible)TooltipChanged?.Invoke(null);_tooltipVisible=false;return;}
        if(!_dwell.IsReady(1,Environment.TickCount64,EnbCurrentRetailTooltipDelay.Milliseconds(_assets!.ResolvePlayerOptions(_owner).Options.InterfaceTooltipDelay)))return;
        var presentation=EnbTooltipContent.Create(frame,font,text,_size,_scale,new(0,1,0),LayoutOutput(id,_point),i=>
        {
            string key="tooltipLine"+i;
            if(!_labels.TryGetValue(key,out var part))_labels[key]=part=new();
            return part;
        });
        if(HoverCard(id,text) is {} card)presentation=presentation with {Card=card};
        _tooltipVisible=true;
        TooltipChanged?.Invoke(presentation);
    }
    private void DisposeAssets(){_layoutPanelPreview?.Dispose();_layoutPanelPreview=null;_terminalOrdinaryStyles.Clear();_terminalOrdinaryAlpha.Clear();_terminalOrdinaryScroll.Clear();_terminalListStyles.Clear();_characterOrdinaryWipes.Clear();_characterTextDefinitions.Clear();_characterTextRows.Clear();ClearVendorText();_lootAsset=null;_lootAnimation=null;_lootThumbAsset=null;_lootOpenedAt=null;ClearFinderAssets();AssetsReleased?.Invoke();_customNameInput?.Editor.Dispose();_customNameInput=null;_friendInput?.Editor.Dispose();_guildNameEditor?.Dispose();_guildGeneralEditor?.Dispose();_guildNameEditor=_guildGeneralEditor=null;_friendInput=null;CloseModernLog(true,false);_mlogInput=null;_mlogPageInput=null;_guildInput=null;_dragItemLayout=null;_dragItemPalette=null;_terminalColors.Clear();_terminalLabels.Clear();_terminalAssets.Clear();_terminalAnimations.Clear();_terminalPose.Clear();_terminalFrames.Clear();_terminalTooltips=null;_wrappedText.Clear();RestoreCursorRequested?.Invoke();_art.Clear();_frames.Clear();_hits.Clear();_icons.Clear();_fonts.Clear();foreach(var l in _labels.Values)l.Clear();_labels.Clear();}
    public void Dispose(){AnimationRequired?.Invoke(false);Suspend();DisposeAssets();_assets=null;}
}
