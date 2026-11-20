using EnbNext.Presentation;
namespace EnbNext.Engine;

internal sealed partial class EnbUiLayoutEditor
{
    // Editor preferences survive profile changes and edit sessions, never enter a layout draft.
    internal bool PanelsOpen {get;private set;}=true;
    internal bool GridToolsOpen {get;private set;}
    internal bool GridVisible {get;private set;}
    internal bool SnapToGrid {get;private set;}
    internal int GridSpacing {get;private set;}=32;
    internal bool BypassSnap {get;set;}
    internal double DisplayScale {get;set;}=1;
    private readonly EnbUiPoint[] toolPositions=[new(double.NaN,double.NaN),new(double.NaN,double.NaN),new(double.NaN,double.NaN)];
    private readonly int[] toolOrder=[1,2,0];
    internal ReadOnlySpan<int> ToolOrder=>toolOrder;
    private int draggingTool=-1;
    private EnbUiPoint toolGrab;
    internal EnbUiRect PanelsWindow=>ToolRect(1);
    internal EnbUiRect GridWindow=>ToolRect(2);
    internal bool ToolVisible(int tool)=>tool==0||tool==1&&PanelsOpen||tool==2&&GridToolsOpen;
    internal EnbUiRect ToolRect(int tool)
    {
        bool three=tool==1&&ElementColumns==3;
        double w=(tool==0?700:tool==1?(three?580:400):300)*Unit,h=(tool==0?178:tool==1?(three?110+((Elements.Length+2)/3)*44:Math.Max(462,154+((Elements.Length-1)/2)*44)):236)*Unit;
        var p=toolPositions[tool];
        double x=!double.IsNaN(p.X)?p.X:tool==0?(Layout.Size.Width-w)/2:tool==1?16:Layout.Size.Width-w-16;
        double y=!double.IsNaN(p.Y)?p.Y:tool==0?16:32+178*Unit;
        return new(Math.Clamp(x,0,Math.Max(0,Layout.Size.Width-w)),Math.Clamp(y,0,Math.Max(0,Layout.Size.Height-h)),w,h);
    }
    internal int ToolAt(EnbUiPoint point)
    {for(int i=toolOrder.Length-1;i>=0;i--){int tool=toolOrder[i];if(ToolVisible(tool)&&ToolRect(tool).Contains(point))return tool;}return -1;}
    private void BringForward(int tool)
    {int index=Array.IndexOf(toolOrder,tool);for(int i=index;i<toolOrder.Length-1;i++)toolOrder[i]=toolOrder[i+1];toolOrder[^1]=tool;}
    internal int ActionTool(int id)=>Managing?3:id is 15 or 16 or 17 or 18 or 20?2:id==19?1:0;
    internal static string ElementName(EnbUiGroup group)=>group switch {EnbUiGroup.DamageMeter=>"Damage meter",EnbUiGroup.GroupFinder=>"Group Finder",EnbUiGroup.Loot=>"Loot / Harvest",EnbUiGroup.PlayerFrame=>"Player frame",EnbUiGroup.TargetFrame=>"Target frame",EnbUiGroup.ActionBar=>"Action bar",EnbUiGroup.Engine=>"Engine",EnbUiGroup.CombatXp=>"Combat XP",EnbUiGroup.ExploreXp=>"Exploration XP",EnbUiGroup.TradeXp=>"Trade XP",EnbUiGroup.TargetButtons=>"Target buttons",EnbUiGroup.Tooltip=>"Tooltip",EnbUiGroup.ModernChat=>"Chat",EnbUiGroup.ModernInventory=>"Inventory",EnbUiGroup.ModernEquipment=>"Equipment",EnbUiGroup.ModernSkills=>"Skills",EnbUiGroup.ModernMissions=>"Missions",EnbUiGroup.ModernFactions=>"Factions",EnbUiGroup.ModernCommands=>"Commands",EnbUiGroup.ModernGroup=>"Group",_=>group.ToString()};
    internal static EnbUiPlacement SnapPlacement(EnbUiPlacement placement,EnbUiRect bounds,EnbUiSize size,double step)
    {
        var rect=placement.Apply(bounds,size);
        double x=Math.Round(rect.X/step,MidpointRounding.AwayFromZero)*step,y=Math.Round(rect.Y/step,MidpointRounding.AwayFromZero)*step;
        return placement with {X=(x-bounds.X*placement.Scale)/size.Width,Y=(y-bounds.Y*placement.Scale)/size.Height};
    }
}
