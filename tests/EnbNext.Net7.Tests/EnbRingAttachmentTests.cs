using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingAttachmentTests
{
    [Fact]
    public void RepeatedInstalledRingsKeepEightDistinctAuthoredPivotsAndPacketBindings()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var resolved=assets.BaseAssetResolver!.Resolve(1856);
        string objectName=resolved.Rsl!.ResourceGraph!.States.First(s=>s.Definition.Type==EnbRslStateType.Object).Definition.Object!;
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources!,objectName,out var source,out _));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out var error),error);
        var tree=resource.Hierarchies.Single();
        var descriptors=source.CurrentRetailAggregate!.Definition.Attachments;
        var selected=descriptors.Select((a,i)=>(a,i)).Where(x=>x.a.SubobjectName=="sk_pd01p1").ToArray();
        Assert.Equal(8,selected.Length);Assert.Equal(9,descriptors.Count);
        var rings=selected.Select(x=>EnbRetailRingAttachment.Load(assets,tree,x.a,x.i)).ToArray();
        Assert.Equal(8,rings.Select(r=>r.PivotIndex).Distinct().Count());
        Assert.Equal(8,rings.Select(r=>r.Ordinal).Distinct().Count());
        var world=Matrix4x4.CreateScale(2)*Matrix4x4.CreateTranslation(10,20,30);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(tree,world,out var poses,out error),error);
        var packets=rings.Select(r=>EnbRetailRingDrawPacket.Build(r.Asset,r.Asset.Sample(.32f,20)!,r.Key,r.Texture,
            poses[r.PivotIndex],true,320,91,7,42,r.Ordinal)).ToArray();
        Assert.Equal(8,packets.Select(p=>p.Mesh.Ordinal).Distinct().Count());
        Assert.True(Vector3.Distance(packets[0].Mesh.Positions[0],packets[1].Mesh.Positions[0])>.1f);
        Assert.All(packets,p=>Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(p,out _,out _)));
        Assert.Equal(0,EnbRetailRingAttachment.Load(assets,tree,new("sk_pd01p1","missing-pivot"),0).PivotIndex);
        var repeated=tree.Copy().SetPivots(tree.Pivots.Select(p=>p with {Name="Repeated"}).ToArray());
        Assert.Equal(0,EnbRetailRingAttachment.Load(assets,repeated,new("sk_pd01p1","repeated"),0).PivotIndex);
        Assert.Throws<InvalidDataException>(()=>EnbRetailRingAttachment.Load(assets,tree.Copy().SetPivots([]),selected[0].a,0));
        Assert.Throws<InvalidDataException>(()=>EnbRetailRingAttachment.Load(assets,tree,descriptors[0],0)); // sibling streak is not dropped or reinterpreted
    }
}
