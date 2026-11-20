using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10EffectSoundTests
{
    [Theory]
    [InlineData("SoundEffect",true)]
    [InlineData("SoundEffect",false)]
    [InlineData("PassEffect",true)]
    [InlineData("PassEffect",false)]
    [InlineData("RObjEffect",true)]
    [InlineData("RObjEffect",false)]
    public void ObjectUnarySoundUsesRetainedBonePose(string effectClass,bool tracks)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass={effectClass}\nDuration=1\nSFX=voice.wav\nTrackSource={tracks}");
        var root=Root() with {GeometryKind=EnbSceneEffectGeometryKind.Object,BoneName="WEAPON"};
        var update=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0);
        var sound=Assert.Single(update.SoundNodes);
        var carrier=Assert.Single(update.VisualNodes);
        Assert.Equal(sound.Sequence,carrier.Sequence);
        Vector3 bone=new(12,34,56);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>new(9999),out var position,out var follows,out var error,
            findEffectPosition:_=>bone),error);
        Assert.Equal(bone,position);Assert.Equal(tracks,follows);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>Vector3.Zero,out _,out _,out _));
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>Vector3.Zero,out _,out _,out _,findEffectPosition:_=>new(float.NaN,0,0)));
    }

    [Fact]
    public void DelayedLinkedSoundLeafReceivesUnaryPoseCarrier()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=RObjEffect\nDuration=.25\nStartLinkID=2\nStartLinkDelay=.25\n[EFFECT-2]\nEffectClass=SoundEffect\nDuration=.5\nSFX=leaf.wav");
        var root=Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,
            TargetGameId=99,BoneName="WEAPON",SpatialOffset=new(3,4,5)};
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var parent=Assert.Single(scheduler.Update([root],catalog,0).VisualNodes,n=>n.Definition.DefinitionId==1);
        var update=scheduler.Update([root],catalog,250);
        var sound=Assert.Single(update.SoundNodes,n=>n.Active);
        var carrier=Assert.Single(update.VisualNodes,n=>n.Active);
        Assert.Equal(2,carrier.Definition.DefinitionId);
        Assert.Equal(sound.Sequence,carrier.Sequence);
        Assert.Equal(parent.Sequence,carrier.ParentSequence);
        Vector3 retained=new(10,20,30);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>new(9999),out var position,out _,out var error,
            findEffectPosition:n=>n.Sequence==carrier.Sequence?retained:null),error);
        Assert.Equal(retained,position);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>Vector3.One,out _,out _,out _));
        Assert.DoesNotContain(scheduler.Update([root],catalog,750).VisualNodes,n=>n.Active);
    }

    [Fact]
    public void LinkedExplosionSoundUsesRetainedBoundMatrixDespitePacketBoneAndOffset()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=StraightSeekEffect\nDuration=.1\nNextLinkID=90\nUnaryNextLinkToRObj=TRUE\n[EFFECT-90]\nEffectClass=RObjEffect\nBaseAssetID=531\nDuration=5\nInFrontOfObject=1\nSFX=plasma_explosion_effect_bigger.wav");
        var root=Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,
            TargetGameId=99,BoneName="WEAPON",SpatialOffset=new(3,4,5)};
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root],catalog,0);
        var node=Assert.Single(scheduler.Update([root],catalog,100).SoundNodes);
        Assert.Equal(90,node.Definition.DefinitionId);
        Assert.Equal(EnbCurrentRetailEffectLinkKind.NextLink,node.LinkKind);
        Vector3 retained=new(10,20,30);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>new(1000,2000,3000),
            out var actual,out _,out var error,findBonePosition:(_,_)=>new(4000,5000,6000),
            findEffectPosition:n=>n.Sequence==node.Sequence?retained:null),error);
        Assert.Equal(retained,actual);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>null,
            out actual,out _,out error,findEffectPosition:_=>retained),error);
        Assert.Equal(retained,actual);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>Vector3.One,out _,out _,out _));
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>Vector3.One,out _,out _,out _,
            findEffectPosition:_=>new(float.NaN,0,0)));
    }

    [Theory]
    [InlineData(true, 10)]
    [InlineData(false, 110)]
    public void LookAtSoundUsesAuthoredEndpoint(bool sourceBased,float expected)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass=RObjLookAtEffect\nSourceBased={sourceBased}\nSFX=x.wav\nDuration=1");
        var root=Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,TargetGameId=99};
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).SoundNodes);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,id=>new Vector3(id==42?10:110,0,0),out var point,out _,out var error),error);
        Assert.Equal(new Vector3(expected,0,0),point);
    }

    [Theory]
    [InlineData("RObjLookAtEffect")]
    [InlineData("ParticleJetEffect")]
    public void BinarySoundUsesRetainedInstancePositionWithoutEndpointFallback(string effectClass)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass={effectClass}\nSFX=x.wav\nDuration=1");
        var root=Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,TargetGameId=99,
            BoneName="WEAPON",SpatialOffset=new(100,200,300)};
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).SoundNodes);
        var retained=new Vector3(12,34,56);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>throw new Exception("Must not rebind packet endpoint"),
            out var actual,out _,out var error,findEffectPosition:_=>retained),error);
        Assert.Equal(retained,actual);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>Vector3.One,
            out _,out _,out _,findEffectPosition:_=>null));
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>Vector3.One,
            out _,out _,out _,findEffectPosition:_=>new(float.NaN,0,0)));
    }

    [Theory]
    [InlineData("RObjLookAtEffect",100)]
    [InlineData("ParticleJetEffect",40)]
    public void PointBinarySoundDoesNotLookupTargetObject(string effectClass,float expected)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass={effectClass}\nSFX=x.wav\nDuration=1");
        var root=Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToPoint,IsObjectToObject=false,TargetGameId=0,WorldPoint=new(100,0,0)};
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).SoundNodes);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,id=>id==root.SourceGameId?Vector3.Zero:throw new Exception("World endpoint has no object"),
            out var point,out _,out var error,listenerPosition:new(40,25,0)),error);
        Assert.Equal(new Vector3(expected,0,0),point);
    }

    [Theory]
    [InlineData(-10,0)]
    [InlineData(40,40)]
    [InlineData(120,100)]
    public void ParticleJetSoundProjectsCameraOntoSegment(float cameraX,float expected)
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=ParticleJetEffect\nSFX=x.wav\nDuration=1");
        var root=Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,TargetGameId=99};
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).SoundNodes);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,id=>new Vector3(id==42?0:100,0,0),out var point,out _,out var error,listenerPosition:new(cameraX,25,0)),error);
        Assert.Equal(new Vector3(expected,0,0),point);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>new Vector3(5,6,7),out point,out _,out error,listenerPosition:Vector3.Zero),error);
        Assert.Equal(new Vector3(5,6,7),point);
    }

    [Theory]
    [InlineData("StraightSeekEffect")]
    [InlineData("SplineRandomSeekEffect")]
    [InlineData("LinearRandomSeekEffect")]
    [InlineData("SpiralSeekEffect")]
    public void SeekSoundUsesCurrentRenderPositionInsteadOfEndpoints(string effectClass)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass={effectClass}\nSFX=nota_laser_11.wav\nDuration=1");
        var root=Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,TargetGameId=99};
        var update=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0);
        var node=Assert.Single(update.SoundNodes);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>Vector3.One,out _,out _,out _));
        Vector3 rendered=new(15,20,30);
        Vector3? Resolve(EnbCurrentRetailEffectSoundNode requested)
        {
            Assert.Equal(node.Sequence,requested.Sequence);
            Assert.Equal(root.InstanceGeneration,requested.Root.InstanceGeneration);
            return rendered;
        }
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>Vector3.One,out var first,out var follows,out _,findEffectPosition:Resolve));
        Assert.Equal(rendered,first); Assert.True(follows);
        rendered=new(60,80,100);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>null,out var next,out _,out _,findEffectPosition:Resolve));
        Assert.Equal(rendered,next);
        rendered=new(float.NaN,0,0);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>Vector3.One,out _,out _,out _,findEffectPosition:Resolve));
    }

    [Fact]
    public void TractorSegmentStartsAtAuthoredBoneWithoutFallingBackToShipCenter()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=LineEffect\nSFX=sn_tractor_beam_loop_2.wav\nDuration=INFINITE");
        var root=Root() with { GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject, IsObjectToObject=true, BoneName="TRACTOR" };
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).SoundNodes);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>Vector3.Zero,out _,out _,out _));
        Vector3 mount=new(40,10,5);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>Vector3.Zero,out var actual,out bool follows,out _,
            (id,bone)=>id==root.SourceGameId && bone=="TRACTOR" ? mount : null,listenerPosition:mount));
        Assert.Equal(mount,actual);
        Assert.True(follows);
    }
    [Theory]
    [InlineData(-20,0)] [InlineData(40,40)] [InlineData(120,100)]
    public void BeamSoundProjectsListenerOntoSegment(float listenerX,float expected)
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=LineEffect\nSFX=x.wav\nDuration=1");
        var root=Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,TargetGameId=99,SpatialOffset=new(10,0,0)};
        var sound=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).SoundNodes);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,id=>id==42?Vector3.Zero:new(90,0,0),out var position,out _,out var error,listenerPosition:new(listenerX,20,0)),error);
        Assert.Equal(new Vector3(expected,0,0),position);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>Vector3.One,out _,out _,out _));
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>Vector3.One,out _,out _,out _,findEffectPosition:_=>null,listenerPosition:Vector3.Zero));
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>new Vector3(9999),out position,out _,out error,findEffectPosition:_=>new Vector3(25,0,0)),error);
        Assert.Equal(new Vector3(25,0,0),position);
        Assert.Equal(Vector3.One,EnbCurrentRetailEffectSoundRuntime.ClosestLinePosition(Vector3.One,Vector3.One,new(30,40,50)));
    }
    [Theory]
    [InlineData("LineEffect")]
    [InlineData("ParticleJetEffect")]
    public void UnorderedSoundProjectionRetainsStartEndpoint(string effectClass)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass={effectClass}\nSFX=x.wav\nDuration=1");
        var root=Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,TargetGameId=99};
        var sound=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).SoundNodes);
        Vector3 start=new(12,34,56);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,id=>id==42?start:new(float.NaN,0,0),
            out var position,out _,out var error,listenerPosition:Vector3.Zero),error);
        Assert.Equal(start,position);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,id=>id==42?start:new(100,200,300),
            out position,out _,out error,listenerPosition:new(float.NaN,0,0)),error);
        Assert.Equal(start,position);
    }

    [Fact]
    public void SoundNodesFollowActualStartLinksExpiryAndDoNotReplay()
    {
        var catalog=Catalog("""
            [EFFECT-1]
            EffectClass=PassEffect
            SFX=phase.wav
            StartDelay=.1
            Duration=.2
            StartLinkID=2
            StartLinkDelay=.05
            NextLinkID=3
            [EFFECT-2]
            EffectClass=SoundEffect
            SFX=loop.wav
            Duration=INFINITE
            [EFFECT-3]
            EffectClass=RObjEffect
            SFX=flash.wav
            Duration=.1
            """);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime(); var root=Root();
        var scheduled=Assert.Single(scheduler.Update([root],catalog,99).SoundNodes);
        Assert.False(scheduled.Active);
        var start=scheduler.Update([root],catalog,120).SoundNodes;
        Assert.True(start[0].Active); Assert.Equal(120u,start[0].ActualStartTick);
        Assert.False(start[1].Active);
        Assert.Equal(scheduled.Sequence,start[0].Sequence);
        var next=scheduler.Update([root],catalog,171).SoundNodes;
        Assert.True(next[1].Active); Assert.Equal(171u,next[1].ActualStartTick);
        Assert.NotEqual(next[0].Sequence,next[1].Sequence);
        var expired=scheduler.Update([root],catalog,320).SoundNodes;
        Assert.DoesNotContain(expired,n=>n.Definition.DefinitionId==1);
        Assert.Contains(expired,n=>n.Definition.DefinitionId==3 && n.ActualStartTick==320);
        Assert.Equal(next[1].Sequence,expired.Single(n=>n.Definition.DefinitionId==2).Sequence);
        Assert.Single(scheduler.Update([root],catalog,421).SoundNodes);
        Assert.Empty(scheduler.Update([],catalog,422).SoundNodes);
    }

    [Fact]
    public void EffectIdReuseAndClearProduceNewSoundLifetimes()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=SoundEffect\nSFX=x.wav\nDuration=INFINITE");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime(); var root=Root();
        var first=Assert.Single(scheduler.Update([root],catalog,0).SoundNodes);
        var second=Assert.Single(scheduler.Update([root with { InstanceGeneration=2 }],catalog,1).SoundNodes);
        Assert.NotEqual(first.Sequence,second.Sequence);
        Assert.Equal(2ul,second.Root.InstanceGeneration);
        scheduler.Clear(); Assert.Empty(scheduler.Update([],catalog,2).SoundNodes);
    }

    [Fact]
    public void TargetLinkedUnarySoundBindsTargetAndStaysIndependentOfVisibility()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=LineEffect\nDuration=1\nStartLinkID=2\nUnaryStartLinkToTarget=TRUE\n[EFFECT-2]\nEffectClass=SoundEffect\nSFX=x.wav\nDuration=1");
        var root=Root() with { TargetGameId=9,GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject };
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).SoundNodes);
        Assert.Equal(9u,node.BoundGameId);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,id=>id==9?new Vector3(1,2,3):null,out var position,out bool follows,out _));
        Assert.Equal(new Vector3(1,2,3),position); Assert.True(follows);
    }

    [Fact]
    public void UnsupportedTriggerIsExplicitNotListenerFallback()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=SoundEffect\nTriggerSFXRadius=4000\nSFX=x.wav\nDuration=1");
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([Root()],catalog,0).SoundNodes);
        Assert.False(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,_=>Vector3.Zero,out _,out _,out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void DirectFileFallbackUsesInitializedSoundDefaultsAndDoesNotInventLoopOverride()
    {
        var catalog=EnbRetailSoundCatalog.ParseText("[SOUND_DEFAULTS]\nDefault_Volume=.25\nDefault_DropOffRadius=5000\n[authored]\nSoundFilename=a.wav\nSoundLoopCount=5");
        var file=catalog.GetDefinitionOrFile("missing-section.wav");
        Assert.Equal("missing-section.wav",Assert.Single(file.Files).LogicalResourceName);
        Assert.Equal(.25f,file.EffectiveVolume); Assert.Equal(5000,file.EffectiveDropOffRadius);
        Assert.Equal(1,file.EffectiveLoopCount);
        Assert.Equal(5,catalog.GetDefinitionOrFile("authored").EffectiveLoopCount);
        Assert.Null(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(catalog.GetDefinitionOrFile("authored")));
        Assert.Null(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(file with { MinPitchShift=.1f }));
        Assert.NotNull(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(file with { MinPitchShift=1f }));
        Assert.Null(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(file with { SoundType=2 }));
        Assert.NotNull(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(file with { SoundType=0 }));
        Assert.NotNull(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(file with { SoundType=3 }));
    }

    internal static EnbRetailPresentationCatalog Catalog(string text)
    { var catalog=new EnbRetailPresentationCatalog(); catalog.LoadEffectsText(text); return catalog; }

    internal static EnbSceneEffectSnapshot Root() => new(1,1,42,0,1,0,0,"",0,false,1,
        default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
}
