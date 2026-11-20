using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbCockpitUi
{
    public Func<EnbShortcutMoveRequest,bool>? MoveShortcutRequested {get;set;}
    public event Action<string>? ShortcutSoundRequested;
    private EnbShortcutMoveRequest? _shortcutDrag;
    private EnbSceneShortcutSlotSnapshot? _shortcutDragSlot;
    private Point _shortcutDragOrigin;
    private bool _shortcutDragging;
    private Point _shortcutDragPoint;
    private readonly EnbRetailText _dragLevelText = new();
    private EnbInterfaceLayoutCatalog? _dragLayout;
    public event Action<bool>? DragCursorChanged;
    public bool IsDragging => _shortcutDragging;

    private static string Roman(int n) => n is >=1 and <=20 ? RomanLevels[n-1] : ""; // 0068FA30 table 00B90138: I..XX (INV-D88)
    private static readonly string[] RomanLevels = ["I","II","III","IV","V","VI","VII","VIII","IX","X","XI","XII","XIII","XIV","XV","XVI","XVII","XVIII","XIX","XX"];

    private void PrepareShortcutDrag(string name,Point point)
    {
        var slot=SlotOf(name);
        if(Session() is not uint character||_shortcuts is null||slot is not {Resolution:EnbSceneShortcutResolution.Resolved,Assignment:{} source})return;
        _shortcutDrag=new(character,_shortcutShipOwner,_shortcuts.Saved,source,null);
        _shortcutDragSlot=slot;
        _shortcutDragOrigin=point;
        ShortcutSoundRequested?.Invoke("TextClick03.wav");
    }

    internal bool MoveShortcutPointer(string name,Point point)
    {
        if(!_pressedRight&&_shortcutDrag is not null&&_pressed)
        {
            if(!_shortcutDragging&&Math.Abs(point.X-_shortcutDragOrigin.X)+Math.Abs(point.Y-_shortcutDragOrigin.Y)>4&&
                _shortcutDragSlot is {} slot&&Icon(slot) is {} raster&&_iconBounds.Width>0)
            {
                DragCursorChanged?.Invoke(true);_shortcutDragging=true;ClearHover();
                ShortcutSoundRequested?.Invoke("SR_ziprollover02.wav");
            }
            if(_shortcutDragging){_shortcutDragPoint=point;return true;}
        }
        Hover(name,point);
        return false;
    }

    private EnbRetailShortcutAddress? ShortcutDropAddress(Point point)
    {
        if(Modern)
        {
            for(int i=0;i<ModernSlotNames.Length;i++)
                if(ModernSlotBounds?.Invoke(i) is {} slot&&slot.Contains(point))return ModernAddress(i);
            return null;
        }
        point=UiLayout?.Inverse(EnbUiGroup.Cockpit,point)??point;
        var design=new Point(point.X*640/_size.Width,point.Y*480/_size.Height);
        for(int bar=0;bar<2;bar++)for(int button=0;button<3;button++)
            if(_hitBounds.TryGetValue($"BTTN_{bar}_{button:00}",out var bounds)&&bounds.Contains(design))
                return new(bar,_pages.Group(bar),button);
        return null;
    }

    private void ClearShortcutDrag()
    {
        _shortcutDrag=null;_shortcutDragSlot=null;_shortcutDragging=false;DragCursorChanged?.Invoke(false);
        _dragLevelText.Clear();
    }

    private ImmutableArray<EnbUiTextDraw> BuildShortcutDrag()
    {
        if(!_shortcutDragging || _shortcutDragSlot is not {} slot || Icon(slot) is not {} icon)return [];
        double sx=_size.Width/640,sy=_size.Height/480;
        var images=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        images.Add(new(icon,new(_shortcutDragPoint.X+_iconBounds.X*sx,_shortcutDragPoint.Y+_iconBounds.Y*sy,
            _iconBounds.Width*sx,_iconBounds.Height*sy)));
        var assignment=slot.Assignment;
        if(assignment?.Source is EnbRetailShortcutSource.Equipment or EnbRetailShortcutSource.Cargo &&
            assignment.SavedIndex is int index && _inventory?.Find(assignment.Source==EnbRetailShortcutSource.Equipment?(byte)2:(byte)1,index) is {Level:>0} item)
        {
            _dragLayout??=EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,"fc_inv.ini"));
            if(_dragLayout.TryResolve("INVENTORY_LEVEL",out var definition) &&
                Font(definition.Properties.GetValueOrDefault("FontType","")) is {} font)
            {
                var offset=Vector(definition.Properties.GetValueOrDefault("Offset","0,0"));
                var color=System.Numerics.Vector3.One;
                if(_fontColors is {} colors)
                    foreach(string section in colors.SectionNames)
                        if(colors.TryGetSection(section,out var values)&&values.GetValueOrDefault("ColorID")==definition.Properties.GetValueOrDefault("Color"))
                        {color=Vector(values.GetValueOrDefault("RGB","255,255,255"))/255;break;}
                _dragLevelText.SetText(Roman(item.Level),font,new(color.X,color.Y,color.Z));
                _dragLevelText.SetDisplayScale(1,1);
                if(_dragLevelText.Raster is {} label)images.Add(new(label,new(
                    _shortcutDragPoint.X+offset.X*_size.Width-_dragLevelText.Width/2,
                    _shortcutDragPoint.Y-offset.Y*_size.Height-_dragLevelText.Height/2,_dragLevelText.Width,_dragLevelText.Height)));
            }
        }
        return images.ToImmutable();
    }
}
