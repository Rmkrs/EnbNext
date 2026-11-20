using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLineBindingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FirstRenderAfterPauseUsesOriginalRetainedEndpoints(bool replaced)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=LineEffect\nDuration=1\nSFX=test.wav");
        var source=Owner(42,1,10);var target=Owner(43,2,100);
        var root=new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"",0,true,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null)
        {InitialSourceObject=source,LastSourceObject=source,PublishedEndpoints=new(source,target)};
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var frame=scheduler.Update([root],catalog,100);
        var node=Assert.Single(frame.VisualNodes);var runtime=new EnbCurrentRetailLineBindingRuntime();
        IEnbRenderObject[] objects=replaced?[Owner(42,3,900),Owner(43,4,999)]:[];
        runtime.Update(frame.VisualNodes,objects,(o,_)=>Matrix4x4.CreateTranslation(o.Spatial!.Transform.Position));
        var binding=Assert.IsType<EnbCurrentRetailLineBindingRuntime.Binding>(runtime.Resolve(node,out var error));
        Assert.Null(error);Assert.True(binding.SourceDetached);Assert.True(binding.TargetDetached);
        Assert.Equal(10,binding.Start.X);Assert.Equal(100,binding.End.X);
        var sound=Assert.Single(frame.SoundNodes);
        Assert.Equal(new Vector3(50,0,0),runtime.SoundPosition(sound,new(50,20,0)));
        runtime.Update(frame.VisualNodes,[Owner(42,5,1000),Owner(43,6,2000)],(_,_)=>throw new Exception("Detached endpoint was refreshed"));
        Assert.Equal(binding,runtime.Resolve(node,out _));
    }

    [Fact]
    public void OrderedLineUpdatesPreserveOtherBindingsAndErrorsWithoutUpdatingThemAgain()
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=LineEffect\nDuration=1");
        var root=new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"",0,true,1,default,0,0,true,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var first=new EnbCurrentRetailEffectOwner(1,catalog.FindEffect(1)!,root,42,true,0);
        var second=EffectOwnerFixture.Copy(first,copy=>{copy.Root=root with {EffectId=2};});
        var runtime=new EnbCurrentRetailLineBindingRuntime();int radiusReads=0;
        void Update(EnbCurrentRetailEffectOwner? selected,bool fail=false)=>runtime.Update([first,second],[Owner(42,1,0),Owner(43,2,100)],
            (_,_)=>fail?throw new InvalidDataException("fixture failure"):Matrix4x4.Identity,
            targetRadius:_=>{radiusReads++;return 20;},updateNode:n=>ReferenceEquals(n,selected));
        Update(null);Assert.Equal(0,runtime.Count);
        Update(first,true);Assert.Null(runtime.Resolve(first,out var error));Assert.Equal("fixture failure",error);
        Update(second);Assert.NotNull(runtime.Resolve(second,out _));Assert.Null(runtime.Resolve(first,out error));Assert.Equal("fixture failure",error);
        Update(first);Assert.NotNull(runtime.Resolve(first,out error));Assert.Null(error);Assert.Equal(2,radiusReads);
        var firstEnd=runtime.Resolve(first,out _)!.End;
        Update(second);Assert.Equal(firstEnd,runtime.Resolve(first,out _)!.End);Assert.Equal(3,radiusReads);
        runtime.Update([],[],(_,_)=>Matrix4x4.Identity);Assert.Equal(0,runtime.Count);
    }

    [Theory]
    [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void CameraFlagsSurviveRemovalButAreNotInheritedByCoordinateBoundChildren(bool localSource,bool localTarget)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=LineEffect\nDuration=.5\nNextLinkID=2\n[EFFECT-2]\nEffectClass=LineEffect\nDuration=1");
        var root=new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"BONE",0,true,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();var runtime=new EnbCurrentRetailLineBindingRuntime();
        Matrix4x4 Pose(IEnbRenderObject o,string bone)=>Matrix4x4.CreateTranslation(o.Spatial!.Transform.Position);
        var nodes=scheduler.Update([root],catalog,0).VisualNodes;var node=Assert.Single(nodes);
        runtime.Update(nodes,[Owner(42,1,0) with {IsLocalShip=localSource},Owner(43,2,3) with {IsLocalShip=localTarget}],Pose);
        runtime.Update(nodes,[],Pose);
        var saved=runtime.Resolve(node,out _)!;
        Assert.Equal(localSource,saved.CameraSourceIsLocal);Assert.Equal(localTarget,saved.CameraTargetIsLocal);
        nodes=scheduler.Update([root],catalog,500).VisualNodes;runtime.Update(nodes,[],Pose);
        var child=runtime.Resolve(Assert.Single(nodes),out _)!;
        Assert.False(child.CameraSourceIsLocal);Assert.False(child.CameraTargetIsLocal);
    }

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)]
    [InlineData(true,false)] [InlineData(true,true)]
    public void DelayedLineRefreshesBothEndpointsAtActivationBeforeApplyingTrackingFlags(bool sourceTrack,bool targetTrack)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=LineEffect\nDuration=2\nStartDelay=.5\nTrackSource={sourceTrack}\nTrackTarget={targetTrack}");
        var root=new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"",0,true,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();var runtime=new EnbCurrentRetailLineBindingRuntime();
        Matrix4x4 Pose(IEnbRenderObject o,string bone)=>Matrix4x4.CreateTranslation(o.Spatial!.Transform.Position);
        var node=Assert.Single(scheduler.Update([root],catalog,0).VisualNodes);
        runtime.Update([node],[Owner(42,1,10),Owner(43,2,100)],Pose);
        runtime.Update([node],[Owner(42,1,20),Owner(43,2,200)],Pose);
        Assert.Equal(10,runtime.Resolve(node,out _)!.Start.X);Assert.Equal(100,runtime.Resolve(node,out _)!.End.X);
        node=Assert.Single(scheduler.Update([root],catalog,500).VisualNodes);
        runtime.Update([node],[Owner(42,1,30),Owner(43,2,300)],Pose);
        Assert.Equal(30,runtime.Resolve(node,out _)!.Start.X);Assert.Equal(300,runtime.Resolve(node,out _)!.End.X);
        runtime.Update([node],[Owner(42,1,40),Owner(43,2,400)],Pose);
        Assert.Equal(sourceTrack?40:30,runtime.Resolve(node,out _)!.Start.X);
        Assert.Equal(targetTrack?400:300,runtime.Resolve(node,out _)!.End.X);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LineIntersectionIsActiveRootOnlyAndObservesALiveUntrackedTarget(bool child)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=LineEffect\nDuration=2\nTrackSource=FALSE\nTrackTarget=FALSE");
        var root=new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"",0,true,1,default,0,0,true,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var node=new EnbCurrentRetailEffectOwner(1,catalog.FindEffect(1)!,root,42,false,500)
            {LinkKind=child?EnbCurrentRetailEffectLinkKind.NextLink:EnbCurrentRetailEffectLinkKind.Root};
        var runtime=new EnbCurrentRetailLineBindingRuntime();
        Matrix4x4 Pose(IEnbRenderObject o,string bone)=>Matrix4x4.CreateTranslation(o.Spatial!.Transform.Position);
        void Update(float source,float target,ulong identity=2)=>runtime.Update([node],[Owner(42,1,source),Owner(43,identity,target)],Pose,targetRadius:_=>20);
        Update(0,100);Assert.Equal(100,runtime.Resolve(node,out _)!.End.X);
        node=EffectOwnerFixture.Copy(node,copy=>{copy.Active=true;});Update(10,200);
        Assert.InRange(MathF.Abs(runtime.Resolve(node,out _)!.End.X-(child?200:180)),0,.001f);
        Update(20,150);
        Assert.Equal(10,runtime.Resolve(node,out _)!.Start.X);
        Assert.InRange(MathF.Abs(runtime.Resolve(node,out _)!.End.X-(child?200:130)),0,.001f);
        var retained=runtime.Resolve(node,out _)!.End;
        Update(30,100,3);Assert.Equal(retained,runtime.Resolve(node,out _)!.End);Assert.True(runtime.Resolve(node,out _)!.TargetDetached);
    }

    [Fact]
    public void LineCanInheritCoordinatesFromANonLineBinaryParent()
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=RObjLookAtEffect\nBaseAssetID=75\nDuration=0.2\nNextLinkID=2\n[EFFECT-2]\nEffectClass=LineEffect\nBaseAssetID=67\nDuration=1");
        var root=new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"",0,true,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root],catalog,0);
        var child=Assert.Single(scheduler.Update([root],catalog,200).VisualNodes);
        var runtime=new EnbCurrentRetailLineBindingRuntime();
        var parent=new EnbCurrentRetailLineBindingRuntime.Binding(Owner(42,1,10),Owner(43,2,100),new(30,0,0),new(300,0,0),true,true);
        runtime.Update([child],[Owner(42,3,900),Owner(43,4,1000)],(_,_)=>throw new Exception("Coordinate binding must not resolve replacement bone"),
            node=>node.ParentSequence==1?parent:null);
        var bound=runtime.Resolve(child,out var error)!;
        Assert.Null(error);Assert.Equal(parent.Start,bound.Start);Assert.Equal(parent.End,bound.End);
        Assert.True(bound.SourceDetached);Assert.True(bound.TargetDetached);
        Assert.Equal(1ul,bound.Source.RenderIdentity);Assert.Equal(2ul,bound.Target!.RenderIdentity);
        runtime.Update([],[],(_,_)=>Matrix4x4.Identity);Assert.Equal(0,runtime.Count);
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)]
    [InlineData(false,true)] [InlineData(true,true)]
    public void SuccessiveBeamsInheritDetachedCoordinatesButCaptureSurvivingObjects(bool reuse,bool loseSource)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=LineEffect\nBaseAssetID=67\nDuration=0.2\nNextLinkID=2\n[EFFECT-2]\nEffectClass=LineEffect\nBaseAssetID=67\nDuration=1\nStartLinkID=3\n[EFFECT-3]\nEffectClass=LineEffect\nBaseAssetID=67\nDuration=1");
        var root=new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"",0,true,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var runtime=new EnbCurrentRetailLineBindingRuntime();
        Matrix4x4 Pose(IEnbRenderObject o,string bone)=>Matrix4x4.CreateTranslation(o.Spatial!.Transform.Position);
        runtime.Update(scheduler.Update([root],catalog,0).VisualNodes,[Owner(42,1,10),Owner(43,2,100)],Pose);
        var children=scheduler.Update([root],catalog,200).VisualNodes;
        Assert.Equal(2,children.Count);
        runtime.Update(children,loseSource?(reuse?[Owner(42,3,900),Owner(43,2,200)]:[Owner(43,2,200)]):
            reuse?[Owner(42,1,20),Owner(43,3,900)]:[Owner(42,1,20)],Pose);
        foreach(var child in children)
        {
            var binding=Assert.IsType<EnbCurrentRetailLineBindingRuntime.Binding>(runtime.Resolve(child,out _));
            Assert.Equal(new Vector3(loseSource?10:20,0,0),binding.Start);
            Assert.Equal(new Vector3(loseSource?200:100,0,0),binding.End);
            Assert.Equal(loseSource,binding.SourceDetached);Assert.Equal(!loseSource,binding.TargetDetached);
            Assert.Equal(1ul,binding.Source.RenderIdentity);Assert.Equal(2ul,binding.Target!.RenderIdentity);
        }
        runtime.Update([],[],Pose);Assert.Equal(0,runtime.Count);
    }

    [Fact]
    public void ImpactKeepsEndedBeamBindingAndObservesLossOnTheTransitionFrame()
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=LineEffect\nBaseAssetID=67\nDuration=0.2\nNextLinkID=2\nUnaryNextLinkToTarget=TRUE\n[EFFECT-2]\nEffectClass=RObjEffect\nBaseAssetID=510\nDuration=1");
        var root=new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"",0,true,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var runtime=new EnbCurrentRetailLineBindingRuntime();
        Matrix4x4 Pose(IEnbRenderObject o,string bone)=>Matrix4x4.CreateTranslation(o.Spatial!.Transform.Position);
        runtime.Update(scheduler.Update([root],catalog,0).VisualNodes,[Owner(42,1,10),Owner(43,2,100)],Pose);
        var child=Assert.Single(scheduler.Update([root],catalog,200).VisualNodes);
        Assert.Equal(2,child.Definition.DefinitionId);
        runtime.Update([child],[],Pose);
        var parent=runtime.ResolveParent(child)!;
        Assert.True(parent.SourceDetached);Assert.True(parent.TargetDetached);Assert.True(parent.UnaryNextToTarget);
        Assert.Equal(new Vector3(10,0,0),parent.Start);Assert.Equal(new Vector3(100,0,0),parent.End);
        runtime.Update([],[],Pose);Assert.Equal(0,runtime.Count);
    }

    [Fact]
    public void ScheduledLineKeepsConstructionEndpointsUntilActivationAndLossStillDetaches()
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=LineEffect\nBaseAssetID=67\nDuration=2\nStartDelay=1");
        var root=new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"",0,true,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var node=Assert.Single(scheduler.Update([root],catalog,0).VisualNodes);
        Assert.False(node.Active);
        var runtime=new EnbCurrentRetailLineBindingRuntime();
        Matrix4x4 Pose(IEnbRenderObject o,string bone)=>Matrix4x4.CreateTranslation(o.Spatial!.Transform.Position);
        runtime.Update([node],[Owner(42,1,10),Owner(43,2,100)],Pose);
        runtime.Update([node],[Owner(42,1,20),Owner(43,2,200)],Pose);
        Assert.Equal(new Vector3(10,0,0),runtime.Resolve(node,out _)!.Start);
        Assert.Equal(new Vector3(100,0,0),runtime.Resolve(node,out _)!.End);
        runtime.Update([node],[Owner(42,1,20)],Pose);
        node=Assert.Single(scheduler.Update([root],catalog,1000).VisualNodes);
        Assert.True(node.Active);
        runtime.Update([node],[Owner(42,1,30),Owner(43,3,900)],Pose);
        Assert.Equal(new Vector3(30,0,0),runtime.Resolve(node,out _)!.Start);
        Assert.Equal(new Vector3(100,0,0),runtime.Resolve(node,out _)!.End);
        Assert.True(runtime.Resolve(node,out _)!.TargetDetached);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void LossRetainsLastPositionsAndNeverRebindsReusedIds(bool tracks)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=LineEffect\nBaseAssetID=67\nDuration=2\nTrackSource={tracks}\nTrackTarget={tracks}");
        var root=new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"BONE",0,true,1,new(0,5,0),0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var node=Assert.Single(scheduler.Update([root],catalog,0).VisualNodes);
        var runtime=new EnbCurrentRetailLineBindingRuntime();
        var source=Owner(42,1,10);var target=Owner(43,2,100);
        int calls=0;
        Matrix4x4 Pose(IEnbRenderObject o,string bone){calls++;Assert.Equal("BONE",bone);return Matrix4x4.CreateTranslation(o.Spatial!.Transform!.Position+new Vector3(3,0,0));}
        runtime.Update([node],[source,target],Pose);
        runtime.Update([node],[Owner(42,1,20),Owner(43,2,200)],Pose);
        var bound=runtime.Resolve(node,out _)!;
        Assert.Equal(new Vector3(tracks?23:13,0,0),bound.Start);
        Assert.Equal(new Vector3(tracks?200:100,5,0),bound.End);
        int beforeLoss=calls;
        runtime.Update([node],[],Pose);
        runtime.Update([node],[Owner(42,3,900),Owner(43,4,1000)],Pose);
        var lost=runtime.Resolve(node,out _)!;
        Assert.Equal(bound.Start,lost.Start);Assert.Equal(bound.End,lost.End);
        Assert.True(lost.SourceDetached);Assert.True(lost.TargetDetached);Assert.Equal(beforeLoss,calls);
        var next=EffectOwnerFixture.Copy(node,copy=>{copy.Root=root with {InstanceGeneration=2};});
        runtime.Update([next],[Owner(42,3,900),Owner(43,4,1000)],Pose);
        Assert.Equal(new Vector3(903,0,0),runtime.Resolve(next,out _)!.Start);
        Assert.Equal(1,runtime.Count);runtime.Update([],[],Pose);Assert.Equal(0,runtime.Count);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ObjectToPointTracksSourceRetainsWorldEndpointAndPassesItToChildren(bool tracks)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=LineEffect\nBaseAssetID=67\nDuration=0.2\nTrackSource={tracks}\nNextLinkID=2\n[EFFECT-2]\nEffectClass=LineEffect\nBaseAssetID=67\nDuration=0.2\nNextLinkID=3\nUnaryNextLinkToTarget=TRUE\n[EFFECT-3]\nEffectClass=RObjEffect\nBaseAssetID=510\nDuration=1");
        var root=new EnbSceneEffectSnapshot(1,1,42,0,1,0,0,"BONE",0,false,1,default,0,0,false,default,1,
            EnbSceneEffectGeometryKind.ObjectToPoint,new(100,25,50),-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();var runtime=new EnbCurrentRetailLineBindingRuntime();
        Matrix4x4 Pose(IEnbRenderObject o,string bone){Assert.Equal("BONE",bone);return Matrix4x4.CreateTranslation(o.Spatial!.Transform.Position+new Vector3(3,0,0));}
        var nodes=scheduler.Update([root],catalog,0).VisualNodes;
        runtime.Update(nodes,[Owner(42,1,10)],Pose);
        runtime.Update(nodes,[Owner(42,1,20)],Pose);
        var binding=runtime.Resolve(Assert.Single(nodes),out var error)!;
        Assert.Null(error);Assert.Null(binding.Target);Assert.True(binding.TargetDetached);
        Assert.Equal(new Vector3(tracks?23:13,0,0),binding.Start);Assert.Equal(root.WorldPoint,binding.End);
        nodes=scheduler.Update([root],catalog,200).VisualNodes;
        runtime.Update(nodes,[Owner(42,2,900)],Pose);
        var child=runtime.Resolve(Assert.Single(nodes),out _)!;
        Assert.Equal(binding.Start,child.Start);Assert.Equal(root.WorldPoint,child.End);Assert.True(child.SourceDetached);Assert.Null(child.Target);
        var impact=Assert.Single(scheduler.Update([root],catalog,400).VisualNodes);
        runtime.Update([impact],[],Pose);
        Assert.True(impact.UsesTargetBinding);Assert.Null(impact.BoundGameId);
        Assert.True(impact.TryGetRObjDefinition(out _,out error),error);
        Assert.Equal(root.WorldPoint,runtime.ResolveParent(impact)!.End);
        runtime.Update([],[],Pose);Assert.Equal(0,runtime.Count);
    }

    private static EnbSceneObjectSnapshot Owner(uint id,ulong identity,float x)=>new(identity,1,id,false,false,1,48,1,0,0,0,0,false,"fixture",new(false,0,null),
        new(EnbSceneSpatialProviderKind.Fixed,new(0,new(x,0,0),Quaternion.Identity,default,0,default,Vector3.UnitX),null),
        null,null,[],[],[],[],[],[],null,[],null,0,0);
}
