using EnbNext.Rendering;
using EnbNext.Assets;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(ulong Identity,ulong Generation),EnbRetailShipDamageRuntime> _shipDamageStates=[];
    private readonly Dictionary<(ulong Owner,string Path),MovieChild> _shipDamageChildren=[];
    private readonly List<(LiveRootInstance Parent,LiveRootInstance Child,int Bone)> _shipDamageAttachments=[];
    /// <summary>Session-log sink for the local ship's damage visuals (levels, hull, damage children, sounds); it reports
    /// only changes. Live incapacitation showed no flames in Next while retail burned; this records what Next applied.</summary>
    internal static Action<string>? ShipDamageDiagnostic { get; set; }
    private string? _localShipDamageReport;

    private void PrepareShipDamage(LiveFrame frame,RootTaskList roots,EnbRetailAssetBundle assets)
    {
        var owner=_shipExhaustOwner??this;
        var live=roots.Where(r=>frame.Movie is null&&!frame.Snapshot.IsTransitioning&&r.Parent is null&&r.Object.ObjectType==1).ToArray();
        var keys=live.Select(r=>(r.Object.RenderIdentity,r.Object.VisualGeneration)).ToHashSet();
        if(_shipExhaustOwner is null)
        {
            foreach(var key in _shipDamageStates.Keys.Where(k=>!keys.Contains(k)).ToArray())
            {
                foreach(var replacement in keys.Where(k=>k.RenderIdentity==key.Identity))
                {
                    var retained=_shipDamageStates[key];retained.ResetVisuals();_shipDamageStates[replacement]=retained;
                }
                _shipDamageStates.Remove(key);
                foreach(var child in _shipDamageChildren.Keys.Where(c=>c.Owner==key.Identity).ToArray())_shipDamageChildren.Remove(child);
            }
            foreach(var root in live)
            {
                var key=(root.Object.RenderIdentity,root.Object.VisualGeneration);
                if(!_shipDamageStates.TryGetValue(key,out var state))_shipDamageStates[key]=state=new();
                string? damageSound=state.TakeSound(unchecked((uint)frame.CurrentClientTime));
                bool soundWorld=false;
                if(damageSound is {} sound && TryBuildObjectWorld(root.Object,out var world))
                {
                    soundWorld=true;
                    _bloodSounds.Add(EnbBloodSoundRuntime.CreateReleased(root.Object,sound,++_bloodSoundSerial,
                        unchecked((uint)frame.CurrentClientTime),world.Translation));
                }
                state.Update(root.Object.ShipPresentation,root.Object.TargetVitals);
                if(root.Object.IsLocalShip && ShipDamageDiagnostic is {} report)
                {
                    var vitals=root.Object.TargetVitals;
                    string line=$"Ship damage (local {root.Object.RenderIdentity}): hull {vitals?.HullPoints?.ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)??"-"}/{vitals?.MaximumHullPoints?.ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)??"-"}"+
                        $" quadrants [{string.Join(",",root.Object.ShipPresentation?.VisibleQuadrantHealth.Select(h=>h.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture))??[])}]"+
                        $" levels [{string.Join(",",state.Quadrants.Select(q=>q.Level))}] overall {state.Overall.Level}"+
                        $" children {owner._shipDamageChildren.Keys.Count(k=>k.Owner==root.Object.RenderIdentity)}";
                    if(line!=_localShipDamageReport){_localShipDamageReport=line;report(line);}
                    if(damageSound is not null)report($"Ship damage sound {damageSound} (position {(soundWorld?"resolved":"unavailable")})");
                }
            }
        }
        var states=new Dictionary<ulong,ShipChildState>();
        foreach(var root in live)
            if(owner._shipDamageStates.TryGetValue((root.Object.RenderIdentity,root.Object.VisualGeneration),out var state))
                states[root.Object.RenderIdentity]=new(false,false,state.Quadrants,state.Overall);
        AppendShipChildren(states,assets,roots,owner._shipDamageChildren,_shipDamageAttachments,"shipdamage",borrowed:_shipExhaustOwner is not null,selectedRoots:live);
    }
}
