using EnbNext.Net7.Protocol;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Session-local guidance from the installed/server galaxy, live objects and fixed profession rules.</summary>
public sealed class EnbSectorRouteGuidance
{
    private Net7GalaxyMapData? map;
    private Guid lifetime;
    private uint? origin;
    private (uint Race,uint Profession)? character;
    private EnbRouteTravel travel = EnbRouteTravel.None;
    private IReadOnlyDictionary<uint, EnbRouteWormhole> wormholeSteps = new Dictionary<uint, EnbRouteWormhole>();
    public string RestrictionNotice { get; private set; } = "";
    /// <summary>Next: Smuggler's Run and the usable wormholes (none unless set).</summary>
    public Func<EnbRouteTravel>? Travel { get; set; }
    /// <summary>The wormhole the route takes into a sector of the route, if it is reached by one.</summary>
    public EnbRouteWormhole? WormholeInto(uint sector) => wormholeSteps.GetValueOrDefault(sector);
    /// <summary>The wormhole the next step takes (the route's second sector reached by a wormhole), if any.</summary>
    public EnbRouteWormhole? NextWormhole => NextSector is uint next ? WormholeInto(next) : null;
    private bool dirty;
    public uint? Destination { get; private set; }
    public IReadOnlyList<uint> Route { get; private set; } = Array.Empty<uint>();
    public uint? Gate { get; private set; }
    public uint? NextSector => Route.Count > 1 ? Route[1] : null;
    public string RouteText { get; private set; } = "";
    public string Status { get; private set; } = "";

    public void Choose(uint? destination)
    {
        Destination = destination;
        Route = Array.Empty<uint>(); Gate = null; RouteText = Status = RestrictionNotice = ""; dirty = true;
        wormholeSteps = new Dictionary<uint, EnbRouteWormhole>();
    }

    /// <summary>Where the server says the character is (GalaxyMap command 4: system and sector names), if known.</summary>
    public Func<(string System, string Sector)?>? Place { get; set; }

    /// <summary>
    /// The sector a route starts from: the server's sector number (0x0042) in space. Docked there is none (a station
    /// entry sends no 0x0042); the station's sector is named instead: by the server's GalaxyMap command 4 (system,
    /// sector and station; captures of 18 Nov 2026: "Beta Hydri", "Carpenter", "Orsini Mining Platform" on a login in
    /// the station), else by the last handoff's source sector and system (as retail's sector map names them docked,
    /// 007C8E50). The galaxy sector of that name in that system is used when exactly one matches.
    /// </summary>
    public static uint? CurrentSector(IEnbSceneFrame? scene, Net7GalaxyMapData? galaxy, (string System, string Sector)? place = null)
    {
        if (scene is null) return null;
        if (scene.ActiveStarbaseId is null) return scene.Environment?.SectorNum;
        var (systemName, sectorName) = place is { Sector.Length: > 0 } named ? named
            : scene.LocationTransition is { SourceSectorName.Length: > 0 } where ? (where.SourceSystemName, where.SourceSectorName) : ("", "");
        if (sectorName.Length == 0 || galaxy is null) return null;
        uint? found = null;
        foreach (var sector in galaxy.Sectors)
        {
            if (!string.Equals(DisplayName(sector.Name), DisplayName(sectorName), StringComparison.OrdinalIgnoreCase)) continue;
            if (systemName.Length > 0 && galaxy.Systems.FirstOrDefault(s => s.SystemHashId == sector.Field2) is { } system &&
                !string.Equals(DisplayName(system.Name), DisplayName(systemName), StringComparison.OrdinalIgnoreCase)) continue;
            if (found is uint other && other != sector.SectorHashId) return null;
            found = sector.SectorHashId;
        }
        return found;
    }

    public void Suspend()
    {
        map = null; origin = null; character = null; RestrictionNotice = ""; Route = Array.Empty<uint>(); Gate = null;
        RouteText = Status = ""; wormholeSteps = new Dictionary<uint, EnbRouteWormhole>();
    }

