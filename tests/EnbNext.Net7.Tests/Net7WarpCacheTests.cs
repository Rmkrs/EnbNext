using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class Net7WarpCacheTests
{
    [Fact]
    public void CockpitReactionsUseFinalPrivateValueAndRetainedRetailGates()
    {
        var world = new Net7WorldState(); world.MaterializeCreate(new(7, 1, 1620, 1, 0, 0, 0));
        var builder = new EnbPresentationSnapshotBuilder(); var runtime = new EnbCockpitWarpCallbacks();
        EnbSceneShipPresentationSnapshot Frame(int mode = 0, EnbRetailCameraState camera = EnbRetailCameraState.Follow, bool incapacitated = false, bool active = true)
        {
            var scene = builder.Build(world, null, 0);
            var status = EnbNext.Net7.Gameplay.Net7WarpStatus.Read(world.Objects.FindFirst(7)!.AuxData, mode) with { Incapacitated = incapacitated };
            scene = scene with { Objects = [scene.Objects.Single() with { IsLocalShip = true }],
                Hud = scene.Hud with { Cockpit = new(null, null, null, status.MaximumSpeed, false) { Warp = status } } };
            return runtime.ApplyFrame(scene, camera, active).Objects.Single().ShipPresentation!;
        }
        Frame(); Apply(world, [(38, 2)]); Apply(world, [(38, 3)]);
        Assert.Equal(3, Assert.Single(Frame().PrivateWarpChanges).State);
        Apply(world, [(38, 3)]);
        Assert.Equal(2, Frame().PrivateWarpChanges.Length); // Same value is a new native callback.
        Assert.Equal(2, Frame().PrivateWarpChanges.Length);
        Apply(world, [(38, 2)]);
        Assert.Equal(2, Frame(mode: 1).PrivateWarpChanges.Length);
        Assert.Equal(2, Frame().PrivateWarpChanges.Length); // Planet did not queue a later callback.
        Apply(world, [(38, 2), (40, 2)]);
        Assert.Equal(3, Frame().PrivateWarpChanges.Length);
        Apply(world, [(38, 3)]);
        Assert.Equal(3, Frame(camera: EnbRetailCameraState.Dock).PrivateWarpChanges.Length);
        Assert.Equal(3, Frame(camera: EnbRetailCameraState.Dock).PrivateWarpChanges.Length);
        var resumed = Frame();
        Assert.Equal(4, resumed.PrivateWarpChanges.Length); Assert.Equal(3, resumed.PrivateWarpChanges[^1].State);
        Assert.Equal(4, Frame().PrivateWarpChanges.Length);
        Apply(world, [(38, 2)]); Frame(incapacitated: true); // 581670 latches incap after the warp switch.
        Apply(world, [(38, 3)]); Assert.Equal(5, Frame(incapacitated: true).PrivateWarpChanges.Length);
        Assert.Equal(5, Frame().PrivateWarpChanges.Length); // Recovery also occurs after the switch.
        Assert.Equal(6, Frame().PrivateWarpChanges.Length);
        // An engine update refreshes the retained warp value even without another warp packet.
        Apply(world, [(20, BitConverter.SingleToInt32Bits(100))]);
        Assert.Equal(7, Frame().PrivateWarpChanges.Length);
        Apply(world, [(38, 2)]);
        Assert.Equal(7, Frame(active: false).PrivateWarpChanges.Length);
        Assert.Equal(7, Frame().PrivateWarpChanges.Length);
    }

    [Fact]
    public void GlobalSideEffectsConsumeFrameCallbacksRatherThanPacketHistory()
    {
        var world=new Net7WorldState();world.MaterializeCreate(new(7,1,1620,1,0,0,0));
        var builder=new EnbPresentationSnapshotBuilder();var cache=new EnbShipWarpCacheRuntime();
        var plume=new EnbWarpPlumeRuntime();var animation=new EnbShipWarpAnimationRuntime();var sound=new EnbWarpSoundRuntime();
        EnbSceneObjectSnapshot Frame()
        {
            var scene=cache.ApplyFrame(builder.Build(world,null,0));
            plume.Update(scene.Objects);animation.Update(scene.Objects,0);sound.Update(scene.Objects);
            return scene.Objects.Single();
        }
        Frame();
        Apply(world,[(39,1),(1910,2),(1922,4)]);Apply(world,[(39,2)]);
        var entered=Frame();
        Assert.Equal(2,Assert.Single(entered.ShipPresentation!.WarpChanges).State);
        Assert.Null(plume.Get(entered.RenderIdentity));Assert.Null(animation.Get(entered.RenderIdentity));
        var cues=sound.Update([entered]);Assert.Single(cues);
        Assert.Equal("warp_enter_2",cues[0].Definition.Sfx);
        Apply(world,[(39,1)]);var charging=Frame();
        Assert.NotNull(animation.Get(charging.RenderIdentity));
        Apply(world,[(39,1)]);Apply(world,[(1910,5),(1922,7)]);
        var repeated=Frame();
        Assert.Equal(3,repeated.ShipPresentation!.WarpChanges.Length);
        Assert.Equal(5,repeated.ShipPresentation.WarpChanges[^1].PowerLevel);
        Assert.Equal(7,repeated.ShipPresentation.WarpChanges[^1].EngineTrailType);
        Assert.Equal(3,sound.Update([repeated]).Count(n=>n.Definition.Sfx?.StartsWith("warp_")==true));
        Apply(world,[(39,2)]);var travel=Frame();var active=plume.Get(travel.RenderIdentity);Assert.NotNull(active);
        world.Objects.FindFirst(7)!.SuppressGlobalWarpUpdates();
        Apply(world,[(39,3)]);var suppressed=Frame();
        Assert.Equal(travel.ShipPresentation!.WarpChanges,suppressed.ShipPresentation!.WarpChanges);
        Assert.Equal(active,plume.Get(suppressed.RenderIdentity));
        Assert.DoesNotContain(sound.Update([suppressed]),n=>n.Definition.Sfx?.StartsWith("warp_exit")==true);
        Assert.Equal(suppressed.ShipPresentation.WarpChanges,Frame().ShipPresentation!.WarpChanges);
    }
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void OrderedCallbacksAndGuardReachBothProductionConsumers(byte type)
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new(7, type, 1620, 1, 0, 0, 0));
        var obj = world.Objects.FindFirst(7)!;
        var builder = new EnbPresentationSnapshotBuilder();
        var runtime = new EnbShipWarpCacheRuntime();
        void Check(int cache, bool exhaust, bool audio)
        {
            var input = builder.Build(world, null, 0);
            var first = runtime.ApplyFrame(input).Objects.Single();
            Assert.Equal(cache, first.ShipPresentation!.CachedWarpState);
            var scene = runtime.ApplyFrame(input).Objects.Single();
            Assert.Equal(cache, scene.ShipPresentation!.CachedWarpState);
            Assert.Equal(exhaust, EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(scene).ShouldDraw);
            Assert.Equal(audio, EnbCurrentRetailEngineSoundRuntime.ShouldPlay(scene));
        }
        Check(0, false, false);
        Apply(world, [(38, 2), (39, 3), (1921, 0)]); Check(3, false, false);
        Apply(world, [(38, 2)]); Check(2, true, false); // Old global 3 must not run again.
        Apply(world, [(1921, 1)]); Check(2, true, false);
        Apply(world, [(39, 3)]); Check(3, true, true); // Repeated value is a fresh callback.
        Apply(world, [(39, 1), (38, 3)]); Check(1, true, false); // Private then global, irrespective of wire order.
        Apply(world, [(38, 3)]); Check(3, true, true);
        Apply(world, [(39, 2)]); Apply(world, [(38, 3)]);
        var coalesced = runtime.ApplyFrame(builder.Build(world, null, 0)).Objects.Single();
        Assert.Equal(3, coalesced.ShipPresentation!.EngineWarpState);
        Assert.Equal(2, coalesced.ShipPresentation.CachedWarpState);
        Check(2, true, false);
        Apply(world, [(38, 3)]); Check(3, true, true);
        obj.SuppressGlobalWarpUpdates();
        Apply(world, [(39, 2)]); Check(3, true, true);
        Apply(world, [(38, 2), (39, 0), (1921, 0)]); Check(2, true, false);
        Apply(world, [(38, 3)]); Check(3, false, false);
        Apply(world, [], stateTwo: 39); Check(3, false, false);
        Apply(world, [], stateZero: 38); Check(3, false, false);
        var bad = PacketFor([(38, 1)]).Payload.ToArray()[..^1];
        world.Apply(Packet.ForOpcode(0x001B, bad), 0);
        Check(1, false, false); // Completed callback precedes the short trailing read.
        world.MaterializeRemove(7);
        world.MaterializeCreate(new(7, type, 1620, 1, 0, 0, 0));
        obj = world.Objects.FindFirst(7)!;
        Assert.False(obj.GlobalWarpUpdatesSuppressed);
        Check(0, false, false);
        Apply(world, [(39, 2), (1921, 0)]); Check(2, true, false);
    }

    [Fact]
    public void CompactWarpInputsSurviveVisualAndCameraOverridesWithoutMutatingQueuedFrames()
    {
        var world=new Net7WorldState();world.MaterializeCreate(new(7,1,1620,1,0,0,0));
        var builder=new EnbPresentationSnapshotBuilder();var cache=new EnbShipWarpCacheRuntime();
        var cockpit=new EnbCockpitWarpCallbacks();
        EnbSceneSnapshot Frame()
        {
            var scene=builder.Build(world,null,0);
            var obj=scene.Objects.Single() with {IsLocalShip=true,RenderPoseSlot=0};
            var status=EnbNext.Net7.Gameplay.Net7WarpStatus.Read(world.Objects.FindFirst(7)!.AuxData,0);
            scene=scene with {Objects=[obj],RenderPoses=[EnbRenderPose.FromObject(obj)],
                Hud=scene.Hud with {Cockpit=new(null,null,null,status.MaximumSpeed,false){Warp=status}}};
            scene=cockpit.ApplyFrame(cache.ApplyFrame(scene),EnbRetailCameraState.Follow);
            var updates=scene.RenderPoses;
            updates.Write(scene.Objects[0] with {ShipVisualBankRadians=.5f});
            scene=scene with {RenderPoses=updates};
            var ship=scene.Objects[0].ShipPresentation!;var pose=scene.RenderPoses[0];
            Assert.True(pose.HasWarpInputs);Assert.True(pose.HasPrivateWarpInputs);
            Assert.Equal(ship.EngineWarpState,pose.EngineWarpState);Assert.Equal(ship.CachedWarpState,pose.CachedWarpState);
            Assert.Equal(ship.WarpChanges,pose.WarpChanges);Assert.Equal(ship.PrivateWarpChanges,pose.PrivateWarpChanges);
            Assert.Equal(.5f,pose.ShipVisualBankRadians);
            return scene;
        }
        Frame();Apply(world,[(38,2),(39,3)]);var queued=Frame();
        Assert.Equal(0,queued.RenderPoses[0].EngineWarpState);Assert.Equal(3,queued.RenderPoses[0].CachedWarpState);
        Apply(world,[(38,3),(39,2)]);var next=Frame();
        Assert.Equal(3,next.RenderPoses[0].EngineWarpState);Assert.Equal(2,next.RenderPoses[0].CachedWarpState);
        Assert.Equal(3,queued.RenderPoses[0].CachedWarpState);Assert.Single(queued.RenderPoses[0].WarpChanges);
        Assert.Equal(2,next.RenderPoses[0].WarpChanges.Length);Assert.Equal(2,next.RenderPoses[0].PrivateWarpChanges.Length);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DecodedBundlePublishesWarpEffectWithCompletedShipState(bool batch)
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1",FeatureFlags=0});
        session.World.IsAvatarViewActive=()=>false;
        session.World.MaterializeCreate(new(7,1,1620,1,0,0,0));
        Apply(session.World,[(38,1),(39,1)]);
        var publisher=new EnbPresentationSnapshotPublisher();var cache=new EnbShipWarpCacheRuntime();
        var fade=new EnbWarpTunnelFade();int frames=0;
        session.PresentationStateChanged+=time=>
        {
            publisher.Publish(session.World,session.Gameplay.Effects,null,time);
            var scene=cache.ApplyFrame(publisher.Latest!);
            if(scene.Effects.Length!=0)fade.Update((uint)++frames,scene.Objects.Single().ShipPresentation!.CachedWarpState==2);
        };
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var transport=(EnbNext.Net7.Transport.Net7TransportState)typeof(Net7Session).GetField("_transportState",flags)!.GetValue(session)!;
        transport.CommitValidatedFixedSessionId(7);transport.CommitGameplayPort(3501);transport.SetGameplayForwardingActive(true);
        typeof(Net7Session).GetField("_gameplayClientTargetActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, true);
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",flags)!;
        var boundary=typeof(Net7Session).GetMethod("SetPresentationBatch",flags)!.CreateDelegate<Action<bool>>(session);
        await using var engine=new EnbNext.Net7.Reliability.Net7ReliabilityEngine(new(){FeatureFlags=0},
            new EnbNext.Net7.Timing.SystemNet7RuntimeClock(),()=>true,(_,_,_)=>Task.FromResult(false),
            (_,_)=>Task.FromResult(EnbNext.Net7.Reliability.Net7DecodedPacketDisposition.Unhandled),
            async(packet,source,token)=>{await (Task)dispatch.Invoke(session,[packet,source,token])!;await Task.Yield();},
            presentationBatch:batch?boundary:null);
        engine.PrepareForMasterLogin();
        var effect=Packet.ForOpcode(9,Convert.FromHexString("030700000017000A00000000000000"));
        byte[] Bundle(params Packet[] packets)
        {
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
            foreach(var packet in packets){writer.Write((ushort)(4+packet.Payload.Length));writer.Write(packet.Header.Opcode);writer.Write(packet.Payload.Span);}
            return stream.ToArray();
        }
        await engine.AcceptOuterAsync(new(0x2016,7,0,Bundle(effect,PacketFor([(38,2),(39,2)]))));
        Assert.Equal(batch?1:2,frames);Assert.Equal(!batch,fade.Ended);
        Assert.Equal(batch?.05f:0,fade.Brightness);
        // A later ordinary packet still publishes, and normal exit retires the cone.
        await engine.AcceptOuterAsync(new(0x2016,7,1,Bundle(PacketFor([(38,3),(39,3)]))));
        Assert.True(fade.Ended);Assert.Equal(batch?2:3,frames);
        // A malformed trailing packet cannot retain the presentation batch.
        var remove=Packet.ForOpcode(15,BitConverter.GetBytes(10u));
        await engine.AcceptOuterAsync(new(0x2016,7,2,Bundle(remove).Concat(new byte[]{0}).ToArray()));
        Assert.Empty(publisher.Latest!.Effects);
        await (Task)dispatch.Invoke(session,[effect,"decoded-inner",CancellationToken.None])!;
        Assert.Single(publisher.Latest!.Effects);

    }

    private static void Apply(Net7WorldState world, (int Id, int Value)[] values, int? stateTwo = null, int? stateZero = null)
        => world.Apply(PacketFor(values, stateTwo, stateZero), 0);
    private static Packet PacketFor((int Id, int Value)[] values, int? stateTwo = null, int? stateZero = null)
    {
        using var delta = new MemoryStream(); using var writer = new BinaryWriter(delta);
        writer.Write((byte)0); writer.Write(values.Length);
        foreach (var (id, value) in values) { writer.Write(id); writer.Write(value); }
        writer.Write(stateTwo.HasValue ? 1 : 0); if (stateTwo.HasValue) writer.Write(stateTwo.Value);
        writer.Write(stateZero.HasValue ? 1 : 0); if (stateZero.HasValue) writer.Write(stateZero.Value);
        using var envelope = new MemoryStream(); using var header = new BinaryWriter(envelope);
        header.Write(7u); header.Write((ushort)delta.Length); header.Write(delta.ToArray());
        return Packet.ForOpcode(0x001B, envelope.ToArray());
    }
}
