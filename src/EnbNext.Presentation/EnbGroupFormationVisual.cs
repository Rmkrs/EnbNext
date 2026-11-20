using System.Numerics;

namespace EnbNext.Presentation;

/// <summary>592C50 construction; 593180 selection and 592A80 material pulse.</summary>
public sealed class EnbGroupFormationVisual
{
    public sealed class Ship(string asset,int position)
    {
        public string Asset {get;}=asset;
        public int Position {get;}=position;
        public Vector3 Emissive {get;internal set;}=BaseColor(position);
    }
    private readonly List<Ship> _ships=[];
    private readonly Ship?[] _slots=new Ship?[6];
    public IReadOnlyList<Ship> Ships=>_ships;
    public IReadOnlyList<Ship?> Slots=>_slots;
    public string? Asset {get;private set;}
    public bool Hidden {get;private set;}
    public int Selected {get;private set;}=-1;
    public float Remaining {get;private set;}
    public bool Animating=>Selected>=0 && Remaining>0;
    private static Vector3 BaseColor(int position)=>position==0?new(1,.6f,0):Vector3.One;
    public void Rebuild(EnbSceneGroupSnapshot? group)
    {
        _ships.Clear();Array.Clear(_slots);Selected=-1;Remaining=0;Hidden=group is null;
        Asset=group is null?"tsi03a_form0":null;
        if(group is null || group.AuxDataState==0)return;
        int variant=group.Formation==0?0:group.FormationName.Equals("Slot Back",StringComparison.OrdinalIgnoreCase)?1:
            group.FormationName.Equals("Block",StringComparison.OrdinalIgnoreCase)?2:group.FormationName.Equals("Pipe",StringComparison.OrdinalIgnoreCase)?3:0;
        Asset="tsi03a_form"+variant;
        if(variant==0)return;
        Add("tsi03a_ShipLrg",group.Position);
        foreach(var member in group.Members.Take(5))Add("tsi03a_ShipSml",member.Position);
    }
    private void Add(string asset,int position)
    {
        // Retail indexes a six-entry array without an upper bound. Contain an
        // invalid optional display position locally; never alter authoritative Aux.
        if((uint)position>=6)return;
        var ship=new Ship(asset,position);_ships.Add(ship);_slots[position]=ship;
    }
    public void Select(int position)
    {
        if((uint)position>=6 || _slots[position] is null)return;
        if(Selected>=0 && _slots[Selected] is {} previous)previous.Emissive=BaseColor(Selected);
        Selected=position;Remaining=2;
    }
    public void Advance(uint elapsed)
    {
        if(!Animating || _slots[Selected] is not {} ship)return;
        float seconds=(float)elapsed*.001f;
        Remaining=Remaining-seconds;
        if(Remaining<0){ship.Emissive=BaseColor(Selected);Remaining=0;Selected=-1;}
        else ship.Emissive=new(Remaining-MathF.Truncate(Remaining),0,0);
    }
}
