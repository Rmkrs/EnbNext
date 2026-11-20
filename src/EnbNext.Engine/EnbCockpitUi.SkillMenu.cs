using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbCockpitUi
{
    private object[] _menuGadgets=[];
    public event Action<int>? MenuRowsChanged;
    public event Action<int,Rect>? MenuBoundsChanged;
    public event Action<int?>? MenuCaptureRequested;
    private EnbShortcutSkillRequest? _menuRequest;
    internal bool SkillMenuOpen => _menuRequest is not null;
    internal int HitMenuRow(Point point)
    {for(int i=_menuBounds.Count-1;i>=0;i--)if(_menuBounds[i].Contains(point))return i;return -1;}
    private ImmutableArray<EnbRetailSkillAbility> _menuChoices = [];
    private readonly List<Rect> _menuBounds = [];
    private readonly List<EnbRetailText> _menuLabels = [];
    private EnbAuthoredUiAsset? _menuPanel, _menuCircle;
    private Rect _menuPanelDesign, _menuCircleDesign, _menuTextDesign;
    private string _menuFontId = "";
    private string? _menuSlotName;
    private bool _pressedRight;
    private int _menuPressed = -1;
    public event Action<EnbShortcutSkillRequest, int>? SkillLevelRequested;
    public event Action<ImmutableList<EnbUiModelDraw>>? SkillMenuChanged;

    // 0062C9B0/0058A4B0: over the menu the away time resets, over its shortcut it pauses, elsewhere it accumulates; past
    // 1000 ms away the menu closes (SKILL-D01).
    private int _menuWhere; private long _menuAway, _menuTickAt;
    internal void MoveMenuPointer(Point point)
    {
        if (_menuBounds.Any(r => r.Contains(point))) { _menuWhere = 0; _menuAway = 0; return; }
        if (_menuSlotName is { } name && Contains(name,point)) { _menuWhere = 1; Hover(name,point); }
        else { _menuWhere = 2; ClearHover(); }
    }
    internal void PressMenuPointer(int row,bool left)
    {
        _menuPressed=left ? row : -1; MenuCaptureRequested?.Invoke(row);
    }
    internal void CancelMenuCapture()
    {
        _menuPressed=-1; MenuCaptureRequested?.Invoke(null);
    }
    internal void ReleaseMenuPointer(int row,Point point,bool left)
    {
        if(row<0){CloseSkillMenu();return;}
        var captured=_menuRequest;
        bool select=left && _menuPressed==row && row<_menuBounds.Count && _menuBounds[row].Contains(point);
        if(select && captured is not null && _assets?.ResolveShortcutSkills() is {} skills &&
            EnbShortcutSkillActivation.ResolveSelection(captured,_menuChoices[row].Id,_shortcuts,_shortcutShipOwner,skills) is {} choice)
        { CloseSkillMenu(); SkillLevelRequested?.Invoke(captured,choice.Id); }
        else CancelMenuCapture();
    }

    private void OpenSkillMenu(string name)
    {
        CancelShortcutKeys();
        CloseSkillMenu();
        var slot = SlotOf(name);
        var request = EnbShortcutSkillActivation.Capture(_shortcuts, slot, _shortcutShipOwner);
        if (request is null || slot?.SkillLevel is not int level || _assets?.ResolveShortcutSkills() is not { } catalog) return;
        if (_menuPanel is null)
        {
            try
            {
            var panel = EnbAuthoredUiAsset.Load(_assets, "tsi01a_right.w3d");
            var circle = EnbAuthoredUiAsset.Load(_assets, "tsi01a_circle.w3d");
            if (panel is null || circle is null) return;
            var layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"fc_skillinfo.ini"));
            if (!layout.TryResolve("WRAP_SKILL_INFO_NAME_RIGHT",out var definition)) return;
            _menuFontId = definition.Properties.GetValueOrDefault("FontType","");
            Rect Design(EnbAuthoredUiAsset a, string mesh) => MeshBounds(a,mesh).Translate(-320,-240);
            _menuPanelDesign = Design(panel,"SKILL"); _menuCircleDesign = Design(circle,"SKILL00");
            _menuTextDesign = Design(panel,"TEXT");
            // The text attachment extends under the separately drawn circle.
            // Reserve that artwork and reuse the authored right-side text inset
            // for the small gap, preserving the text area's right edge for wrapping.
            double inset = Math.Max(0, _menuPanelDesign.Right - _menuTextDesign.Right);
            double textLeft = Math.Max(_menuTextDesign.X, _menuCircleDesign.Right + inset);
            _menuTextDesign = new(textLeft, _menuTextDesign.Y,
                Math.Max(0, _menuTextDesign.Right - textLeft), _menuTextDesign.Height);
            _menuPanel = panel with { Resource = panel.Resource.Copy().SetMeshes(panel.Resource.Meshes.Where(m => m.Name == "SKILL").ToArray()) };
            _menuCircle = circle with { Resource = circle.Resource.Copy().SetMeshes(circle.Resource.Meshes.Where(m => m.Name == "SKILL00").ToArray()) };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { Error = "Skill level artwork unavailable: " + ex.Message; return; }
        }
        _menuChoices = catalog.Choices(slot.Assignment!.FamilyName!, level);
        if (_menuChoices.IsEmpty) return;
        _menuRequest = request; _menuSlotName = name; _menuWhere = 1; _menuAway = 0; _menuTickAt = Environment.TickCount64;
        _menuGadgets=new object[_menuChoices.Length];
        for(int i=0;i<_menuGadgets.Length;i++)_menuGadgets[i]=new object();
        MenuRowsChanged?.Invoke(_menuChoices.Length);
        UpdateSkillMenu();
    }

    internal void HoverMenu(int row, Point point)
    {
        if (_menuRequest is null) return;
        if ((uint)row >= _menuBounds.Count || !_menuBounds[row].Contains(point)) { ClearHover(); return; }
        string name = "ABILITY_" + row;
        if (_hovered != name) _dwell.Reset();
        _hovered = name; _pointer = point; ShowTooltip();
    }

    internal bool CloseSkillMenu()
    {
        bool wasOpen = _menuRequest is not null;
        _menuRequest = null; _menuSlotName = null; _menuChoices = []; _menuBounds.Clear();
        CancelMenuCapture();
        _menuGadgets=[]; MenuRowsChanged?.Invoke(0);
        if (wasOpen) { ClearHover(); SkillMenuChanged?.Invoke([]); }
        return wasOpen;
    }

    private void UpdateSkillMenu()
    {
        if (_menuRequest is not { } request || _menuPanel is null || _menuCircle is null) return;
        long now = Environment.TickCount64;
        if (_menuWhere == 2) _menuAway += now - _menuTickAt;
        _menuTickAt = now;
        if (_menuAway > 1000) { CloseSkillMenu(); return; }
        var slot = Modern ? _shortcuts?.Slots.FirstOrDefault(s => s.Address == request.Assignment.Address) : VisibleSlot(request.Assignment.Address.Bar, request.Assignment.Address.Button);
        if (!_interactive || !Shown(request.Assignment.Address) ||
            EnbShortcutSkillActivation.Capture(_shortcuts,slot,_shortcutShipOwner) != request ||
            _assets?.ResolveShortcutSkills() is not { } catalog || slot?.SkillLevel is not int level ||
            !catalog.Choices(slot.Assignment!.FamilyName!,level).SequenceEqual(_menuChoices)) { CloseSkillMenu(); return; }
        var font = Font(_menuFontId);
        if (font is null) return;
        double sx = _size.Width / 640, sy = _size.Height / 480;
        double rowHeight = _menuPanelDesign.Height * sy;
        Rect anchor;
        // Modern UI action bar slots are already in screen space.
        if(IsModernSlot(_menuSlotName!)&&ModernSlotBounds?.Invoke(ModernIndex(_menuSlotName!)) is {} modernAnchor)
            anchor=new(modernAnchor.X/sx,modernAnchor.Y/sy,modernAnchor.Width/sx,modernAnchor.Height/sy);
        else
        {
        var authoredAnchor=_hitBounds[_menuSlotName!];
        var screenAnchor=UiLayout?.Apply(EnbUiGroup.Cockpit,new(authoredAnchor.X*sx,authoredAnchor.Y*sy,authoredAnchor.Width*sx,authoredAnchor.Height*sy))??new(authoredAnchor.X*sx,authoredAnchor.Y*sy,authoredAnchor.Width*sx,authoredAnchor.Height*sy);
        anchor=new Rect(screenAnchor.X/sx,screenAnchor.Y/sy,screenAnchor.Width/sx,screenAnchor.Height/sy);
        }
        double x = Math.Clamp(anchor.Center.X * sx, -_menuCircleDesign.X*sx,
            _size.Width-_menuPanelDesign.Right*sx);
        double y = Math.Max(-_menuCircleDesign.Y*sy,
            anchor.Top*sy - (_menuChoices.Length-1)*rowHeight - _menuPanelDesign.Bottom*sy);
        _menuBounds.Clear();
        var draws = ImmutableList.CreateBuilder<EnbUiModelDraw>();
        int labelIndex = 0;
        for (int i = 0; i < _menuChoices.Length; i++)
        {
            var choice = _menuChoices[i]; double rowY = y + i*rowHeight;
            Rect Place(Rect r) => new(x+r.X*sx,rowY+r.Y*sy,r.Width*sx,r.Height*sy);
            var panel = Place(_menuPanelDesign); var circle = Place(_menuCircleDesign);
            var bounds = panel.Union(circle);
            // Rows overlap icon artwork, but each pointer row has the authored panel height.
            var hitBounds = new Rect(bounds.X,panel.Y,bounds.Width,panel.Height);
            _menuBounds.Add(hitBounds);
            MenuBoundsChanged?.Invoke(i,hitBounds);
            var labels = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
            var textBounds = Place(_menuTextDesign);
            string remaining = choice.Name; double textY = textBounds.Y;
            while (remaining.Length > 0)
            {
                int length = remaining.Length;
                while (length > 1 && font.MeasureWidth(remaining[..length])*font.Scale > textBounds.Width) length--;
                if (length < remaining.Length && remaining.LastIndexOf(' ',length) is int space && space > 0) length=space;
                while (_menuLabels.Count <= labelIndex) _menuLabels.Add(new());
                var label = _menuLabels[labelIndex++]; label.SetText(remaining[..length],font,new(1,1,1));
                label.SetDisplayScale(1,1);
                if (label.Raster is { } raster) labels.Add(new(raster,new(textBounds.X,textY,label.Width,label.Height)));
                textY += label.Height; remaining = remaining[length..].TrimStart();
            }
            draws.Add(new(_menuPanel,0,(panel),_hovered == "ABILITY_"+i) { GadgetOwner=_menuGadgets[i], DesignBounds=(_menuPanelDesign), Images=labels.ToImmutable() });
            var icon = Icon(slot with { IconTint=choice.IconTint(level) });
            draws.Add(new(_menuCircle,0,(circle),false) { DesignBounds=(_menuCircleDesign),
                Images=icon is null ? [] : [new(icon,(circle))] });
        }
        SkillMenuChanged?.Invoke(draws.ToImmutable());
    }
}
