using EnbNext.Assets;
namespace EnbNext.Presentation;

// Explicit interprocess presentation: no remote inventory or mutable game owner crosses it.
public sealed record EnbRemoteShortcut(int Slot,string Name,string Tooltip,string? Icon,float Red,float Green,float Blue,bool Usable,bool Cooling,bool Active)
{
    public int RangeFrame {get;init;}=-1;
    public float RangeAngle {get;init;}
    public static EnbRetailShortcutAddress Address(int slot)=>new(slot%6/3,slot/6,slot%3);
    public static int Index(EnbRetailShortcutAddress address)=>address.Group*6+address.Bar*3+address.Button;
}
public sealed record EnbRemoteShortcutMember(uint Avatar,string Key,string Name,bool Online,EnbRemoteShortcut[] Slots,int[] Bindings);
