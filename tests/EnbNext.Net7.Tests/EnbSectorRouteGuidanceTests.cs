using EnbNext.Net7.Protocol;
using EnbNext.Rendering;
using EnbNext.Presentation;
using System.Numerics;

namespace EnbNext.Net7.Tests;

public sealed class EnbSectorRouteGuidanceTests
{
    private static Net7GalaxyMapData Map(params (uint From,uint To)[] edges) => new([], [new(0,10,"Home",[],[],0,0),new(1,20,"Elsewhere",[],[],0,0)],
        [new(6,0,1,10,"Alpha",6,[]),new(6,1,2,10,"Beta",6,[]),new(6,2,3,20,"Gamma",6,[]),new(6,3,4,20,"Delta",6,[])],
        edges.Select((e,i)=>new Net7GalaxyGateRecord((uint)i,e.From,e.To)).ToArray());

    [Fact] public void RouteIsDirectedShortestAndHandlesCyclesAndDisconnection()
    {
        var map=Map((1,2),(2,1),(2,3),(1,4),(4,3));
        Assert.Equal(new uint[]{1,2,3},EnbSectorRouteGuidance.BuildRoute(map,1,3));
        Assert.Empty(EnbSectorRouteGuidance.BuildRoute(map,3,1));
        Assert.Equal(new uint[]{1},EnbSectorRouteGuidance.BuildRoute(map,1,1));
        Assert.Empty(EnbSectorRouteGuidance.BuildRoute(map,1,99));
    }

    [Theory]
    [InlineData("Gate to Beta",2u)]
    [InlineData("Gate To Beta",2u)]
    [InlineData("Gate To Elsewhere System",3u)]
    [InlineData("Sector Gate to Beta",2u)]
    [InlineData("Sector Accelerator to Beta",2u)]
    [InlineData("System Gate to Elsewhere System",3u)]
    [InlineData("Gate to Elsewhere",3u)]
    [InlineData("Gate to Gamma",3u)]
    public void LiveNamesResolveOnlyAgainstOutgoingConnections(string name,uint target)
        => Assert.Equal(target,EnbSectorRouteGuidance.ResolveGate(Map((1,2),(1,3)),1,name));

    [Theory]
    [InlineData(null)] [InlineData("Beta")] [InlineData("Mystery Gate")]
    [InlineData("Gate to Bet")] [InlineData("Gate to Beta Extra")] [InlineData("Gate to Delta")]
    public void MissingUnknownPartialAndNonAdjacentNamesDoNotResolve(string? name)
        => Assert.Null(EnbSectorRouteGuidance.ResolveGate(Map((1,2),(1,3)),1,name));

    [Fact] public void AmbiguousSystemAndDuplicateConnectionsAreUnresolved()
    {
        Assert.Null(EnbSectorRouteGuidance.ResolveGate(Map((1,3),(1,4)),1,"Gate to Elsewhere System"));
        Assert.Null(EnbSectorRouteGuidance.ResolveGate(Map((1,3),(1,4)),1,"Gate To Elsewhere System"));
        Assert.Null(EnbSectorRouteGuidance.ResolveGate(Map((1,2),(1,2)),1,"Gate to Beta"));
    }

