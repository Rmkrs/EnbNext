using System.Numerics;
using System.Collections;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbEffectOwnerLifetimeTests
{
    private static (EnbCurrentRetailEffectOwner Node,EnbRetailPresentationCatalog Catalog) Create()
    {
        var catalog=new EnbRetailPresentationCatalog();catalog.LoadEffectsText("[EFFECT-674]\nEffectClass=AttachEffect\nDuration=INFINITE");
        var root=new EnbSceneEffectSnapshot(1,1,23,23,674,0,0,"",0,false,1,default,0,0,true,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null)
            {EndpointReceipt=new(7,null)};
        return (new(1,catalog.FindEffect(674)!,root,23,true,0),catalog);
    }
    [Fact]
    public void PersistentSourceNeedsNoFrameLookupAndRetirementCannotRebindRecycledId()
    {
        var (node,_)=Create();var source=new SourceOwner {GameId=23,RenderIdentity=99,WorldCreationOrdinal=7};
        node.UpdateAttachment([source]);Assert.Same(source,node.Attachment!.Owner);Assert.Equal(1,source.Listeners);
        var forbidden=new ForbiddenLookup();
        for(int i=0;i<100;i++)node.UpdateAttachment(forbidden);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++)node.UpdateAttachment(forbidden);
        long bytes=GC.GetAllocatedBytesForCurrentThread()-before;Assert.Equal(0,bytes);
        source.Retire();Assert.Null(node.Attachment.Owner);Assert.Equal(0,source.Listeners);
        node.UpdateAttachment([new SourceOwner{GameId=23,RenderIdentity=100,WorldCreationOrdinal=7}]);
        Assert.Null(node.Attachment.Owner);node.ClearAttachment();Assert.Null(node.Attachment);
    }
    [Fact]
    public void EffectReleaseUnsubscribesAndSchedulerReturnsTheActualSameOwner()
    {
        var (node,catalog)=Create();var source=new SourceOwner {GameId=23,WorldCreationOrdinal=7};
        node.UpdateAttachment([source]);node.ClearAttachment();Assert.Equal(0,source.Listeners);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var first=Assert.Single(scheduler.Update([node.Root],catalog,0,worldObjects:[source]).VisualNodes);
        first.UpdateAttachment([source]);Assert.Equal(1,source.Listeners);
        var second=Assert.Single(scheduler.Update([node.Root],catalog,10,worldObjects:[source]).VisualNodes);
        Assert.Same(first,second);Assert.Same(first.Attachment,second.Attachment);
        scheduler.Clear();Assert.Equal(0,source.Listeners);Assert.Null(first.Attachment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnarySourceOwnerHasZeroWarmLookupsAndRetainsTheFinalPose(bool tracks)
    {
        var (node,catalog)=Create();
        catalog.LoadEffectsText($"[EFFECT-674]\nEffectClass=RObjEffect\nBaseAssetID=1502\nDuration=2\nTrackSource={tracks}\nRObjHSV=.1,.2,.3");
        node.Definition=catalog.FindEffect(674)!;
        var spatial=new EnbRenderSpatialState();
        spatial.AdoptDefinition(new EnbSceneSpatialSnapshot(EnbSceneSpatialProviderKind.Fixed,
            new(0,new(1,2,3),Quaternion.Identity,default,0,default,Vector3.UnitX),null));
        var source=new SourceOwner {GameId=23,RenderIdentity=99,WorldCreationOrdinal=7,Spatial=spatial};
        node.UpdateSource([source]);Assert.Equal(1,source.Listeners);
        Assert.True(node.TryGetRObjDefinition(out var definition,out _));
        var forbidden=new ForbiddenLookup();
        for(int i=0;i<100;i++)node.UpdateSource(forbidden);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++){node.UpdateSource(forbidden);node.TryGetRObjDefinition(out _,out _);node.ResolveSource(forbidden);}
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        spatial.SetWorldTransform(spatial.Transform,Matrix4x4.CreateTranslation(10,20,30));
        node.UpdateSource(forbidden);
        var expected=tracks?new Vector3(10,20,30):new Vector3(1,2,3);
        Assert.Equal(expected,node.ResolveSource(forbidden)!.Spatial!.Transform.Position);
        source.Retire();Assert.Equal(0,source.Listeners);Assert.True(node.SourceBinding!.IsDetached);
        spatial.SetWorldTransform(spatial.Transform,Matrix4x4.CreateTranslation(900,900,900));
        node.UpdateSource(forbidden);Assert.Equal(expected,node.ResolveSource(forbidden)!.Spatial!.Transform.Position);
        Assert.True(node.TryGetRObjDefinition(out var after,out _));Assert.Same(definition,after);
        node.ClearSource();Assert.Null(node.SourceBinding);
    }

    [Fact]
    public void NetworkPoseObservationsKeepConstructionAndExplicitReplacementInvalidatesIt()
    {
        var (_,catalog)=Create();
        catalog.LoadEffectsText("[EFFECT-674]\nEffectClass=RObjEffect\nBaseAssetID=1502\nDuration=2\nRObjScale=2");
        var (sample,_)=Create();var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var first=Assert.Single(scheduler.Update([sample.Root with {EndpointReceipt=null}],catalog,0).VisualNodes);
        Assert.True(first.TryGetRObjDefinition(out var initial,out _));Assert.Equal(2,initial.AssetScale);
        var observed=first.Root with {PoseContext=Guid.NewGuid()};
        var second=Assert.Single(scheduler.Update([observed],catalog,1).VisualNodes);
        Assert.Same(first,second);Assert.True(second.TryGetRObjDefinition(out var retained,out _));Assert.Same(initial,retained);
        second.Root=observed with {Scale=3};
        Assert.True(second.TryGetRObjDefinition(out var replacement,out _));Assert.Equal(6,replacement.AssetScale);Assert.NotSame(initial,replacement);
        scheduler.Clear();Assert.Null(second.SourceBinding);
    }

    private sealed class SourceOwner:EnbRenderObjectState,IEnbRenderObjectLifetime
    {
        private Action? retired;public bool IsRetired{get;private set;}
        public int Listeners=>retired?.GetInvocationList().Length??0;
        public event Action Retired{add=>retired+=value;remove=>retired-=value;}
        public void Retire(){IsRetired=true;retired?.Invoke();}
    }
    private sealed class ForbiddenLookup:IReadOnlyList<IEnbRenderObject>
    {
        public int Count=>throw new InvalidOperationException("Unchanged source must not inspect membership");
        public IEnbRenderObject this[int i]=>throw new InvalidOperationException();
        public IEnumerator<IEnbRenderObject> GetEnumerator()=>throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
}
