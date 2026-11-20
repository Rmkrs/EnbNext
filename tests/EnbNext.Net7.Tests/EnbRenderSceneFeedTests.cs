using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbRenderSceneFeedTests
{
    private static Net7CreateUpdate Create(uint id)=>new(id,1,10,1,0,0,0);
    [Fact]
    public void CoalescedFramesKeepOrderedReplacementAndDoNotConsumeFutureMutations()
    {
        var world=new Net7WorldState();var publisher=new EnbPresentationSnapshotPublisher();
        using var feed=publisher.RenderScene.Subscribe();
        world.MaterializeCreate(Create(7));world.MaterializeCreate(Create(264));
        var first=publisher.Publish(world,null,0);
        var nodes=new List<EnbSceneObjectSnapshot>();var changes=new List<EnbRenderObjectChange>();
        void Drain(ulong through)
        {
            while(feed.TryReadThrough(through,out var batch))
                foreach(var change in batch.Changes)
                {
                    changes.Add(change);
                    switch(change.Kind)
                    {
                        case EnbRenderObjectChangeKind.Reset:nodes.Clear();break;
                        case EnbRenderObjectChangeKind.Remove:nodes.RemoveAll(x=>x.RenderIdentity==change.Identity);break;
                        case EnbRenderObjectChangeKind.Add:
                            int at=change.After is {} prior?nodes.FindIndex(x=>x.RenderIdentity==prior):-1;
                            Assert.True(change.After is null||at>=0);nodes.Insert(at+1,change.Definition!);break;
                        case EnbRenderObjectChangeKind.Definition:
                            int index=nodes.FindIndex(x=>x.RenderIdentity==change.Identity);Assert.True(index>=0);
                            nodes[index]=change.Definition!;break;
                    }
                }
        }
        Drain(first.RenderSceneSequence);changes.Clear();
        var old=nodes.Single(x=>x.GameId==7);
        using var payload=new MemoryStream();
        using(var writer=new BinaryWriter(payload,System.Text.Encoding.UTF8,true))
        {writer.Write(7u);writer.Write(11);writer.Write(2f);writer.Write(.1f);writer.Write(.2f);writer.Write(.3f);}
        world.Apply(Packet.ForOpcode(0x0026,payload.ToArray()),1);
        var replacement=publisher.Publish(world,null,1);
        world.MaterializeRemove(7);world.MaterializeCreate(Create(8));
        var removal=publisher.Publish(world,null,2);
        Drain(first.RenderSceneSequence);Assert.Empty(changes);
        Drain(replacement.RenderSceneSequence);
        Assert.Single(changes);Assert.Equal(EnbRenderObjectChangeKind.Definition,changes[0].Kind);
        Assert.Equal(11,nodes.Single(x=>x.GameId==7).BaseAsset);Assert.Equal(10,old.BaseAsset);
        changes.Clear();Drain(removal.RenderSceneSequence);
        Assert.Equal(new[]{EnbRenderObjectChangeKind.Remove,EnbRenderObjectChangeKind.Add},changes.Select(x=>x.Kind));
        Assert.Equal(removal.Objects.Select(x=>x.RenderIdentity),nodes.Select(x=>x.RenderIdentity));
        Assert.Equal(10,old.BaseAsset);
    }

    [Fact]
    public void UnchangedPublicationsProduceNoDefinitionOrRetirementWorkAndLateViewsBootstrap()
    {
        var world=new Net7WorldState();world.MaterializeCreate(Create(7));
        var publisher=new EnbPresentationSnapshotPublisher();var scene=publisher.Publish(world,null,0);
        using var feed=publisher.RenderScene.Subscribe();
        Assert.True(feed.TryReadThrough(scene.RenderSceneSequence,out var initial));
        Assert.Equal(2,initial.Changes.Length);
        for(uint frame=1;frame<=100;frame++)
        {
            scene=publisher.Publish(world,null,frame);
            Assert.False(feed.TryReadThrough(scene.RenderSceneSequence,out _));
        }
        using var late=publisher.RenderScene.Subscribe();
        Assert.True(late.TryReadThrough(scene.RenderSceneSequence,out var bootstrap));
        Assert.Equal(scene.Objects[0].RenderIdentity,bootstrap.Changes[1].Identity);
        publisher.Clear();Assert.False(feed.TryReadThrough(scene.RenderSceneSequence,out _));
        Assert.True(feed.TryReadThrough(ulong.MaxValue,out var reset));
        Assert.Equal(EnbRenderObjectChangeKind.Reset,Assert.Single(reset.Changes).Kind);
        feed.Dispose();world.MaterializeCreate(Create(8));publisher.Publish(world,null,101);
        Assert.False(feed.TryReadThrough(ulong.MaxValue,out _));
    }
}
