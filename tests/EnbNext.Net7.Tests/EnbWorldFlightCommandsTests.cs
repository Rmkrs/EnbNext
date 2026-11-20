using System.Numerics;
using EnbNext.Engine;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;
public sealed class EnbWorldFlightCommandsTests
{
    private static void Forward(EnbWorldFlightCommands<int> owner)
    {owner.Input.KeyDown(1,"Move Forward",true,true,out _,out _);owner.Input.Resolve(true,true,0,false);}
    [Fact]
    public async Task PendingSendDrainsLatestReleaseWithoutQueueingIntermediateIntent()
    {
        var gate=new TaskCompletionSource<bool>();var sent=new List<Net7FlightInput>();
        var owner=new EnbWorldFlightCommands<int>(input=>{sent.Add(input);return sent.Count==1?gate.Task:Task.FromResult(true);},()=>{},_=>{});
        Forward(owner);var pending=owner.PumpAsync();Assert.Single(sent);
        owner.Input.KeyDown(2,"Turn Left",true,true,out _,out _);owner.Input.Resolve(true,true,0,false);
        await owner.PumpAsync();Assert.Single(sent);
        owner.Input.Suspend();await owner.PumpAsync();Assert.Single(sent);
        gate.SetResult(true);await pending;
        Assert.Equal(new Net7FlightInput[]{new(0,0,1),default},sent);
    }
    [Fact]
    public async Task FailedSendResetsIntentAndReleasesCaptureWithoutReplay()
    {
        int releases=0,sends=0;
        var owner=new EnbWorldFlightCommands<int>(_=>{sends++;throw new IOException("retired");},()=>releases++,_=>{});
        Forward(owner);await owner.PumpAsync();Assert.Equal(default,owner.Input.Intent);Assert.Equal(1,releases);Assert.Equal(1,sends);
    }
    [Fact]
    public async Task WarpSuspendsBeforeDispatchAndBlocksOrdinaryPendingRelease()
    {
        var flight=new TaskCompletionSource<bool>();var warp=new TaskCompletionSource<bool>();
        int sends=0,releases=0,thrust=-1,warps=0;
        var owner=new EnbWorldFlightCommands<int>(_=>{sends++;return flight.Task;},()=>releases++,v=>thrust=v);
        Forward(owner);var pending=owner.PumpAsync();
        Task<bool> Activate(uint id,ulong identity,IReadOnlyList<uint> route)
        {warps++;Assert.Equal(1u,id);Assert.Equal(1ul,identity);Assert.Empty(route);Assert.Equal(default,owner.Input.Intent);Assert.Equal(0,thrust);return warp.Task;}
        var requesting=owner.RequestWarpAsync(Scene(),true,Activate);
        Assert.True(owner.WarpSending);Assert.Equal(1,releases);
        await owner.RequestWarpAsync(Scene(),true,Activate);Assert.Equal(1,warps);
        flight.SetResult(true);await pending;await owner.PumpAsync();Assert.Equal(1,sends);
        warp.SetException(new OperationCanceledException());await requesting;Assert.False(owner.WarpSending);Assert.Equal(1,warps);
    }
    [Fact]
    public async Task WarpSendsTheBuiltRouteEvenWhenTheSelectedTargetHasNone()
    {
        // 00583250 -> 007BD980 sends StartWarp with the rebuilt route; an unbuildable
        // route is sent empty (free warp). Transition and cockpit gates still apply.
        int calls=0;IReadOnlyList<uint>? sent=null;var owner=new EnbWorldFlightCommands<int>(null,()=>{},_=>{});
        Task<bool> Activate(uint id,ulong identity,IReadOnlyList<uint> route){calls++;sent=route;return Task.FromResult(true);}
        var scene=Scene();
        await owner.RequestWarpAsync(scene with {IsTransitioning=true},true,Activate);
        await owner.RequestWarpAsync(scene,false,Activate);Assert.Equal(0,calls);
        await owner.RequestWarpAsync(scene with {Hud=scene.Hud with {CurrentTargetId=999}},true,Activate);
        Assert.Equal(1,calls);Assert.Empty(sent!);
        await owner.RequestWarpAsync(scene,true,Activate);Assert.Equal(2,calls);
    }
    private static EnbSceneSnapshot Scene()
    {
        var transform=new EnbSceneTransformSnapshot(0,Vector3.Zero,Quaternion.Identity,Vector3.Zero,0,Vector3.Zero,Vector3.UnitX);
        var ship=new EnbSceneObjectSnapshot(1,1,1,false,true,0,1620,1,0,0,0,0,false,"ship",
            new(true,1e9f,null),new(EnbSceneSpatialProviderKind.Fixed,transform,null),null,null,[],[],[],[],[],[],null,[],null,0,0){IsLocalShip=true};
        return new(1,0,1,1,null,false,null,
            new(null,null,false,null,null,null,null,false,[],1,new(1,1000000),[]),[],
            new(null,null,false,false){Cockpit=new(null,null,null,null,false){Warp=new(0,2,null,false,false,100,0)}},
            new(0,[]),[ship],[],null,null,[],[]);
    }
}
