using System.Numerics;
namespace EnbNext.Presentation;

public enum EnbUiGroup { None, Cockpit, Buffs, Scanner, Chat, Group, Target, Cargo, Equipment, Character, Utility, Vault, Friends, Guild, GroupFinder, Options, Map, Loot, Multibox, DamageMeter, PlayerFrame, TargetFrame, ActionBar, Engine, CombatXp, ExploreXp, TradeXp, TargetButtons, Tooltip, ModernChat, ModernInventory, ModernEquipment, ModernSkills, ModernMissions, ModernFactions, ModernCommands, ModernGroup }

// Coordinates are in the owner's existing logical viewport. Identity preserves
// the original arithmetic, including its rounding at the GPU boundary.
public readonly record struct EnbUiPlacement(double X, double Y, double Scale)
{
    public static EnbUiPlacement Identity => new(0,0,1);
    public bool IsIdentity => X==0 && Y==0 && Scale==1;
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Scale) && Scale>=.5 && Scale<=3;
    public EnbUiPoint Apply(EnbUiPoint p, EnbUiSize size) => IsIdentity?p:new(p.X*Scale+X*size.Width,p.Y*Scale+Y*size.Height);
    public Vector2 Apply(Vector2 p,EnbUiSize size)=>IsIdentity?p:new((float)(p.X*Scale+X*size.Width),(float)(p.Y*Scale+Y*size.Height));
    public EnbUiPoint Inverse(EnbUiPoint p, EnbUiSize size) => IsIdentity?p:new((p.X-X*size.Width)/Scale,(p.Y-Y*size.Height)/Scale);
    public EnbUiRect Apply(EnbUiRect r, EnbUiSize size)
    { if(IsIdentity)return r;var p=Apply(new EnbUiPoint(r.X,r.Y),size);return new(p.X,p.Y,r.Width*Scale,r.Height*Scale); }
    public Matrix4x4 ClipTransform=>IsIdentity?Matrix4x4.Identity:Matrix4x4.CreateScale((float)Scale,(float)Scale,1)*Matrix4x4.CreateTranslation((float)(Scale+2*X-1),(float)(1-Scale-2*Y),0);
}