    private static EnbSceneObjectSnapshot Gate(uint id=80,string name="Gate to Beta",byte type=11,byte navType=2)
        => new(id,1,id,false,false,type,48,1,0,0,0,2,false,name,new(true,30000,new(30000,true,navType,false)),
            new(EnbSceneSpatialProviderKind.Fixed,new(0,Vector3.Zero,Quaternion.Identity,Vector3.Zero,0,Vector3.Zero,Vector3.UnitX),null),
            null,null,[],[],[],[],[],[],null,[],null,0,0);
    private static EnbSceneFrame Frame(uint sector=1) => new()
    {
        Hud={Inventory=new(null,0,[],[],[],System.Collections.Immutable.ImmutableDictionary<string,string>.Empty,System.Collections.Immutable.ImmutableDictionary<string,string>.Empty,[],[],[])
                {RequirementState=new(true,false,System.Collections.Immutable.ImmutableDictionary<string,int>.Empty,System.Collections.Immutable.ImmutableDictionary<string,int>.Empty.Add("Race",2).Add("Profession",0))}},
        ClientSoundStreamId=Guid.NewGuid(),
        Environment=new(0,false,sector,[],null,0,0,false,false,0,0,0,0),Objects=[Gate()]
    };
    private static EnbSectorRouteGuidance Start(Net7GalaxyMapData map,EnbSceneFrame scene,uint destination=3)
    {
        var owner=new EnbSectorRouteGuidance();owner.Update(map,scene);owner.Choose(destination);owner.Update(map,scene);return owner;
    }

