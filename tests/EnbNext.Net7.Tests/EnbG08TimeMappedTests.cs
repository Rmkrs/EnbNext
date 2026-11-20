using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.TestFixtures;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbG08TimeMappedTests
{
    [Fact]
    public void RetainedResourcePublicationIsOneTimeAndNewResourcesKeepFirstWinner()
    {
        var owner=new EnbW3dTimeMappedAcquisition();
        var file=new EnbResolvedRslFileDependency("",EnbRslFileDependencyStatus.NotAuthored);
        var animation=new EnbW3dRenderHierarchyAnimation(0,0x40000,"A","RIG",3,30,false,[],[]);
        var first=G08Fixture.Empty().Copy().SetAnimations([animation]);
        Assert.Same(animation,owner.Resolve(first,file,null,null,null,"RIG.A"));
        for(int i=0;i<20;i++)owner.Resolve(first,file,null,null,null,"RIG.A");
        long start=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<100;i++)owner.Resolve(first,file,null,null,null,"RIG.A");
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-start);
        Assert.Equal(1,owner.RetainedPublicationCount);
        var replacement=animation.Copy().SetNumFrames(10);
        var added=animation.Copy().SetName("B");
        var second=first.Copy().SetAnimations([replacement,added]);
        Assert.Same(animation,owner.Resolve(second,file,null,null,null,"RIG.A"));
        Assert.Same(added,owner.Resolve(second,file,null,null,null,"RIG.B"));
        Assert.Equal(2,owner.RetainedPublicationCount);
        owner.Clear();
        Assert.Same(replacement,owner.Resolve(second,file,null,null,null,"RIG.A"));
        Assert.Equal(1,owner.RetainedPublicationCount);
    }
    [Fact]
    public void RetainedResourcePublicationDoesNotKeepDiscardedResourceAlive()
    {
        var owner=new EnbW3dTimeMappedAcquisition();var weak=PublishTransientResource(owner);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        Assert.False(weak.IsAlive);Assert.Equal(1,owner.Count);GC.KeepAlive(owner);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference PublishTransientResource(EnbW3dTimeMappedAcquisition owner)
    {
        var resource=G08Fixture.Empty().Copy().SetAnimations([new(0,0x40000,"A","RIG",3,30,false,[],[])]);
        owner.Resolve(resource,new("",EnbRslFileDependencyStatus.NotAuthored),null,null,null,"RIG.A");return new(resource);
    }

    [Fact]
    public void NegativeNameSurvivesAnotherDrawOwnerUntilExplicitPublication()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext.G08",Guid.NewGuid().ToString("N"));
        try
        {
            var bytes=G08Fixture.Classic("A",0,1,[0,20,4]);
            var catalog=G08Fixture.Mix(directory,("A.w3d",bytes));
            string archive=Path.Combine(directory,"mixfile_art_01.mix");
            byte[] saved=System.IO.File.ReadAllBytes(archive);
            System.IO.File.Delete(archive);
            var registry=new EnbCurrentRetailRslHAnimRegistryRuntime();
            var absent=new EnbResolvedRslFileDependency("",EnbRslFileDependencyStatus.NotAuthored);
            Assert.False(registry.TryResolve(G08Fixture.Empty(),absent,null,catalog,null,"RIG.A",out _,out _));
            System.IO.File.WriteAllBytes(archive,saved);
            var another=G08Fixture.Empty().Copy().SetKey(new("another.w3d","fixture",0,0));
            Assert.False(registry.TryResolve(another,absent,null,catalog,null,"RIG.A",out _,out _));
            var publication=G08Fixture.Decode(bytes);
            Assert.True(registry.TryResolve(another,publication,null,catalog,null,"RIG.A",out var found,out var error),error);
            Assert.True(EnbW3dAnimationSampler.TrySamplePivot(found.Animation,1,1,out var sample,out error),error);
            Assert.Equal(20,sample.Translation.X);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void PersistentRegistryUsesFirstClassicPublicationAcrossFilesAndFamilies()
    {
        var registry=new EnbCurrentRetailRslHAnimRegistryRuntime();
        var first=G08Fixture.Decode(G08Fixture.Join(G08Fixture.Hierarchy(),G08Fixture.Classic("A",0,1,[0,20,4]))) with {LogicalFileName="first.w3d"};
        var second=G08Fixture.Decode(G08Fixture.Join(G08Fixture.Hierarchy(),G08Fixture.Classic("B",0,1,[0,80,16]),G08Fixture.Mapped("A",["RIG.B"]))) with {LogicalFileName="second.w3d"};
        Assert.True(registry.TryResolve(G08Fixture.Empty(),first,null,null,null,"RIG.A",out var original,out var error),error);
        Assert.True(registry.TryResolve(G08Fixture.Empty(),second,null,null,null,"rig.a",out var duplicate,out error),error);
        Assert.Same(original.Animation,duplicate.Animation);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(duplicate.Animation,1,1,out var sample,out error),error);
        Assert.Equal(20,sample.Translation.X);
        registry.Clear();
        Assert.True(registry.TryResolve(G08Fixture.Empty(),second,null,null,null,"RIG.A",out var replaced,out error),error);
        Assert.NotSame(original.Animation,replaced.Animation);
        Assert.NotNull(replaced.Animation.TimeMappedPlayback);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(original.Animation,1,1,out sample,out error),error);
        Assert.Equal(20,sample.Translation.X);
    }

    [Fact]
    public void DuplicateClassicRootsKeepFirstFileOrderAndRecoverEarlierMiss()
    {
        var registry=new EnbCurrentRetailRslHAnimRegistryRuntime();
        var retained=G08Fixture.Empty();
        var absent=new EnbResolvedRslFileDependency("",EnbRslFileDependencyStatus.NotAuthored);
        Assert.False(registry.TryResolve(retained,absent,null,null,null,"RIG.A",out _,out _));
        var file=G08Fixture.Decode(G08Fixture.Join(G08Fixture.Hierarchy(),G08Fixture.Classic("A",0,1,[0,20,4]),G08Fixture.Classic("A",0,1,[0,90,18])));
        Assert.True(registry.TryResolve(retained,file,null,null,null,"RIG.A",out var first,out var error),error);
        Assert.True(registry.TryResolve(retained,absent,null,null,null,"RIG.A",out var recovered,out error),error);
        Assert.Same(first.Animation,recovered.Animation);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(first.Animation,1,1,out var sample,out error),error);
        Assert.Equal(20,sample.Translation.X);
    }

    [Fact]
    public void DuplicateFileIdentityTrackingDoesNotRetainDiscardedDecodedOwners()
    {
        var owner=new EnbW3dTimeMappedAcquisition();
        owner.Resolve(G08Fixture.Empty(),G08Fixture.Decode(G08Fixture.File()),null,null,null,"RIG.MAP");
        var weak=PublishDuplicate(owner);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        Assert.False(weak.IsAlive);Assert.Equal(1,owner.DuplicateCount);GC.KeepAlive(owner);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference PublishDuplicate(EnbW3dTimeMappedAcquisition owner)
    {
        var file=G08Fixture.Decode(G08Fixture.File());
        owner.Resolve(G08Fixture.Empty(),file,null,null,null,"RIG.MAP");return new(file.Model);
    }
    [Fact]
    public void RepeatedPartialPivotWritesOverwriteInFileOrder()
    {
        var mapped=G08Fixture.Mapped("MAP",["RIG.A","RIG.B"]);
        mapped=G08Fixture.Chunk(0x2c0,G08Fixture.Join(mapped[8..],G08Fixture.Chunk(0x2c5,[0,0,0,0,1])));
        var file=G08Fixture.Decode(G08Fixture.Join(G08Fixture.Hierarchy(),G08Fixture.Classic("A",0,1,[0,20,4]),
            G08Fixture.Classic("B",1,2,[0,40,8]),mapped));
        var clip=new EnbW3dTimeMappedAcquisition().Resolve(G08Fixture.Empty(),file,null,null,null,"RIG.MAP")!;
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,1,2,out var first,out _));Assert.Equal(0,first.Translation.X);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,2,2,out var second,out _));Assert.Equal(4,second.Translation.Y);
    }

    [Fact]
    public void UnsafeSourceStorageDoesNotDiscardValidSibling()
    {
        var file=G08Fixture.Decode(G08Fixture.Join(G08Fixture.Hierarchy(),G08Fixture.Mapped("BAD",["RIG.A"],count:2),
            G08Fixture.Classic("A",0,1,[0,20,4]),G08Fixture.Mapped("GOOD",["RIG.A"])));
        var owner=new EnbW3dTimeMappedAcquisition();
        Assert.Null(owner.Resolve(G08Fixture.Empty(),file,null,null,null,"RIG.BAD"));
        var clip=owner.Resolve(G08Fixture.Empty(),file,null,null,null,"RIG.GOOD")!;
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,1,2,out var sample,out _));Assert.Equal(2,sample.Translation.X);
    }
    [Fact]
    public void RotationSamplesEndpointQuaternionsAndIgnoresSourceVisibilityStorage()
    {
        var rotation=new EnbW3dRenderAnimationChannel(0,2,4,6,EnbW3dAnimationChannelKind.Quaternion,1,1,
            [0,0,0,1, 0,0,0,1, 1,0,0,0]);
        var source=new EnbW3dRenderHierarchyAnimation(0,0x40000,"A","RIG",3,30,false,[rotation],
            [new(0,2,0,EnbW3dAnimationBitChannelKind.Visibility,1,1,0,[]) ]);
        var retained=G08Fixture.Empty().Copy().SetAnimations([source]);
        var file=G08Fixture.Decode(G08Fixture.Join(G08Fixture.Hierarchy(),G08Fixture.Mapped("MAP",["RIG.A"])));
        var clip=new EnbW3dTimeMappedAcquisition().Resolve(retained,file,null,null,null,"RIG.MAP")!;
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,1,2,out var sample,out var error),error);
        Assert.InRange(sample.Rotation.X,.7070f,.7072f);Assert.InRange(sample.Rotation.W,.7070f,.7072f);Assert.True(sample.Visible);
    }

    [Fact]
    public void SameFileOrderingDoesNotPublishLaterSourceBeforeMappedAcquisition()
    {
        var file=G08Fixture.Decode(G08Fixture.Join(G08Fixture.Hierarchy(),G08Fixture.Mapped("MAP",["RIG.LATE"]),G08Fixture.Classic("LATE",0,1,[0,20,4])));
        var location=new EnbMixResourceLocation("mapped.w3d","fixture",0,0);
        file=file with {ResourceLocation=location};
        var source=new EnbResolvedW3dResource("mapped.w3d","",location,file.Model!,[]);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var retained,out var error),error);
        var owner=new EnbW3dTimeMappedAcquisition();var clip=owner.Resolve(retained,file,null,null,null,"RIG.MAP")!;
        Assert.NotNull(clip);Assert.False(EnbW3dAnimationSampler.TrySamplePivot(clip,1,2,out _,out _));
        Assert.NotNull(owner.Resolve(retained,file,null,null,null,"RIG.LATE"));
    }

    [Fact]
    public void FractionalRateFeedsTheProductionRslClock()
    {
        var clip=new EnbW3dTimeMappedAcquisition().Resolve(G08Fixture.Empty(),G08Fixture.Decode(G08Fixture.File()),null,null,null,"RIG.MAP")!;
        var state=EnbCurrentRetailRslAnimationFrameRuntime.Advance(0,clip.PlaybackFrameRate,clip.NumFrames,.8f);
        Assert.Equal(2,state.RawFrame);Assert.False(state.TerminalCallback);
        state=EnbCurrentRetailRslAnimationFrameRuntime.Advance(7,clip.PlaybackFrameRate,clip.NumFrames,1);
        Assert.True(state.TerminalCallback);Assert.Equal(.5f,EnbCurrentRetailRslAnimationFrameRuntime.Publish(state,0x10));
    }
    [Fact]
    public void RecursiveLazyFilesAcquireHierarchyAndSourcesAndSurviveSourceDeletion()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext.G08",Guid.NewGuid().ToString("N"));
        try
        {
            var catalog=G08Fixture.Mix(directory,("MAP.w3d",G08Fixture.Mapped("MAP",["RIG.INNER"])),
                ("RIG.w3d",G08Fixture.Hierarchy()),("INNER.w3d",G08Fixture.Mapped("INNER",["RIG.A"])),
                ("A.w3d",G08Fixture.Classic("A",0,1,[0,20,4])));
            var registry=new EnbCurrentRetailRslHAnimRegistryRuntime();var empty=G08Fixture.Empty();
            var file=new EnbResolvedRslFileDependency("",EnbRslFileDependencyStatus.NotAuthored);
            Assert.True(registry.TryResolve(empty,file,null,catalog,null,"RIG.MAP",out var first,out var error),error);
            System.IO.File.Delete(Path.Combine(directory,"mixfile_art_01.mix"));
            Assert.True(registry.TryResolve(empty,file,null,catalog,null,"RIG.MAP",out var again,out error),error);
            Assert.Same(first,again);
            Assert.True(EnbW3dAnimationSampler.TrySamplePivot(first.Animation,1,2,out var sample,out error),error);Assert.Equal(1,sample.Translation.X);
            registry.Clear();Assert.True(EnbW3dAnimationSampler.TrySamplePivot(first.Animation,1,2,out _,out _));
        }
        finally{Directory.Delete(directory,true);}
    }

    [Fact]
    public void CyclicOptionalSourceCannotStallClient()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext.G08",Guid.NewGuid().ToString("N"));
        try
        {
            var catalog=G08Fixture.Mix(directory,("MAP.w3d",G08Fixture.Join(G08Fixture.Hierarchy(),G08Fixture.Mapped("MAP",["RIG.MAP"]))));
            var owner=new EnbW3dTimeMappedAcquisition();
            var clip=owner.Resolve(G08Fixture.Empty(),new("",EnbRslFileDependencyStatus.NotAuthored),null,catalog,null,"RIG.MAP");
            Assert.NotNull(clip);Assert.False(EnbW3dAnimationSampler.TrySamplePivot(clip,1,1,out _,out _));
        }
        finally{Directory.Delete(directory,true);}
    }
    [Theory]
    [InlineData(-1,0,0)]
    [InlineData(0,0,0)]
    [InlineData(2,2,4)]
    [InlineData(4,4,8)]
    [InlineData(6,12,24)]
    [InlineData(8,20,40)]
    [InlineData(100,20,40)]
    public void FileSourcesReachProductionSampler(float frame,float x,float y)
    {
        var registry=new EnbCurrentRetailRslHAnimRegistryRuntime();
        Assert.True(registry.TryResolve(G08Fixture.Empty(),G08Fixture.Decode(G08Fixture.File()),null,null,null,"RIG.MAP",out var found,out var error),error);
        Assert.Equal(2.5f,found.Animation.PlaybackFrameRate);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(found.Animation,1,frame,out var a,out error),error);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(found.Animation,2,frame,out var b,out error),error);
        Assert.Equal(x,a.Translation.X);Assert.Equal(y,b.Translation.Y);Assert.True(a.Visible);Assert.True(b.Visible);
    }

    [Fact]
    public void RetainedSourceOwnersSurviveRegistryRetirementAndReverseQueries()
    {
        var owner=new EnbW3dTimeMappedAcquisition();var file=G08Fixture.Decode(G08Fixture.File());
        var clip=owner.Resolve(G08Fixture.Empty(),file,null,null,null,"RIG.MAP")!;
        owner.Clear();Assert.Equal(0,owner.Count);
        foreach(float frame in new float[]{8,2,6,0,4,2})
        {Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,1,frame,out var sample,out var error),error);Assert.Equal(frame==8?20:frame==6?12:frame,sample.Translation.X);}
    }

    [Fact]
    public void DuplicateConstructsBeforeFirstNameWins()
    {
        var owner=new EnbW3dTimeMappedAcquisition();
        var first=owner.Resolve(G08Fixture.Empty(),G08Fixture.Decode(G08Fixture.File()),null,null,null,"RIG.MAP");
        var duplicate=owner.Resolve(G08Fixture.Empty(),G08Fixture.Decode(G08Fixture.File(rate:90)),null,null,null,"RIG.MAP");
        Assert.Same(first,duplicate);Assert.Equal(2,owner.ConstructedCount);Assert.Equal(1,owner.DuplicateCount);
        Assert.Equal(2.5f,duplicate!.PlaybackFrameRate);
    }

    [Fact]
    public void MissingSourceIsRetainedAndContainedOnlyWhenSelected()
    {
        var owner=new EnbW3dTimeMappedAcquisition();
        var clip=owner.Resolve(G08Fixture.Empty(),G08Fixture.Decode(G08Fixture.File(second:"RIG.MISSING")),null,null,null,"RIG.MAP")!;
        Assert.NotNull(clip);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,1,2,out _,out _));
        Assert.False(EnbW3dAnimationSampler.TrySamplePivot(clip,2,2,out _,out var error));Assert.Contains("missing",error);
    }

    [Fact]
    public void MissingHierarchyDoesNotPublishMappedOwnerOrLoseSibling()
    {
        var owner=new EnbW3dTimeMappedAcquisition();
        var file=G08Fixture.Decode(G08Fixture.Join(G08Fixture.Mapped("MAP",["RIG.A"]),G08Fixture.Classic("A",0,1,[0,1,2])));
        Assert.Null(owner.Resolve(G08Fixture.Empty(),file,null,null,null,"RIG.MAP"));
        Assert.NotNull(owner.Resolve(G08Fixture.Empty(),file,null,null,null,"RIG.A"));
        Assert.Equal(0,owner.ConstructedCount);
    }

    [Fact]
    public void NestedMappedSourceUsesItsOwnTrackAndCanBeRetiredIndependently()
    {
        var bytes=G08Fixture.Join(G08Fixture.File(),G08Fixture.Mapped("OUTER",["RIG.MAP"]));
        var owner=new EnbW3dTimeMappedAcquisition();
        var clip=owner.Resolve(G08Fixture.Empty(),G08Fixture.Decode(bytes),null,null,null,"RIG.OUTER")!;
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,1,2,out var sample,out var error),error);
        Assert.Equal(1,sample.Translation.X);
    }
}
