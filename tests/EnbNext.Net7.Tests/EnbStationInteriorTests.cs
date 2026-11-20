using EnbNext.Assets;
using System.Reflection;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationInteriorTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void SupplementalAttachmentsUseLooseAndParentSearchButDoNotRetryOpenedCorruptFiles(bool parent,bool corruptPrimary)
    {
        string root=Path.Combine(Path.GetTempPath(),"EnbNext-StationSearch-"+Guid.NewGuid().ToString("N"));
        string client=Path.Combine(root,"release");Directory.CreateDirectory(client);
        try
        {
            var paths=EnbInstalledAssetLocator.TryLocate()!;
            var assets=EnbRetailAssetBundle.Load(paths with {ClientExecutablePath=Path.Combine(client,"client.exe")});
            Assert.True(assets.RetailMixResources!.TryReadResource("pextra_lounge.w3d",out var bytes,out _));
            const string name="station-search-fixture.w3d";
            File.WriteAllBytes(Path.Combine(parent?root:client,name),bytes);
            if(corruptPrimary)File.WriteAllBytes(Path.Combine(client,name),[1,2,3]);
            var room=EnbCurrentRetailStationInterior.Load(assets,new EnbNext.Presentation.EnbSceneStationDefinition(2,[new(3,0,[])]),3);
            var attached=room.WithSupplementalAttachments(assets,name);
            if(corruptPrimary)
            {
                Assert.Null(attached.ExtraAttachments);Assert.Null(attached.SupplementalResource);
                Assert.NotNull(attached.AttachmentDiagnostic);Assert.Same(room.Resource,attached.Resource);
            }
            else
            {
                Assert.NotNull(attached.ExtraAttachments);Assert.NotNull(attached.SupplementalResource);
                Assert.Null(attached.AttachmentDiagnostic);
                Assert.Equal(room.ExtraAttachments!.Pivots.Select(p=>p.Name),attached.ExtraAttachments.Pivots.Select(p=>p.Name));
            }
        }
        finally {Directory.Delete(root,true);}
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetUsesNamedBoneOrRootAndExactlyOneCameraDraw(bool named)
    {
        var q=System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ,.3f);
        EnbW3dRenderPivot Pivot(int index,string name,System.Numerics.Vector3 position)=>new(index,name,index==0?uint.MaxValue:0,
            position,default,q,System.Numerics.Matrix4x4.CreateFromQuaternion(q)*System.Numerics.Matrix4x4.CreateTranslation(position));
        var hierarchy=new EnbW3dRenderHierarchy(0,"reset",default,false,
            [Pivot(0,"ROOT",new(12,23,34)),Pivot(1,named?"SAFE_SPOT":"other",new(45,56,67))],[]);
        var resource=new EnbW3dRenderResource(default,"reset",[],[hierarchy],[],[],[]);
        var room=new EnbCurrentRetailStationInterior("reset",resource,default,System.Numerics.Quaternion.Identity);
        int draws=0;
        var pose=room.SelectResetSpawn(new EnbRetailCameraRandomSource(()=>{draws++;return 0x40000000u;}));
        Assert.Equal(1,draws);
        Assert.Equal(named?new System.Numerics.Vector3(45,56,67):System.Numerics.Vector3.Zero,pose.Translation);
        Assert.Equal((named?.3f:0)+MathF.PI/2,MathF.Atan2(pose.M12,pose.M11),5);
    }
    [Fact]
    public void CapturedRoomFogUsesExplicitGateAndRawParameterOrder()
    {
        var words=System.Collections.Immutable.ImmutableArray.Create(0u,1153138688u,1065353216u,0u,0u);
        var enabled=EnbCurrentRetailStationInterior.ResolveRoomFog(new(0,320,words));
        Assert.True(enabled.Enabled);Assert.Equal(0xFF0000u,enabled.SceneColorRgb24);
        Assert.Equal(0f,enabled.Start);Assert.Equal(1500f,enabled.End);
        foreach(uint flags in new uint[]{0,16,32,36,64})
            Assert.Equal(new EnbW3dSceneFogState(false,0,0,1000),EnbCurrentRetailStationInterior.ResolveRoomFog(new(0,flags,words)));
    }
    [Theory]
    [InlineData(0u,0u,"tsd01a")]
    [InlineData(1u,0x40u,"jsd01a2")]
    [InlineData(2u,0xc0u,"psd01a3")]
    [InlineData(9u,0u,"tsd07a")]
    public void EntryUsesDefinitionAndRoomFlags(uint definition,uint flags,string expected)
        => Assert.Equal(expected,EnbCurrentRetailStationInterior.EntryResourceName(definition,flags));

    [Fact]
    public async Task LoungeDefinitionPublishesImmediatelyAndIsDetached()
    {
        // One room, five parameters; no facilities/NPCs. No network connection.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach(uint value in new uint[] {2,1,0,0x40,10,20,30,40,50,0,0}) writer.Write(value);
        await using var session = new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        var publisher = new EnbPresentationSnapshotPublisher();
        EnbSceneSnapshot? observed = null;
        session.PresentationStateChanged += time => observed = publisher.Publish(session.World,null,null,time,
            gameplay:session.Gameplay,activeStarbaseId:10651);
        var dispatch = typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await (Task)dispatch.Invoke(session,[Packet.ForOpcode(0x0052,stream.ToArray()),"offline",CancellationToken.None])!;
        Assert.NotNull(observed?.StationDefinition);
        Assert.Equal(2u,observed.StationDefinition.DefinitionValue);
        var room = Assert.Single(observed.StationDefinition.Rooms);
        Assert.Equal(0x40u,room.Flags);
        ((uint[])session.Gameplay.LoungeDefinition!.Rooms[0].Parameters)[0]=999;
        Assert.Equal(10u,room.Parameters[0]);
        Assert.Null(publisher.Publish(session.World,null,null,20).StationDefinition);
        Assert.Equal(2u,observed.StationDefinition.DefinitionValue);
    }
}