    public void Update(Net7GalaxyMapData? galaxy, IEnbSceneFrame scene)
    {
        if (lifetime != scene.ClientSoundStreamId)
        {
            lifetime = scene.ClientSoundStreamId;
            Choose(null);
        }
        bool wasRecognised=Gate.HasValue;
        Gate = null;
        if (!Destination.HasValue) return;
        if (scene.IsTransitioning || galaxy is null || CurrentSector(scene, galaxy, Place?.Invoke()) is not uint current)
        {
            Suspend(); return;
        }
        var fields=scene.Hud.Inventory?.RequirementState;
        if(fields is not {RpgAvailable:true} || !fields.Fields.TryGetValue("Race",out int race) ||
            !fields.Fields.TryGetValue("Profession",out int profession) || (uint)race>2 || (uint)profession>2)
        { Suspend(); return; }
        var identity=((uint)race,(uint)profession);
        var available=Travel?.Invoke() ?? EnbRouteTravel.None;
        bool replan=dirty || !ReferenceEquals(map, galaxy) || origin != current || character!=identity || !available.Equals(travel);
        if (replan)
        {
            map = galaxy; origin = current; character=identity; travel=available; dirty = false;
            var steps=new Dictionary<uint,EnbRouteWormhole>();
            Route = BuildRoute(galaxy, current, Destination.Value,identity,available,steps);
            wormholeSteps=steps;
            var requirement=ProfessionRule(galaxy,Destination.Value);
            RestrictionNotice=requirement is {} rule && (rule.Race,rule.Profession)!=identity
                ? "Destination is profession-restricted: requires "+rule.Name+"."
                : InSmugglersRun(galaxy,Destination.Value) && !InSmugglersRun(galaxy,current) && !available.SmugglersRun
                ? "Smuggler's Run needs a standing of "+SmugglersRunStanding+" with The Red Dragon." : "";
            RouteText = string.Join(" > ", Route.Select(id => DisplayName(galaxy.Sectors.First(s => s.SectorHashId == id).Name)+(steps.ContainsKey(id)?" (wormhole)":"")));
            if(RestrictionNotice.Length>0 && Route.Count>0) RouteText+=requirement is not null ? " [profession-restricted]" : " [Red Dragon standing]";
        }
        string destination = galaxy.Sectors.FirstOrDefault(s => s.SectorHashId == Destination)?.Name ?? "Unavailable destination";
        if (Route.Count == 0) { if(replan) Status = "No route to " + DisplayName(destination); return; }
        if (Route.Count == 1) { if(replan) Status = "Arrived: " + DisplayName(destination); return; }
        var from = galaxy.Sectors.First(s => s.SectorHashId == current);
        var next = galaxy.Sectors.First(s => s.SectorHashId == NextSector);
        if (NextWormhole is { } wormhole)
        {
            if(replan) Status = "Next sector: " + DisplayName(next.Name) + " by wormhole" + (wormhole.Caster.Length > 0 ? " (" + wormhole.Caster + ")" : "");
            return;
        }
        if (from.Subtype == 6 && next.Subtype == 6 && scene.ActiveStarbaseId is null)
        {
            Gate = ResolveLiveGate(galaxy, current, NextSector!.Value, scene.Objects);
        }
        if(replan || wasRecognised!=Gate.HasValue) Status = "Next sector: " + DisplayName(next.Name) + (Gate.HasValue || scene.ActiveStarbaseId is not null ? "" : ", gate not recognised");
    }

    // Presentation only: the installed map can contain UTF-8 right apostrophes
    // in otherwise byte-preserved names; the retail font indexes single bytes.
    internal static string DisplayName(string name) => name.Replace("\u00e2\u0080\u0099", "'").Replace("\u2019", "'");