    [Fact] public void DockedRoutesStartFromTheStationsNamedSector()
    {
        // Docked there is no sector number: the handoff names the station's sector and system, as retail's sector map shows.
        var map=Map((1,2),(2,3));var scene=Frame();scene.ActiveStarbaseId=21;scene.Objects=[];
        scene.LocationTransition=new(1,"Beta","Home","Beta Station","");
        var owner=Start(map,scene);
        Assert.Equal(new uint[]{2,3},owner.Route);Assert.Null(owner.Gate);Assert.Equal("Next sector: Gamma",owner.Status);
        Assert.Equal(2u,EnbSectorRouteGuidance.CurrentSector(scene,map));
        scene.LocationTransition=new(1,"Beta","Elsewhere","Beta Station","");Assert.Null(EnbSectorRouteGuidance.CurrentSector(scene,map));
        scene.LocationTransition=null;Assert.Null(EnbSectorRouteGuidance.CurrentSector(scene,map));
        // A login in the station has no handoff: the server's GalaxyMap command 4 names the system and sector.
        Assert.Equal(2u,EnbSectorRouteGuidance.CurrentSector(scene,map,("Home","Beta")));
        var placed=new EnbSectorRouteGuidance{Place=()=>("Home","Beta")};placed.Update(map,scene);placed.Choose(3);placed.Update(map,scene);
        Assert.Equal(new uint[]{2,3},placed.Route);
    }
    [Fact] public void HandoffRetainsOnlyDestinationAndReplansFromServerSector()
    {
        var map=Map((1,2),(2,3),(4,1));var scene=Frame();var owner=Start(map,scene);
        Assert.Equal(80u,owner.Gate);Assert.Equal("Next sector: Beta",owner.Status);
        scene.IsTransitioning=true;owner.Update(map,scene);
        Assert.Equal(3u,owner.Destination);Assert.Empty(owner.Route);Assert.Null(owner.Gate);
        scene.IsTransitioning=false;scene.Environment=scene.Environment! with {FinalRawValue=4};scene.Objects=[];
        owner.Update(map,scene);Assert.Equal(new uint[]{4,1,2,3},owner.Route);Assert.Null(owner.Gate);
        scene.Environment=scene.Environment with {FinalRawValue=3};owner.Update(map,scene);
        Assert.Equal("Arrived: Gamma",owner.Status);Assert.Null(owner.Gate);
    }
    [Theory]
    [InlineData((byte)1,"Gate To Elsewhere System")] [InlineData((byte)2,"Gate To Elsewhere System")]
    [InlineData((byte)2,"Ancient Portal")]
    public void AutopilotContinuesAfterHandoffToCapitalizedSystemGate(byte navType,string name)
    {
        var map=Map((1,2),(2,3));var scene=Frame();
        var ship=Gate(1,"Ship",type:1) with {IsLocalShip=true};scene.Objects=[ship,Gate()];
        scene.Hud.Cockpit=new EnbCockpitFrame {Warp=new(0,2,null,false,false,100,0)};
        var guidance=Start(map,scene);var commands=new List<string>();
        var pilot=new EnbNext.Client.EnbSectorAutopilot((id,_)=>{commands.Add("target:"+id);scene.Hud.HasLocalTargetSelection=true;scene.Hud.LocalSelectedTargetId=id;return Task.FromResult(true);},
            (_,_,_,_)=>{commands.Add("warp");return Task.FromResult(true);},(_,_,_)=>{commands.Add("gate");return Task.FromResult(true);});
        void Tick(bool ready=true){guidance.Update(map,scene);pilot.Tick(scene,true,guidance.Destination,guidance.Gate,false,ready,()=>[guidance.Gate!.Value]);}
        Assert.True(pilot.Start(scene,true,guidance.Destination,guidance.Gate,false));Tick();
        scene.Hud.CurrentTargetVerbs=[0x000A0000];Tick();
        scene.IsTransitioning=true;Tick();Assert.True(pilot.Active);
        scene.Environment=scene.Environment! with {FinalRawValue=2};scene.IsTransitioning=false;
        scene.Objects=[ship,Gate(81,name,navType:navType)];scene.Hud.CurrentTargetVerbs=[];
        Tick(false);Tick();Tick();
        Assert.True(pilot.Active);Assert.Equal(new[]{"target:80","gate","target:81","warp"},commands);
        scene.Environment=scene.Environment with {FinalRawValue=3};Tick();
        Assert.False(pilot.Active);Assert.Equal("Destination reached",pilot.StopReason);
    }
    [Fact] public void NewCharacterClearsDestinationButPresentationSuspensionDoesNot()
    {
        var map=Map((1,2),(2,3));var scene=Frame();var owner=Start(map,scene);
        owner.Suspend();owner.Update(map,scene);Assert.Equal(3u,owner.Destination);
        scene.ClientSoundStreamId=Guid.NewGuid();owner.Update(map,scene);
        Assert.Null(owner.Destination);Assert.Empty(owner.Route);Assert.Null(owner.Gate);
    }
    [Fact] public void LiveGateRemovalRenameDuplicatesAndLookalikesClearHighlight()
    {
        var map=Map((1,2),(1,4),(2,3));var scene=Frame();var owner=Start(map,scene);
        foreach(var objects in new IEnbRenderObject[][] {[],[Gate(name:"Mystery")],[Gate(),Gate(81)],[Gate(type:37)]})
        {
            scene.Objects=objects;owner.Update(map,scene);Assert.Null(owner.Gate);
            Assert.Equal("Next sector: Beta, gate not recognised",owner.Status);
        }
        scene.Objects=[Gate()];owner.Update(map,scene);Assert.Equal(80u,owner.Gate);
    }
    [Theory] [InlineData(7u)] [InlineData(8u)] [InlineData(11u)]
    public void SpecialAndPlanetEdgesRemainManualEvenWhenNamesMatch(uint subtype)
    {
        var map=Map((1,2),(2,3));map=map with {Sectors=map.Sectors.Select(s=>s.SectorHashId==2?s with {Subtype=subtype}:s).ToArray()};
        var owner=Start(map,Frame());Assert.Equal(2u,owner.NextSector);Assert.Null(owner.Gate);
        Assert.EndsWith("gate not recognised",owner.Status);
    }
    [Fact] public void ServerMapReplacementReplansAndMissingSectorDoesNotReuseOldGate()
    {
        var scene=Frame();var owner=Start(Map((1,2),(2,3)),scene);
        owner.Update(Map((1,4),(4,3)),scene);Assert.Equal(new uint[]{1,4,3},owner.Route);Assert.Null(owner.Gate);
        scene.Environment=null;owner.Update(Map((1,2),(2,3)),scene);Assert.Empty(owner.Route);Assert.Null(owner.Gate);
    }
    [Theory]
    [InlineData(100,"Alpha > Beta > Gamma > Delta > Omega")]
    [InlineData(30,"Alpha > Beta > ... > Omega")]
    [InlineData(25,"Alpha > ... > Omega")]
    [InlineData(11,"... > Omega")]
    public void CompactRouteKeepsLongestPrefixAndDestination(int width,string expected)
        => Assert.Equal(expected,EnbNext.Engine.EnbNavigationMapUi.CompactRoute("Alpha > Beta > Gamma > Delta > Omega",s=>s.Length,width));
    [Theory]
    [InlineData("Odin\u00e2\u0080\u0099s Belt","Odin's Belt")]
    [InlineData("Odin\u2019s Belt","Odin's Belt")]
    [InlineData("Odin's Belt","Odin's Belt")]
    [InlineData("Other name","Other name")]
    public void DisplayRepairsApostropheWithoutChangingSource(string source,string expected)
    {
        var map=Map((1,2));map=map with {Sectors=map.Sectors.Select(s=>s.SectorHashId==2?s with {Name=source}:s).ToArray()};
        var owner=Start(map,Frame(),2);
        Assert.EndsWith(expected,owner.RouteText);Assert.StartsWith("Next sector: "+expected,owner.Status);
        Assert.Equal(source,map.Sectors[1].Name);
        Assert.Equal(2u,EnbSectorRouteGuidance.ResolveGate(map,1,"Gate to "+source));
        Assert.Equal(2u,EnbSectorRouteGuidance.ResolveGate(map,1,"Gate to "+expected));
    }
    [Theory]
    [InlineData("Equatorial Earth",0u,2u)] [InlineData("Europa",1u,0u)]
    [InlineData("Ganymede",1u,1u)] [InlineData("High Earth",0u,1u)]
    [InlineData("Io",1u,2u)] [InlineData("Luna",0u,0u)]
    [InlineData("Mars Alpha",2u,0u)] [InlineData("Mars Beta",2u,2u)]
    [InlineData("Mars Gamma",2u,1u)]
    public void ProfessionRulesFilterTransitButKeepExplicitDestination(string sector,uint race,uint profession)
    {
        var map=Map((1,2),(2,3),(1,4),(4,3));
        map=map with {Systems=map.Systems.Select(s=>s.SystemHashId==10?s with {Name="Sol"}:s).ToArray(),
            Sectors=map.Sectors.Select(s=>s.SectorHashId==2?s with {Name=sector}:s).ToArray()};
        var matching=(race,profession);var other=((race+1)%3,profession);
        Assert.Equal(new uint[]{1,2,3},EnbSectorRouteGuidance.BuildRoute(map,1,3,matching));
        Assert.Equal(new uint[]{1,4,3},EnbSectorRouteGuidance.BuildRoute(map,1,3,other));
        Assert.Equal(new uint[]{1,2},EnbSectorRouteGuidance.BuildRoute(map,1,2,other));
        Assert.Equal(new uint[]{2,3},EnbSectorRouteGuidance.BuildRoute(map,2,3,other));
        var scene=Frame();SetProfession(scene,other.Item1,other.Item2);var owner=Start(map,scene,2);
        Assert.EndsWith("[profession-restricted]",owner.RouteText);Assert.Contains("requires",owner.RestrictionNotice);
        SetProfession(scene,race,profession);owner.Update(map,scene);Assert.Empty(owner.RestrictionNotice);Assert.Equal("Alpha > "+sector,owner.RouteText);
        owner.Choose(3);owner.Update(map,scene);SetProfession(scene,other.Item1,other.Item2);owner.Update(map,scene);
        Assert.Equal(new uint[]{1,4,3},owner.Route);
    }
    [Theory]
    [InlineData("Accelerator to bETA")]
    [InlineData("Ancient Gate to Beta")]
    [InlineData("Pirate Gate - to Beta")]
    public void GenericDestinationWordingNeedsNoPrefixCatalogue(string name)
        => Assert.Equal(2u,EnbSectorRouteGuidance.ResolveGate(Map((1,2)),1,name));

