using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using EnbNext.Assets;
using EnbNext.Presentation;
using System.Numerics;
namespace EnbNext.Engine;
internal sealed partial class EnbInventoryUi
{
    internal IReadOnlyDictionary<string,EnbInventoryHit> Hits=>_hits;
    internal bool DraggingItemOrAbility=>_draggingItem||_draggingAbility;
    public bool IsVisible {get;private set;}
    internal event Action? TransportChanged,AssetsReleased,RestoreCursorRequested,HideCursorRequested;
    internal event Action<bool>? AnimationRequired;
    internal event Action<string?>? CaptureRequested;
    internal event Action<EnbUiTextInput>? TextInputCreated;
    internal event Action<EnbUiRaster,EnbUiPoint>? PreviewCursorRequested;
    internal Func<Uri,string,string,IEnbInventoryBrowser>? BrowserFactory {get;set;}
    internal void AdvanceAnimation(){AdvanceSkillPanel();Publish();}
    internal void PointerEnter(string id,Point point){FinderPointerAt(id,point);Hover(id,point);}
    internal void PointerExit(string id){if(id.StartsWith("finder",StringComparison.Ordinal))_finderHoverPoint=null;ClearHover();if(id.StartsWith("mfaction/row/",StringComparison.Ordinal)||id.StartsWith("mitems/row/",StringComparison.Ordinal))Publish();} // Hovered rows show their details: leaving one hides them.
    internal void PointerMove(string id,EnbInventoryPointer e){
                FinderPointerAt(id,e.Position);
                if(_reloadItem is not null){SetReloadQuantity(e.Position);Publish();return;}
                if(_pressed?.StartsWith("ability/")==true)
                {
                    _point=e.Position;
                    if(!_draggingAbility && e.IsLeftButtonPressed && Math.Abs(_point.X-_pressOrigin.X)+Math.Abs(_point.Y-_pressOrigin.Y)>4)
                    {_draggingAbility=true;InteractionSoundRequested?.Invoke("SR_ziprollover02.wav");}
                    if(_draggingAbility&&_assets?.ResolveShortcutSkills()?.Abilities.FirstOrDefault(a=>a.Id==int.Parse(_pressed.AsSpan(8))) is {} dragged)
                    {
                        var skill=Data?.Skills.FirstOrDefault(s=>s.Name.Equals(dragged.FamilyName,StringComparison.OrdinalIgnoreCase));
                        // Modern UI: an ability dragged from the skills window is the action bar's icon size, not the retail drag size.
                        if(skill is not null)PreviewCursor(skill.Icon,dragged.IconTint(skill.Level),ModernAbilityDragPixels());
                    }
                }
                else if(e.IsLeftButtonPressed && DragModernScroll(e.Position)){}
                else if(e.IsLeftButtonPressed && DragModernOptionSlider(e.Position)){}
                else if(_pressed=="mlocal/resize" && e.IsLeftButtonPressed){ResizeModernLocal(e.Position);Publish();}
                else if(IsModernResizeGrip(_pressed) && e.IsLeftButtonPressed){ResizeModernWindow(e.Position);Publish();}
                else if(IsModernWindowBody(_pressed) && e.IsLeftButtonPressed){DragModernInventory(e.Position);Publish();}
                else if(_pressedItem is {Inventory:1 or 2 or 3 or 12 or 14 or 16} && (e.IsLeftButtonPressed || e.IsRightButtonPressed))
                { _point=e.Position;BeginItemDrag(_point);Publish(); }
                else if(_pressed=="detailDrag")
                { var p=e.Position; _detailOrigin+=new Vector((p.X-_lastDrag.X)*640/_size.Width,(p.Y-_lastDrag.Y)*480/_size.Height);_lastDrag=p;Publish(); }
                else if(_pressed=="custom/VIEW")
                { DragCustomization(e.Position);Publish(); }
                else if(_pressed=="custom/HEAD_AREA")
                { _point=e.Position;MoveCustomizationTattoo(_point);Publish(); }
                else if(_pressed=="custom/COLOR_PALETTE")
                { _point=e.Position;ChooseCustomizationColor(_point);Publish(); }
                else if(_pressed=="shipPreview")
                {
                    var buttons=e;
                    var point=e.Position;
                    if(buttons.PointerUpdateKind is EnbInventoryPointerChange.LeftButtonPressed or EnbInventoryPointerChange.RightButtonPressed)
                    { BeginEquipmentDrag(buttons.PointerUpdateKind==EnbInventoryPointerChange.RightButtonPressed);_lastDrag=point; }
                    else if(buttons.PointerUpdateKind is EnbInventoryPointerChange.LeftButtonReleased or EnbInventoryPointerChange.RightButtonReleased)
                    { _equipmentDragArmed=false;RestoreCursorRequested?.Invoke(); }
                    else MoveEquipmentPointer(point,buttons.IsLeftButtonPressed,buttons.IsRightButtonPressed);
                    Publish();
                }
                else if(_pressed?.StartsWith("socialSlider/",StringComparison.Ordinal)==true){SocialSliderMove(e.Position);}
                else if(_pressed is "lootThumb" or "lootTrack" or "lootPrevious" or "lootNext"){LootMove(e.Position);}
                else if(_pressed?.StartsWith("terminalSlider",StringComparison.Ordinal)==true){TerminalSliderMove(e.Position);}
                else if(_pressed?.StartsWith("finder",StringComparison.Ordinal)==true){FinderMove(e.Position);}
                else if(_pressed=="reloadTrack"){SetReloadQuantity(e.Position);Publish();}
                else if(_pressed=="pageTrack") { SetPage(e.Position);Publish(); }
                else Hover(id,e.Position);}
    internal bool PointerWheel(string id,EnbInventoryPointer e){bool handled=false;
                FinderPointerAt(id,e.Position);
                if(id.StartsWith("socialSlider/",StringComparison.Ordinal) && e.Delta.Y!=0){var key=id.Split('/')[1];SetSocialScroll(key,_socialSliders[key].Value+(e.Delta.Y<0?1:-1));handled=true;Publish();return true;}
                if(id.StartsWith("loot",StringComparison.Ordinal) && e.Delta.Y!=0){SetLootScroll(_lootScroll+(e.Delta.Y<0?1:-1));handled=true;ClearHover();Publish();return true;}
                if(id.StartsWith("finderList/",StringComparison.Ordinal) && _finderList) { SetFinderScroll(_finderScroll + (e.Delta.Y < 0 ? 1 : -1)); handled=true; Publish(); }
                else if(id.StartsWith("friends/",StringComparison.Ordinal) && !_friendEditing) { _friendsPage += e.Delta.Y < 0 ? 1 : -1; handled=true; Publish(); }
                else if(id.StartsWith("guild/",StringComparison.Ordinal) && !GuildEditing && !GuildConfirmationOpen)
                { if(_guildDisplay)_guildDisplayPage+=(e.Delta.Y<0?1:-1);else _guildPage+=(e.Delta.Y<0?1:-1);handled=true;Publish(); }
                else if(id=="shipPreview"){ZoomEquipment((float)e.Delta.Y*.05f);handled=true;Publish();}
                else if(ScrollModernMission(id,e.Delta.Y))handled=true;
                else if(ScrollModernFaction(id,e.Delta.Y))handled=true;
                else if(ScrollModernCommands(id,e.Delta.Y))handled=true;
                else if(ScrollModernLocal(id,e.Delta.Y))handled=true;
                else if(ScrollModernMusic(id,e.Delta.Y))handled=true;
                else if(ScrollModernItems(id,e.Delta.Y))handled=true;
                else if(ScrollModernLog(id,e.Delta.Y))handled=true;
                else if(ScrollModernOptions(id,e.Delta.Y))handled=true;
                else if(ScrollModernDetails(id,e.Delta.Y))handled=true;
                else if(ScrollModernVendor(id,e.Delta.Y))handled=true;
                else if(ScrollModernJobs(id,e.Delta.Y))handled=true;
                else if(ScrollModernVault(id,e.Delta.Y))handled=true;
                else if(VendorOpen && id.StartsWith("vendor",StringComparison.Ordinal) && _tradeConfirmation is null && e.Delta.Y!=0){SetSocialScroll("vendor",_vendorPage+(e.Delta.Y<0?1:-1));handled=true;ClearHover();Publish();}
                else if(_terminal?.Type is 0 or 2 or 3 && (id.StartsWith("terminal") || id.StartsWith("jobType")) && e.Delta.Y!=0){SetTerminalScroll(_terminalScroll+(e.Delta.Y<0?1:-1));handled=true;ClearHover();Publish();}
                else if(_terminal is not null && (id.StartsWith("terminal") || id.StartsWith("jobType"))){_terminalPage=Math.Max(0,_terminalPage+(e.Delta.Y<0?1:-1));handled=true;ClearHover();Publish();}
                else if(id.StartsWith("vault",StringComparison.Ordinal) && !_vaultFilterOpen){_vaultPage=Math.Max(0,_vaultPage+(e.Delta.Y<0?1:-1));handled=true;ClearHover();Publish();}
                else if(_character && _characterTab==1 && (id=="characterFrame" || id.StartsWith("mission/")) && e.Delta.Y!=0){SetSocialScroll("missions",_characterPage+(e.Delta.Y<0?1:-1));handled=true;Publish();}
                else if(_character && _characterTab==2 && (id=="characterFrame" || id.StartsWith("faction/")) && e.Delta.Y!=0){SetSocialScroll("factions",_factionPage+(e.Delta.Y<0?1:-1));handled=true;Publish();}
                else if(_character && _characterTab==0 && (id=="characterFrame"||id.StartsWith("notch/")||id.StartsWith("ability/")||id.StartsWith("skill/")) && e.Delta.Y!=0){SetSocialScroll("skills",_skillPage+(e.Delta.Y<0?1:-1));handled=true;Publish();}
                else if(id.StartsWith("cargo")||id=="pageTrack") { _page=Math.Clamp(_page+(e.Delta.Y<0?1:-1),0,_pageCount-1);handled=true;ClearHover();Publish(); }
        return handled;}
    internal void PointerPress(string id,EnbInventoryPointer e){
                 var kind=e.PointerUpdateKind;
                if(!_interactive || kind is not (EnbInventoryPointerChange.LeftButtonPressed or EnbInventoryPointerChange.RightButtonPressed)) return;
                FinderPointerAt(id,e.Position);
                if(CustomizationOpen)_customInFrontOfChat=true;
                if(kind==EnbInventoryPointerChange.LeftButtonPressed && (id.StartsWith("ability/",StringComparison.Ordinal)||_items.ContainsKey(id)))InteractionSoundRequested?.Invoke("TextClick03.wav");
                if(id=="shipPreview")BeginEquipmentDrag(kind==EnbInventoryPointerChange.RightButtonPressed);
                _point=e.Position; _pressOrigin=_point;_draggingAbility=false; _lastDrag=_point; if(id=="custom/VIEW"){_customDragTick=Environment.TickCount64;_customDragVelocity=Vector2.Zero;PreviewCursor("c_handgrab.tga");} _pressed=id; BeginModernScroll(id,e.Position); BeginModernOptionSlider(id,e.Position); if(ModernWindowOf(id) is {} pressedWindow)RaiseModern(pressedWindow); if(IsModernWindowBody(id))BeginModernDrag(id,e.Position); if(id=="mlocal/resize")BeginLocalResize(e.Position); if(IsModernResizeGrip(id))BeginModernResize(id,e.Position); _pressedItem=_items.GetValueOrDefault(id); ManufacturingSlotInput(.35f); _itemPressModifiers=e.Modifiers; _itemPressRight=kind==EnbInventoryPointerChange.RightButtonPressed; _captured=true;CaptureRequested?.Invoke(id); if(kind==EnbInventoryPointerChange.LeftButtonPressed){FinderPress(id);LootPress(id);TerminalSliderPress(id);SocialSliderPress(id);} ClearHover(); Publish();
                if(kind==EnbInventoryPointerChange.LeftButtonPressed && e.Modifiers==EnbKeyModifiers.None && IsLegacyGroupRowCommand(id)){_pressed=null;_ = PressGroupRow(id);}
                // 0064B230: either button held outside the symbol menu closes it at once, choosing nothing (GROUP-D23).
                else if(id=="group/CloseSymbols" && !ModernUi){_pressed=null;_groupSymbolOrigin=null;Publish();}}
    // GROUP-D20: 00595530/0066B910 invoke group-row and symbol-option commands on the
    // ordinary press; the captured release only restores appearance.
    private bool IsLegacyGroupRowCommand(string id)=>!ModernUi && id.StartsWith("group/",StringComparison.Ordinal) && id.AsSpan(6).IndexOf('/')>0;
    private async Task PressGroupRow(string id)
    {
        try{await Activate(id);}
        catch(Exception ex){System.Diagnostics.Trace.TraceWarning("Group row press failed: "+ex.Message);}
        Publish();
    }
    internal async Task PointerRelease(string id,EnbInventoryPointer e){
                if(_terminal?.Type is 5 or 6)TickCustomization();
                if(_pressed=="shipPreview")
                {
                    _equipmentDragArmed=false;RestoreCursorRequested?.Invoke();
                    var held=e;
                    if(held.IsLeftButtonPressed||held.IsRightButtonPressed){Publish();return;}
                }
                if(IsModernWindowBody(_pressed)&&EndModernDrag()){CancelPointer();Publish();return;}
                if(_pressed=="mlocal/resize"){if(ModernLocalSize is {} sized)ModernLocalResized?.Invoke(sized);CancelPointer();Publish();return;}
                if(IsModernResizeGrip(_pressed)){EndModernResize();CancelPointer();Publish();return;}
                 bool inside=_pressed==id&&_hits.TryGetValue(id,out var hit)&&hit.IsVisible&&hit.Bounds.Contains(e.Position);
                var item=_pressedItem;
                bool itemDragged=_draggingItem && item is not null && _dragItem is not null;
                var captured=_dragItem;
                bool dragged=_draggingAbility&&_pressed==id&&id.StartsWith("ability/")&&e.InitialButton==1;
                if(dragged||itemDragged)InteractionSoundRequested?.Invoke("TextClick02.wav");
                if(dragged&&_owner is uint character && SkillDropped?.Invoke(character,_ship,int.Parse(id.AsSpan(8)),LayoutOutput(id,e.Position))==true)
                    InteractionSoundRequested?.Invoke("shortcut_created_sn_1.wav");
                var screenPoint=LayoutOutput(id,e.Position);
                var loadedAmmo=itemDragged?ModernDragSubstitute(_pressed):null;
                CancelPointer();
                if(itemDragged)await DropItem(item!,captured!,screenPoint,loadedAmmo);
                else if(inside && !dragged && e.InitialButton==1 &&
                    await VendorQuickClick(id,item,_itemPressModifiers)) { }
                else if(inside && e.InitialButton==2 && item is not null && (_itemPressModifiers&(EnbKeyModifiers.Control|EnbKeyModifiers.Shift))!=0)
                    await QuickTransfer(item,(_itemPressModifiers&EnbKeyModifiers.Control)==0);
                else if(inside&&!dragged) await Activate(id,e.InitialButton==2,item);
                Publish();}
    internal void PointerCaptureLost(){_equipmentDragArmed=false;ManufacturingSlotInput(1);_finderRepeat=0;_pressed=null;_pressedItem=null;_captured=false;_draggingAbility=false;_draggingItem=false;_dragItem=null;RestoreCursorRequested?.Invoke();Publish();}
    private static ushort? ParseRetailKey(string name){ushort code=EnbRetailKeyIdentity.Code(name);return code==0?null:code;}
}
internal sealed class EnbInventoryHit {public EnbUiGroup LayoutGroup;public EnbUiRect Bounds,Design;public bool IsVisible;public int ZIndex,Order;}
internal enum EnbInventoryPointerChange {Other,LeftButtonPressed,RightButtonPressed,LeftButtonReleased,RightButtonReleased}
internal readonly record struct EnbInventoryPointer(EnbUiPoint Position,bool IsLeftButtonPressed,bool IsRightButtonPressed,EnbInventoryPointerChange PointerUpdateKind,EnbKeyModifiers Modifiers,byte InitialButton=0,EnbUiPoint Delta=default);
internal interface IEnbInventoryBrowser:IDisposable {
 bool IsVisible {get;set;} EnbUiRect Bounds {get;set;}
 event Action<string>? Failed;event Action? PageLoaded,CloseRequested;
 /// <summary>A request the page makes to the game (a page address on EnbInventoryUi.PageRequestHost; never loaded).</summary>
 event Action<Uri>? Requested;
 /// <summary>Runs a script in the page.</summary>
 void Script(string code);
}
internal static class EnbInventoryKeys {public const ushort None=0,Back=8,Tab=9,Enter=13,Escape=27,End=35,Home=36,Left=37,Right=39,Insert=45,Delete=46,D0=48,D9=57,NumPad0=96,NumPad9=105;}