    /// <summary>
    /// The fewest-hop route. Gates into a profession's starting sector need that profession, and gates into Smuggler's
    /// Run from outside it need its standing (both allowed as the destination itself, with a notice). With wormholes,
    /// each usable wormhole is one more hop from any sector to its destination; gates are preferred at equal length.
    /// <paramref name="wormholeSteps"/> receives the sectors the route reaches by wormhole.
    /// </summary>
    public static IReadOnlyList<uint> BuildRoute(Net7GalaxyMapData galaxy, uint from, uint to, (uint Race,uint Profession)? character=null,
        EnbRouteTravel? travel=null, IDictionary<uint,EnbRouteWormhole>? wormholeSteps=null)
    {
        travel ??= EnbRouteTravel.None;
        var nodes = galaxy.Sectors.Select(s => s.SectorHashId).ToHashSet();
        if (!nodes.Contains(from) || !nodes.Contains(to)) return Array.Empty<uint>();
        var wormholes = new List<(uint Sector, EnbRouteWormhole Wormhole)>();
        foreach (var wormhole in travel.Wormholes)
            if (WormholeSector(galaxy, wormhole.Destination) is uint sector && wormholes.All(w => w.Sector != sector)) wormholes.Add((sector, wormhole));
        var parents = new Dictionary<uint, uint> { [from] = from };
        var byWormhole = new Dictionary<uint, EnbRouteWormhole>();
        var pending = new Queue<uint>(); pending.Enqueue(from);
        while (pending.TryDequeue(out uint current))
        {
            if (current == to)
            {
                var route = new List<uint> { to };
                while (current != from) { if (byWormhole.TryGetValue(current, out var used)) wormholeSteps?.Add(current, used); current = parents[current]; route.Add(current); }
                route.Reverse(); return route.ToArray();
            }
            bool inside = InSmugglersRun(galaxy, current);
            foreach (var edge in galaxy.Gates)
                if (edge.SectorReferenceA == current && nodes.Contains(edge.SectorReferenceB) &&
                    (edge.SectorReferenceB==to || ProfessionRule(galaxy,edge.SectorReferenceB) is not {} rule ||
                        character==(rule.Race,rule.Profession)) &&
                    (edge.SectorReferenceB==to || inside || travel.SmugglersRun || !InSmugglersRun(galaxy,edge.SectorReferenceB)) &&
                    parents.TryAdd(edge.SectorReferenceB, current)) pending.Enqueue(edge.SectorReferenceB);
            foreach (var (sector, wormhole) in wormholes)
                if (sector != current && parents.TryAdd(sector, current)) { byWormhole[sector] = wormhole; pending.Enqueue(sector); }
        }
        return Array.Empty<uint>();
    }

    /// <summary>The standing with The Red Dragon its gates into Smuggler's Run ask for.</summary>
    public const int SmugglersRunStanding = 2000;
    /// <summary>A sector of Smuggler's Run (Blackbeard's Wake, Paramis Nebula), the Red Dragon system.</summary>
    internal static bool InSmugglersRun(Net7GalaxyMapData galaxy, uint sectorId)
    {
        var sector = galaxy.Sectors.FirstOrDefault(s => s.SectorHashId == sectorId);
        return sector is not null && galaxy.Systems.FirstOrDefault(s => s.SystemHashId == sector.Field2)?.Name is { } system &&
            NormalizeGateName(system) == "smugglers run";
    }
    /// <summary>The galaxy sector a wormhole opens to, by name ("The Valkyrie Twins" for Valkyrie Twins), when exactly one matches.</summary>
    internal static uint? WormholeSector(Net7GalaxyMapData galaxy, string destination)
    {
        uint? found = null;
        foreach (var sector in galaxy.Sectors)
        {
            if (!SameDestination(NormalizeGateName(destination), sector.Name)) continue;
            if (found is uint other && other != sector.SectorHashId) return null;
            found = sector.SectorHashId;
        }
        return found;
    }

    // Fixed game rules: N7CM GalaxyTopologyCatalog.cs RequiredProfession,
    // source commit 2dbc8c73f35671399ff1ade677b61b1cad46e14e. No faction rules.
    internal static (uint Race,uint Profession,string Name)? ProfessionRule(Net7GalaxyMapData galaxy,uint sectorId)
    {
        var sector=galaxy.Sectors.FirstOrDefault(s=>s.SectorHashId==sectorId);
        if(sector is null || galaxy.Systems.FirstOrDefault(s=>s.SystemHashId==sector.Field2)?.Name!="Sol")return null;
        return sector.Name switch
        {
            "Equatorial Earth" => (0u,2u,"Terran Scout"),
            "Europa" => (1u,0u,"Jenquai Defender"),
            "Ganymede" => (1u,1u,"Jenquai Seeker"),
            "High Earth" => (0u,1u,"Terran Trader"),
            "Io" => (1u,2u,"Jenquai Explorer"),
            "Luna" => (0u,0u,"Terran Enforcer"),
            "Mars Alpha" => (2u,0u,"Progen Warrior"),
            "Mars Beta" => (2u,2u,"Progen Sentinel"),
            "Mars Gamma" => (2u,1u,"Progen Privateer"),
            _ => null,
        };
    }

