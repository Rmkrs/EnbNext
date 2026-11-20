using System.Numerics;
using System.Collections.Immutable;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7ObjectToEffectCaptureTests
{
    [Theory]
    [InlineData("LineEffect",0,15)]
    [InlineData("LineEffect",1,17.5f)]
    [InlineData("ParticleJetEffect",0,15)]
    [InlineData("ParticleJetEffect",1,17.5f)]
    public void SegmentBoneContextUsesOriginalSourceHierarchy(string effectClass,short selector,float expected)
    {
        var session=new Net7Session(new Net7ServerOptions());
        session.Presentation.LoadEffectsText($"[EFFECT-65]\nEffectClass={effectClass}\n");
        var create=Write(0x4,w=>{w.Write(42u);w.Write(1f);w.Write((short)69);w.Write((byte)0);w.Write(new byte[12]);});
        session.World.Apply(create,0);
        var game=session.Gameplay;game.Apply(SeekTarget());var target=game.Effects[99];
        Net7EffectSourceControllerPose Node(float x)=>new(Matrix4x4.CreateTranslation(x,20,0),
            ImmutableDictionary.Create<string,Matrix4x4>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string,Matrix4x4>(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary.Create<string,Net7EffectSourceControllerPose>(StringComparer.Ordinal));
        var weapon=Node(15); // No EMITTER: retail returns the child root transform.
        var section=Node(12) with {Children=Node(0).Children.Add("~WEAP_03",weapon),
            Mounts=Node(0).Mounts.Add("~WEAP_04",Matrix4x4.CreateTranslation(18,20,0)),
            Hierarchy=Node(0).Hierarchy.Add("Tip",Matrix4x4.CreateTranslation(16,20,0))};
        var controller=Node(11) with {Children=Node(0).Children.Add("~02",section)};
        var controllers=ImmutableDictionary<(uint,ulong),Net7EffectSourceControllerPose>.Empty.Add((42,1),controller);
        game.PublishRetainedEffectPositions(System.Collections.Immutable.ImmutableDictionary<Net7EffectPoseKey,Net7RetainedEffectPosition>.Empty
            .Add(new(target.PoseContext,99,target.InstanceGeneration),new(new(10,0,0),new(20,0,0)){SourceControllers=controllers}));
        game.Apply(Directed(selector,"~02/~WEAP_03"));Assert.Equal(new Vector3(expected,0,0),game.Effects[10].CapturedTargetEffectPoint);
        foreach(var (chain,x) in new[]{("~02/~weap_03",12f),("~02/~WEAP_04/ignored",18f),
            ("~02/tIP/ignored",16f),("~02//ignored",12f),("~02/~WEAP_03/",15f),("absent/ignored",11f)})
        {
            game.Apply(Directed(selector,chain));
            Assert.Equal(new Vector3(selector==0?x:x*.5f+10,0,0),game.Effects[10].CapturedTargetEffectPoint);
        }
        session.World.MaterializeRemove(42);
        session.World.Apply(create,0); // Reused source ID must not consume its predecessor's hierarchy.
        game.Apply(Directed(selector,"~02/~WEAP_03"));Assert.Null(game.Effects[10].CapturedTargetEffectPoint);
    }
    [Fact]
    public void SessionUsesCurrentSpatialClockRatherThanTheEffectPacketTimestamp()
    {
        var clock=new CaptureClock {Now=500};
        var session=new Net7Session(new Net7ServerOptions(),clock:clock);
        session.Presentation.LoadEffectsText("[EFFECT-65]\nEffectClass=LineEffect\n");
        session.World.Apply(Write(0x4,w=>{w.Write(42u);w.Write(1f);w.Write((short)69);w.Write((byte)0);w.Write(new byte[12]);}),0);
        session.World.Apply(Write(0x8,w=>
        {w.Write(42u);w.Write(0u);Vector(w,new(10,20,0));w.Write(0f);w.Write(0f);w.Write(0f);w.Write(1f);Vector(w,new(.01f,0,0));}),0);
        var game=session.Gameplay;game.Apply(SeekTarget());var target=game.Effects[99];
        game.PublishRetainedEffectPositions(System.Collections.Immutable.ImmutableDictionary<Net7EffectPoseKey,Net7RetainedEffectPosition>.Empty
            .Add(new(target.PoseContext,99,target.InstanceGeneration),new(new(10,0,0),new(20,0,0))));
        game.Apply(Directed(0));Assert.Equal(new Vector3(15,0,0),game.Effects[10].CapturedTargetEffectPoint);
        clock.Now=800;game.Apply(Directed(0));Assert.Equal(new Vector3(18,0,0),game.Effects[10].CapturedTargetEffectPoint);
    }
    private sealed class CaptureClock : EnbNext.Net7.Timing.INet7RuntimeClock
    {
        public uint Now {get;set;}
        public uint GetMilliseconds()=>Now;
        public Task DelayAsync(TimeSpan delay,CancellationToken cancellationToken=default)=>Task.CompletedTask;
    }

    [Theory]
    [InlineData("LineEffect",0,0,10)]
    [InlineData("LineEffect",0,1,15)]
    [InlineData("LineEffect",15,0,15)]
    [InlineData("LineEffect",15,1,17.5f)]
    [InlineData("LineEffect",30,0,20)]
    [InlineData("LineEffect",30,1,20)]
    [InlineData("ParticleJetEffect",0,0,10)]
    [InlineData("ParticleJetEffect",0,1,15)]
    [InlineData("ParticleJetEffect",15,0,15)]
    [InlineData("ParticleJetEffect",15,1,17.5f)]
    [InlineData("ParticleJetEffect",30,0,20)]
    [InlineData("ParticleJetEffect",30,1,20)]
    public void SegmentGetterProjectsPacketSourceThenAppliesSelector(string effectClass,float sourceX,short selector,float expectedX)
    {
        var catalog=new Net7PresentationCatalogs();catalog.LoadEffectsText($"[EFFECT-65]\nEffectClass={effectClass}\n");
        var context=new Vector3(sourceX,20,0);int calls=0;
        var game=new Net7GameplayState(catalog,resolveEffectSourcePosition:id=>{Assert.Equal(42u,id);calls++;return context;});
        game.Apply(SeekTarget());var target=game.Effects[99];
        game.PublishRetainedEffectPositions(System.Collections.Immutable.ImmutableDictionary<Net7EffectPoseKey,Net7RetainedEffectPosition>.Empty
            .Add(new(target.PoseContext,99,target.InstanceGeneration),new(new(10,0,0),new(20,0,0))));
        game.Apply(Directed(selector));context=new(999,999,999);
        Assert.Equal(new Vector3(expectedX,0,0),game.Effects[10].CapturedTargetEffectPoint);Assert.Equal(1,calls);
        game.Apply(Directed(selector,"~WEAP_01"));
        Assert.Null(game.Effects[10].CapturedTargetEffectPoint);Assert.Equal(1,calls);
    }

    [Theory]
    [InlineData("LineEffect")]
    [InlineData("ParticleJetEffect")]
    [InlineData("RObjLookAtEffect")]
    public void CaptureKeepsNonfiniteGetterResult(string effectClass)
    {
        var catalog=new Net7PresentationCatalogs();catalog.LoadEffectsText($"[EFFECT-65]\nEffectClass={effectClass}\n");
        var game=new Net7GameplayState(catalog,resolveEffectSourcePosition:_=>new(float.NaN,0,0));
        game.Apply(SeekTarget());var target=game.Effects[99];
        var position=effectClass=="RObjLookAtEffect"?new Vector3(float.NaN,0,0):new Vector3(10,0,0);
        game.PublishRetainedEffectPositions(ImmutableDictionary<Net7EffectPoseKey,Net7RetainedEffectPosition>.Empty
            .Add(new(target.PoseContext,99,target.InstanceGeneration),new(position,new(20,0,0))));
        game.Apply(Directed(0));
        var captured=game.Effects[10].CapturedTargetEffectPoint;
        Assert.True(captured.HasValue);
        if(effectClass=="RObjLookAtEffect")Assert.True(float.IsNaN(captured.Value.X));
        else Assert.Equal(new Vector3(10,0,0),captured.Value);
        Assert.Equal(target.InstanceGeneration,game.Effects[10].TargetEffectGeneration);
    }

    [Fact]
    public void MissingSegmentSourceContextIsNotReplacedByAnEndpoint()
    {
        var catalog=new Net7PresentationCatalogs();catalog.LoadEffectsText("[EFFECT-65]\nEffectClass=LineEffect\n");
        var game=new Net7GameplayState(catalog);game.Apply(SeekTarget());var target=game.Effects[99];
        game.PublishRetainedEffectPositions(System.Collections.Immutable.ImmutableDictionary<Net7EffectPoseKey,Net7RetainedEffectPosition>.Empty
            .Add(new(target.PoseContext,99,target.InstanceGeneration),new(new(10,0,0),new(20,0,0))));
        game.Apply(Directed(0));Assert.Null(game.Effects[10].CapturedTargetEffectPoint);
    }
    [Theory]
    [InlineData("StraightSeekEffect",0)]
    [InlineData("StraightSeekEffect",1)]
    [InlineData("SpiralSeekEffect",0)]
    [InlineData("SpiralSeekEffect",-1)]
    [InlineData("SplineRandomSeekEffect",0)]
    [InlineData("SplineRandomSeekEffect",1)]
    [InlineData("LinearRandomSeekEffect",0)]
    [InlineData("LinearRandomSeekEffect",-1)]
    [InlineData("RObjLookAtEffect",0)]
    [InlineData("RObjLookAtEffect",1)]
    public void SeekReferenceCapturesRetainedRenderPositionOrBinaryMidpoint(string targetClass,short selector)
    {
        var catalog=new Net7PresentationCatalogs();catalog.LoadEffectsText($"[EFFECT-65]\nEffectClass={targetClass}\n");
        var game=new Net7GameplayState(catalog);game.Apply(SeekTarget());
        var target=game.Effects[99];var key=new Net7EffectPoseKey(target.PoseContext,99,target.InstanceGeneration);
        var frame=System.Collections.Immutable.ImmutableDictionary<Net7EffectPoseKey,Net7RetainedEffectPosition>.Empty
            .Add(key,new(new(10,20,30),new(70,80,90)));
        game.PublishRetainedEffectPositions(frame);game.Apply(Directed(selector));
        game.PublishRetainedEffectPositions(frame.SetItem(key,new(new(100,200,300),new(700,800,900))));
        Assert.Equal(selector==0?new Vector3(10,20,30):new Vector3(40,50,60),game.Effects[10].CapturedTargetEffectPoint);
        Assert.Equal(new Vector3(123,456,789),game.Effects[10].SpatialOffset);
    }
    [Fact]
    public void BinaryMidpointWithoutEndpointRemainsUnresolved()
    {
        var catalog=new Net7PresentationCatalogs();catalog.LoadEffectsText("[EFFECT-65]\nEffectClass=StraightSeekEffect\n");
        var game=new Net7GameplayState(catalog);game.Apply(SeekTarget());
        var target=game.Effects[99];
        game.PublishRetainedEffectPositions(System.Collections.Immutable.ImmutableDictionary<Net7EffectPoseKey,Net7RetainedEffectPosition>.Empty
            .Add(new(target.PoseContext,99,target.InstanceGeneration),new(new(10,20,30))));
        game.Apply(Directed(1));Assert.Null(game.Effects[10].CapturedTargetEffectPoint);
        game.Apply(Directed(0));Assert.Equal(new Vector3(10,20,30),game.Effects[10].CapturedTargetEffectPoint);
    }
    [Theory]
    [InlineData("RObjEffect")]
    [InlineData("SoundEffect")]
    [InlineData("PassEffect")]
    [InlineData("AttachEffect")]
    [InlineData("WarpEffect")]
    public void MovingUnaryCaptureReadsCurrentPublicationOnce(string targetClass)
    {
        var catalog=new Net7PresentationCatalogs();
        catalog.LoadEffectsText($"[EFFECT-65]\nEffectClass={targetClass}\n");
        var game=new Net7GameplayState(catalog);
        game.Apply(ObjectTarget());
        var target=game.Effects[99];
        var key=new Net7EffectPoseKey(target.PoseContext,99,target.InstanceGeneration);
        var positions=System.Collections.Immutable.ImmutableDictionary<Net7EffectPoseKey,EnbNext.Net7.Gameplay.Net7RetainedEffectPosition>.Empty.Add(key,new(new(10,20,30)));
        game.PublishRetainedEffectPositions(positions);
        game.Apply(Directed(1));
        game.PublishRetainedEffectPositions(positions.SetItem(key,new(new(90,80,70))));
        Assert.Equal(new Vector3(10,20,30),game.Effects[10].CapturedTargetEffectPoint);
        game.Apply(Directed(0));
        Assert.Equal(new Vector3(90,80,70),game.Effects[10].CapturedTargetEffectPoint);
        game.Apply(ObjectTarget());
        game.Apply(Directed(0));
        Assert.Null(game.Effects[10].CapturedTargetEffectPoint);
        var other=new Net7GameplayState(catalog);
        other.Apply(ObjectTarget());
        other.PublishRetainedEffectPositions(positions);
        other.Apply(Directed(0));
        Assert.Null(other.Effects[10].CapturedTargetEffectPoint);
    }

    [Fact]
    public void MissingMovingPoseCannotBeFilledByALaterPublication()
    {
        var catalog=new Net7PresentationCatalogs();catalog.LoadEffectsText("[EFFECT-65]\nEffectClass=RObjEffect\n");
        var game=new Net7GameplayState(catalog);game.Apply(ObjectTarget());game.Apply(Directed(0));
        var target=game.Effects[99];
        game.PublishRetainedEffectPositions(System.Collections.Immutable.ImmutableDictionary<Net7EffectPoseKey,EnbNext.Net7.Gameplay.Net7RetainedEffectPosition>.Empty
            .Add(new(target.PoseContext,99,target.InstanceGeneration),new(new(10,20,30))));
        Assert.Null(game.Effects[10].CapturedTargetEffectPoint);
    }

    [Theory]
    [InlineData("RObjEffect", 0)]
    [InlineData("RObjEffect", -1)]
    [InlineData("SoundEffect", 1)]
    public void CapturesUnaryPointAtReceiptAndIgnoresLaterReplacement(string targetClass, short auxiliary)
    {
        var catalog = new Net7PresentationCatalogs();
        catalog.LoadEffectsText($"[EFFECT-65]\nEffectClass={targetClass}\n[EFFECT-933]\nEffectClass=LineEffect\n");
        var game = new Net7GameplayState(catalog);
        var point = new Vector3(20, 40, 60);
        game.Apply(Point(point));
        ulong generation = game.Effects[99].InstanceGeneration;
        game.Apply(Directed(auxiliary));
        game.Apply(Point(new(999, 888, 777)));
        var captured = game.Effects[10];
        Assert.Equal(generation, captured.TargetEffectGeneration);
        Assert.Equal(point, captured.CapturedTargetEffectPoint);
        Assert.Equal(99u, captured.TargetId);
        Assert.False(captured.IsObjectToObject);
        Assert.Equal(Net7EffectGeometryKind.ObjectToEffect, captured.GeometryKind);
        // Raw fields remain available, but native 00735520 never applies the
        // wire offset to this coordinate and unary +14 returns NULL.
        Assert.Equal(new Vector3(123, 456, 789), captured.SpatialOffset);
        var scene = new EnbPresentationSnapshotPublisher().Publish(new Net7WorldState(), game.Effects, null, 0);
        var render = Assert.Single(scene.Effects, e => e.EffectId == 10);
        Assert.Equal(EnbSceneEffectGeometryKind.ObjectToPoint, render.GeometryKind);
        Assert.Equal(point, render.WorldPoint);
        Assert.Equal(Vector3.Zero, render.SpatialOffset);
        Assert.Equal(0u, render.TargetGameId);
    }

    [Fact]
    public void MissingReferenceDoesNotCaptureLaterArrival()
    {
        var game = new Net7GameplayState();
        game.Apply(Directed(0));
        game.Apply(Point(new(10, 20, 30)));
        Assert.Null(game.Effects[10].CapturedTargetEffectPoint);
        Assert.Null(game.Effects[10].TargetEffectGeneration);
        Assert.False(game.Effects[10].IsObjectToObject);
    }

    private static Packet Point(Vector3 point) => Write(0xA, w =>
    {
        w.Write(99u); w.Write(0u); Vector(w, point); w.Write((short)-1); w.Write((ushort)65);
        w.Write(1f); Vector(w, default);
    });
    private static Packet ObjectTarget() => Write(0x9, w =>
    { w.Write((byte)1);w.Write(77u);w.Write((ushort)65);w.Write(99u); });
    private static Packet SeekTarget() => Write(0xD,w =>
    {
        w.Write(99u);w.Write(0u);w.Write(77u);w.Write((byte)0);Vector(w,new(70,80,90));
        w.Write((short)3000);w.Write((ushort)65);w.Write(1f);Vector(w,default);w.Write(1f);
    });
    private static Packet Directed(short auxiliary,string bone="") => Write(0xC, w =>
    {
        w.Write(10u); w.Write(0u); w.Write(42u); w.Write(System.Text.Encoding.Latin1.GetBytes(bone)); w.Write((byte)0); w.Write(99u);
        w.Write((short)0); w.Write((ushort)933); Vector(w, new(123,456,789));
        w.Write(auxiliary); w.Write((short)7); w.Write(1f); Vector(w, default); w.Write(1f);
    });
    private static void Vector(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    private static Packet Write(ushort opcode, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.Latin1, true)) write(writer);
        return Packet.ForOpcode(opcode, stream.ToArray());
    }
}