    [Theory]
    [InlineData("Gate to The Beta")]
    [InlineData("Gate to Beta and a Companion")]
    public void ArticlesAndCompoundNamesResolveWholeComponents(string name)
        => Assert.Equal(2u,EnbSectorRouteGuidance.ResolveGate(Map((1,2),(1,3)),1,name));
    [Fact] public void CompoundNamesCannotHideTwoOutgoingMatches()
        => Assert.Null(EnbSectorRouteGuidance.ResolveGate(Map((1,2),(1,3)),1,"Gate to Beta and Gamma"));

    [Fact] public void EliminationRequiresCompleteUniqueOrdinaryConnections()
    {
        var map=Map((1,2),(1,3));var gates=new IEnbRenderObject[]{Gate(),Gate(81,"Ancient Portal")};
        Assert.Equal(81u,EnbSectorRouteGuidance.ResolveLiveGate(map,1,3,gates));
        Assert.Null(EnbSectorRouteGuidance.ResolveLiveGate(map,1,3,[gates[1]]));
        Assert.Null(EnbSectorRouteGuidance.ResolveLiveGate(map,1,3,[..gates,Gate(82,"Local Accelerator")]));
        Assert.Null(EnbSectorRouteGuidance.ResolveLiveGate(map,1,3,[Gate(name:"Other Portal"),gates[1]]));
        Assert.Null(EnbSectorRouteGuidance.ResolveLiveGate(map,1,3,[Gate(),Gate(81,"Gate to Delta")]));
        Assert.Null(EnbSectorRouteGuidance.ResolveLiveGate(map,1,3,[Gate(),Gate(81," ")]));
        Assert.Null(EnbSectorRouteGuidance.ResolveLiveGate(map,1,3,[Gate(),Gate(81,"Ancient Portal") with {Spatial=null}]));
        Assert.Null(EnbSectorRouteGuidance.ResolveLiveGate(Map((1,2),(1,2),(1,3)),1,3,[Gate(),Gate(82),gates[1]]));
        var special=map with {Sectors=map.Sectors.Select(s=>s.SectorHashId==3?s with {Subtype=7}:s).ToArray()};
        Assert.Null(EnbSectorRouteGuidance.ResolveLiveGate(special,1,3,gates));
    }