    // Normalize spelling only; never introduce sector aliases or surveyed mappings.
    private static string NormalizeGateName(string name)
    {
        var text=new System.Text.StringBuilder();bool space=false;
        foreach(char c in DisplayName(name))
        {
            if(c=='\'')continue;
            if(char.IsLetterOrDigit(c)){if(space && text.Length>0)text.Append(' ');text.Append(char.ToLowerInvariant(c));space=false;}
            else space=true;
        }
        return text.ToString();
    }

    private static string DestinationLabel(string name)
    {
        string label=NormalizeGateName(name);
        int to=label.LastIndexOf(" to ",StringComparison.Ordinal);
        return to>=0?label[(to+4)..]:label;
    }

    private static bool SameDestination(string label,string name)
    {
        static string WithoutArticle(string value)=>value.StartsWith("the ",StringComparison.Ordinal)?value[4..]:value;
        foreach(string left in label.Split(" and ",StringSplitOptions.None))
            foreach(string right in NormalizeGateName(name).Split(" and ",StringSplitOptions.None))
                if(WithoutArticle(left).Length>0 && WithoutArticle(left)==WithoutArticle(right))return true;
        return false;
    }

    public static uint? ResolveGate(Net7GalaxyMapData galaxy, uint current, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if(!NormalizeGateName(name).Contains(" to ",StringComparison.Ordinal))return null;
        string label=DestinationLabel(name);
        uint? match=null;int count=0;
        foreach(var edge in galaxy.Gates)
        {
            if(edge.SectorReferenceA!=current)continue;
            var sector=galaxy.Sectors.FirstOrDefault(s=>s.SectorHashId==edge.SectorReferenceB);
            if(sector is null)continue;
            string? system=galaxy.Systems.FirstOrDefault(s=>s.SystemHashId==sector.Field2)?.Name;
            bool matches=SameDestination(label,sector.Name) || system is not null &&
                (SameDestination(label,system) || label.EndsWith(" system",StringComparison.Ordinal) && SameDestination(label[..^7],system));
            if(matches){match=edge.SectorReferenceB;count++;}
        }
        return count==1?match:null;
    }

    public static uint? ResolveLiveGate(Net7GalaxyMapData galaxy,uint current,uint next,IReadOnlyList<IEnbRenderObject> objects)
    {
        var gates=objects.Where(o=>o.ObjectType==11).ToArray();
        var edges=galaxy.Gates.Where(e=>e.SectorReferenceA==current).ToArray();
        var matched=new HashSet<uint>();
        uint? direct=null;int directCount=0,unknownCount=0;IEnbRenderObject? unknown=null;
        bool complete=edges.Length==gates.Length && edges.Select(e=>e.SectorReferenceB).Distinct().Count()==edges.Length &&
            edges.All(e=>galaxy.Sectors.Any(s=>s.SectorHashId==e.SectorReferenceB && s.Subtype==6));
        foreach(var gate in gates)
        {
            string? name=gate.DisplayNameProperties?.Name??gate.Name;
            if(gate.Spatial is null || gate.Radar.Navigation is null || string.IsNullOrWhiteSpace(name))
            {complete=false;continue;}
            uint? destination=ResolveGate(galaxy,current,name);
            if(destination is uint resolved)
            {
                if(!matched.Add(resolved))complete=false;
                if(resolved==next){direct=gate.GameId;directCount++;}
            }
            else
            {
                unknown=gate;unknownCount++;
                // A named destination outside this graph, or an ambiguous system,
                // must not be reassigned to the remaining edge by elimination.
                if(NormalizeGateName(name).Contains(" to ",StringComparison.Ordinal))complete=false;
            }
        }
        if(directCount!=0)return directCount==1?direct:null;
        return complete && unknownCount==1 && matched.Count==edges.Length-1 &&
            edges.Any(e=>e.SectorReferenceB==next) && !matched.Contains(next)?unknown!.GameId:null;
    }
}