    private static void SetProfession(EnbSceneFrame scene,uint race,uint profession)
        => scene.Hud.Inventory=scene.Hud.Inventory! with {RequirementState=scene.Hud.Inventory.RequirementState with
            {Fields=scene.Hud.Inventory.RequirementState.Fields.SetItem("Race",(int)race).SetItem("Profession",(int)profession)}};
    [Fact] public void MissingCharacterDataWaitsAndNeverReusesPreviousIdentity()
    {
        var map=Map((1,2),(2,3));var scene=Frame();var owner=Start(map,scene);
        scene.Hud.Inventory=null;owner.Update(map,scene);
        Assert.Empty(owner.Route);Assert.Null(owner.Gate);Assert.Empty(owner.Status);Assert.Equal(3u,owner.Destination);
        scene.Hud.Inventory=Frame().Hud.Inventory;owner.Update(map,scene);Assert.Equal(new uint[]{1,2,3},owner.Route);
    }
    [Fact] public void FactionSectorsAndSameNamesOutsideSolAreNotFiltered()
    {
        var map=Map((1,2),(2,3));
        foreach(string name in new[]{"Mars Beta","Blackbeard's Wake","Chavez"})
        {
            map=map with {Sectors=map.Sectors.Select(s=>s.SectorHashId==2?s with {Name=name}:s).ToArray()};
            Assert.Equal(new uint[]{1,2,3},EnbSectorRouteGuidance.BuildRoute(map,1,3,(0u,0u)));
        }
        map=map with {Systems=map.Systems.Select(s=>s.SystemHashId==10?s with {Name="Sol"}:s).ToArray()};
        Assert.Equal(new uint[]{1,2,3},EnbSectorRouteGuidance.BuildRoute(map,1,3,(0u,0u)));
    }
}
